package dev.cordmic

import android.Manifest
import android.content.ComponentName
import android.content.Intent
import android.content.pm.PackageManager
import android.content.res.ColorStateList
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.provider.Settings
import android.view.LayoutInflater
import android.view.View
import android.widget.ArrayAdapter
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.Toast
import androidx.activity.enableEdgeToEdge
import androidx.annotation.VisibleForTesting
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import androidx.core.view.ViewCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.isVisible
import androidx.core.view.updatePadding
import com.google.android.material.appbar.MaterialToolbar
import com.google.android.material.button.MaterialButton
import com.google.android.material.materialswitch.MaterialSwitch
import com.google.android.material.slider.Slider
import com.google.android.material.shape.MaterialShapeDrawable
import com.google.android.material.shape.ShapeAppearanceModel
import com.google.android.material.textfield.MaterialAutoCompleteTextView
import com.google.android.material.textfield.TextInputEditText
import com.google.android.material.textfield.TextInputLayout
import java.text.NumberFormat
import java.util.Locale

class MainActivity : AppCompatActivity() {
    private lateinit var ip: TextInputEditText
    private lateinit var ipLayout: TextInputLayout
    private lateinit var port: TextInputEditText
    private lateinit var portLayout: TextInputLayout
    private lateinit var pairCode: TextInputEditText
    private lateinit var pairCodeLayout: TextInputLayout
    private var pairHintShown = false
    private lateinit var preset: MaterialAutoCompleteTextView
    private lateinit var presetLayout: TextInputLayout
    private lateinit var aaudio: MaterialSwitch
    private lateinit var frontFocus: MaterialSwitch
    private lateinit var frontFocusAr: MaterialSwitch
    private lateinit var autoOptimize: MaterialSwitch
    private lateinit var autoUpdate: MaterialSwitch
    private lateinit var detectedSummary: TextView
    private lateinit var speechLowWarning: TextView
    private lateinit var updateStatus: TextView
    private lateinit var ncMode: MaterialAutoCompleteTextView
    private lateinit var ncModeLayout: TextInputLayout
    private lateinit var voiceClarity: MaterialSwitch
    private lateinit var autoVolume: MaterialSwitch
    private lateinit var gain: Slider
    private lateinit var gainValue: TextView
    private var applyingRemote = false
    private lateinit var toggle: MaterialButton
    private lateinit var hintAction: MaterialButton
    private lateinit var stateIcon: ImageView
    private lateinit var stateTitle: TextView
    private lateinit var stateMessage: TextView
    private lateinit var level: LevelMeterView
    private lateinit var levelValue: TextView
    private lateinit var detailsBody: LinearLayout
    private val handler = Handler(Looper.getMainLooper())

    private var presetIndex = 0
    private var ncModeIndex = 1
    private var permissionDenied = false
    private var settingsOverride: Boolean? = null   // user's explicit expand/collapse, reset when running changes
    private var detailsExpanded = false
    private var lastRunning: Boolean? = null
    private var usbCache: String? = null
    private var usbCheckedAt = 0L
    private var installBusy = false
    private var updateBusy = false

    /** Screenshot tests render fixed states; the live loop then leaves the screen alone. */
    @VisibleForTesting var previewState: UiState? = null
        set(v) { field = v; v?.let { render(it, immediate = true) } }

    // label + help resource -> AAudio input preset / AudioRecord source (same numeric values)
    private class Preset(val label: Int, val help: Int, val source: Int)
    private val presets = listOf(
        Preset(R.string.preset_voice_recognition, R.string.preset_voice_recognition_help, 6),
        Preset(R.string.preset_unprocessed, R.string.preset_unprocessed_help, 9),
        Preset(R.string.preset_voice_communication, R.string.preset_voice_communication_help, 7),
    )

