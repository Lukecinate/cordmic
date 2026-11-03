package dev.cordmic

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.widget.ArrayAdapter
import android.widget.Button
import android.widget.CheckBox
import android.widget.EditText
import android.widget.Spinner
import android.widget.TextView
import android.widget.Toast

class MainActivity : Activity() {
    private lateinit var ip: EditText
    private lateinit var port: EditText
    private lateinit var preset: Spinner
    private lateinit var aaudio: CheckBox
    private lateinit var status: TextView
    private val handler = Handler(Looper.getMainLooper())

    // label resource -> AAudio input preset / AudioRecord source (same numeric values)
    private val presets = listOf(
        R.string.preset_voice_recognition to 6,
        R.string.preset_unprocessed to 9,
        R.string.preset_voice_communication to 7,
    )

    private val refresh = object : Runnable {
        override fun run() {
            val s = MicService.current
            status.text = buildString {
                appendLine(getString(R.string.st_usb_iface, TetherNet.describe(getString(R.string.iface_none))))
                if (s == null) append(getString(R.string.st_stopped))
                else {
                    appendLine(getString(R.string.st_status, s.status))
                    appendLine(getString(R.string.st_engine, s.engineInfo))
                    append(getString(R.string.st_sent, s.sent, s.sendErrors))
                }
            }
            handler.postDelayed(this, 500)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)
        ip = findViewById(R.id.ip)
        port = findViewById(R.id.port)
        preset = findViewById(R.id.preset)
        aaudio = findViewById(R.id.aaudio)
        status = findViewById(R.id.status)
        preset.adapter = ArrayAdapter(this, android.R.layout.simple_spinner_dropdown_item, presets.map { getString(it.first) })

        val p = getPreferences(MODE_PRIVATE)
        ip.setText(p.getString("ip", ""))
        port.setText(p.getInt("port", Protocol.DEFAULT_PORT).toString())
        preset.setSelection(p.getInt("preset", 0))
        aaudio.isChecked = p.getBoolean("aaudio", true)

        findViewById<Button>(R.id.start).setOnClickListener { startClicked() }
        findViewById<Button>(R.id.stop).setOnClickListener { MicService.stop(this) }
    }

    override fun onResume() { super.onResume(); handler.post(refresh) }
    override fun onPause() { super.onPause(); handler.removeCallbacks(refresh) }

    private fun startClicked() {
        val needed = mutableListOf(Manifest.permission.RECORD_AUDIO)
        if (Build.VERSION.SDK_INT >= 33) needed += Manifest.permission.POST_NOTIFICATIONS
        val missing = needed.filter { checkSelfPermission(it) != PackageManager.PERMISSION_GRANTED }
        if (missing.isNotEmpty()) { requestPermissions(missing.toTypedArray(), REQ); return }
        doStart()
    }

    private fun doStart() {
        val host = ip.text.toString().trim()
        val portNum = port.text.toString().toIntOrNull()?.takeIf { it in 1..65535 }
        if (portNum == null) { Toast.makeText(this, R.string.err_port, Toast.LENGTH_SHORT).show(); return }
        if (host.isNotEmpty() && !Regex("^\\d{1,3}(\\.\\d{1,3}){3}$").matches(host)) {
            Toast.makeText(this, R.string.err_ip, Toast.LENGTH_SHORT).show(); return
        }
        getPreferences(MODE_PRIVATE).edit().putString("ip", host).putInt("port", portNum)
            .putInt("preset", preset.selectedItemPosition).putBoolean("aaudio", aaudio.isChecked).apply()
        val i = Intent(this, MicService::class.java)
            .putExtra(MicService.EXTRA_HOST, host)
            .putExtra(MicService.EXTRA_PORT, portNum)
            .putExtra(MicService.EXTRA_PRESET, presets[preset.selectedItemPosition].second)
            .putExtra(MicService.EXTRA_AAUDIO, aaudio.isChecked)
        startForegroundService(i)
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, results: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, results)
        if (requestCode != REQ) return
        val micOk = checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED
        if (micOk) doStart() else Toast.makeText(this, R.string.err_mic_permission, Toast.LENGTH_LONG).show()
    }

    companion object { private const val REQ = 1 }
}
