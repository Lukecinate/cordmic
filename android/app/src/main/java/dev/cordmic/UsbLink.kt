package dev.cordmic

import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.os.Build
import android.provider.Settings

/**
 * 0.7.2: what the phone's USB port is doing, from the sticky system broadcast
 * `android.hardware.usb.action.USB_STATE` (read-only; CordMic never changes USB functions and holds no USB permission).
 */
enum class UsbLink {
    /** No broadcast seen (unknown). */
    UNKNOWN,
    /** No USB cable to a computer. */
    NO_CABLE,
    /** Cable connected, but the phone is set to "Charging phone only" (no tethering function). */
    CHARGING_ONLY,
    /** Tethering function selected; the network interface is not up yet. */
    TETHER_STARTING,
    /** Tethering interface is up. */
    TETHER_ON;

    companion object {
        const val ACTION_USB_STATE = "android.hardware.usb.action.USB_STATE"

        /**
         * [connected]: extra "connected"; [rndis]/[ncm]: function extras; [ifaceUp]: [TetherNet.find] found an address.
         * A missing broadcast ([connected] null) is UNKNOWN unless the interface is up.
         */
        fun classify(connected: Boolean?, rndis: Boolean, ncm: Boolean, ifaceUp: Boolean): UsbLink = when {
            ifaceUp -> TETHER_ON
            connected == null -> UNKNOWN
            !connected -> NO_CABLE
            rndis || ncm -> TETHER_STARTING
            else -> CHARGING_ONLY
        }

        fun fromIntent(i: Intent?, ifaceUp: Boolean): UsbLink {
            if (i == null) return classify(null, false, false, ifaceUp)
            return classify(
                if (i.hasExtra("connected")) i.getBooleanExtra("connected", false) else null,
                i.getBooleanExtra("rndis", false),
                i.getBooleanExtra("ncm", false),
                ifaceUp,
            )
        }

        /** Last USB_STATE broadcast (sticky). No receiver is kept. */
        fun read(context: Context, ifaceUp: Boolean): UsbLink {
            val sticky = try {
                val filter = IntentFilter(ACTION_USB_STATE)
                if (Build.VERSION.SDK_INT >= 33) context.registerReceiver(null, filter, Context.RECEIVER_NOT_EXPORTED)
                else context.registerReceiver(null, filter)
            } catch (_: Exception) { null }
            return fromIntent(sticky, ifaceUp)
        }

        /**
         * Screens that may hold the USB tethering switch, most specific first: the AOSP/One UI tethering activity,
         * the older TetherSettings class, wireless settings, then Settings itself.
         */
        fun tetherSettingsIntents(): List<Intent> = listOf(
            Intent().setComponent(ComponentName("com.android.settings", "com.android.settings.Settings\$TetherSettingsActivity")),
            Intent().setComponent(ComponentName("com.android.settings", "com.android.settings.TetherSettings")),
            Intent(Settings.ACTION_WIRELESS_SETTINGS),
            Intent(Settings.ACTION_SETTINGS),
        )
    }
}
