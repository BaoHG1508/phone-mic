package com.phonemic.app.transport

import android.util.Log
import com.phonemic.app.protocol.Pmic
import com.phonemic.app.protocol.PmicPacket
import java.io.BufferedInputStream
import java.io.DataInputStream
import java.io.IOException
import java.io.OutputStream
import java.net.InetSocketAddress
import java.net.Socket

/**
 * TCP stream of length-delimited packets. Used for USB (through `adb reverse`, which only
 * forwards TCP) and as a fallback on networks that drop UDP. TCP_NODELAY keeps latency low.
 */
class TcpTransport(private val host: String, private val port: Int) : Transport {

    override val description: String = "TCP $host:$port"
    override var onControlPacket: ((PmicPacket) -> Unit)? = null

    private var socket: Socket? = null
    private var output: OutputStream? = null

    @Volatile
    private var connected = false

    override val isConnected: Boolean
        get() = connected

    @Throws(IOException::class)
    override fun connect() {
        closeSocket()
        val s = Socket()
        s.tcpNoDelay = true
        s.connect(InetSocketAddress(host, port), CONNECT_TIMEOUT_MS)
        s.soTimeout = 0
        socket = s
        output = s.getOutputStream()
        connected = true
        Thread({ receiveLoop(s) }, "pmic-tcp-rx").apply {
            isDaemon = true
            start()
        }
    }

    @Throws(IOException::class)
    override fun send(packet: ByteArray, length: Int) {
        if (!connected) throw IOException("disconnected")
        val out = output ?: throw IOException("not connected")
        out.write(packet, 0, length)
        out.flush()
    }

    private fun receiveLoop(s: Socket) {
        try {
            val input = DataInputStream(BufferedInputStream(s.getInputStream()))
            while (connected && !s.isClosed) {
                val header = ByteArray(Pmic.HEADER_SIZE)
                input.readFully(header)
                val len = Pmic.payloadLength(header)
                if (len < 0) throw IOException("bad packet header from PC")
                val whole = header.copyOf(Pmic.HEADER_SIZE + len)
                input.readFully(whole, Pmic.HEADER_SIZE, len)
                val pkt = Pmic.decode(whole, 0, whole.size) ?: continue
                if (pkt.type != Pmic.TYPE_AUDIO) onControlPacket?.invoke(pkt)
            }
        } catch (e: IOException) {
            // EOF / reset: the sender thread sees isConnected == false and reconnects.
            if (socket === s) Log.w("PhoneMic", "TCP receive ended: ${e.message ?: e.javaClass.simpleName}")
        } finally {
            if (socket === s) connected = false
        }
    }

    private fun closeSocket() {
        connected = false
        try {
            socket?.close()
        } catch (e: IOException) {
            // ignore
        }
        socket = null
        output = null
    }

    override fun close() = closeSocket()

    private companion object {
        const val CONNECT_TIMEOUT_MS = 3000
    }
}
