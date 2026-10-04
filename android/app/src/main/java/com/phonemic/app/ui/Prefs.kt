package com.phonemic.app.ui

import android.content.Context
import android.media.MediaRecorder
import androidx.core.content.edit
import com.phonemic.app.protocol.Pmic
import com.phonemic.app.service.StreamConfig
import com.phonemic.app.service.TransportMode

/** Remembers the last-used connection and audio settings. */
class Prefs(context: Context) {

    private val sp = context.getSharedPreferences("phonemic", Context.MODE_PRIVATE)

    var host: String
        get() = sp.getString(K_HOST, "") ?: ""
        set(value) = sp.edit { putString(K_HOST, value) }

    val port: Int get() = sp.getInt(K_PORT, Pmic.DEFAULT_PORT)

    val transport: TransportMode
        get() = try {
            TransportMode.valueOf(sp.getString(K_TRANSPORT, TransportMode.WIFI_UDP.name) ?: TransportMode.WIFI_UDP.name)
        } catch (e: IllegalArgumentException) {
            TransportMode.WIFI_UDP
        }

    val sampleRate: Int get() = sp.getInt(K_RATE, 48000)
    val channels: Int get() = sp.getInt(K_CHANNELS, 1)
    val audioSource: Int get() = sp.getInt(K_SOURCE, MediaRecorder.AudioSource.MIC)
    val noiseSuppression: Boolean get() = sp.getBoolean(K_NS, true)
    val autoGain: Boolean get() = sp.getBoolean(K_AGC, false)

    fun save(cfg: StreamConfig) = sp.edit {
        putString(K_HOST, cfg.host)
        putInt(K_PORT, cfg.port)
        putString(K_TRANSPORT, cfg.transport.name)
        putInt(K_RATE, cfg.sampleRate)
        putInt(K_CHANNELS, cfg.channels)
        putInt(K_SOURCE, cfg.audioSource)
        putBoolean(K_NS, cfg.noiseSuppression)
        putBoolean(K_AGC, cfg.autoGain)
    }

    private companion object {
        const val K_HOST = "host"
        const val K_PORT = "port"
        const val K_TRANSPORT = "transport"
        const val K_RATE = "rate"
        const val K_CHANNELS = "channels"
        const val K_SOURCE = "source"
        const val K_NS = "ns"
        const val K_AGC = "agc"
    }
}