    private val refresh = object : Runnable {
        override fun run() {
            if (previewState == null) render(liveState(), immediate = false)
            showPairingHint()
            handler.postDelayed(this, 100)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        enableEdgeToEdge()
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)
        val toolbar = findViewById<MaterialToolbar>(R.id.toolbar)
        val scroll = findViewById<View>(R.id.scroll)
        ViewCompat.setOnApplyWindowInsetsListener(findViewById(R.id.root)) { _, insets ->
            val bars = insets.getInsets(WindowInsetsCompat.Type.systemBars() or WindowInsetsCompat.Type.displayCutout())
            val ime = insets.getInsets(WindowInsetsCompat.Type.ime())
            toolbar.updatePadding(left = bars.left, top = bars.top, right = bars.right)
            scroll.updatePadding(left = bars.left, right = bars.right, bottom = maxOf(bars.bottom, ime.bottom))
            WindowInsetsCompat.CONSUMED
        }

        ip = findViewById(R.id.ip); ipLayout = findViewById(R.id.ipLayout)
        port = findViewById(R.id.port); portLayout = findViewById(R.id.portLayout)
        pairCode = findViewById(R.id.pairCode); pairCodeLayout = findViewById(R.id.pairCodeLayout)
        preset = findViewById(R.id.preset); presetLayout = findViewById(R.id.presetLayout)
        aaudio = findViewById(R.id.aaudio)
        frontFocus = findViewById(R.id.frontFocus)
        frontFocusAr = findViewById(R.id.frontFocusAr)
        autoOptimize = findViewById(R.id.autoOptimize)
        autoUpdate = findViewById(R.id.autoUpdate)
        detectedSummary = findViewById(R.id.detectedSummary)
        speechLowWarning = findViewById(R.id.speechLowWarning)
        updateStatus = findViewById(R.id.updateStatus)
        ncMode = findViewById(R.id.ncMode); ncModeLayout = findViewById(R.id.ncModeLayout)
        voiceClarity = findViewById(R.id.voiceClarity)
        autoVolume = findViewById(R.id.autoVolume)
        gain = findViewById(R.id.gain)
        gainValue = findViewById(R.id.gainValue)
        toggle = findViewById(R.id.toggle)
        hintAction = findViewById(R.id.hintAction)
        stateIcon = findViewById(R.id.stateIcon)
        stateTitle = findViewById(R.id.stateTitle)
        stateMessage = findViewById(R.id.stateMessage)
        level = findViewById(R.id.level)
        levelValue = findViewById(R.id.levelValue)
        detailsBody = findViewById(R.id.detailsBody)

        DeviceProbe.ensureStarted(this)
        val p = getPreferences(MODE_PRIVATE)
        migrateUserLocks(p)
        ip.setText(p.getString("ip", ""))
        port.setText(p.getInt("port", Protocol.DEFAULT_PORT).toString())
        pairCode.setText(p.getString("pairCode", ""))
        presetIndex = p.getInt("preset", 0).coerceIn(0, presets.lastIndex)
        aaudio.isChecked = p.getBoolean("aaudio", true)
        frontFocus.isChecked = p.getBoolean("frontFocus", true)
        frontFocusAr.isChecked = p.getBoolean("frontFocusAr", false)
        autoOptimize.isChecked = p.getBoolean("autoOptimize", true)
        autoUpdate.isChecked = p.getBoolean("autoUpdate", false)
        ncModeIndex = when (p.getString("ncMode", null)) {
            "off" -> 0
            "strong" -> 2
            "light" -> 1
            else -> if (p.getBoolean("nc", true)) 1 else 0
        }
        voiceClarity.isChecked = p.getBoolean("clarity", true)
        autoVolume.isChecked = p.getBoolean("agc", true)
        gain.value = p.getInt("gain", VoiceChain.DEFAULT_GAIN_DB).coerceIn(0, VoiceChain.MAX_GAIN_DB).toFloat()
        showGain()

        preset.setAdapter(ArrayAdapter(this, R.layout.item_dropdown, presets.map { getString(it.label) }))
        preset.setOnItemClickListener { _, _, pos, _ ->
            presetIndex = pos
            markUserSet("userSetPreset")
            showPreset()
        }
        showPreset()
        val ncLabels = ncModes.map { getString(it.second) }
        ncMode.setAdapter(ArrayAdapter(this, R.layout.item_dropdown, ncLabels))
        ncMode.setOnItemClickListener { _, _, pos, _ ->
            ncModeIndex = pos
            markUserSet("userSetNc")
            persistAudio()
            MicService.current?.applyNoiseMode(ncModes[pos].first)
        }
        showNcMode()

        ip.doOnTextChange { ipLayout.error = null; updateSummary() }
        port.doOnTextChange { portLayout.error = null; updateSummary() }
        pairCode.doOnTextChange { pairCodeLayout.error = null; pairHintShown = false }
        findViewById<View>(R.id.aaudioRow).setOnClickListener { if (aaudio.isEnabled) aaudio.toggle() }
        findViewById<View>(R.id.frontFocusRow).setOnClickListener { frontFocus.toggle() }
        findViewById<View>(R.id.frontFocusArRow).setOnClickListener { frontFocusAr.toggle() }
        findViewById<View>(R.id.autoOptimizeRow).setOnClickListener { autoOptimize.toggle() }
        findViewById<View>(R.id.autoUpdateRow).setOnClickListener { autoUpdate.toggle() }
        findViewById<View>(R.id.clarityRow).setOnClickListener { voiceClarity.toggle() }
        findViewById<View>(R.id.autoVolRow).setOnClickListener { autoVolume.toggle() }
        aaudio.setOnCheckedChangeListener { _, _ ->
            if (applyingRemote) return@setOnCheckedChangeListener
            markUserSet("userSetAaudio")
            persistAudio()
        }
        frontFocus.setOnCheckedChangeListener { _, checked ->
            if (applyingRemote) return@setOnCheckedChangeListener
            markUserSet("userSetFrontFocus")
            if (!checked && frontFocusAr.isChecked) {
                applyingRemote = true
                frontFocusAr.isChecked = false
                applyingRemote = false
            }
            persistAudio()
            MicService.current?.applyFrontFocus(checked)
        }
        frontFocusAr.setOnCheckedChangeListener { _, checked ->
            if (applyingRemote) return@setOnCheckedChangeListener
            markUserSet("userSetFrontFocus")
            if (checked && !frontFocus.isChecked) {
                applyingRemote = true
                frontFocus.isChecked = true
                applyingRemote = false
            }
            persistAudio()
            MicService.current?.applyFrontFocusAr(checked)
        }
        autoUpdate.setOnCheckedChangeListener { _, checked ->
            if (applyingRemote) return@setOnCheckedChangeListener
            getPreferences(MODE_PRIVATE).edit().putBoolean("autoUpdate", checked).apply()
        }
        findViewById<View>(R.id.checkUpdate).setOnClickListener { checkForUpdates(interactive = true) }
        autoOptimize.setOnCheckedChangeListener { _, checked ->
            if (applyingRemote) return@setOnCheckedChangeListener
            persistAudio()
            if (checked) applyAdvice()
            showDetected()
        }
        findViewById<View>(R.id.redetect).setOnClickListener {
            DeviceProbe.refresh(this, tryOpen = true, reason = "re-detect")
            if (autoOptimize.isChecked) applyAdvice()
            showDetected()
        }
        voiceClarity.setOnCheckedChangeListener { _, checked ->
            if (applyingRemote) return@setOnCheckedChangeListener
            persistAudio()
            MicService.current?.applyClarity(checked)
        }
        autoVolume.setOnCheckedChangeListener { _, checked ->
            if (applyingRemote) return@setOnCheckedChangeListener
            persistAudio()
            MicService.current?.applyAgc(checked)
        }
        findViewById<View>(R.id.resetAudio).setOnClickListener {
            ncModeIndex = 1
            voiceClarity.isChecked = true
            autoVolume.isChecked = true
            frontFocus.isChecked = true
            frontFocusAr.isChecked = false
            gain.value = VoiceChain.DEFAULT_GAIN_DB.toFloat()
            showNcMode()
            clearUserSets()
            persistAudio()
            if (autoOptimize.isChecked) applyAdvice()
            MicService.current?.let {
                it.applyNoiseMode(NoiseMode.LIGHT)
                it.applyClarity(true)
                it.applyAgc(true)
                it.applyGain(gain.value.toInt())
                it.applyFrontFocus(frontFocus.isChecked)
                it.applyFrontFocusAr(frontFocusAr.isChecked)
            }
        }
        gain.addOnChangeListener { _, value, fromUser ->
            showGain()
            if (!fromUser || applyingRemote) return@addOnChangeListener
            val db = value.toInt()
            markUserSet("userSetGain")
            getPreferences(MODE_PRIVATE).edit().putInt("gain", db).apply()
            MicService.current?.applyGain(db)
        }

        toggle.setOnClickListener {
            if (currentState().running) MicService.stop(this) else startClicked()
        }
        hintAction.setOnClickListener {
            when (UiCopy.hint(currentState())) {
                HintAction.OPEN_TETHERING -> openTetheringSettings()
                HintAction.OPEN_APP_SETTINGS -> openAppSettings()
                HintAction.NONE -> Unit
            }
        }
        findViewById<View>(R.id.settingsHeader).setOnClickListener {
            settingsOverride = !isSettingsExpanded(currentState()); render(currentState(), immediate = true)
        }
        findViewById<View>(R.id.detailsHeader).setOnClickListener {
            detailsExpanded = !detailsExpanded; render(currentState(), immediate = true)
        }
        if (autoOptimize.isChecked) applyAdvice()
        showDetected()
        render(liveState(), immediate = true)
        UpdateInstaller.ensureRegistered(this)
        maybeAutoCheckUpdates()
    }

