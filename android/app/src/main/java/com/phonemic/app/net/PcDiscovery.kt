package com.phonemic.app.net

import android.os.Build
import com.phonemic.app.protocol.Pmic
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.IOException
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.SocketTimeoutException

data class DiscoveredPc(val address: String, val name: String)

/**
 * Finds PC receivers on the LAN: broadcast DISCOVER a few times, collect ANNOUNCE replies
 * (payload = PC host name) until the timeout elapses.
 */
object PcDiscovery {

    suspend fun discover(port: Int, timeoutMs: Long = 2500L): List<DiscoveredPc> = withContext(Dispatchers.IO) {
        val found = LinkedHashMap<String, DiscoveredPc>()
        val probe = Pmic.control(Pmic.TYPE_DISCOVER, Build.MODEL ?: "Android phone")
        DatagramSocket().use { socket ->
            socket.broadcast = true
            socket.soTimeout = 250
            val targets = NetUtils.broadcastAddresses()
            val deadline = System.currentTimeMillis() + timeoutMs
            var nextProbeAt = 0L
            val buf = ByteArray(Pmic.MAX_PACKET)
            val dp = DatagramPacket(buf, buf.size)
            while (System.currentTimeMillis() < deadline) {
                val now = System.currentTimeMillis()
                if (now >= nextProbeAt) {
                    for (t in targets) {
                        try {
                            socket.send(DatagramPacket(probe, probe.size, t, port))
                        } catch (e: IOException) {
                            // some interfaces refuse broadcast; try the others
                        }
                    }
                    nextProbeAt = now + 700
                }
                try {
                    dp.setData(buf, 0, buf.size)
                    socket.receive(dp)
                    val pkt = Pmic.decode(dp.data, dp.offset, dp.length)
                    if (pkt != null && pkt.type == Pmic.TYPE_ANNOUNCE) {
                        val addr = dp.address?.hostAddress ?: continue
                        if (!found.containsKey(addr)) {
                            found[addr] = DiscoveredPc(addr, pkt.text.ifBlank { addr })
                        }
                    }
                } catch (e: SocketTimeoutException) {
                    // keep waiting until the deadline
                }
            }
        }
        found.values.toList()
    }
}
