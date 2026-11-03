package dev.cordmic

import java.net.Inet4Address
import java.net.InetAddress
import java.net.NetworkInterface

/**
 * Finds the USB-tethering interface. When USB tethering is on, the PHONE is the gateway/DHCP
 * server of a small subnet (e.g. 192.168.42.129/24 on rndis0/ncm0) and the PC gets an address
 * in it. The PC address is learned by broadcasting DISCOVER on this subnet.
 */
object TetherNet {
    data class Iface(val name: String, val local: InetAddress, val broadcast: InetAddress?, val prefix: Short)

    private val USB_NAMES = Regex("^(rndis|ncm|usb|eth)\\d+")
    private val EXCLUDE = Regex("^(wlan|swlan|ap|p2p|rmnet|ccmni|dummy|tun|ppp|lo|v4-|bt-|aware|ifb)")

    fun find(): Iface? {
        val cands = mutableListOf<Iface>()
        val all = try { NetworkInterface.getNetworkInterfaces()?.toList().orEmpty() } catch (e: Exception) { emptyList() }
        for (ni in all) {
            try {
                if (!ni.isUp || ni.isLoopback) continue
            } catch (e: Exception) { continue }
            val name = ni.name ?: continue
            if (EXCLUDE.containsMatchIn(name)) continue
            for (ia in ni.interfaceAddresses) {
                val a = ia.address
                if (a is Inet4Address && !a.isLoopbackAddress) cands += Iface(name, a, ia.broadcast, ia.networkPrefixLength)
            }
        }
        return cands.firstOrNull { USB_NAMES.containsMatchIn(it.name) }
    }

    fun describe(): String = find()?.let { "${it.name} ${it.local.hostAddress}/${it.prefix}" } ?: "tidak ada (USB tethering mati?)"
}
