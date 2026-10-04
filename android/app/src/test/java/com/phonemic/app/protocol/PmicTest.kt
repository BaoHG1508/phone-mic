package com.phonemic.app.protocol

import com.phonemic.app.audio.AudioCapture
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class PmicTest {

    private fun ByteArray.hex() = joinToString("") { "%02x".format(it) }

    /** Same vector as pc-receiver/tests/test_protocol.py - keeps the two implementations in lock-step. */
    @Test
    fun audioHeaderMatchesSharedVector() {
        val pcm = byteArrayOf(0x01, 0x00, 0xFF.toByte(), 0x7F) // samples 1 and 32767, little-endian
        val out = ByteArray(Pmic.HEADER_SIZE + pcm.size)
        Pmic.writeHeader(out, 0, Pmic.TYPE_AUDIO, 258L, 48000, 1, 16, pcm.size)
        System.arraycopy(pcm, 0, out, Pmic.HEADER_SIZE, pcm.size)
        assertEquals("504d0101" + "00000102" + "0000bb80" + "01" + "10" + "0004" + "0100ff7f", out.hex())
    }

    @Test
    fun decodeRoundTrip() {
        val pcm = ByteArray(960) { (it * 7).toByte() }
        val out = ByteArray(Pmic.HEADER_SIZE + pcm.size)
        Pmic.writeHeader(out, 0, Pmic.TYPE_AUDIO, 0xFFFFFFFFL, 44100, 2, 16, pcm.size)
        System.arraycopy(pcm, 0, out, Pmic.HEADER_SIZE, pcm.size)

        val pkt = Pmic.decode(out, 0, out.size)!!
        assertEquals(Pmic.TYPE_AUDIO, pkt.type)
        assertEquals(0xFFFFFFFFL, pkt.seq)
        assertEquals(44100, pkt.sampleRate)
        assertEquals(2, pkt.channels)
        assertEquals(16, pkt.bitsPerSample)
        assertArrayEquals(pcm, pkt.payload)
        assertArrayEquals(out, pkt.encode())
    }

    @Test
    fun controlPacketCarriesUtf8Text() {
        val bytes = Pmic.control(Pmic.TYPE_ANNOUNCE, "Gaming-PC ✓")
        val pkt = Pmic.decode(bytes, 0, bytes.size)!!
        assertEquals(Pmic.TYPE_ANNOUNCE, pkt.type)
        assertEquals(0L, pkt.seq)
        assertEquals("Gaming-PC ✓", pkt.text)
        assertEquals(bytes.size - Pmic.HEADER_SIZE, Pmic.payloadLength(bytes))
    }

    @Test
    fun rejectsGarbage() {
        assertNull(Pmic.decode(ByteArray(3), 0, 3))
        val bad = Pmic.control(Pmic.TYPE_PING)
        bad[0] = 'X'.code.toByte()
        assertNull(Pmic.decode(bad, 0, bad.size))
        assertEquals(-1, Pmic.payloadLength(bad))
        // Truncated payload
        val truncated = Pmic.control(Pmic.TYPE_ANNOUNCE, "hello")
        assertNull(Pmic.decode(truncated, 0, truncated.size - 2))
    }

    @Test
    fun decodeHonoursOffset() {
        val inner = Pmic.control(Pmic.TYPE_PONG, "x")
        val padded = ByteArray(5) + inner + ByteArray(3)
        val pkt = Pmic.decode(padded, 5, inner.size)!!
        assertEquals(Pmic.TYPE_PONG, pkt.type)
        assertEquals("x", pkt.text)
    }

    @Test
    fun audioFramesFitInOneMtu() {
        for (rate in intArrayOf(16000, 24000, 44100, 48000)) {
            for (ch in 1..2) {
                val ms = AudioCapture.chooseFrameMs(rate, ch)
                val bytes = rate * ms / 1000 * ch * 2
                assertTrue("$rate Hz x$ch -> $ms ms = $bytes bytes", bytes <= Pmic.TARGET_AUDIO_PAYLOAD)
                assertTrue(ms in listOf(5, 10, 20))
            }
        }
        assertEquals(10, AudioCapture.chooseFrameMs(48000, 1))
        assertEquals(5, AudioCapture.chooseFrameMs(48000, 2))
        assertEquals(20, AudioCapture.chooseFrameMs(16000, 1))
    }

    @Test
    fun levelMeter() {
        val silence = ByteArray(200)
        assertEquals(-96f, AudioCapture.levelDb(silence, 0, silence.size), 0.01f)

        // Full-scale square wave: alternating +32767 / -32768 -> ~0 dBFS
        val loud = ByteArray(400)
        for (i in 0 until 200) {
            val s = if (i % 2 == 0) 32767 else -32768
            loud[i * 2] = (s and 0xFF).toByte()
            loud[i * 2 + 1] = ((s shr 8) and 0xFF).toByte()
        }
        assertEquals(0f, AudioCapture.levelDb(loud, 0, loud.size), 0.01f)

        // Half scale -> about -6 dBFS
        val half = ByteArray(400)
        for (i in 0 until 200) {
            val s = if (i % 2 == 0) 16384 else -16384
            half[i * 2] = (s and 0xFF).toByte()
            half[i * 2 + 1] = ((s shr 8) and 0xFF).toByte()
        }
        assertEquals(-6.02f, AudioCapture.levelDb(half, 0, half.size), 0.05f)
    }
}
