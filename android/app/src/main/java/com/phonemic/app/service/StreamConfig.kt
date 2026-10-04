package com.phonemic.app.service

import android.content.Intent
import android.media.MediaRecorder
import com.phonemic.app.protocol.Pmic

enum class TransportMode { WIFI_UDP, WIFI_TCP, USB_TCP }

data class StreamConfig(
    val host: String,
    val port: Int = Pmic.DEFAULT_PORT,
    val transport: TransportMode = TransportMode.WIFI_UDP,
    val sampleRate: Int = 48000,
    val channels: Int = 1,
    val audioSource: Int = MediaRecorder.AudioSource.MIC,
    val noiseSuppression: Boolean = true,
    val autoGain: Boolean = false,
) {
    /** USB mode talks to localhost; `adb reverse` forwards it to the PC. */
    val effectiveHost: String
        get() = if (transport == TransportMode.USB_TCP) "127.0.0.1" else host

    val targetLabel: String
        get() = if (transport == TransportMode.USB_TCP) "PC over USB" else "$host:$port"

    fun writeTo(intent: Intent): Intent = intent.apply {
        putExtra(K_HOST, host)
        putExtra(K_PORT, port)
        putExtra(K_TRANSPORT, transport.name)
        putExtra(K_RATE, sampleRate)
        putExtra(K_CHANNELS, channels)
        putExtra(K_SOURCE, audioSource)
        putExtra(K_NS, noiseSuppression)
        putExtra(K_AGC, autoGain)
    }

    companion object {
        private const val K_HOST = "host"
        private const val K_PORT = "port"
        private const val K_TRANSPORT = "transport"
        private const val K_RATE = "rate"
        private const val K_CHANNELS = "channels"
        private const val K_SOURCE = "source"
        private const val K_NS = "ns"
        private const val K_AGC = "agc"

        fun readFrom(intent: Intent): StreamConfig? {
            val host = intent.getStringExtra(K_HOST) ?: return null
            val transport = try {
                TransportMode.valueOf(intent.getStringExtra(K_TRANSPORT) ?: return null)
            } catch (e: IllegalArgumentException) {
                return null
            }
            return StreamConfig(
                host = host,
                port = intent.getIntExtra(K_PORT, Pmic.DEFAULT_PORT),
                transport = transport,
                sampleRate = intent.getIntExtra(K_RATE, 48000),
                channels = intent.getIntExtra(K_CHANNELS, 1),
                audioSource = intent.getIntExtra(K_SOURCE, MediaRecorder.AudioSource.MIC),
                noiseSuppression = intent.getBooleanExtra(K_NS, true),
                autoGain = intent.getBooleanExtra(K_AGC, false),
            )
        }
    }
}
