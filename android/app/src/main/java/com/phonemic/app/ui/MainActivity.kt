package com.phonemic.app.ui

import android.Manifest
import android.annotation.SuppressLint
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.pm.PackageManager
import android.media.MediaRecorder
import android.os.Build
import android.os.Bundle
import android.widget.ArrayAdapter
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import androidx.core.view.isVisible
import androidx.core.widget.doAfterTextChanged
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.lifecycleScope
import androidx.lifecycle.repeatOnLifecycle
import com.google.android.material.dialog.MaterialAlertDialogBuilder
import com.phonemic.app.R
import com.phonemic.app.databinding.ActivityMainBinding
import com.phonemic.app.net.NetUtils
import com.phonemic.app.net.PcDiscovery
import com.phonemic.app.protocol.Pmic
import com.phonemic.app.service.MicStreamService
import com.phonemic.app.service.Status
import com.phonemic.app.service.StreamConfig
import com.phonemic.app.service.StreamState
import com.phonemic.app.service.StreamStateHolder
import com.phonemic.app.service.TransportMode
import com.phonemic.app.service.isActive
import kotlinx.coroutines.launch

class MainActivity : AppCompatActivity() {

    private lateinit var b: ActivityMainBinding
    private lateinit var prefs: Prefs
    private var startAfterPermission = false

    private val sampleRates = intArrayOf(16000, 24000, 44100, 48000)
    private val sampleRateLabels = arrayOf("16 kHz (voice, lowest bandwidth)", "24 kHz", "44.1 kHz", "48 kHz (best quality)")

    private val sources = intArrayOf(
        MediaRecorder.AudioSource.MIC,
        MediaRecorder.AudioSource.VOICE_COMMUNICATION,
        MediaRecorder.AudioSource.VOICE_RECOGNITION,
        MediaRecorder.AudioSource.UNPROCESSED,
        MediaRecorder.AudioSource.CAMCORDER,
    )
    private val sourceLabels = arrayOf(
        "Default microphone",
        "Voice call (echo + noise processed by the phone)",
        "Voice recognition (flat, light processing)",
        "Unprocessed (raw, no processing)",
        "Camcorder (far-field / room)",
    )

