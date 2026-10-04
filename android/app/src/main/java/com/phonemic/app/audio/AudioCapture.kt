package com.phonemic.app.audio

import android.annotation.SuppressLint
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.audiofx.AudioEffect
import android.media.audiofx.AutomaticGainControl
import android.media.audiofx.NoiseSuppressor
import android.os.Process
import android.os.SystemClock
import com.phonemic.app.protocol.Pmic
import com.phonemic.app.service.StreamConfig
import java.util.Arrays
import kotlin.math.log10
import kotlin.math.max
import kotlin.math.sqrt

/**
 * Pulls 16-bit PCM from the microphone on a dedicated high-priority thread and hands out
 * ready-to-send packets (header already written). Frame length is chosen so a packet fits in
 * one Ethernet/Wi-Fi MTU.
 */
class AudioCapture(
    private val config: StreamConfig,
    private val onPacket: (ByteArray) -> Unit,
    private val onLevel: (Float) -> Unit,
    private val onError: (String) -> Unit,
) {
    @Volatile
    var muted: Boolean = false

    @Volatile
    private var running = false
    private var thread: Thread? = null

    val frameMs: Int = chooseFrameMs(config.sampleRate, config.channels)
    val frameBytes: Int = config.sampleRate * frameMs / 1000 * config.channels * 2

    fun start() {
        running = true
        thread = Thread(::loop, "pmic-capture").apply { start() }
    }

    fun stop() {
        running = false
        thread?.join(1500)
        thread = null
    }

    @SuppressLint("MissingPermission") // The activity checks RECORD_AUDIO before starting the service.
    private fun loop() {
        Process.setThreadPriority(Process.THREAD_PRIORITY_URGENT_AUDIO)
        val channelMask = if (config.channels == 2) AudioFormat.CHANNEL_IN_STEREO else AudioFormat.CHANNEL_IN_MONO
        val minBuf = AudioRecord.getMinBufferSize(config.sampleRate, channelMask, AudioFormat.ENCODING_PCM_16BIT)
        if (minBuf <= 0) {
            onError("This phone cannot record ${config.sampleRate} Hz ${if (config.channels == 2) "stereo" else "mono"}")
            return
        }
        val bufferSize = max(minBuf, frameBytes * 8)
        val recorder = try {
            AudioRecord(config.audioSource, config.sampleRate, channelMask, AudioFormat.ENCODING_PCM_16BIT, bufferSize)
        } catch (e: Exception) {
            onError("Cannot open microphone: ${e.message ?: e.javaClass.simpleName}")
            return
        }
        if (recorder.state != AudioRecord.STATE_INITIALIZED) {
            recorder.release()
            onError("Microphone failed to initialise (try another source or sample rate)")
            return
        }
        val effects = attachEffects(recorder.audioSessionId)
        try {
            recorder.startRecording()
            if (recorder.recordingState != AudioRecord.RECORDSTATE_RECORDING) {
                onError("Microphone is busy (another app may be recording)")
                return
            }
            var seq = 0L
            var lastLevelAt = 0L
            while (running) {
                val packet = ByteArray(Pmic.HEADER_SIZE + frameBytes)
                var filled = 0
                while (filled < frameBytes && running) {
                    val n = recorder.read(packet, Pmic.HEADER_SIZE + filled, frameBytes - filled, AudioRecord.READ_BLOCKING)
                    if (n < 0) {
                        onError("Microphone read error ($n)")
                        return
                    }
                    filled += n
                }
                if (!running) break
                if (muted) Arrays.fill(packet, Pmic.HEADER_SIZE, packet.size, 0.toByte())
                val now = SystemClock.elapsedRealtime()
                if (now - lastLevelAt >= LEVEL_INTERVAL_MS) {
                    onLevel(levelDb(packet, Pmic.HEADER_SIZE, frameBytes))
                    lastLevelAt = now
                }
                Pmic.writeHeader(packet, 0, Pmic.TYPE_AUDIO, seq, config.sampleRate, config.channels, 16, frameBytes)
                seq = (seq + 1) and 0xFFFFFFFFL
                onPacket(packet)
            }
        } finally {
            try {
                recorder.stop()
            } catch (e: Exception) {
                // already stopped
            }
            effects.forEach { it.release() }
            recorder.release()
        }
    }

    private fun attachEffects(sessionId: Int): List<AudioEffect> {
        val list = ArrayList<AudioEffect>()
        if (config.noiseSuppression && NoiseSuppressor.isAvailable()) {
            NoiseSuppressor.create(sessionId)?.let {
                it.enabled = true
                list.add(it)
            }
        }
        if (config.autoGain && AutomaticGainControl.isAvailable()) {
            AutomaticGainControl.create(sessionId)?.let {
                it.enabled = true
                list.add(it)
            }
        }
        return list
    }

    companion object {
        private const val LEVEL_INTERVAL_MS = 50L

        /** Largest of 20/10/5 ms whose PCM payload stays under the MTU target. */
        fun chooseFrameMs(sampleRate: Int, channels: Int): Int {
            val bytesPerMs = sampleRate * channels * 2 / 1000.0
            return listOf(20, 10, 5).firstOrNull { it * bytesPerMs <= Pmic.TARGET_AUDIO_PAYLOAD } ?: 5
        }

        /** RMS level of little-endian 16-bit PCM in dBFS, clamped to [-96, 0]. */
        fun levelDb(buf: ByteArray, offset: Int, length: Int): Float {
            val n = length / 2
            if (n == 0) return -96f
            var sum = 0.0
            var i = offset
            val end = offset + n * 2
            while (i < end) {
                val s = ((buf[i + 1].toInt() shl 8) or (buf[i].toInt() and 0xFF)).toShort().toInt()
                sum += (s * s).toDouble()
                i += 2
            }
            val rms = sqrt(sum / n)
            if (rms < 1.0) return -96f
            return (20.0 * log10(rms / 32768.0)).toFloat().coerceIn(-96f, 0f)
        }
    }
}