    override fun onResume() { super.onResume(); handler.post(refresh) }
    override fun onPause() { super.onPause(); handler.removeCallbacks(refresh) }
    override fun onDestroy() {
        handler.removeCallbacksAndMessages(null)
        super.onDestroy()
    }

    /** Posts [block] to the UI only while this Activity is alive (update worker threads outlive it). */
    private fun postIfAlive(block: () -> Unit) {
        handler.post { if (!isFinishing && !isDestroyed) block() }
    }

    /** The PC asks for a pairing code (0.6.1): open Settings once and point at the field. */
    private fun showPairingHint() {
        val needed = MicService.current?.pairingNeeded == true
        if (needed && !pairHintShown) {
            pairHintShown = true
            pairCodeLayout.error = getString(R.string.pairing_needed)
            settingsOverride = true
            render(currentState(), immediate = true)
        } else if (!needed && pairHintShown) {
            pairHintShown = false
            pairCodeLayout.error = null
        }
    }

    private fun currentState(): UiState = previewState ?: liveState()

    private fun usbNow(): String? {
        val now = SystemClock.elapsedRealtime()
        if (usbCheckedAt == 0L || now - usbCheckedAt > 1000) {
            usbCheckedAt = now
            usbCache = TetherNet.find()?.let { "${it.name} · ${it.local.hostAddress}/${it.prefix}" }
        }
        return usbCache
    }

