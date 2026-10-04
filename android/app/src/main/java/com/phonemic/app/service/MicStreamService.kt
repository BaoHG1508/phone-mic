package com.phonemic.app.service

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.wifi.WifiManager
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.os.PowerManager
import android.os.SystemClock
import android.util.Log
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import androidx.core.content.ContextCompat
import com.phonemic.app.R
import com.phonemic.app.audio.AudioCapture
import com.phonemic.app.protocol.Pmic
import com.phonemic.app.transport.TcpTransport
import com.phonemic.app.transport.Transport
import com.phonemic.app.transport.UdpTransport
import com.phonemic.app.ui.MainActivity
import kotlinx.coroutines.flow.update
import java.io.IOException
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicLong
import kotlin.math.max
import kotlin.math.min

/**
 * Foreground service (type "microphone") that owns the capture thread, the send queue, the
 * network transport and the keep-alive/stat bookkeeping. It keeps streaming while the app is in
 * the background or the screen is off.
 *
 * Pipeline:  AudioCapture thread -> ArrayBlockingQueue -> sender thread -> Transport -> PC
 */
class MicStreamService : Service() {

    private var config: StreamConfig? = null
    private var capture: AudioCapture? = null
    private var transport: Transport? = null
    private var senderThread: Thread? = null
    private var housekeepingThread: Thread? = null
    private val queue = ArrayBlockingQueue<ByteArray>(QUEUE_CAPACITY)

    @Volatile
    private var running = false

    private var wakeLock: PowerManager.WakeLock? = null
    private var wifiLock: WifiManager.WifiLock? = null

    private val packetsSent = AtomicLong()
    private val bytesSent = AtomicLong()

    @Volatile
    private var lastPongAt = 0L

    @Volatile
    private var startedAt = 0L

