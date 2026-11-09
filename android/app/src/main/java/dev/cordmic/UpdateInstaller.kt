package dev.cordmic

import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageInstaller
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.provider.Settings
import android.util.Log
import java.io.File
import java.net.HttpURLConnection
import java.net.URL

/**
 * Checks GitHub Releases for a newer CordMic and, after hash + signing-cert
 * verification, starts a PackageInstaller session. The user still confirms Install.
 */
object UpdateInstaller {
    private const val TAG = "CordMicUpdate"
    private const val ACTION = "dev.cordmic.UPDATE_SESSION"

    data class Offer(val tag: String, val apkName: String, val apkUrl: String, val expectedSha256: String)

    fun canInstallPackages(ctx: Context): Boolean =
        if (Build.VERSION.SDK_INT >= 26) ctx.packageManager.canRequestPackageInstalls() else true

    fun requestUnknownSources(ctx: Context) {
        if (Build.VERSION.SDK_INT < 26) return
        val i = Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:${ctx.packageName}"))
        i.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        ctx.startActivity(i)
    }

    fun fetchOffer(currentVersion: String): Result<Offer?> = runCatching {
        val json = httpGet(AppUpdate.LATEST_API) ?: error("update check failed")
        val info = AppUpdate.parseLatestJson(json)
        if (!AppUpdate.isNewer(info.tag, currentVersion)) return@runCatching null
        if (AppUpdate.isDowngrade(info.tag, currentVersion)) error("refusing to downgrade to ${info.tag}")
        val apkUrl = info.apkUrl ?: error("release has no APK")
        val apkName = info.apkName ?: error("release has no APK name")
        val sumsUrl = info.sumsUrl ?: error("release has no SHA256SUMS")
        val sums = AppUpdate.parseSha256Sums(httpGet(sumsUrl) ?: error("could not download SHA256SUMS"))
        val hash = AppUpdate.hashForAsset(sums, apkName) ?: error("SHA256SUMS has no entry for $apkName")
        Offer(info.tag, apkName, apkUrl, hash)
    }

    fun downloadAndVerify(ctx: Context, offer: Offer): File {
        val dir = File(ctx.cacheDir, "updates").apply { mkdirs() }
        val dest = File(dir, offer.apkName)
        httpDownload(offer.apkUrl, dest)
        if (!AppUpdate.hashMatchesFile(dest, offer.expectedSha256)) {
            dest.delete()
            error("APK SHA-256 mismatch; aborting")
        }
        val current = signingCerts(ctx, null)
        val incoming = signingCerts(ctx, dest)
        if (!certsMatch(current, incoming)) {
            dest.delete()
            error("APK signing certificate does not match the installed app; aborting")
        }
        return dest
    }

    fun startInstall(ctx: Context, apk: File) {
        if (!canInstallPackages(ctx)) {
            requestUnknownSources(ctx)
            error("allow installing unknown apps for CordMic, then try again")
        }
        val installer = ctx.packageManager.packageInstaller
        val params = PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL)
        params.setAppPackageName(ctx.packageName)
        val sessionId = installer.createSession(params)
        installer.openSession(sessionId).use { session ->
            session.openWrite("apk", 0, apk.length()).use { out ->
                apk.inputStream().use { it.copyTo(out) }
                session.fsync(out)
            }
            val flags = if (Build.VERSION.SDK_INT >= 31)
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_MUTABLE
            else PendingIntent.FLAG_UPDATE_CURRENT
            val pi = PendingIntent.getBroadcast(ctx, sessionId, Intent(ACTION).setPackage(ctx.packageName), flags)
            session.commit(pi.intentSender)
        }
    }

    fun register(ctx: Context): BroadcastReceiver {
        val r = object : BroadcastReceiver() {
            override fun onReceive(context: Context, intent: Intent) {
                val status = intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE)
                if (status == PackageInstaller.STATUS_PENDING_USER_ACTION) {
                    val confirm = if (Build.VERSION.SDK_INT >= 33)
                        intent.getParcelableExtra(Intent.EXTRA_INTENT, Intent::class.java)
                    else @Suppress("DEPRECATION") intent.getParcelableExtra(Intent.EXTRA_INTENT)
                    confirm?.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                    if (confirm != null) context.startActivity(confirm)
                } else if (status != PackageInstaller.STATUS_SUCCESS) {
                    val msg = intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE)
                    Log.w(TAG, "install status=$status $msg")
                }
            }
        }
        val filter = IntentFilter(ACTION)
        if (Build.VERSION.SDK_INT >= 33) ctx.registerReceiver(r, filter, Context.RECEIVER_NOT_EXPORTED)
        else ctx.registerReceiver(r, filter)
        return r
    }

    fun signingCerts(ctx: Context, apk: File?): List<ByteArray> {
        val pm = ctx.packageManager
        return try {
            if (Build.VERSION.SDK_INT >= 28) {
                val info = if (apk != null)
                    pm.getPackageArchiveInfo(apk.absolutePath, PackageManager.GET_SIGNING_CERTIFICATES)
                else pm.getPackageInfo(ctx.packageName, PackageManager.GET_SIGNING_CERTIFICATES)
                (info?.signingInfo?.apkContentsSigners ?: emptyArray()).map { it.toByteArray() }
            } else {
                @Suppress("DEPRECATION")
                val info = if (apk != null)
                    pm.getPackageArchiveInfo(apk.absolutePath, PackageManager.GET_SIGNATURES)
                else pm.getPackageInfo(ctx.packageName, PackageManager.GET_SIGNATURES)
                @Suppress("DEPRECATION")
                (info?.signatures ?: emptyArray()).map { it.toByteArray() }
            }
        } catch (t: Throwable) {
            Log.w(TAG, "signing certs: $t")
            emptyList()
        }
    }

    fun certsMatch(a: List<ByteArray>, b: List<ByteArray>): Boolean {
        if (a.isEmpty() || b.isEmpty()) return false
        return a.any { left -> b.any { right -> left.contentEquals(right) } }
    }

    private fun httpGet(url: String): String? {
        val c = open(url)
        return try {
            if (c.responseCode !in 200..299) {
                Log.w(TAG, "GET $url -> ${c.responseCode}")
                null
            } else c.inputStream.bufferedReader().use { it.readText() }
        } finally { c.disconnect() }
    }

    private fun httpDownload(url: String, dest: File) {
        val c = open(url)
        try {
            if (c.responseCode !in 200..299) error("download failed (${c.responseCode})")
            dest.outputStream().use { out -> c.inputStream.use { it.copyTo(out) } }
        } finally { c.disconnect() }
    }

    private fun open(url: String): HttpURLConnection {
        val c = URL(url).openConnection() as HttpURLConnection
        c.connectTimeout = 15000
        c.readTimeout = 30000
        c.instanceFollowRedirects = true
        c.setRequestProperty("User-Agent", AppUpdate.USER_AGENT)
        c.setRequestProperty("Accept", "application/vnd.github+json, application/octet-stream, */*")
        return c
    }
}