    private fun liveState(): UiState {
        val s = MicService.current
        val usb = usbNow()
        if (s == null) return UiState(if (permissionDenied) Phase.PERMISSION_DENIED else Phase.IDLE, usb = usb)
        val phase = Phase.of(s.state)
        val heard = s.lastHeardAt
        return UiState(
            phase = phase,
            target = s.targetAddress?.let { "${it.address.hostAddress}:${it.port}" },
            engine = s.engineName.ifEmpty { null },
            levelDb = if (phase == Phase.STREAMING) Level.peakToDb(s.takePeak()) else null,
            sent = s.sent,
            sendErrors = s.sendErrors,
            lastReplyMs = if (heard > 0) SystemClock.elapsedRealtime() - heard else null,
            usb = usb,
            stats = s.engineStats,
            micSilenced = s.micSilenced,
        )
    }

    private fun isSettingsExpanded(ui: UiState) = settingsOverride ?: !ui.running

    @VisibleForTesting
    fun setSectionsExpanded(settings: Boolean?, details: Boolean) {
        settingsOverride = settings; detailsExpanded = details
        render(currentState(), immediate = true)
    }

    private fun render(ui: UiState, immediate: Boolean) {
        if (lastRunning != null && lastRunning != ui.running) settingsOverride = null
        lastRunning = ui.running

        // Status card
        stateTitle.setText(UiCopy.title(ui.phase))
        stateMessage.text = if (ui.phase == Phase.STREAMING && ui.micSilenced) getString(R.string.state_mic_silenced_msg)
        else if (ui.phase == Phase.STREAMING)
            getString(R.string.state_streaming_msg, ui.target ?: getString(R.string.value_none), ui.engine ?: getString(R.string.value_none))
        else getString(UiCopy.message(ui))
        stateIcon.setImageResource(UiCopy.icon(ui.phase))
        val (soft, strong) = toneColors(ui.phase.tone)
        stateIcon.background = MaterialShapeDrawable(ShapeAppearanceModel.builder().setAllCornerSizes(
            com.google.android.material.shape.RelativeCornerSize(0.5f)).build()).apply { fillColor = ColorStateList.valueOf(soft) }
        stateIcon.imageTintList = ColorStateList.valueOf(strong)

        when (UiCopy.hint(ui)) {
            HintAction.NONE -> hintAction.isVisible = false
            HintAction.OPEN_TETHERING -> hintAction.showHint(R.string.action_open_tethering, R.drawable.ic_usb)
            HintAction.OPEN_APP_SETTINGS -> hintAction.showHint(R.string.action_open_app_settings, R.drawable.ic_error_outline)
        }

        val live = findViewById<View>(R.id.liveBlock)
        live.isVisible = ui.phase == Phase.STREAMING
        if (live.isVisible) {
            val db = ui.levelDb ?: Level.FLOOR_DB
            level.setLevel(db, SystemClock.elapsedRealtime(), immediate)
            val shown = Level.displayDb(db)?.let { getString(R.string.level_value_db, it) } ?: getString(R.string.level_silent)
            levelValue.text = shown
            level.contentDescription = getString(R.string.level_cd, shown)
            val nf = NumberFormat.getIntegerInstance(locale())
            setStat(R.id.statSent, R.id.statSentBox, nf.format(ui.sent), R.string.stat_sent)
            setStat(R.id.statErrors, R.id.statErrorsBox, nf.format(ui.sendErrors), R.string.stat_errors)
            val reply = ui.lastReplyMs?.let { getString(R.string.stat_reply_value, String.format(locale(), "%.1f", it / 1000f)) }
                ?: getString(R.string.value_none)
            setStat(R.id.statReply, R.id.statReplyBox, reply, R.string.stat_reply)
        }

        // Primary action
        if (ui.running) {
            val red = ContextCompat.getColor(this, R.color.cm_red)
            toggle.setText(R.string.stop)
            toggle.contentDescription = getString(R.string.stop_cd)
            toggle.setIconResource(R.drawable.ic_stop)
            toggle.backgroundTintList = ColorStateList.valueOf(ContextCompat.getColor(this, R.color.cm_red_soft))
            toggle.setTextColor(red); toggle.iconTint = ColorStateList.valueOf(red)
            toggle.strokeColor = ColorStateList.valueOf(red); toggle.strokeWidth = dp(1)
        } else {
            val on = ContextCompat.getColor(this, R.color.cm_on_accent)
            toggle.setText(R.string.start)
            toggle.contentDescription = getString(R.string.start_cd)
            toggle.setIconResource(R.drawable.ic_mic)
            toggle.backgroundTintList = ColorStateList.valueOf(ContextCompat.getColor(this, R.color.cm_accent))
            toggle.setTextColor(on); toggle.iconTint = ColorStateList.valueOf(on)
            toggle.strokeWidth = 0
        }

        // Settings
        val expanded = isSettingsExpanded(ui)
        findViewById<View>(R.id.settingsBody).isVisible = expanded
        findViewById<View>(R.id.settingsSummary).isVisible = !expanded || ui.running
        findViewById<View>(R.id.settingsLocked).isVisible = ui.running
        for (v in listOf(ipLayout, portLayout, pairCodeLayout, presetLayout, aaudio, findViewById(R.id.aaudioRow))) v.isEnabled = !ui.running
        setChevron(R.id.settingsChevron, R.id.settingsHeader, expanded, R.string.settings_title)
        updateSummary()
        showDetected()
        val liveStreamer = MicService.current
        if (liveStreamer == null) speechLowWarning.visibility = android.view.View.GONE
        if (liveStreamer != null && !gain.isPressed) {
            applyingRemote = true
            val wantIdx = ncModes.indexOfFirst { it.first == liveStreamer.noiseMode }.let { if (it < 0) ncModeIndex else it }
            if (wantIdx != ncModeIndex) { ncModeIndex = wantIdx; showNcMode() }
            if (voiceClarity.isChecked != liveStreamer.voiceClarity) voiceClarity.isChecked = liveStreamer.voiceClarity
            if (autoVolume.isChecked != liveStreamer.agc) autoVolume.isChecked = liveStreamer.agc
            if (frontFocus.isChecked != liveStreamer.frontFocus) frontFocus.isChecked = liveStreamer.frontFocus
            if (frontFocusAr.isChecked != liveStreamer.frontFocusAr) frontFocusAr.isChecked = liveStreamer.frontFocusAr
            speechLowWarning.visibility = if (liveStreamer.speechLow) android.view.View.VISIBLE else android.view.View.GONE
            val g = liveStreamer.gainDb.coerceIn(0, VoiceChain.MAX_GAIN_DB).toFloat()
            if (gain.value != g) gain.value = g
            applyingRemote = false
            showGain()
        }

        // Details
        findViewById<View>(R.id.detailsBody).isVisible = detailsExpanded
        setChevron(R.id.detailsChevron, R.id.detailsHeader, detailsExpanded, R.string.details_title)
        if (detailsExpanded) renderDetails(ui)
    }

