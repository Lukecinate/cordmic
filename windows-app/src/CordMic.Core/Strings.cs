using System.Globalization;

namespace CordMic.Core;

/// <summary>
/// Minimal string table for the Windows app UI. English is the default; Indonesian is used when the
/// Windows display language is Indonesian ("id"), or when the user picks it in the tray menu.
/// Format placeholders follow <see cref="string.Format(string, object[])"/>.
/// </summary>
public static class L
{
    public const string Auto = "auto";
    public const string English = "en";
    public const string Indonesian = "id";

    private static IReadOnlyDictionary<string, string>? _current; // null until Set(); falls back to En (declared below)

    /// <summary>Active language code ("en" or "id").</summary>
    public static string Current { get; private set; } = English;

    /// <summary>Resolves a setting value ("auto", "en", "id") plus the OS UI language name to a supported code.</summary>
    public static string Resolve(string? setting, string? osLanguage)
    {
        var s = (setting ?? Auto).Trim().ToLowerInvariant();
        if (s == English || s == Indonesian) return s;
        var os = (osLanguage ?? "").Trim().ToLowerInvariant();
        // "id", "id-ID", and the legacy ISO 639 code "in" all mean Indonesian.
        return os == "id" || os.StartsWith("id-") || os == "in" || os.StartsWith("in-") ? Indonesian : English;
    }

    public static void Set(string code)
    {
        Current = code == Indonesian ? Indonesian : English;
        _current = Current == Indonesian ? Id : En;
    }

    /// <summary>OS display language from CultureInfo.CurrentUICulture (may be invariant in globalization-invariant mode).</summary>
    public static string OsLanguageFromCulture()
    {
        try { return CultureInfo.CurrentUICulture.Name; } catch (Exception) { return ""; }
    }

    public static string T(string key) =>
        (_current ?? En).TryGetValue(key, out var v) ? v : En.TryGetValue(key, out var e) ? e : key;

    public static string T(string key, params object?[] args) =>
        string.Format(CultureInfo.InvariantCulture, T(key), args);

    public static IReadOnlyDictionary<string, string> Table(string code) => code == Indonesian ? Id : En;

