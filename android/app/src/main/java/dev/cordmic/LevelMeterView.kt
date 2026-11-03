package dev.cordmic

import android.content.Context
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.RectF
import android.util.AttributeSet
import android.view.View
import androidx.core.content.ContextCompat

/**
 * Horizontal input meter, -60..0 dBFS. Fill follows the real captured peak with a short release so it is readable;
 * colour changes near full scale (amber from -9 dB, red from -1 dB = clipping risk).
 */
class LevelMeterView @JvmOverloads constructor(ctx: Context, attrs: AttributeSet? = null) : View(ctx, attrs) {
    private val track = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = ContextCompat.getColor(ctx, R.color.cm_surface_muted) }
    private val fill = Paint(Paint.ANTI_ALIAS_FLAG)
    private val okColor = ContextCompat.getColor(ctx, R.color.cm_accent)
    private val warnColor = ContextCompat.getColor(ctx, R.color.cm_meter_amber)
    private val clipColor = ContextCompat.getColor(ctx, R.color.cm_red)
    private val rect = RectF()
    private var shownDb = Level.FLOOR_DB
    private var lastUpdate = 0L

    init { importantForAccessibility = IMPORTANT_FOR_ACCESSIBILITY_YES }

    /** Instant attack, ~30 dB/s release. Pass [immediate] to skip the release (e.g. for still renders). */
    fun setLevel(db: Float, nowMs: Long, immediate: Boolean = false) {
        val dt = if (lastUpdate == 0L) 0L else nowMs - lastUpdate
        lastUpdate = nowMs
        shownDb = if (immediate || db >= shownDb) db else maxOf(db, shownDb - dt * 0.03f)
        invalidate()
    }

    override fun onDraw(canvas: Canvas) {
        val r = height / 2f
        rect.set(0f, 0f, width.toFloat(), height.toFloat())
        canvas.drawRoundRect(rect, r, r, track)
        val f = ((shownDb - Level.FLOOR_DB) / -Level.FLOOR_DB).coerceIn(0f, 1f)
        if (f <= 0f) return
        fill.color = when { shownDb >= -1f -> clipColor; shownDb >= -9f -> warnColor; else -> okColor }
        rect.right = maxOf(height.toFloat(), width * f)
        canvas.drawRoundRect(rect, r, r, fill)
    }
}