    private fun MaterialButton.showHint(text: Int, icon: Int) {
        isVisible = true; setText(text); setIconResource(icon)
    }

    private fun setStat(valueId: Int, boxId: Int, value: String, label: Int) {
        findViewById<TextView>(valueId).text = value
        findViewById<View>(boxId).contentDescription = "${getString(label)}: $value"
    }

    private fun setChevron(chevronId: Int, headerId: Int, expanded: Boolean, title: Int) {
        findViewById<View>(chevronId).rotation = if (expanded) 180f else 0f
        val t = getString(title)
        findViewById<View>(headerId).contentDescription =
            "$t. " + getString(if (expanded) R.string.collapse_cd else R.string.expand_cd, t)
    }

    private fun updateSummary() {
        val host = ip.text?.toString()?.trim().orEmpty().ifEmpty { getString(R.string.ip_auto) }
        val portNum = port.text?.toString()?.toIntOrNull() ?: Protocol.DEFAULT_PORT
        findViewById<TextView>(R.id.settingsSummary).text =
            getString(R.string.settings_summary, host, portNum, getString(presets[presetIndex].label))
    }

    private fun showGain() {
        gainValue.text = getString(R.string.gain_value, gain.value.toInt())
    }

    private val ncModes = listOf(
        NoiseMode.OFF to R.string.nc_off,
        NoiseMode.LIGHT to R.string.nc_light,
        NoiseMode.STRONG to R.string.nc_strong,
    )