    private val mainHandler = Handler(Looper.getMainLooper())

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_START -> {
                val cfg = StreamConfig.readFrom(intent)
                if (cfg == null) {
                    stopSelf()
                } else {
                    startStreaming(cfg)
                }
            }
            ACTION_TOGGLE_MUTE -> toggleMute()
            ACTION_STOP -> stopStreaming(keepError = false)
            else -> if (!running) stopSelf()
        }
        return START_NOT_STICKY
    }

    override fun onDestroy() {
        stopPipeline()
        releaseLocks()
        super.onDestroy()
    }

    // ---------------------------------------------------------------- lifecycle

    private fun startStreaming(cfg: StreamConfig) {
        Log.i(TAG, "start: ${cfg.transport} -> ${cfg.targetLabel}, ${cfg.sampleRate} Hz x${cfg.channels}, source ${cfg.audioSource}")
        if (running) stopPipeline()
        config = cfg
        createNotificationChannel()
        val notification = buildNotification(cfg, muted = false)
        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                startForeground(NOTIFICATION_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE)
            } else {
                startForeground(NOTIFICATION_ID, notification)
            }
        } catch (e: Exception) {
            publish { it.copy(status = Status.ERROR, error = "Cannot start foreground service: ${e.message}") }
            stopSelf()
            return
        }
        acquireLocks()

        running = true
        startedAt = SystemClock.elapsedRealtime()
        lastPongAt = 0L
        packetsSent.set(0)
        bytesSent.set(0)
        queue.clear()
        publish { StreamState(status = Status.STARTING, config = cfg, startedAtMs = startedAt) }

        val t: Transport = when (cfg.transport) {
            TransportMode.WIFI_UDP -> UdpTransport(cfg.effectiveHost, cfg.port)
            TransportMode.WIFI_TCP, TransportMode.USB_TCP -> TcpTransport(cfg.effectiveHost, cfg.port)
        }
        t.onControlPacket = { pkt ->
            if (pkt.type == Pmic.TYPE_PONG || pkt.type == Pmic.TYPE_ANNOUNCE) {
                lastPongAt = SystemClock.elapsedRealtime()
            }
        }
        transport = t

        val cap = AudioCapture(
            config = cfg,
            onPacket = ::enqueue,
            onLevel = { db -> publish { it.copy(levelDb = db) } },
            onError = { message -> onFatalError(message) },
        )
        capture = cap

        senderThread = Thread({ senderLoop(t) }, "pmic-sender").apply { start() }
        housekeepingThread = Thread(::housekeepingLoop, "pmic-housekeeping").apply {
            isDaemon = true
            start()
        }
        cap.start()
    }

    private fun stopStreaming(keepError: Boolean) {
        Log.i(TAG, "stop")
        stopPipeline()
        releaseLocks()
        publish { if (keepError && it.status == Status.ERROR) it else StreamState() }
        ServiceCompat.stopForeground(this, ServiceCompat.STOP_FOREGROUND_REMOVE)
        stopSelf()
    }

    private fun stopPipeline() {
        running = false
        capture?.stop()
        capture = null
        housekeepingThread?.interrupt()
        housekeepingThread = null
        senderThread?.join(2000)
        senderThread = null
        transport?.close()
        transport = null
        queue.clear()
    }

    private fun onFatalError(message: String) {
        Log.e(TAG, "fatal: $message")
        publish { it.copy(status = Status.ERROR, error = message) }
        // Called from the capture thread; tearing down joins that thread, so hop to the main looper.
        mainHandler.post { if (running) stopStreaming(keepError = true) }
    }

    private fun toggleMute() {
        val cap = capture ?: return
        val cfg = config ?: return
        cap.muted = !cap.muted
        publish { it.copy(muted = cap.muted) }
        getSystemService(NotificationManager::class.java).notify(NOTIFICATION_ID, buildNotification(cfg, cap.muted))
    }

    // ---------------------------------------------------------------- pipeline threads

    private fun enqueue(packet: ByteArray) {
        if (!queue.offer(packet)) {
            // The link is slower than the microphone: drop the oldest frame to bound latency.
            queue.poll()
            queue.offer(packet)
        }
    }

    private fun senderLoop(t: Transport) {
        var backoffMs = INITIAL_BACKOFF_MS
        while (running) {
            try {
                Log.i(TAG, "connecting: ${t.description}")
                t.connect()
                Log.i(TAG, "connected: ${t.description}")
                val connectedAt = SystemClock.elapsedRealtime()
                publish { it.copy(status = Status.STREAMING, error = null) }
                while (running) {
                    // Only a link that survives a while proves the PC is really there. Over USB, adb
                    // accepts the local connection even when nothing listens on the PC and drops it a
                    // few ms later; resetting the backoff there would make us hammer it twice a second.
                    if (backoffMs != INITIAL_BACKOFF_MS && SystemClock.elapsedRealtime() - connectedAt > STABLE_LINK_MS) {
                        backoffMs = INITIAL_BACKOFF_MS
                    }
                    val pkt = queue.poll(200, TimeUnit.MILLISECONDS) ?: continue
                    if (!t.isConnected) throw IOException("connection lost")
                    t.send(pkt)
                    packetsSent.incrementAndGet()
                    bytesSent.addAndGet(pkt.size.toLong())
                }
            } catch (e: IOException) {
                if (!running) break
                Log.w(TAG, "link error (${e.message}); retrying in $backoffMs ms")
                publish { it.copy(status = Status.RECONNECTING, error = e.message) }
                queue.clear()
                try {
                    Thread.sleep(backoffMs)
                } catch (ie: InterruptedException) {
                    break
                }
                backoffMs = min(backoffMs * 2, MAX_BACKOFF_MS)
            } catch (e: InterruptedException) {
                break
            }
        }
        try {
            if (t.isConnected) t.send(Pmic.control(Pmic.TYPE_BYE))
        } catch (e: IOException) {
            // best effort
        }
        t.close()
    }

    private fun housekeepingLoop() {
        var lastBytes = 0L
        var lastAt = SystemClock.elapsedRealtime()
        var lastWakeRenewAt = lastAt
        var lastReachable = true
        while (running) {
            try {
                Thread.sleep(1000)
            } catch (e: InterruptedException) {
                return
            }
            if (!running) return
            enqueue(Pmic.control(Pmic.TYPE_PING))
            val now = SystemClock.elapsedRealtime()
            if (now - lastWakeRenewAt >= WAKE_LOCK_TIMEOUT_MS / 2) {
                wakeLock?.acquire(WAKE_LOCK_TIMEOUT_MS)
                lastWakeRenewAt = now
            }
            val bytes = bytesSent.get()
            // bits per millisecond == kbit/s
            val kbps = ((bytes - lastBytes) * 8 / max(1L, now - lastAt)).toInt()
            lastBytes = bytes
            lastAt = now
            val reachable = now - max(lastPongAt, startedAt) < PC_TIMEOUT_MS
            if (reachable != lastReachable) {
                Log.i(TAG, if (reachable) "PC receiver is answering" else "no PONG from PC for ${PC_TIMEOUT_MS} ms")
                lastReachable = reachable
            }
            publish { it.copy(packetsSent = packetsSent.get(), kbps = kbps, pcReachable = reachable) }
        }
    }

    private inline fun publish(transform: (StreamState) -> StreamState) {
        StreamStateHolder.state.update(transform)
    }

    // ---------------------------------------------------------------- locks & notification

    private fun acquireLocks() {
        releaseLocks()
        val pm = getSystemService(PowerManager::class.java)
        wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "PhoneMic:stream").apply {
            setReferenceCounted(false)
            acquire(WAKE_LOCK_TIMEOUT_MS) // renewed by the housekeeping thread while streaming
        }
        val wm = applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
        @Suppress("DEPRECATION")
        val mode = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            WifiManager.WIFI_MODE_FULL_LOW_LATENCY
        } else {
            WifiManager.WIFI_MODE_FULL_HIGH_PERF
        }
        wifiLock = wm.createWifiLock(mode, "PhoneMic:wifi").apply {
            setReferenceCounted(false)
            acquire()
        }
    }

    private fun releaseLocks() {
        wakeLock?.let { if (it.isHeld) it.release() }
        wakeLock = null
        wifiLock?.let { if (it.isHeld) it.release() }
        wifiLock = null
    }

    private fun createNotificationChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val channel = NotificationChannel(
                CHANNEL_ID,
                getString(R.string.notif_channel_name),
                NotificationManager.IMPORTANCE_LOW,
            ).apply { description = getString(R.string.notif_channel_desc) }
            getSystemService(NotificationManager::class.java).createNotificationChannel(channel)
        }
    }

    private fun buildNotification(cfg: StreamConfig, muted: Boolean): Notification {
        val flags = PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        val openIntent = PendingIntent.getActivity(
            this, 0,
            Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP),
            flags,
        )
        val stopIntent = PendingIntent.getService(
            this, 1, Intent(this, MicStreamService::class.java).setAction(ACTION_STOP), flags,
        )
        val muteIntent = PendingIntent.getService(
            this, 2, Intent(this, MicStreamService::class.java).setAction(ACTION_TOGGLE_MUTE), flags,
        )
        val text = getString(R.string.notif_text, cfg.targetLabel) +
            if (muted) getString(R.string.status_muted_suffix) else ""
        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_mic)
            .setContentTitle(getString(R.string.notif_title))
            .setContentText(text)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setSilent(true)
            .setContentIntent(openIntent)
            .setCategory(NotificationCompat.CATEGORY_SERVICE)
            .setVisibility(NotificationCompat.VISIBILITY_PUBLIC)
            .addAction(R.drawable.ic_mic, getString(if (muted) R.string.notif_unmute else R.string.notif_mute), muteIntent)
            .addAction(R.drawable.ic_stop, getString(R.string.notif_stop), stopIntent)
            .build()
    }

    companion object {
        private const val TAG = "PhoneMic"
        const val ACTION_START = "com.phonemic.app.action.START"
        const val ACTION_STOP = "com.phonemic.app.action.STOP"
        const val ACTION_TOGGLE_MUTE = "com.phonemic.app.action.TOGGLE_MUTE"

        private const val CHANNEL_ID = "phonemic_stream"
        private const val NOTIFICATION_ID = 1001
        private const val QUEUE_CAPACITY = 64 // ~0.3-1.3 s of audio depending on frame size
        private const val PC_TIMEOUT_MS = 3000L
        private const val INITIAL_BACKOFF_MS = 500L
        private const val MAX_BACKOFF_MS = 5000L
        private const val STABLE_LINK_MS = 2000L
        private const val WAKE_LOCK_TIMEOUT_MS = 10 * 60 * 1000L

        fun start(context: Context, cfg: StreamConfig) {
            val intent = cfg.writeTo(Intent(context, MicStreamService::class.java).setAction(ACTION_START))
            ContextCompat.startForegroundService(context, intent)
        }

        fun stop(context: Context) {
            context.startService(Intent(context, MicStreamService::class.java).setAction(ACTION_STOP))
        }

        fun toggleMute(context: Context) {
            context.startService(Intent(context, MicStreamService::class.java).setAction(ACTION_TOGGLE_MUTE))
        }
    }
}
