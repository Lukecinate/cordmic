package dev.cordmic.usb

import android.app.Activity
import android.content.Intent
import android.hardware.usb.UsbManager
import android.os.Bundle
import dev.cordmic.MicService

/**
 * Receives ACTION_USB_ACCESSORY_ATTACHED. Does not change USB functions.
 * The system may show "Open CordMic for this accessory?".
 */
class AccessoryActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val acc = intent?.getParcelableExtra<android.hardware.usb.UsbAccessory>(UsbManager.EXTRA_ACCESSORY)
        if (acc != null) {
            val i = Intent(this, MicService::class.java)
            i.action = MicService.ACTION_ACCESSORY
            i.putExtra(UsbManager.EXTRA_ACCESSORY, acc)
            startForegroundService(i)
        }
        finish()
    }
}