    private fun persistAudio() {
        getPreferences(MODE_PRIVATE).edit()
            .putBoolean("nc", ncModes[ncModeIndex].first != NoiseMode.OFF)
            .putString("ncMode", listOf("off", "light", "strong")[ncModeIndex])
            .putBoolean("clarity", voiceClarity.isChecked)
            .putBoolean("agc", autoVolume.isChecked)
            .putBoolean("aaudio", aaudio.isChecked)
            .putBoolean("frontFocus", frontFocus.isChecked)
            .putBoolean("frontFocusAr", frontFocusAr.isChecked)
            .putBoolean("autoOptimize", autoOptimize.isChecked)
            .putBoolean("autoUpdate", autoUpdate.isChecked)
            .putInt("gain", gain.value.toInt())
            .putInt("preset", presetIndex)
            .apply()
    }

    private fun markUserSet(key: String) {
        getPreferences(MODE_PRIVATE).edit().putBoolean(key, true).apply()
    }

    private fun userSet(key: String) = getPreferences(MODE_PRIVATE).getBoolean(key, false)

    private fun clearUserSets() {
        getPreferences(MODE_PRIVATE).edit()
            .putBoolean("userSetGain", false)
            .putBoolean("userSetNc", false)
            .putBoolean("userSetClarity", false)
            .putBoolean("userSetAgc", false)
            .putBoolean("userSetAaudio", false)
            .putBoolean("userSetPreset", false)
            .putBoolean("userSetFrontFocus", false)
            .putBoolean("upgradeGainNudge", false)
            .apply()
    }

    private fun migrateUserLocks(p: android.content.SharedPreferences) {
        val ver = p.getInt("prefsVersion", 0)
        if (ver >= 3) return
        val ed = p.edit()
        if (ver == 0) {
            val upgrading = listOf("gain", "aaudio", "preset", "nc", "ncMode", "clarity", "agc").any { p.contains(it) }
            if (upgrading) {
                if (p.contains("aaudio")) ed.putBoolean("userSetAaudio", true)
                if (p.contains("preset")) ed.putBoolean("userSetPreset", true)
                if (p.contains("nc") || p.contains("ncMode")) ed.putBoolean("userSetNc", true)
                if (p.contains("clarity")) ed.putBoolean("userSetClarity", true)
                if (p.contains("agc")) ed.putBoolean("userSetAgc", true)
            }
        }
        // Allow a one-shot gain nudge after upgrade when speech stays far below target.
        ed.putBoolean("upgradeGainNudge", true)
        ed.putInt("prefsVersion", 3).apply()
    }

    private fun applyAdvice() {
        val advice = DeviceTune.withOverrides(
            DeviceTune.advise(DeviceProbe.profile),
            useAAudio = if (userSet("userSetAaudio")) aaudio.isChecked else null,
            frontFocus = if (userSet("userSetFrontFocus")) frontFocus.isChecked else null,
            noiseMode = if (userSet("userSetNc")) ncModes[ncModeIndex].first else null,
            gainDb = if (userSet("userSetGain")) gain.value.toInt() else null,
            inputPreset = if (userSet("userSetPreset")) presets[presetIndex].source else null,
        )
        applyingRemote = true
        aaudio.isChecked = advice.useAAudio
        frontFocus.isChecked = advice.frontFocus
        val wantNc = ncModes.indexOfFirst { it.first == advice.noiseMode }.let { if (it < 0) ncModeIndex else it }
        if (wantNc != ncModeIndex) { ncModeIndex = wantNc; showNcMode() }
        gain.value = advice.suggestedGainDb.coerceIn(0, VoiceChain.MAX_GAIN_DB).toFloat()
        if (!userSet("userSetPreset")) {
            val idx = presets.indexOfFirst { it.source == advice.inputPreset }
            if (idx >= 0 && idx != presetIndex) { presetIndex = idx; showPreset() }
        }
        applyingRemote = false
        showGain()
        persistAudio()
        MicService.current?.let {
            it.applyNoiseMode(advice.noiseMode)
            it.applyGain(advice.suggestedGainDb)
            it.applyFrontFocus(advice.frontFocus)
            it.applyFrontFocusAr(frontFocusAr.isChecked)
        }
    }

    private fun showDetected() {
        val text = MicService.current?.detectedSummary ?: DeviceProbe.summary
        detectedSummary.text = text.ifBlank { getString(R.string.detected_placeholder) }
    }

    private fun showNcMode() {
        ncMode.setText(getString(ncModes[ncModeIndex].second), false)
    }

    private fun showPreset() {
        preset.setText(getString(presets[presetIndex].label), false)
        presetLayout.helperText = getString(presets[presetIndex].help)
        updateSummary()
    }

