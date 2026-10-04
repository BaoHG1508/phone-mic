package com.phonemic.app.net

import android.content.Context
import android.net.ConnectivityManager
import java.net.Inet4Address
import java.net.InetAddress
import java.net.NetworkInterface

object NetUtils {

    /** IPv4 address of the active network (Wi-Fi/Ethernet), or of any up interface, or null. */
    fun localIpv4(context: Context): String? {
        try {
            val cm = context.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
            val lp = cm.activeNetwork?.let { cm.getLinkProperties(it) }
            lp?.linkAddresses
                ?.map { it.address }
                ?.firstOrNull { it is Inet4Address && !it.isLoopbackAddress }
                ?.let { return it.hostAddress }
        } catch (e: Exception) {
            // fall through to interface enumeration
        }
        return try {
            (NetworkInterface.getNetworkInterfaces()?.toList() ?: emptyList())
                .filter { it.isUp && !it.isLoopback }
                .flatMap { it.inetAddresses.toList() }
                .firstOrNull { it is Inet4Address && !it.isLoopbackAddress }
                ?.hostAddress
        } catch (e: Exception) {
            null
        }
    }

    /** Broadcast addresses of every up, non-loopback IPv4 interface plus the limited broadcast address. */
    fun broadcastAddresses(): List<InetAddress> {
        val out = LinkedHashSet<InetAddress>()
        try {
            for (nif in NetworkInterface.getNetworkInterfaces()?.toList() ?: emptyList()) {
                if (!nif.isUp || nif.isLoopback) continue
                for (ia in nif.interfaceAddresses) {
                    val b = ia.broadcast ?: continue
                    out.add(b)
                }
            }
        } catch (e: Exception) {
            // ignore, we still have the limited broadcast address
        }
        out.add(InetAddress.getByName("255.255.255.255"))
        return out.toList()
    }
}
