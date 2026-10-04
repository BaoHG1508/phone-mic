package com.phonemic.app.transport

import com.phonemic.app.protocol.PmicPacket
import java.io.Closeable
import java.io.IOException

/**
 * A one-way audio pipe from the phone to the PC plus a back-channel for the PC's small control
 * replies (PONG / ANNOUNCE). Implementations are driven by a single sender thread.
 */
interface Transport : Closeable {
    val description: String

    /** Opens the socket / connects. Throws IOException on failure. May be called again after a failure. */
    @Throws(IOException::class)
    fun connect()

    val isConnected: Boolean

    /** Sends one complete packet (header + payload). Throws IOException when the link is broken. */
    @Throws(IOException::class)
    fun send(packet: ByteArray, length: Int = packet.size)

    /** Invoked from an internal thread whenever the PC sends a control packet back. */
    var onControlPacket: ((PmicPacket) -> Unit)?
}