    private fun renderDetails(ui: UiState) {
        val rows = mutableListOf(R.string.d_usb to (ui.usb ?: getString(R.string.d_usb_off)))
        val st = ui.stats
        if (ui.running && st != null) {
            val nf = NumberFormat.getIntegerInstance(locale())
            val ms = { frames: Int -> String.format(locale(), "%.1f", frames * 1000f / st.sampleRate.coerceAtLeast(1)) }
            rows += R.string.d_engine to listOfNotNull(
                st.engine,
                st.lowLatency?.let { getString(if (it) R.string.d_mode_low_latency else R.string.d_mode_normal) },
                st.exclusive?.let { getString(if (it) R.string.d_share_exclusive else R.string.d_share_shared) },
            ).joinToString(" · ")
            rows += R.string.d_rate to getString(R.string.d_rate_value, nf.format(st.sampleRate))
            st.framesPerBurst?.let { rows += R.string.d_burst to getString(R.string.d_frames_ms, nf.format(it), ms(it)) }
            st.bufferFrames?.let { rows += R.string.d_buffer to getString(R.string.d_frames_ms, nf.format(it), ms(it)) }
            st.xruns?.let { rows += R.string.d_xruns to nf.format(it) }
        } else {
            rows += R.string.d_engine to getString(R.string.d_not_running)
        }
        rows += R.string.d_version to BuildConfig.VERSION_NAME
        if (detailsBody.childCount != rows.size) {
            detailsBody.removeAllViews()
            repeat(rows.size) { LayoutInflater.from(this).inflate(R.layout.item_detail_row, detailsBody, true) }
        }
        rows.forEachIndexed { i, (label, value) ->
            val row = detailsBody.getChildAt(i)
            row.findViewById<TextView>(R.id.label).setText(label)
            row.findViewById<TextView>(R.id.value).text = value
            row.contentDescription = "${getString(label)}: $value"
        }
    }

    private fun toneColors(t: Tone): Pair<Int, Int> {
        val c = { id: Int -> ContextCompat.getColor(this, id) }
        return when (t) {
            Tone.NEUTRAL -> c(R.color.cm_surface_muted) to c(R.color.cm_on_surface_variant)
            Tone.OK -> c(R.color.cm_accent_soft) to c(R.color.cm_accent)
            Tone.WARNING -> c(R.color.cm_amber_soft) to c(R.color.cm_amber)
            Tone.ERROR -> c(R.color.cm_red_soft) to c(R.color.cm_red)
        }
    }

    private fun locale(): Locale = resources.configuration.locales[0]
    private fun dp(v: Int) = (v * resources.displayMetrics.density).toInt()