    public static readonly IReadOnlyDictionary<string, string> En = new Dictionary<string, string>
    {
        // status
        ["status.starting"] = "Starting…",
        ["status.portBusy.title"] = "Port busy",
        ["status.portBusy.detail"] = "UDP port {0} is in use by another app (e.g. the Python receiver). Close it and wait.",
        ["status.portBusy.fallback"] = "Cannot open the UDP port.",
        ["status.portBusy.tip"] = "CordMic: UDP port busy\nClose other apps using port 50005",
        ["status.outputGone.title"] = "Output device missing",
        ["status.outputGone.detail"] = "\"{0}\" was not found. Choose it again in the Output device menu.",
        ["status.outputGone.tip"] = "CordMic: output device missing\nChoose it again in the menu",
        ["status.noCable.title"] = "No virtual mic yet",
        ["status.noCable.detail"] = "Install VB-CABLE first. Open the Setup guide.",
        ["status.noCable.tip"] = "CordMic: virtual mic not installed\nDouble-click for the setup guide",
        ["status.audioError.title"] = "Audio output error",
        ["status.audioError.fallback"] = "Opening audio device…",
        ["status.audioError.tip"] = "CordMic: audio output error\nRetrying automatically",
        ["status.waiting.title"] = "Waiting for phone",
        ["status.waiting.detail"] = "Plug in the USB cable and turn on USB tethering on the phone.",
        ["status.waiting.tip"] = "CordMic: waiting for phone\nPlug in the cable & turn on USB tethering",
        ["status.detected.title"] = "Phone detected",
        ["status.detected.detail"] = "Adapter {0} ({1}). Open CordMic on the phone and tap Start.",
        ["status.detected.tip"] = "CordMic: phone detected\nOpen CordMic on the phone, tap Start",
        ["status.connected.tip"] = "CordMic: connected\nLatency ~{0} ms (PC side) · RTT {1} ms\nLoss {2}% · buffer {3} ms",
        ["status.silent.title"] = "Connected, audio stopped",
        ["status.silent.detail"] = "The phone is connected but not sending audio. Check the phone app.",
        ["status.silent.tip"] = "CordMic: connected but no audio\nCheck the phone app",
        ["status.viaPhone.title"] = "Connected (check network)",
        ["status.viaPhone.detail"] = "PC internet is routed through the phone. Use \"Fix phone network priority\" so games keep using LAN/Wi-Fi.",
        ["status.viaPhone.tipSuffix"] = "\n! Internet via phone",
        ["status.connected.title"] = "Connected",
        ["status.connected.detail"] = "Latency ~{0} ms PC side, RTT {1} ms, loss {2}% (10 s).",
        // balloon
        ["balloon.lost.title"] = "Phone mic disconnected",
        ["balloon.lost.noAdapter"] = "Check the cable and turn USB tethering back on.",
        ["balloon.lost.adapter"] = "Open CordMic on the phone and tap Start.",
        // menu
        ["menu.output"] = "Output device",
        ["menu.jitter"] = "Jitter buffer",
        ["menu.micMode"] = "Mic mode (set on the phone)…",
        ["menu.wizard"] = "Setup guide…",
        ["menu.fixRoute"] = "Fix phone network priority…",
        ["menu.openLog"] = "Open log",
        ["menu.startup"] = "Start with Windows",
        ["menu.language"] = "Language / Bahasa",
        ["menu.language.auto"] = "Automatic (Windows language)",
        ["menu.quit"] = "Exit",
        ["menu.output.autoNone"] = "Automatic (no virtual mic yet)",
        ["menu.output.auto"] = "Automatic: {0}",
        ["menu.output.micFor"] = "{0}  →  mic: {1}",
        // jitter presets
        ["jitter.low"] = "Low (5 ms) – good cable, minimum latency",
        ["jitter.normal"] = "Normal (10 ms) – recommended",
        ["jitter.safe"] = "Safe (20 ms) – if audio crackles",
        // dialogs
        ["dlg.notCable"] = "\"{0}\" is not a virtual cable. Phone audio will play on this speaker/headset and will NOT appear as a microphone.\n\nUse it anyway? (useful only for a listening test)",
        ["dlg.micMode.title"] = "Mic mode",
        ["dlg.micMode.body"] = "The microphone mode is chosen in the CordMic phone app (before tapping Start):\n\n" +
            "• Voice Recognition – lowest latency, natural sound. Recommended for gaming.\n" +
            "• Unprocessed – raw audio without filters (if the phone supports it).\n" +
            "• Voice Communication – phone noise suppression + echo cancellation, slightly higher latency.\n\n" +
            "Tip: with Voice Communication, turn off Noise Suppression/Echo Cancellation in Discord so audio is not processed twice.",
        ["dlg.noScript"] = "The repair script was not found. Reinstall CordMic with the installer.",
        ["dlg.startupFailed"] = "Could not change the startup setting: {0}",
        // wizard
        ["wiz.title"] = "CordMic – Setup guide",
        ["wiz.next"] = "Next",
        ["wiz.back"] = "Back",
        ["wiz.finish"] = "Finish",
        ["wiz.skip"] = "Skip for now",
        ["wiz.step"] = "Step {0} of 3",
        ["wiz.cable.title"] = "Install a virtual mic",
        ["wiz.cable.body"] = "CordMic sends the phone's audio into a virtual audio cable. Discord, Zoom and games read it as a microphone named \"CABLE Output\".",
        ["wiz.cable.open"] = "Open the VB-CABLE download page",
        ["wiz.cable.s1"] = "Download the VB-CABLE Driver package (free) from the official VB-Audio site.",
        ["wiz.cable.s2"] = "Extract the zip, right-click VBCABLE_Setup_x64.exe → Run as administrator → Install Driver.",
        ["wiz.cable.s3"] = "Come back here. This page checks automatically. If it is not detected after a minute, restart the PC.",
        ["wiz.cable.other"] = "Already have another virtual cable (VoiceMeeter, VAC)? CordMic recognizes those too.",
        ["wiz.cable.ok"] = "Detected: {0}. Ready to continue.",
        ["wiz.cable.missing"] = "No virtual mic yet. Install VB-CABLE, then wait a moment.",
        ["wiz.phone.title"] = "Connect the phone",
        ["wiz.phone.s1"] = "Connect the phone to the PC with a USB data cable (not a charge-only cable).",
        ["wiz.phone.s2"] = "On the phone: Settings → Connections → Mobile Hotspot and Tethering → USB tethering → turn on.",
        ["wiz.phone.s3"] = "Open the CordMic app on the phone, choose Voice Recognition mode, then tap Start.",
        ["wiz.phone.note"] = "Samsung turns USB tethering off every time the cable is unplugged. After replugging, turn the toggle back on – the PC reconnects by itself. No USB debugging or Wi-Fi needed.",
        ["wiz.phone.ok"] = "Connected. Phone audio is coming in – try speaking.",
        ["wiz.phone.silent"] = "Connected, but no audio yet. Tap Start on the phone.",
        ["wiz.phone.detected"] = "Phone detected ({0}). Waiting for Start in the phone app…",
        ["wiz.phone.waiting"] = "Waiting for the cable and USB tethering…",
        ["wiz.apps.title"] = "Choose the mic in your apps",
        ["wiz.apps.s1"] = "Discord: User Settings → Voice & Video → Input Device → CABLE Output (VB-Audio Virtual Cable).",
        ["wiz.apps.s2"] = "Zoom: Settings → Audio → Microphone → CABLE Output.",
        ["wiz.apps.s3"] = "Games without a mic option: make CABLE Output the default recording device in Windows.",
        ["wiz.apps.sound"] = "Open Windows Sound settings",
        ["wiz.apps.note"] = "Speak now: the bar below should move. CordMic keeps running in the tray (bottom right) – a green icon means connected. Right-click the icon to change the jitter buffer or device.",
    };

