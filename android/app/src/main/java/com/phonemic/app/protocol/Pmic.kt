package com.phonemic.app.protocol

/**
 * Wire format shared with the PC receiver (see PROTOCOL.md at the repository root).
 *
 * Header, 16 bytes, multi-byte integers big-endian:
 *   0..1   magic 'P' 'M'
 *   2      version (1)
 *   3      type
 *   4..7   seq (uint32) - increments per AUDIO packet, 0 for control packets
 *   8..11  sampleRate (uint32, Hz)
 *   12     channels
 *   13     bitsPerSample (16)
 *   14..15 payloadLen (uint16)
 * Payload: AUDIO -> interleaved little-endian signed 16-bit PCM; control types -> UTF-8 text.
 *
 * Over UDP each datagram carries exactly one packet. Over TCP packets are concatenated and
 * payloadLen delimits them.
 */
object Pmic {
    const val MAGIC0: Byte = 0x50
    const val MAGIC1: Byte = 0x4D
    const val VERSION: Byte = 1
    const val HEADER_SIZE = 16
    const val DEFAULT_PORT = 48111
    const val MAX_PAYLOAD = 1400
    const val MAX_PACKET = HEADER_SIZE + MAX_PAYLOAD

    /** Keep audio payloads at or below this so one packet fits a 1500-byte MTU without fragmentation. */
    const val TARGET_AUDIO_PAYLOAD = 1200

    const val TYPE_AUDIO: Byte = 1
    const val TYPE_DISCOVER: Byte = 2
    const val TYPE_ANNOUNCE: Byte = 3
    const val TYPE_PING: Byte = 4
    const val TYPE_PONG: Byte = 5
    const val TYPE_BYE: Byte = 6

    fun writeHeader(
        dest: ByteArray,
        offset: Int,
        type: Byte,
        seq: Long,
        sampleRate: Int,
        channels: Int,
        bitsPerSample: Int,
        payloadLen: Int,
    ) {
        require(payloadLen in 0..0xFFFF) { "payload too large: $payloadLen" }
        dest[offset] = MAGIC0
        dest[offset + 1] = MAGIC1
        dest[offset + 2] = VERSION
        dest[offset + 3] = type
        putU32(dest, offset + 4, seq)
        putU32(dest, offset + 8, sampleRate.toLong())
        dest[offset + 12] = channels.toByte()
        dest[offset + 13] = bitsPerSample.toByte()
        dest[offset + 14] = (payloadLen ushr 8).toByte()
        dest[offset + 15] = payloadLen.toByte()
    }

    /** Builds a complete control packet (DISCOVER, PING, BYE, ...) with an optional UTF-8 text payload. */
    fun control(type: Byte, text: String = ""): ByteArray {
        val payload = text.toByteArray(Charsets.UTF_8)
        val out = ByteArray(HEADER_SIZE + payload.size)
        writeHeader(out, 0, type, 0L, 0, 0, 0, payload.size)
        System.arraycopy(payload, 0, out, HEADER_SIZE, payload.size)
        return out
    }

    /** Returns null when the buffer does not hold a complete, valid packet. */
    fun decode(buf: ByteArray, offset: Int, length: Int): PmicPacket? {
        if (length < HEADER_SIZE) return null
        val payloadLen = payloadLength(buf, offset)
        if (payloadLen < 0 || length < HEADER_SIZE + payloadLen) return null
        val payload = buf.copyOfRange(offset + HEADER_SIZE, offset + HEADER_SIZE + payloadLen)
        return PmicPacket(
            type = buf[offset + 3],
            seq = getU32(buf, offset + 4),
            sampleRate = getU32(buf, offset + 8).toInt(),
            channels = buf[offset + 12].toInt() and 0xFF,
            bitsPerSample = buf[offset + 13].toInt() and 0xFF,
            payload = payload,
        )
    }

    /** Payload length declared by a header, or -1 if the magic/version is wrong. Used by the TCP framer. */
    fun payloadLength(header: ByteArray, offset: Int = 0): Int {
        if (header.size < offset + HEADER_SIZE) return -1
        if (header[offset] != MAGIC0 || header[offset + 1] != MAGIC1 || header[offset + 2] != VERSION) return -1
        return ((header[offset + 14].toInt() and 0xFF) shl 8) or (header[offset + 15].toInt() and 0xFF)
    }

    private fun putU32(dest: ByteArray, off: Int, v: Long) {
        dest[off] = (v ushr 24).toByte()
        dest[off + 1] = (v ushr 16).toByte()
        dest[off + 2] = (v ushr 8).toByte()
        dest[off + 3] = v.toByte()
    }

    private fun getU32(buf: ByteArray, off: Int): Long =
        ((buf[off].toLong() and 0xFF) shl 24) or
            ((buf[off + 1].toLong() and 0xFF) shl 16) or
            ((buf[off + 2].toLong() and 0xFF) shl 8) or
            (buf[off + 3].toLong() and 0xFF)
}

class PmicPacket(
    val type: Byte,
    val seq: Long,
    val sampleRate: Int,
    val channels: Int,
    val bitsPerSample: Int,
    val payload: ByteArray,
) {
    val text: String get() = String(payload, Charsets.UTF_8)

    fun encode(): ByteArray {
        val out = ByteArray(Pmic.HEADER_SIZE + payload.size)
        Pmic.writeHeader(out, 0, type, seq, sampleRate, channels, bitsPerSample, payload.size)
        System.arraycopy(payload, 0, out, Pmic.HEADER_SIZE, payload.size)
        return out
    }
}
