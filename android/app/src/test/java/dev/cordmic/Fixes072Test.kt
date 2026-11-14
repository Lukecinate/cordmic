package dev.cordmic

import android.content.Intent
import android.provider.Settings
import androidx.test.core.app.ApplicationProvider
import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

/** 0.7.2: USB state (charging only vs no cable), tethering screen fallback chain, guidance text, mobile data. */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [34])
class Fixes072Test {
    @Test fun classifyDistinguishesChargingOnlyFromNoCable() {
        assertEquals(UsbLink.CHARGING_ONLY, UsbLink.classify(connected = true, rndis = false, ncm = false, ifaceUp = false))
        assertEquals(UsbLink.NO_CABLE, UsbLink.classify(connected = false, rndis = false, ncm = false, ifaceUp = false))
        assertEquals(UsbLink.TETHER_STARTING, UsbLink.classify(connected = true, rndis = true, ncm = false, ifaceUp = false))
        assertEquals(UsbLink.TETHER_STARTING, UsbLink.classify(connected = true, rndis = false, ncm = true, ifaceUp = false))
        assertEquals(UsbLink.TETHER_ON, UsbLink.classify(connected = true, rndis = true, ncm = false, ifaceUp = true))
        assertEquals(UsbLink.TETHER_ON, UsbLink.classify(connected = null, rndis = false, ncm = false, ifaceUp = true))
        assertEquals(UsbLink.UNKNOWN, UsbLink.classify(connected = null, rndis = false, ncm = false, ifaceUp = false))
    }

    @Test fun fromIntentReadsTheUsbStateExtras() {
        val charging = Intent(UsbLink.ACTION_USB_STATE).putExtra("connected", true).putExtra("configured", true).putExtra("mtp", false)
        assertEquals(UsbLink.CHARGING_ONLY, UsbLink.fromIntent(charging, ifaceUp = false))
        val tether = Intent(UsbLink.ACTION_USB_STATE).putExtra("connected", true).putExtra("rndis", true)
        assertEquals(UsbLink.TETHER_STARTING, UsbLink.fromIntent(tether, ifaceUp = false))
        assertEquals(UsbLink.NO_CABLE, UsbLink.fromIntent(Intent(UsbLink.ACTION_USB_STATE).putExtra("connected", false), ifaceUp = false))
        assertEquals(UsbLink.UNKNOWN, UsbLink.fromIntent(null, ifaceUp = false))
        assertEquals(UsbLink.UNKNOWN, UsbLink.fromIntent(Intent(UsbLink.ACTION_USB_STATE), ifaceUp = false))
    }

    @Test fun readNeverThrowsWithoutABroadcast() {
        val ctx = ApplicationProvider.getApplicationContext<android.content.Context>()
        assertEquals(UsbLink.UNKNOWN, UsbLink.read(ctx, ifaceUp = false))
        assertEquals(UsbLink.TETHER_ON, UsbLink.read(ctx, ifaceUp = true))
    }

    @Test fun tetherScreensAreTriedMostSpecificFirst() {
        val l = UsbLink.tetherSettingsIntents()
        assertEquals("com.android.settings.Settings\$TetherSettingsActivity", l[0].component?.className)
        assertEquals("com.android.settings.TetherSettings", l[1].component?.className)
        assertEquals(Settings.ACTION_WIRELESS_SETTINGS, l[2].action)
        assertEquals(Settings.ACTION_SETTINGS, l.last().action)
    }

    @Test fun messagesExplainWhyTetheringIsNotUp() {
        fun msg(phase: Phase, link: UsbLink) = UiCopy.message(UiState(phase, link = link))
        assertEquals(R.string.state_msg_charging_only, msg(Phase.IDLE, UsbLink.CHARGING_ONLY))
        assertEquals(R.string.state_msg_no_cable, msg(Phase.WAITING_TETHER, UsbLink.NO_CABLE))
        assertEquals(R.string.state_msg_tether_starting, msg(Phase.WAITING_TETHER, UsbLink.TETHER_STARTING))
        assertEquals(R.string.state_idle_msg_tether_off, msg(Phase.IDLE, UsbLink.UNKNOWN))
        assertEquals(R.string.state_idle_msg_tether_on, UiCopy.message(UiState(Phase.IDLE, usb = "rndis0", link = UsbLink.TETHER_ON)))
        assertEquals(HintAction.OPEN_TETHERING, UiCopy.hint(UiState(Phase.IDLE, link = UsbLink.CHARGING_ONLY)))
    }

    @Test fun guidanceNamesTheExactSamsungPathInBothLanguages() {
        val en = File("src/main/res/values/strings.xml").readText()
        val id = File("src/main/res/values-in/strings.xml").readText()
        assertTrue(en.contains("Settings › Connections › Mobile Hotspot and Tethering › USB tethering"))
        assertTrue(en.contains("USB notification › USB tethering"))
        assertTrue(id.contains("Pengaturan › Koneksi › Hotspot Seluler dan Tethering › Tethering USB"))
        assertTrue(id.contains("notifikasi USB › Tethering USB"))
    }

    @Test fun automaticUpdateChecksSkipMeteredNetworks() {
        val src = File("src/main/java/dev/cordmic/MainActivity.kt").readText()
        val auto = src.substringAfter("private fun maybeAutoCheckUpdates()").substringBefore("private fun isMetered")
        assertTrue("auto check must stop on a metered network", auto.contains("if (isMetered()) return"))
        assertTrue(src.contains("isActiveNetworkMetered"))
    }

    @Test fun appNeverChangesUsbFunctions() {
        val manifest = File("src/main/AndroidManifest.xml").readText()
        assertTrue(!manifest.contains("MANAGE_USB") && !manifest.contains("TETHER_PRIVILEGED"))
        val code = File("src/main/java/dev/cordmic").listFiles()!!.filter { it.extension == "kt" }.joinToString("\n") { it.readText() }
        assertTrue(!code.contains("setCurrentFunctions") && !code.contains("startTethering"))
    }
}