    public static readonly IReadOnlyDictionary<string, string> Id = new Dictionary<string, string>
    {
        ["status.starting"] = "Memulai…",
        ["status.portBusy.title"] = "Port sibuk",
        ["status.portBusy.detail"] = "Port UDP {0} dipakai aplikasi lain (mis. receiver Python). Tutup lalu tunggu.",
        ["status.portBusy.fallback"] = "Tidak bisa membuka port UDP.",
        ["status.portBusy.tip"] = "CordMic: port UDP sibuk\nTutup aplikasi lain yang memakai port 50005",
        ["status.outputGone.title"] = "Perangkat output hilang",
        ["status.outputGone.detail"] = "\"{0}\" tidak ditemukan. Pilih lagi di menu Perangkat output.",
        ["status.outputGone.tip"] = "CordMic: perangkat output tidak ada\nPilih ulang di menu",
        ["status.noCable.title"] = "Mic virtual belum ada",
        ["status.noCable.detail"] = "Pasang VB-CABLE dulu. Buka Panduan penyiapan.",
        ["status.noCable.tip"] = "CordMic: mic virtual belum terpasang\nKlik dua kali untuk panduan",
        ["status.audioError.title"] = "Output audio error",
        ["status.audioError.fallback"] = "Membuka perangkat audio…",
        ["status.audioError.tip"] = "CordMic: output audio error\nMencoba lagi otomatis",
        ["status.waiting.title"] = "Menunggu HP",
        ["status.waiting.detail"] = "Colok kabel USB dan nyalakan Tethering USB di HP.",
        ["status.waiting.tip"] = "CordMic: menunggu HP\nColok kabel & nyalakan Tethering USB",
        ["status.detected.title"] = "HP terdeteksi",
        ["status.detected.detail"] = "Adapter {0} ({1}). Buka CordMic di HP lalu tekan Start.",
        ["status.detected.tip"] = "CordMic: HP terdeteksi\nBuka CordMic di HP, tekan Start",
        ["status.connected.tip"] = "CordMic: terhubung\nLatensi ~{0} ms (sisi PC) · RTT {1} ms\nLoss {2}% · buffer {3} ms",
        ["status.silent.title"] = "Terhubung, audio berhenti",
        ["status.silent.detail"] = "HP tersambung tapi tidak mengirim suara. Cek aplikasi di HP.",
        ["status.silent.tip"] = "CordMic: terhubung tapi tidak ada suara\nCek aplikasi di HP",
        ["status.viaPhone.title"] = "Terhubung (cek jaringan)",
        ["status.viaPhone.detail"] = "Internet PC lewat HP. Pakai menu \"Perbaiki prioritas jaringan HP\" agar game tetap pakai LAN/Wi-Fi.",
        ["status.viaPhone.tipSuffix"] = "\n! Internet lewat HP",
        ["status.connected.title"] = "Terhubung",
        ["status.connected.detail"] = "Latensi ~{0} ms sisi PC, RTT {1} ms, loss {2}% (10 dtk).",
        ["balloon.lost.title"] = "Mic HP terputus",
        ["balloon.lost.noAdapter"] = "Cek kabel dan nyalakan lagi Tethering USB di HP.",
        ["balloon.lost.adapter"] = "Buka CordMic di HP dan tekan Start.",
        ["menu.output"] = "Perangkat output",
        ["menu.jitter"] = "Jitter buffer",
        ["menu.micMode"] = "Mode mic (diatur di HP)…",
        ["menu.wizard"] = "Panduan penyiapan…",
        ["menu.fixRoute"] = "Perbaiki prioritas jaringan HP…",
        ["menu.openLog"] = "Buka log",
        ["menu.startup"] = "Jalan saat Windows mulai",
        ["menu.language"] = "Language / Bahasa",
        ["menu.language.auto"] = "Otomatis (bahasa Windows)",
        ["menu.quit"] = "Keluar",
        ["menu.output.autoNone"] = "Otomatis (belum ada mic virtual)",
        ["menu.output.auto"] = "Otomatis: {0}",
        ["menu.output.micFor"] = "{0}  →  mic: {1}",
        ["jitter.low"] = "Rendah (5 ms) – kabel bagus, latensi minimum",
        ["jitter.normal"] = "Normal (10 ms) – disarankan",
        ["jitter.safe"] = "Aman (20 ms) – kalau suara putus-putus",
        ["dlg.notCable"] = "\"{0}\" bukan kabel virtual. Suara HP akan keluar di speaker/headset ini dan TIDAK muncul sebagai mikrofon.\n\nTetap pakai? (berguna hanya untuk tes dengar)",
        ["dlg.micMode.title"] = "Mode mic",
        ["dlg.micMode.body"] = "Mode mikrofon dipilih di aplikasi CordMic di HP (sebelum menekan Start):\n\n" +
            "• Voice Recognition – latensi paling rendah, suara natural. Disarankan untuk game.\n" +
            "• Unprocessed – mentah tanpa filter (kalau HP mendukung).\n" +
            "• Voice Communication – peredam bising + echo cancel dari HP, latensi sedikit lebih tinggi.\n\n" +
            "Tips: kalau sudah pakai Voice Communication, matikan Noise Suppression/Echo Cancellation di Discord supaya tidak diproses dua kali.",
        ["dlg.noScript"] = "Skrip perbaikan tidak ditemukan. Pasang ulang CordMic lewat installer.",
        ["dlg.startupFailed"] = "Gagal mengubah startup: {0}",
        ["wiz.title"] = "CordMic – Panduan penyiapan",
        ["wiz.next"] = "Lanjut",
        ["wiz.back"] = "Kembali",
        ["wiz.finish"] = "Selesai",
        ["wiz.skip"] = "Lewati dulu",
        ["wiz.step"] = "Langkah {0} dari 3",
        ["wiz.cable.title"] = "Pasang mic virtual",
        ["wiz.cable.body"] = "CordMic mengirim suara HP ke kabel audio virtual. Discord, Zoom, dan game membacanya sebagai mikrofon bernama \"CABLE Output\".",
        ["wiz.cable.open"] = "Buka halaman unduhan VB-CABLE",
        ["wiz.cable.s1"] = "Unduh paket VB-CABLE Driver (gratis) dari situs resmi VB-Audio.",
        ["wiz.cable.s2"] = "Ekstrak zip, klik kanan VBCABLE_Setup_x64.exe → Run as administrator → Install Driver.",
        ["wiz.cable.s3"] = "Kembali ke sini. Halaman ini mengecek otomatis. Kalau belum terdeteksi setelah 1 menit, restart PC.",
        ["wiz.cable.other"] = "Sudah punya kabel virtual lain (VoiceMeeter, VAC)? CordMic juga mengenalinya.",
        ["wiz.cable.ok"] = "Terdeteksi: {0}. Siap lanjut.",
        ["wiz.cable.missing"] = "Belum ada mic virtual. Pasang VB-CABLE, lalu tunggu sebentar.",
        ["wiz.phone.title"] = "Sambungkan HP",
        ["wiz.phone.s1"] = "Colok HP ke PC dengan kabel USB data (bukan kabel charge saja).",
        ["wiz.phone.s2"] = "Di HP: Pengaturan → Koneksi → Hotspot Seluler dan Tethering → Tethering USB → nyalakan.",
        ["wiz.phone.s3"] = "Buka aplikasi CordMic di HP, pilih mode Voice Recognition, lalu tekan Start.",
        ["wiz.phone.note"] = "Samsung mematikan Tethering USB setiap kabel dicabut. Setelah colok ulang, nyalakan lagi toggle-nya – PC akan tersambung sendiri. Tidak perlu USB debugging atau Wi-Fi.",
        ["wiz.phone.ok"] = "Terhubung. Suara HP masuk – coba bicara.",
        ["wiz.phone.silent"] = "Terhubung, tapi belum ada suara. Tekan Start di HP.",
        ["wiz.phone.detected"] = "HP terdeteksi ({0}). Menunggu tombol Start di aplikasi HP…",
        ["wiz.phone.waiting"] = "Menunggu kabel dan Tethering USB…",
        ["wiz.apps.title"] = "Pilih mic di aplikasi",
        ["wiz.apps.s1"] = "Discord: User Settings → Voice & Video → Input Device → CABLE Output (VB-Audio Virtual Cable).",
        ["wiz.apps.s2"] = "Zoom: Settings → Audio → Microphone → CABLE Output.",
        ["wiz.apps.s3"] = "Game tanpa pilihan mic: jadikan CABLE Output perangkat rekaman default di Windows.",
        ["wiz.apps.sound"] = "Buka pengaturan Sound Windows",
        ["wiz.apps.note"] = "Bicara sekarang: bar di bawah harus bergerak. CordMic tetap jalan di tray (pojok kanan bawah) – ikon hijau berarti terhubung. Klik kanan ikon untuk ganti jitter buffer atau perangkat.",
    };
}
