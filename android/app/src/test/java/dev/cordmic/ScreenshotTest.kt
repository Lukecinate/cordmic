package dev.cordmic

import android.graphics.Bitmap
import android.graphics.Canvas
import android.view.View
import java.io.File
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Robolectric
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config
import org.robolectric.annotation.GraphicsMode

/**
 * Renders the real activity layout, theme and fonts on the JVM (Robolectric native graphics) with fixture states.
 * Fixture numbers are sample values for documentation, not measurements. Without -PrecordScreenshots the images
 * are rendered and checked but not written.
 */
@RunWith(RobolectricTestRunner::class)
@GraphicsMode(GraphicsMode.Mode.NATIVE)
@Config(sdk = [34])
class ScreenshotTest {
    private val size = "w393dp-h852dp-port"
    private val phone = "$size-xhdpi"
    private val usb = "rndis0 · 192.168.42.129/24"
    private val stats = EngineStats("AAudio", 48000, framesPerBurst = 96, lowLatency = true, exclusive = true,
        bufferFrames = 192, xruns = 0)
    private val streaming = UiState(Phase.STREAMING, target = "192.168.42.157:50005", engine = "AAudio",
        levelDb = -17f, sent = 18432, sendErrors = 0, lastReplyMs = 400, usb = usb, stats = stats)

    private fun shot(name: String, qualifiers: String, state: UiState, settings: Boolean? = null, details: Boolean = false) {
        RuntimeEnvironment.setQualifiers(qualifiers)
        val ctl = Robolectric.buildActivity(MainActivity::class.java).setup()
        val a = ctl.get()
        a.previewState = state
        a.setSectionsExpanded(settings, details)
        val root = a.findViewById<View>(R.id.root)
        val w = root.width.takeIf { it > 0 } ?: a.resources.displayMetrics.widthPixels
        val screenH = a.resources.displayMetrics.heightPixels
        // Full-length render: grow the window to fit the scroll content so nothing is cut off.
        val toolbar = a.findViewById<View>(R.id.toolbar)
        val content = a.findViewById<View>(R.id.content)
        content.measure(View.MeasureSpec.makeMeasureSpec(w, View.MeasureSpec.EXACTLY), View.MeasureSpec.UNSPECIFIED)
        val h = maxOf(screenH, toolbar.height + content.measuredHeight)
        root.measure(View.MeasureSpec.makeMeasureSpec(w, View.MeasureSpec.EXACTLY), View.MeasureSpec.makeMeasureSpec(h, View.MeasureSpec.EXACTLY))
        root.layout(0, 0, w, h)
        val bmp = Bitmap.createBitmap(w, h, Bitmap.Config.ARGB_8888)
        val canvas = Canvas(bmp)
        canvas.drawColor(a.getColor(R.color.cm_background)) // the window background, which is not part of the view tree
        root.draw(canvas)
        val bg = bmp.getPixel(0, h - 1)
        assertTrue("$name rendered blank", (0 until h step 11).any { y -> (0 until w step 11).any { x -> bmp.getPixel(x, y) != bg } })
        System.getProperty("cordmic.screenshots.dir")?.let { dir ->
            File(dir).mkdirs()
            File(dir, "$name.png").outputStream().use { bmp.compress(Bitmap.CompressFormat.PNG, 100, it) }
        }
        ctl.pause().stop().destroy()
    }

    @Test fun idleSettings() = shot("android-settings", phone, UiState(Phase.IDLE, usb = usb))
    @Test fun waiting() = shot("android-waiting", phone, UiState(Phase.WAITING_TETHER))
    @Test fun searching() = shot("android-searching", phone, UiState(Phase.SEARCHING, engine = "AAudio", usb = usb, stats = stats))
    @Test fun streaming() = shot("android-streaming", phone, streaming)
    @Test fun details() = shot("android-details", phone, streaming, details = true)
    @Test fun error() = shot("android-error", phone, UiState(Phase.MIC_FAILED, usb = usb))
    @Test fun dark() = shot("android-dark", "$size-night-xhdpi", streaming)
    @Test fun indonesian() = shot("android-streaming-id", "in-$phone", streaming)
    @Test fun small() = shot("android-small", "w320dp-h640dp-port-xhdpi", UiState(Phase.IDLE))
    @Test fun landscape() = shot("android-landscape", "w852dp-h393dp-land-xhdpi", streaming)
}