    /** Tethering screen differs per vendor: try the AOSP/One UI screen, then wireless settings, then Settings. */
    private fun openTetheringSettings() {
        val candidates = listOf(
            Intent().setComponent(ComponentName("com.android.settings", "com.android.settings.TetherSettings")),
            Intent(Settings.ACTION_WIRELESS_SETTINGS),
            Intent(Settings.ACTION_SETTINGS),
        )
        for (i in candidates) {
            try { startActivity(i.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)); return } catch (_: Exception) {}
        }
        Toast.makeText(this, R.string.err_no_settings_app, Toast.LENGTH_LONG).show()
    }

    private fun openAppSettings() {
        try {
            startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.fromParts("package", packageName, null)))
        } catch (_: Exception) {
            Toast.makeText(this, R.string.err_mic_permission, Toast.LENGTH_LONG).show()
        }
    }


    private fun maybeAutoCheckUpdates() {
        val p = getPreferences(MODE_PRIVATE)
        if (!p.getBoolean("autoUpdate", false)) return
        val last = p.getLong("lastUpdateCheck", 0L)
        if (System.currentTimeMillis() - last < AppUpdate.MIN_CHECK_MS) return
        checkForUpdates(interactive = false)
    }

    private fun checkForUpdates(interactive: Boolean) {
        if (updateBusy) return
        updateBusy = true
        updateStatus.text = getString(R.string.update_checking)
        Thread({
            val result = UpdateInstaller.fetchOffer(BuildConfig.VERSION_NAME)
            handler.post { updateBusy = false }
            postIfAlive {
                result.fold(
                    onSuccess = { offer ->
                        getPreferences(MODE_PRIVATE).edit().putLong("lastUpdateCheck", System.currentTimeMillis()).apply()
                        if (offer == null) {
                            updateStatus.text = getString(R.string.update_current, BuildConfig.VERSION_NAME)
                        } else {
                            updateStatus.text = getString(R.string.update_available, offer.tag)
                            if (interactive || getPreferences(MODE_PRIVATE).getBoolean("autoUpdate", false)) {
                                applyUpdate(offer)
                            }
                        }
                    },
                    onFailure = { e ->
                        updateStatus.text = getString(R.string.update_failed, e.message ?: e.toString())
                    },
                )
            }
        }, "cordmic-update").start()
    }

    private fun applyUpdate(offer: UpdateInstaller.Offer) {
        if (updateBusy || installBusy) return
        installBusy = true
        updateStatus.text = getString(R.string.update_installing)
        val app = applicationContext
        Thread({
            val r = runCatching {
                val apk = UpdateInstaller.downloadAndVerify(app, offer)
                UpdateInstaller.startInstall(app, apk)
            }
            handler.post { installBusy = false }
            postIfAlive {
                r.onFailure { updateStatus.text = getString(R.string.update_failed, it.message ?: it.toString()) }
            }
        }, "cordmic-update-dl").start()
    }

    private fun startClicked() {
        val needed = mutableListOf(Manifest.permission.RECORD_AUDIO)
        if (Build.VERSION.SDK_INT >= 33) needed += Manifest.permission.POST_NOTIFICATIONS
        val missing = needed.filter { checkSelfPermission(it) != PackageManager.PERMISSION_GRANTED }
        if (missing.isNotEmpty()) { requestPermissions(missing.toTypedArray(), REQ); return }
        doStart()
    }

    private fun doStart() {
        val host = ip.text?.toString()?.trim().orEmpty()
        val portNum = port.text?.toString()?.toIntOrNull()?.takeIf { it in 1..65535 }
        var ok = true
        if (portNum == null) { portLayout.error = getString(R.string.err_port); ok = false }
        if (host.isNotEmpty() && !isIpv4(host)) { ipLayout.error = getString(R.string.err_ip); ok = false }
        val codeText = pairCode.text?.toString().orEmpty()
        val code = Pairing.normalize(codeText)
        if (codeText.isNotBlank() && code == null) { pairCodeLayout.error = getString(R.string.err_pair_code); ok = false }
        if (!ok || portNum == null) { settingsOverride = true; render(currentState(), immediate = true); return }
        permissionDenied = false
        val gainDb = gain.value.toInt()
        getPreferences(MODE_PRIVATE).edit().putString("ip", host).putInt("port", portNum)
            .putString("pairCode", code.orEmpty())
            .putInt("preset", presetIndex).putBoolean("aaudio", aaudio.isChecked)
            .putBoolean("nc", ncModes[ncModeIndex].first != NoiseMode.OFF)
            .putString("ncMode", listOf("off", "light", "strong")[ncModeIndex])
            .putBoolean("clarity", voiceClarity.isChecked)
            .putBoolean("agc", autoVolume.isChecked)
            .putBoolean("frontFocus", frontFocus.isChecked)
            .putBoolean("frontFocusAr", frontFocusAr.isChecked)
            .putBoolean("autoOptimize", autoOptimize.isChecked)
            .putBoolean("autoUpdate", autoUpdate.isChecked)
            .putInt("gain", gainDb).apply()
        val i = Intent(this, MicService::class.java)
            .putExtra(MicService.EXTRA_HOST, host)
            .putExtra(MicService.EXTRA_PORT, portNum)
            .putExtra(MicService.EXTRA_PAIR_CODE, code)
            .putExtra(MicService.EXTRA_PRESET, presets[presetIndex].source)
            .putExtra(MicService.EXTRA_AAUDIO, aaudio.isChecked)
            .putExtra(MicService.EXTRA_NC, ncModes[ncModeIndex].first != NoiseMode.OFF)
            .putExtra(MicService.EXTRA_NC_MODE, ncModes[ncModeIndex].first.code)
            .putExtra(MicService.EXTRA_CLARITY, voiceClarity.isChecked)
            .putExtra(MicService.EXTRA_AGC, autoVolume.isChecked)
            .putExtra(MicService.EXTRA_GAIN, gainDb)
            .putExtra(MicService.EXTRA_FRONT_FOCUS, frontFocus.isChecked)
            .putExtra(MicService.EXTRA_FRONT_FOCUS_AR, frontFocusAr.isChecked)
            .putExtra(MicService.EXTRA_AUTO_OPTIMIZE, autoOptimize.isChecked)
            .putExtra(MicService.EXTRA_USER_SET_GAIN, userSet("userSetGain"))
            .putExtra(MicService.EXTRA_UPGRADE_GAIN_NUDGE, getPreferences(MODE_PRIVATE).getBoolean("upgradeGainNudge", false))
        startForegroundService(i)
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, results: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, results)
        if (requestCode != REQ) return
        val micOk = checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED
        if (micOk) doStart() else { permissionDenied = true; render(currentState(), immediate = true) }
    }

    companion object {
        private const val REQ = 1
        fun isIpv4(s: String): Boolean {
            val parts = s.split('.')
            return parts.size == 4 && parts.all { p -> p.length in 1..3 && p.all(Char::isDigit) && p.toInt() <= 255 }
        }
    }
}

private fun TextInputEditText.doOnTextChange(block: () -> Unit) = addTextChangedListener(object : android.text.TextWatcher {
    override fun beforeTextChanged(s: CharSequence?, start: Int, count: Int, after: Int) {}
    override fun onTextChanged(s: CharSequence?, start: Int, before: Int, count: Int) {}
    override fun afterTextChanged(s: android.text.Editable?) = block()
})