    private val permissionLauncher =
        registerForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) {
            val micGranted = ContextCompat.checkSelfPermission(this, Manifest.permission.RECORD_AUDIO) ==
                PackageManager.PERMISSION_GRANTED
            if (micGranted) {
                if (startAfterPermission) startStreaming()
            } else {
                toast(R.string.error_permission)
            }
            startAfterPermission = false
        }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        b = ActivityMainBinding.inflate(layoutInflater)
        setContentView(b.root)
        prefs = Prefs(this)
        setupControls()
        observeState()
    }

    override fun onResume() {
        super.onResume()
        refreshPhoneIp()
    }

    // ---------------------------------------------------------------- setup

    @SuppressLint("SetTextI18n") // a port number must never get locale grouping separators
    private fun setupControls() {
        b.etHost.setText(prefs.host)
        b.etPort.setText(prefs.port.toString())

        b.spSampleRate.adapter = ArrayAdapter(this, android.R.layout.simple_spinner_dropdown_item, sampleRateLabels)
        b.spSampleRate.setSelection(sampleRates.indexOf(prefs.sampleRate).coerceAtLeast(0))

        b.spChannels.adapter = ArrayAdapter(
            this, android.R.layout.simple_spinner_dropdown_item,
            listOf(getString(R.string.mono), getString(R.string.stereo)),
        )
        b.spChannels.setSelection(if (prefs.channels == 2) 1 else 0)

        b.spSource.adapter = ArrayAdapter(this, android.R.layout.simple_spinner_dropdown_item, sourceLabels)
        b.spSource.setSelection(sources.indexOf(prefs.audioSource).coerceAtLeast(0))

        b.swNoise.isChecked = prefs.noiseSuppression
        b.swAgc.isChecked = prefs.autoGain

        when (prefs.transport) {
            TransportMode.WIFI_UDP -> b.rbUdp.isChecked = true
            TransportMode.WIFI_TCP -> b.rbTcp.isChecked = true
            TransportMode.USB_TCP -> b.rbUsb.isChecked = true
        }
        b.rgTransport.setOnCheckedChangeListener { _, _ -> updateTransportUi() }
        b.etPort.doAfterTextChanged { updateTransportUi() }
        updateTransportUi()

        b.btnFindPc.setOnClickListener { findPc() }
        b.btnCopyCmd.setOnClickListener { copyToClipboard(b.tvUsbCommand.text.toString()) }
        b.btnStartStop.setOnClickListener {
            if (StreamStateHolder.state.value.status.isActive) {
                MicStreamService.stop(this)
            } else {
                startStreamingWithPermission()
            }
        }
        b.btnMute.setOnClickListener { MicStreamService.toggleMute(this) }
    }

    private fun updateTransportUi() {
        val usb = b.rbUsb.isChecked
        b.layoutUsbHelp.isVisible = usb
        b.tilHost.isEnabled = !usb
        b.btnFindPc.isEnabled = !usb
        val port = b.etPort.text?.toString()?.trim()?.toIntOrNull() ?: Pmic.DEFAULT_PORT
        b.tvUsbCommand.text = getString(R.string.usb_command, port)
    }

    private fun refreshPhoneIp() {
        val ip = NetUtils.localIpv4(this)
        b.tvPhoneIp.text = if (ip != null) getString(R.string.label_phone_ip, ip) else getString(R.string.label_phone_ip_unknown)
    }

    // ---------------------------------------------------------------- actions

    private fun currentConfig(): StreamConfig? {
        val port = b.etPort.text?.toString()?.trim()?.toIntOrNull()
        if (port == null || port !in 1..65535) {
            toast(R.string.error_invalid_port)
            return null
        }
        val transport = when {
            b.rbUsb.isChecked -> TransportMode.USB_TCP
            b.rbTcp.isChecked -> TransportMode.WIFI_TCP
            else -> TransportMode.WIFI_UDP
        }
        val host = b.etHost.text?.toString()?.trim() ?: ""
        if (transport != TransportMode.USB_TCP && host.isEmpty()) {
            toast(R.string.error_invalid_address)
            return null
        }
        val cfg = StreamConfig(
            host = host,
            port = port,
            transport = transport,
            sampleRate = sampleRates[b.spSampleRate.selectedItemPosition.coerceIn(0, sampleRates.size - 1)],
            channels = if (b.spChannels.selectedItemPosition == 1) 2 else 1,
            audioSource = sources[b.spSource.selectedItemPosition.coerceIn(0, sources.size - 1)],
            noiseSuppression = b.swNoise.isChecked,
            autoGain = b.swAgc.isChecked,
        )
        prefs.save(cfg)
        return cfg
    }

    private fun startStreamingWithPermission() {
        if (currentConfig() == null) return
        val needed = mutableListOf<String>()
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            needed += Manifest.permission.RECORD_AUDIO
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            needed += Manifest.permission.POST_NOTIFICATIONS
        }
        if (needed.isEmpty()) {
            startStreaming()
        } else {
            startAfterPermission = true
            permissionLauncher.launch(needed.toTypedArray())
        }
    }

    private fun startStreaming() {
        val cfg = currentConfig() ?: return
        MicStreamService.start(this, cfg)
    }

    private fun findPc() {
        val port = b.etPort.text?.toString()?.trim()?.toIntOrNull() ?: Pmic.DEFAULT_PORT
        val progress = MaterialAlertDialogBuilder(this)
            .setTitle(R.string.discovery_title)
            .setMessage(R.string.discovery_searching)
            .setNegativeButton(R.string.discovery_cancel, null)
            .show()
        val job = lifecycleScope.launch {
            val found = PcDiscovery.discover(port)
            if (!progress.isShowing) return@launch
            progress.setOnDismissListener(null)
            progress.dismiss()
            if (found.isEmpty()) {
                MaterialAlertDialogBuilder(this@MainActivity)
                    .setTitle(R.string.discovery_title)
                    .setMessage(R.string.discovery_none)
                    .setPositiveButton(android.R.string.ok, null)
                    .show()
                return@launch
            }
            val labels = found.map { "${it.name}\n${it.address}" }.toTypedArray()
            MaterialAlertDialogBuilder(this@MainActivity)
                .setTitle(R.string.discovery_title)
                .setItems(labels) { _, which ->
                    b.etHost.setText(found[which].address)
                    prefs.host = found[which].address
                }
                .setNegativeButton(R.string.discovery_cancel, null)
                .show()
        }
        progress.setOnDismissListener { job.cancel() }
    }

    private fun copyToClipboard(text: String) {
        val cm = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        cm.setPrimaryClip(ClipData.newPlainText("adb command", text))
        toast(R.string.copied)
    }

    // ---------------------------------------------------------------- state rendering

    private fun observeState() {
        lifecycleScope.launch {
            repeatOnLifecycle(Lifecycle.State.STARTED) {
                StreamStateHolder.state.collect { render(it) }
            }
        }
    }

    private fun render(s: StreamState) {
        val active = s.status.isActive
        b.btnStartStop.text = getString(if (active) R.string.btn_stop else R.string.btn_start)
        b.btnStartStop.setIconResource(if (active) R.drawable.ic_stop else R.drawable.ic_mic)
        b.btnMute.isEnabled = active
        b.btnMute.text = getString(if (s.muted) R.string.btn_unmute else R.string.btn_mute)
        setSettingsEnabled(!active)

        val target = s.config?.targetLabel ?: ""
        val mutedSuffix = if (s.muted) getString(R.string.status_muted_suffix) else ""
        val (text, color) = when (s.status) {
            Status.IDLE -> getString(R.string.status_idle) to R.color.pm_muted_text
            Status.STARTING -> getString(R.string.status_starting) to R.color.pm_primary
            Status.STREAMING -> {
                val res = if (s.pcReachable) R.string.status_streaming else R.string.status_streaming_no_pc
                (getString(res, target) + mutedSuffix) to (if (s.pcReachable) R.color.pm_secondary else R.color.pm_error)
            }
            Status.RECONNECTING -> getString(R.string.status_reconnecting, target) to R.color.pm_error
            Status.ERROR -> getString(R.string.status_error, s.error ?: "unknown") to R.color.pm_error
        }
        b.tvStatus.text = text
        b.tvStatus.setTextColor(ContextCompat.getColor(this, color))

        b.pbLevel.progress = if (active && !s.muted) ((s.levelDb + 60f) / 60f * 100f).toInt().coerceIn(0, 100) else 0

        val cfg = s.config
        b.tvStats.text = if (active && cfg != null) {
            getString(
                R.string.stats_format,
                cfg.sampleRate,
                if (cfg.channels == 2) getString(R.string.stereo) else getString(R.string.mono),
                s.kbps,
                s.packetsSent,
                transportLabel(cfg.transport),
            )
        } else {
            ""
        }
    }

    private fun setSettingsEnabled(enabled: Boolean) {
        b.tilHost.isEnabled = enabled && !b.rbUsb.isChecked
        b.tilPort.isEnabled = enabled
        b.btnFindPc.isEnabled = enabled && !b.rbUsb.isChecked
        b.rbUdp.isEnabled = enabled
        b.rbTcp.isEnabled = enabled
        b.rbUsb.isEnabled = enabled
        b.spSampleRate.isEnabled = enabled
        b.spChannels.isEnabled = enabled
        b.spSource.isEnabled = enabled
        b.swNoise.isEnabled = enabled
        b.swAgc.isEnabled = enabled
    }

    private fun transportLabel(mode: TransportMode): String = when (mode) {
        TransportMode.WIFI_UDP -> "UDP"
        TransportMode.WIFI_TCP -> "TCP"
        TransportMode.USB_TCP -> "USB"
    }

    private fun toast(resId: Int) = Toast.makeText(this, resId, Toast.LENGTH_LONG).show()
}
