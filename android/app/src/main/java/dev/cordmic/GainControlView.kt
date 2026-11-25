package dev.cordmic

import android.content.Context
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.RectF
import android.os.Build
import android.util.AttributeSet
import android.view.KeyEvent
import android.view.MotionEvent
import android.view.View
import android.view.accessibility.AccessibilityEvent
import android.view.accessibility.AccessibilityNodeInfo
import androidx.core.content.ContextCompat
import kotlin.math.atan2
import kotlin.math.cos
import kotlin.math.roundToInt
import kotlin.math.sin

/**
 * Mic-gain editor: a flat knob, a slider and a numeric dB readout, all showing the same value.
 * Double-tap resets to 0 dB. Arrow keys and a mouse wheel move 0.5 dB (Shift: 0.1 dB).
 * While dragging, the parent scroll view is asked not to intercept the touch.
 */
class GainControlView @JvmOverloads constructor(ctx: Context, attrs: AttributeSet? = null) : View(ctx, attrs) {
    private val accent = ContextCompat.getColor(ctx, R.color.cm_accent)
    private val ink = ContextCompat.getColor(ctx, R.color.cm_on_surface)
    private val outline = ContextCompat.getColor(ctx, R.color.cm_outline)
    private val ring = Paint(Paint.ANTI_ALIAS_FLAG).apply { style = Paint.Style.STROKE; strokeCap = Paint.Cap.ROUND }
    private val fill = Paint(Paint.ANTI_ALIAS_FLAG)
    private val textPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = accent
        textSize = 13f * resources.displayMetrics.scaledDensity
        isFakeBoldText = true
    }
    private val knobRect = RectF()
    private val barRect = RectF()
    private val thumbRect = RectF()
    private var db = 0.0
    private var knobDown = false
    private var barDown = false
    private var lastTapMs = 0L

    var onGainChanged: ((Double) -> Unit)? = null

    init {
        isClickable = true
        isFocusable = true
        isFocusableInTouchMode = true
        importantForAccessibility = IMPORTANT_FOR_ACCESSIBILITY_YES
        contentDescription = context.getString(R.string.gain_option)
        if (Build.VERSION.SDK_INT >= 26) defaultFocusHighlightEnabled = true
    }

    var value: Double
        get() = db
        set(v) {
            val n = VoiceChain.snapGain(v)
            if (kotlin.math.abs(n - db) < 0.001) return
            db = n
            refreshAccess()
            invalidate()
        }

    fun setValueFromHost(v: Double) {
        value = v
    }

    private fun commit(v: Double, fromUser: Boolean) {
        val n = VoiceChain.snapGain(v)
        if (kotlin.math.abs(n - db) < 0.001) return
        db = n
        refreshAccess()
        invalidate()
        if (fromUser) onGainChanged?.invoke(db)
    }

    private fun refreshAccess() {
        contentDescription = context.getString(R.string.gain_access, VoiceChain.formatGain(db))
        sendAccessibilityEvent(AccessibilityEvent.TYPE_VIEW_TEXT_CHANGED)
    }

    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        val d = resources.displayMetrics.density
        val h = (52 * d).roundToInt()
        val w = MeasureSpec.getSize(widthMeasureSpec).coerceAtLeast((200 * d).roundToInt())
        setMeasuredDimension(w, resolveSize(h, heightMeasureSpec))
    }

    override fun onDraw(canvas: Canvas) {
        val d = resources.displayMetrics.density
        val k = (44 * d)
        val pad = 2 * d
        val cy = height / 2f
        knobRect.set(pad, cy - k / 2, pad + k, cy + k / 2)
        val barLeft = knobRect.right + 12 * d
        val barRight = width - pad
        val barH = 22 * d
        barRect.set(barLeft, cy - barH / 2, barRight, cy + barH / 2)

        val cx = knobRect.centerX()
        val ky = knobRect.centerY()
        val r = k / 2 - 3 * d
        ring.strokeWidth = 3 * d
        ring.color = outline
        canvas.drawArc(cx - r, ky - r, cx + r, ky + r, 135f, 270f, false, ring)
        val ang = angleFromDb(db)
        val zero = angleFromDb(0.0)
        val sweep = (ang - zero).toFloat()
        if (kotlin.math.abs(sweep) > 0.4f) {
            ring.color = accent
            if (sweep >= 0) canvas.drawArc(cx - r, ky - r, cx + r, ky + r, zero.toFloat(), sweep, false, ring)
            else canvas.drawArc(cx - r, ky - r, cx + r, ky + r, ang.toFloat(), -sweep, false, ring)
        }
        // pointer
        val rad = Math.toRadians(ang)
        fill.color = ink
        fill.strokeWidth = 2 * d
        fill.style = Paint.Style.STROKE
        fill.strokeCap = Paint.Cap.ROUND
        canvas.drawLine(cx, ky, (cx + cos(rad) * (r - 2 * d)).toFloat(), (ky + sin(rad) * (r - 2 * d)).toFloat(), fill)
        fill.style = Paint.Style.FILL
        canvas.drawCircle(cx, ky, 4 * d, fill)

        // track + thumb
        val trackH = 4 * d
        val trackTop = cy - trackH / 2
        fill.color = outline
        canvas.drawRect(barRect.left, trackTop, barRect.right, trackTop + trackH, fill)
        val t = ((db - VoiceChain.MIN_GAIN_DB) / (VoiceChain.MAX_GAIN_DB - VoiceChain.MIN_GAIN_DB)).toFloat().coerceIn(0f, 1f)
        val tw = 10 * d
        val th = 18 * d
        val tx = barRect.left + t * (barRect.width() - tw)
        fill.color = accent
        canvas.drawRect(barRect.left, trackTop, tx + tw / 2, trackTop + trackH, fill)
        thumbRect.set(tx, cy - th / 2, tx + tw, cy + th / 2)
        canvas.drawRect(thumbRect, fill)

        val label = VoiceChain.formatGain(db)
        canvas.drawText(label, barRect.right - textPaint.measureText(label), (barRect.top - 2 * d).coerceAtLeast(textPaint.textSize), textPaint)
    }

    override fun onTouchEvent(event: MotionEvent): Boolean {
        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                parent?.requestDisallowInterceptTouchEvent(true)
                requestFocus()
                val now = event.eventTime
                if (now - lastTapMs < 280) { commit(0.0, true); lastTapMs = 0; return true }
                lastTapMs = now
                if (knobRect.contains(event.x, event.y)) { knobDown = true; commitFromKnob(event.x, event.y) }
                else { barDown = true; commit(dbFromBar(event.x), true) }
                return true
            }
            MotionEvent.ACTION_MOVE -> {
                if (knobDown) commitFromKnob(event.x, event.y)
                else if (barDown) commit(dbFromBar(event.x), true)
                return true
            }
            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                knobDown = false; barDown = false
                parent?.requestDisallowInterceptTouchEvent(false)
                return true
            }
        }
        return super.onTouchEvent(event)
    }

    override fun onGenericMotionEvent(event: MotionEvent): Boolean {
        if (event.action == MotionEvent.ACTION_SCROLL) {
            val delta = event.getAxisValue(MotionEvent.AXIS_VSCROLL)
            val steps = if (delta > 0) 1 else if (delta < 0) -1 else 0
            if (steps != 0) {
                val fine = event.metaState and KeyEvent.META_SHIFT_ON != 0
                commit(VoiceChain.nudgeGain(db, steps, fine), true)
                return true
            }
        }
        return super.onGenericMotionEvent(event)
    }

    override fun onKeyDown(keyCode: Int, event: KeyEvent): Boolean {
        val fine = event.isShiftPressed
        when (keyCode) {
            KeyEvent.KEYCODE_DPAD_LEFT, KeyEvent.KEYCODE_DPAD_DOWN, KeyEvent.KEYCODE_MINUS -> {
                commit(VoiceChain.nudgeGain(db, -1, fine), true); return true
            }
            KeyEvent.KEYCODE_DPAD_RIGHT, KeyEvent.KEYCODE_DPAD_UP, KeyEvent.KEYCODE_PLUS, KeyEvent.KEYCODE_EQUALS -> {
                commit(VoiceChain.nudgeGain(db, 1, fine), true); return true
            }
            KeyEvent.KEYCODE_MOVE_HOME -> { commit(VoiceChain.MIN_GAIN_DB.toDouble(), true); return true }
            KeyEvent.KEYCODE_MOVE_END -> { commit(VoiceChain.MAX_GAIN_DB.toDouble(), true); return true }
        }
        return super.onKeyDown(keyCode, event)
    }

    override fun onInitializeAccessibilityNodeInfo(info: AccessibilityNodeInfo) {
        super.onInitializeAccessibilityNodeInfo(info)
        info.className = "android.widget.SeekBar"
        info.isScrollable = true
        val range = AccessibilityNodeInfo.RangeInfo.obtain(
            AccessibilityNodeInfo.RangeInfo.RANGE_TYPE_FLOAT,
            VoiceChain.MIN_GAIN_DB.toFloat(), VoiceChain.MAX_GAIN_DB.toFloat(), db.toFloat(),
        )
        info.rangeInfo = range
        info.text = VoiceChain.formatGain(db)
    }

    private fun dbFromBar(x: Float): Double {
        val tw = thumbRect.width().coerceAtLeast(1f)
        val t = ((x - barRect.left - tw / 2) / (barRect.width() - tw)).toDouble().coerceIn(0.0, 1.0)
        return VoiceChain.MIN_GAIN_DB + t * (VoiceChain.MAX_GAIN_DB - VoiceChain.MIN_GAIN_DB)
    }

    private fun commitFromKnob(x: Float, y: Float) {
        val cx = knobRect.centerX(); val cy = knobRect.centerY()
        var deg = Math.toDegrees(atan2((y - cy).toDouble(), (x - cx).toDouble()))
        if (deg < 0) deg += 360
        val travel = when {
            deg >= 135 -> deg - 135
            deg <= 45 -> 225 + deg
            deg < 90 -> 270.0
            else -> 0.0
        }
        val t = (travel / 270.0).coerceIn(0.0, 1.0)
        commit(VoiceChain.MIN_GAIN_DB + t * (VoiceChain.MAX_GAIN_DB - VoiceChain.MIN_GAIN_DB), true)
    }

    private fun angleFromDb(v: Double): Double {
        val t = ((v - VoiceChain.MIN_GAIN_DB) / (VoiceChain.MAX_GAIN_DB - VoiceChain.MIN_GAIN_DB)).coerceIn(0.0, 1.0)
        return 135 + t * 270
    }
}
