package com.phonemic.app.transport

import com.phonemic.app.protocol.Pmic
import com.phonemic.app.protocol.PmicPacket
import java.io.IOException
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.SocketTimeoutException

/**
 * Fire-and-forget UDP: lowest latency, no head-of-line blocking. A lost packet is simply a
 * short gap that the PC receiver fills with silence.
 */
class UdpTransport(private val host: String, private val port: Int) : Transport {

    override val description: String = "UDP $host:$port"
    override var onControlPacket: ((PmicPacket) -> Unit)? = null

    private var socket: DatagramSocket? = null
    private var target: InetAddress? = null

    @Volatile
    private var closed = false

    override val isConnected: Boolean
        get() = socket?.isClosed == false

    @Throws(IOException::class)
    override fun connect() {
        closeSocket()
        val address = InetAddress.getByName(host)
        val s = DatagramSocket()
        try {
            // DSCP "Expedited Forwarding": lets Wi-Fi QoS (WMM) prioritise the audio.
            s.trafficClass = 0xB8
        } catch (e: Exception) {
            // Not supported everywhere; purely an optimisation.
        }
        s.soTimeout = 500
        closed = false
        socket = s
        target = address
        Thread({ receiveLoop(s) }, "pmic-udp-rx").apply {
            isDaemon = true
            start()
        }
    }

    @Throws(IOException::class)
    override fun send(packet: ByteArray, length: Int) {
        val s = socket ?: throw IOException("socket not open")
        val t = target ?: throw IOException("no target address")
        s.send(DatagramPacket(packet, 0, length, t, port))
    }

    private fun receiveLoop(s: DatagramSocket) {
        val buf = ByteArray(Pmic.MAX_PACKET)
        val dp = DatagramPacket(buf, buf.size)
        while (!closed && !s.isClosed) {
            try {
                dp.setData(buf, 0, buf.size)
                s.receive(dp)
                val pkt = Pmic.decode(dp.data, dp.offset, dp.length) ?: continue
                if (pkt.type != Pmic.TYPE_AUDIO) onControlPacket?.invoke(pkt)
            } catch (e: SocketTimeoutException) {
                // Poll again; the timeout only exists so the thread can notice a close.
            } catch (e: IOException) {
                if (closed || s.isClosed) return
            }
        }
    }

    private fun closeSocket() {
        closed = true
        socket?.close()
        socket = null
        target = null
    }

    override fun close() = closeSocket()
}
