<p align="center"><img src="docs/assets/logo.png" width="112" height="112" alt="Logo CordMic"></p>

# CordMic

[English](README.md) | **Bahasa Indonesia**

CordMic menjadikan HP Android sebagai mikrofon PC berlatensi rendah lewat **kabel USB**. CordMic memakai fitur bawaan
Android **USB tethering**, jadi tidak perlu Wi-Fi, tidak perlu USB debugging/ADB, dan tidak perlu driver di HP.
Suara dikirim dari HP ke aplikasi tray Windows, lalu diputar ke kabel audio virtual yang bisa dipakai Discord, Zoom,
dan game sebagai mikrofon.

- **Hanya kabel** – HP muncul sebagai adapter jaringan kabel (RNDIS/NCM); audio tidak lewat Wi-Fi atau internet.
- **Dirancang untuk latensi rendah** – PCM 48 kHz mono dalam paket 5 ms, capture AAudio low-latency, jitter buffer
  kecil, dan output WASAPI. Target rancangannya sekitar **30–45 ms end-to-end** (lihat [Latensi](#latensi)).
- **Jalan di background** – foreground service Android dengan notifikasi permanen; aplikasi tray Windows yang
  mendeteksi HP, menyambung ulang otomatis, dan bisa ikut jalan saat Windows mulai.
- **Installer Windows plug & play** – aturan firewall, prioritas adapter HP (supaya game tetap pakai LAN/Wi-Fi),
  dan panduan penyiapan 3 langkah.
- **Bahasa Inggris dan Indonesia** – aplikasi Android, aplikasi Windows, dan installer mengikuti bahasa sistem;
  menu tray Windows punya pilihan *Language / Bahasa*.

Unduhan: lihat [Releases](https://github.com/Lukecinate/cordmic/releases/latest).

---

## Isi
- [Tangkapan layar](#tangkapan-layar)
- [Kebutuhan](#kebutuhan)
- [Pasang & mulai cepat](#pasang--mulai-cepat)
- [Aplikasi Windows](#aplikasi-windows)
- [Aplikasi Android](#aplikasi-android)
- [Troubleshooting](#troubleshooting)
- [Keterbatasan yang diketahui](#keterbatasan-yang-diketahui)
- [Arsitektur & protokol](#arsitektur--protokol)
- [Latensi](#latensi)
- [Build dari source](#build-dari-source)
- [Receiver Python (alat developer)](#receiver-python-alat-developer)
- [Upgrade dari UsbMic](#upgrade-dari-usbmic)
- [Roadmap](#roadmap)
- [Lisensi](#lisensi)

## Tangkapan layar

**Aplikasi Android.** Render asli dari layout aplikasi, dibuat oleh test screenshot Robolectric
(`./gradlew testDebugUnitTest -PrecordScreenshots`) dengan nilai contoh; bukan foto dari HP fisik.

| Menunggu USB tethering | Mencari PC | Streaming ke PC |
|---|---|---|
| ![Menunggu](docs/screenshots/android-waiting.png) | ![Mencari](docs/screenshots/android-searching.png) | ![Streaming](docs/screenshots/android-streaming.png) |

| Detail dibuka | Pengaturan (diam) | Mikrofon tidak tersedia |
|---|---|---|
| ![Detail](docs/screenshots/android-details.png) | ![Pengaturan](docs/screenshots/android-settings.png) | ![Error](docs/screenshots/android-error.png) |

| Tema gelap | Bahasa Indonesia | Layar kecil (320 dp) |
|---|---|---|
| ![Gelap](docs/screenshots/android-dark.png) | ![Indonesia](docs/screenshots/android-streaming-id.png) | ![Kecil](docs/screenshots/android-small.png) |

Lanskap: [android-landscape.png](docs/screenshots/android-landscape.png).

**Aplikasi Windows.** *Pratinjau desain*: render HTML yang dibuat dari token warna, font, dan tabel teks aplikasi
Windows dengan nilai contoh (Chrome headless). Ini bukan tangkapan layar aplikasi WinForms yang sedang berjalan.

| Panduan, streaming | Panduan, menunggu | Menu tray dan tooltip |
|---|---|---|
| ![Panduan Windows, streaming (pratinjau desain)](docs/screenshots/windows-wizard-streaming-id-preview.png) | ![Panduan Windows, menunggu (pratinjau desain)](docs/screenshots/windows-wizard-waiting-preview.png) | ![Tray Windows (pratinjau desain)](docs/screenshots/windows-tray-preview.png) |

Lainnya: [langkah mic virtual](docs/screenshots/windows-wizard-cable-preview.png),
[versi Inggris](docs/screenshots/windows-wizard-streaming-preview.png).

## Kebutuhan

| | |
|---|---|
| PC | Windows 10 atau 11, 64-bit (x64) |
| HP | Android 10 atau lebih baru (minSdk 29), mendukung USB tethering |
| Kabel | Kabel USB **data** (kabel khusus charge tidak bisa) |
| Mic virtual | [VB-CABLE](https://vb-audio.com/Cable/) (gratis, dipasang terpisah) |

## Pasang & mulai cepat

Dari [rilis terbaru](https://github.com/Lukecinate/cordmic/releases/latest) unduh `CordMic-Setup-<versi>.exe`
(Windows) dan `CordMic-<versi>-release.apk` (Android).

1. **Pasang di PC.** Jalankan `CordMic-Setup-<versi>.exe` dan setujui UAC. Installer belum ditandatangani
   (code signing), jadi SmartScreen bisa menampilkan "Windows protected your PC": klik **More info → Run anyway**.
   Saat CordMic berjalan, panduan penyiapan membantu memasang **VB-CABLE** (membuka halaman unduhan resmi dan
   mendeteksi driver secara otomatis).
2. **Pasang di HP lalu sambungkan.** Salin APK ke HP dan buka (izinkan "Install unknown apps" untuk aplikasi
   pengelola file; di Samsung, matikan sementara Auto Blocker jika aktif). Colok kabel USB, nyalakan
   **Pengaturan → Koneksi → Hotspot Seluler dan Tethering → Tethering USB**, buka CordMic, lalu ketuk **Mulai**.
   HP menampilkan **Streaming ke PC** dan ikon tray berubah teal.
3. **Pilih mic di aplikasi.** Pilih **CABLE Output (VB-Audio Virtual Cable)** sebagai input, misalnya Discord:
   *User Settings → Voice & Video → Input Device*; Zoom: *Settings → Audio → Microphone*. Untuk game tanpa pilihan
   mic, jadikan CABLE Output perangkat rekaman default di pengaturan Sound Windows.

Yang dilakukan installer:
- memasang ke `C:\Program Files\CordMic` dengan shortcut di Start menu (opsional: jalan saat Windows mulai,
  shortcut di Desktop);
- membuat aturan firewall masuk **CordMic (UDP 50005)**, hanya subnet lokal, untuk semua profil (jaringan tethering
  biasanya dianggap "Public");
- membuat scheduled task `\CordMic\SetTetherMetric` (jalan sebagai SYSTEM saat boot, login, dan saat jaringan
  berubah) yang memberi adapter HP **metric 9999**, supaya internet tetap lewat LAN/Wi-Fi dan HP hanya membawa audio.

Copot pemasangan lewat *Settings → Apps → CordMic → Uninstall*: menghapus aplikasi, shortcut, startup, aturan
firewall, dan scheduled task. Setting dan log di `%APPDATA%` / `%LOCALAPPDATA%` sengaja dibiarkan.

## Aplikasi Windows

### Ikon tray

| Warna | Arti |
|---|---|
| Abu-abu | Menunggu USB tethering, atau *HP ditemukan* tapi *Mulai* belum diketuk |
| Teal | Streaming dari HP, suara masuk |
| Amber | Perlu perhatian: mic virtual belum ada, HP terhubung tapi tidak mengirim suara, atau internet PC lewat HP |
| Merah | Error: port UDP 50005 dipakai aplikasi lain, atau perangkat audio gagal dibuka (dicoba ulang otomatis) |

Arahkan mouse ke ikon untuk melihat perkiraan jeda sisi PC beserta rinciannya (jaringan = ½ RTT USB, rata-rata
waktu tunggu di jitter buffer, dan buffer output Windows yang terukur), serta RTT dan loss 10 detik terakhir.
Panduan penyiapan menampilkan rincian yang sama di bawah angkanya. Klik kanan untuk menu:

- **Perangkat output** – *Otomatis* (kabel virtual terbaik) atau pilih manual (VB-CABLE A–D, VAC, VoiceMeeter).
- **Mode output** – *Latensi rendah* (default: mode shared, kira-kira satu periode audio Windows yang diantrekan),
  *Stabil* (buffer Windows selalu penuh seperti sebelum 0.4.2; pakai kalau suara putus-putus saat PC sibuk) atau
  *Eksklusif* (mode eksklusif WASAPI, jeda terendah; CordMic memakai "CABLE Input" sendiri dan kembali ke Latensi
  rendah bila perangkat menolak).
- **Jitter buffer** – Rendah 5 ms / Normal 10 ms (default) / Aman 20 ms (pakai Aman kalau suara putus-putus).
- **Mode mic** – penjelasan mode capture (dipilih di aplikasi HP).
- **Panduan penyiapan**, **Perbaiki prioritas jaringan HP** (muncul hanya bila internet lewat HP; minta UAC),
  **Buka log**, **Jalan saat Windows mulai**, **Language / Bahasa** (Otomatis, English, Bahasa Indonesia), **Keluar**.

### Setting dan log

| Apa | Lokasi |
|---|---|
| Setting | `%APPDATA%\CordMic\settings.json` |
| Log aplikasi (statistik tiap 10 detik saat terhubung) | `%LOCALAPPDATA%\CordMic\logs\cordmic.log` |
| Log installer / task metric | `C:\ProgramData\CordMic\` |

## Aplikasi Android

Layarnya berisi satu kartu status dan satu tombol. Kartu menjelaskan keadaan dengan bahasa sederhana
(*Siap dimulai*, *Menunggu USB tethering*, *Mencari PC*, *Streaming ke PC*, *Mikrofon tidak tersedia*) dan memberi
satu langkah berikutnya, misalnya tombol **Buka pengaturan tethering**. Saat streaming, kartu menampilkan level
input langsung, paket terkirim, gagal kirim, dan kapan PC terakhir membalas. Waktu pulang-pergi (RTT) diukur di sisi
PC dan ditampilkan di aplikasi Windows.

1. Nyalakan USB tethering (lihat di bawah).
2. Cek **Pengaturan** (terbuka saat diam, terlipat dan terkunci saat streaming):
   - **Alamat IP PC** – kosongkan agar PC ditemukan otomatis; **Port UDP** `50005`.
   - **Mode mikrofon**:
     **Pengenalan suara** (default, pemrosesan minimal, biasanya latensi paling rendah),
     **Tanpa pemrosesan** (sinyal mentah tanpa filter), atau
     **Komunikasi suara** (peredam bising dan peredam gema; menambah latensi, cocok tanpa headset).
   - **Mesin latensi rendah (AAudio)** – aktif secara default; matikan hanya jika suara putus-putus.
3. Ketuk **Mulai**, lalu izinkan Mikrofon dan Notifikasi. Notifikasi "CordMic" (dengan tombol Hentikan) tetap tampil
   dan capture terus berjalan saat layar mati atau saat memakai aplikasi lain. **Detail** menampilkan mesin capture,
   sample rate, ukuran buffer, dan antarmuka USB.

Aplikasi mengikuti tema terang/gelap dan bahasa sistem (Inggris atau Indonesia).
4. Agar tidak dimatikan One UI: **Pengaturan → Aplikasi → CordMic → Baterai → Tidak dibatasi**, dan pastikan CordMic
   tidak ada di daftar "Aplikasi tidur".

Android 14+ hanya mengizinkan foreground service mikrofon dimulai saat aplikasi terlihat, jadi **Mulai** harus
ditekan dari layar aplikasi dan service tidak otomatis jalan setelah reboot.

### USB tethering di Samsung
1. Sambungkan HP dengan kabel USB data.
2. **Pengaturan → Koneksi → Hotspot Seluler dan Tethering → Tethering USB → ON** (toggle abu-abu sampai kabel
   tersambung). USB debugging **tidak** perlu.
3. Windows menampilkan adapter baru, biasanya "Remote NDIS based Internet Sharing Device" atau "UsbNcm".

## Troubleshooting

| Gejala | Coba |
|---|---|
| Ikon abu-abu terus, "Menunggu USB tethering" | Pakai kabel **data**. Nyalakan lagi *Tethering USB* (Samsung mematikannya tiap kabel dicabut). Di Windows *Settings → Network* harus ada adapter "Remote NDIS…" / "UsbNcm…". |
| "HP ditemukan" tapi tidak streaming | Buka CordMic di HP dan ketuk *Mulai*. Pastikan aturan firewall *CordMic (UDP 50005)* ada (jalankan ulang installer bila hilang). |
| Merah, "Port sibuk" | Receiver lain (misalnya `cordmic_receiver` Python) masih jalan. Tutup; CordMic mencoba lagi tiap 5 detik. |
| Amber, "Mic virtual belum ada" | Pasang VB-CABLE (langkah 1 panduan); restart PC bila belum muncul. |
| Suara putus-putus / robot | *Jitter buffer → Aman (20 ms)*; kalau masih, *Mode output → Stabil*. Lihat `lost` / `underrun` di log. Hindari hub USB; pakai port langsung di PC. |
| Ping game naik setelah colok HP | Menu *Perbaiki prioritas jaringan HP* (atau jalankan `C:\Program Files\CordMic\tools\set-tether-metric.ps1` sebagai admin). Alternatif: matikan data seluler di HP. |
| Discord tidak mendengar apa-apa | Input Device harus **CABLE Output** (bukan CABLE Input). Bar level di panduan langkah 3 harus bergerak saat bicara. |
| Suara terlalu diproses / terasa delay | Pilih *Voice Recognition* di HP. Kalau memakai *Voice Communication*, matikan noise suppression dan echo cancellation di Discord supaya tidak diproses dua kali. |

Perbaikan metric manual (PowerShell sebagai admin):
```powershell
Get-NetAdapter                                                  # cari adapter HP, mis. "Ethernet 3"
Set-NetIPInterface -InterfaceAlias "Ethernet 3" -InterfaceMetric 9999
Get-NetRoute 0.0.0.0/0                                          # route default harus lewat LAN/Wi-Fi
```

## Keterbatasan yang diketahui

- **Installer Windows belum ditandatangani.** Installer dan `CordMic.exe` belum memakai tanda tangan Authenticode,
  jadi SmartScreen memperingatkan saat pertama dijalankan (*More info → Run anyway*). Cocokkan unduhan dengan
  `SHA256SUMS.txt` di halaman rilis.
- **USB tethering harus dinyalakan manual.** Android tidak mengizinkan aplikasi biasa menyalakan tethering, dan
  Samsung mematikannya setiap kabel dicabut. Nyalakan lagi toggle-nya setelah colok ulang; sisi PC menyambung sendiri.
- **Butuh VB-CABLE.** Windows hanya menampilkan mikrofon yang berasal dari driver audio, dan aplikasi user-mode tidak
  bisa membuatnya. Karena itu CordMic memutar suara ke kabel virtual yang sudah ada (lihat [Roadmap](#roadmap)).
- **Tanda tangan APK.** Mulai 0.3.1, APK release ditandatangani dengan release key milik proyek (sertifikat
  `CN=CordMic, O=CordMic contributors`, SHA-256
  `2c:da:64:ae:df:4e:ba:6f:9d:4a:fa:06:e6:85:43:04:65:a6:0d:5d:2a:25:ef:99:ee:f3:cc:16:3e:47:1c:8b`). APK
  didistribusikan di luar Google Play, jadi Android meminta izin instal dari sumber tidak dikenal. Versi 0.3.0
  ditandatangani dengan debug key; uninstall dulu sebelum memasang 0.3.1 (Android menolak update dengan key berbeda).
- **RTT hanya di sisi PC.** HP menjawab PING dari PC tetapi tidak mengukurnya, jadi aplikasi HP menampilkan
  berapa lama sejak balasan PC terakhir, bukan angka pulang-pergi.
- **Belum ada kompensasi clock drift.** Pada sesi panjang, jitter buffer menangani perbedaan jam HP/PC dengan
  membuang paket saat buffer melewati batas, yang bisa menimbulkan klik singkat.
- **Aturan start Android 14+.** Capture harus dimulai dari aplikasi yang sedang terlihat; tidak jalan otomatis saat boot.

## Arsitektur & protokol

```
┌──────────────── HP Android ──────────────────┐   kabel USB     ┌──────────────── PC Windows ──────────────────┐
│ Mic → AAudio (LOW_LATENCY, 48 kHz, mono,     │ (USB tethering, │ Aplikasi tray CordMic (.NET 8)               │
│       16-bit, frame 5 ms = 240 sampel)       │  RNDIS/NCM =    │  UDP :50005 → jitter buffer (default 10 ms)  │
│   → header 20 byte (seq + timestamp)         │  link jaringan) │  → WASAPI (NAudio)                           │
│   → UDP ke PC                                │ ──────────────▶ │  → "CABLE Input" (VB-CABLE)                  │
│ Foreground service type=microphone           │                 │  Discord/Zoom/game memakai "CABLE Output"    │
│ + notifikasi permanen + wake lock            │ ◀── PING/HELLO  │  sebagai mikrofon                            │
└──────────────────────────────────────────────┘                 └──────────────────────────────────────────────┘
```

- **Transport.** USB tethering membuat HP muncul sebagai adapter jaringan kabel di Windows (driver RNDIS/NCM bawaan).
  Tanpa ADB, tanpa Wi-Fi, dan tidak bergantung pada mode USB debugging.
- **Discovery.** Saat tethering aktif, HP adalah gateway/DHCP server (contoh: HP `192.168.42.129`, PC
  `192.168.42.x`). Aplikasi mencari interface tethering (`rndis0` / `ncm0` / `usb0`) lalu mengirim **DISCOVER**
  broadcast di subnet itu; PC membalas **HELLO** (aplikasi Windows juga mengirim HELLO lebih dulu saat melihat
  adapter tethering), lalu HP mengirim audio unicast ke PC. IP PC juga bisa diisi manual.
- **Keep-alive dan reconnect.** PC mengirim **PING** tiap 500 ms dan HP membalas **PONG** (juga untuk mengukur RTT).
  Kalau HP tidak mendengar PC selama 3 detik, atau socket error setelah kabel dicabut, HP kembali ke discovery.
  Kalau stream AAudio terputus (misalnya route audio berubah), stream dibuka ulang.
- **Header** (20 byte, little-endian; identik di `Protocol.kt`, `protocol.py`, dan `CordMic.Core`):

  | Offset | Ukuran | Field |
  |---|---|---|
  | 0 | 2 | magic `"UM"` |
  | 2 | 1 | versi (1) |
  | 3 | 1 | tipe: 1 AUDIO, 2 DISCOVER, 3 HELLO, 4 PING, 5 PONG, 6 BYE |
  | 4 | 4 | nomor urut (uint32, wrap) |
  | 8 | 8 | timestamp µs (jam monotonic pengirim) |
  | 16 | 2 | jumlah frame PCM |
  | 18 | 2 | flags: bit 0–3 codec (0 = PCM s16le), bit 4–7 channel − 1, bit 8–11 sample rate (0 = 48 kHz) |

  Payload AUDIO berisi PCM mentah: 240 frame × 2 byte = 480 byte per 5 ms (~100 kbit/s).
- **Jitter buffer.** Menyusun ulang paket berdasarkan nomor urut; playback mulai setelah target terisi (Rendah 5 /
  Normal 10 / Aman 20 ms). Paket hilang diganti hening dan paket terlambat dibuang. Setiap ~0,5 dtk buffer
  memeriksa isi terkecil tepat setelah dibaca; bila tetap di atas target (lonjakan paket, atau jam HP sedikit lebih
  cepat), dua paket berurutan diputar sebagai satu dengan crossfade 5 ms sampai kelebihannya habis. Batas keras
  tetap membuang paket terlama bila buffer melebihi maksimum.
- **Output.** Loop render WASAPI berbasis event di thread MMCSS "Pro Audio". Pada mode *Latensi rendah* yang
  diantrekan hanya sekitar satu periode perangkat Windows ditambah 3 ms, bukan seluruh buffer endpoint.
- **RTT.** PC mengirim PING dari socket UDP kedua di port acak dengan thread sendiri; HP membalas ke pengirim,
  sehingga PONG tidak pernah mengantre di belakang paket audio.

### Struktur repositori

```
cordmic/
├── android/                      proyek Gradle (Kotlin + C++/AAudio)
│   └── app/src/main/
│       ├── AndroidManifest.xml   izin + foregroundServiceType="microphone"
│       ├── cpp/aaudio_capture.cpp  capture AAudio low-latency (JNI)
│       ├── java/dev/cordmic/     MainActivity, MicService, Streamer, Capture, TetherNet, Protocol
│       └── res/values[-in]/      teks bahasa Inggris dan Indonesia (+ values-night untuk tema gelap)
├── docs/                         tangkapan layar, logo (assets/), THIRD_PARTY_NOTICES.md
├── third_party/fonts/            Plus Jakarta Sans + lisensi OFL
├── windows-app/                  aplikasi Windows, C# / .NET 8
│   ├── src/CordMic.Core/         protokol, jitter buffer, receiver UDP, setting, tabel teks
│   ├── src/CordMic.App/          aplikasi tray WinForms + WASAPI (NAudio) + panduan penyiapan
│   ├── src/CordMic.Harness/      receiver console untuk tes loopback
│   ├── tests/CordMic.Tests/      test xUnit (termasuk interop dengan sim_sender.py)
│   ├── installer/                cordmic.nsi + tools/*.ps1 (firewall, metric adapter)
│   └── build.sh                  test → publish win-x64 → installer
└── windows-receiver/             receiver referensi Python + simulator HP (sim_sender.py)
```

## Latensi

Pipeline ini **dirancang untuk sekitar 30–45 ms end-to-end**. Ini anggaran rancangan, bukan hasil pengukuran:
capture AAudio ~5–10 ms + paket 5 ms + link USB 1–2 ms + jitter buffer 10 ms + WASAPI/VB-CABLE ~10–20 ms.
Latensi sebenarnya bergantung pada HP, mode mic, dan sistem audio PC.

**Jeda sisi PC yang ditampilkan aplikasi.** Jaringan (½ RTT) + jitter buffer (rata-rata terhadap waktu) + output
Windows (rata-rata isi buffer WASAPI CordMic yang terukur). Tidak termasuk capture di HP, periode engine audio
Windows, buffer internal VB-CABLE, dan aplikasi perekam. Sebelum 0.4.2 bagian output memakai angka tetap 10 ms
padahal NAudio sebenarnya menjaga buffer yang lebih besar tetap penuh, dan bagian buffer hanya satu pembacaan
sesaat; jadi angka 0.4.1 dan 0.4.2 tidak bisa dibandingkan langsung.

Seorang pengguna mengukur jeda sisi PC 25–45 ms dengan 0.4.1. Versi 0.4.2 menyasar penyebab yang ditemukan di kode
(antrean yang bisa naik sampai 3× target jitter, buffer WASAPI yang selalu penuh, dan underrun berulang setelah
lanjut); efeknya di perangkat nyata belum diukur. Untuk jeda terendah: *Jitter buffer → Rendah*, *Mode output →
Latensi rendah* (lalu coba *Eksklusif*), dan atur "CABLE Input" serta "CABLE Output" ke 48000 Hz di pengaturan
Sound Windows agar tidak terjadi resampling.

Cara mengukur di perangkat Anda: di OBS tambahkan dua *Audio Input Capture*, (a) "CABLE Output" dan (b) mic fisik
lain (headset/webcam). Rekam, tepuk tangan di dekat **kedua** mic, buka rekaman di Audacity, lalu ukur jarak antar
puncak tepukan. Selisihnya adalah latensi CordMic dikurangi latensi mic pembanding. Ulangi dengan preset Rendah.

## Build dari source

### Aplikasi Windows dan installer
Butuh .NET 8 SDK, NSIS 3 (`makensis`), dan Python 3 (untuk test interop). Bisa di Windows (Git Bash/WSL), Linux,
atau macOS:
```bash
cd windows-app && ./build.sh
```
Menjalankan test, mem-publish `windows-app/publish/CordMic.exe` (win-x64, self-contained, single file), dan menulis
`dist/CordMic-Setup-<versi>.exe`.

### Aplikasi Android
Butuh JDK 17, Android SDK Platform 34 dan Build-Tools 34, NDK 26.1.10909125, dan CMake 3.22.1 (Android Studio atau
`sdkmanager`). Dibangun dengan AGP 8.5.2, Gradle 8.7, dan Kotlin 1.9.24.
```bash
cd android
echo "sdk.dir=/path/ke/Android/Sdk" > local.properties   # Android Studio membuat ini otomatis
./gradlew testDebugUnitTest lint assembleRelease
```
Tanda tangan release dibaca dari file properties di luar repositori (`storeFile`, `storePassword`, `keyAlias`,
`keyPassword`), lewat `CORDMIC_SIGNING_PROPERTIES=/path/file` atau `-PcordmicSigning=/path/file`. Tanpa itu, build
release lokal ditandatangani dengan debug key.

### Test
| Suite | Perintah | Cakupan |
|---|---|---|
| Python (24 test) | `cd windows-receiver && python -m unittest discover -s tests` | protokol, jitter buffer, callback output, loopback UDP (bersih dan lossy) |
| .NET (52 test) | `cd windows-app && dotnet test` | golden vector protokol, jitter buffer dan catch-up, socket ping terpisah, aturan adapter/kabel virtual, setting dan migrasi, loopback UDP, interop dengan `sim_sender.py`, tabel teks |
| Kotlin (17 test) | `cd android && ./gradlew testDebugUnitTest` | kompatibilitas byte header dengan Python, kesamaan teks Inggris/Indonesia, helper level meter dan IP, render screenshot Robolectric (`-PrecordScreenshots` menulis ulang `docs/screenshots/android-*.png`) |

## Receiver Python (alat developer)

`windows-receiver/` berisi receiver referensi untuk pengembangan protokol dan debugging. Untuk pemakaian sehari-hari,
pasang aplikasi Windows (keduanya memakai UDP 50005, jadi jalankan salah satu saja).

```bat
cd windows-receiver
pip install -r requirements.txt
python -m cordmic_receiver --list-devices
python -m cordmic_receiver --device "CABLE Input" --jitter-ms 10
```

| Opsi | Default | Arti |
|---|---|---|
| `--device` | output default | index atau sebagian nama device (contoh `"CABLE Input"`) |
| `--jitter-ms` | 10 | target jitter buffer; turunkan ke 5 kalau statistik bersih, naikkan kalau sering `plc` / `underrun` |
| `--exclusive` | off | WASAPI exclusive: latensi output paling rendah, tapi device dikunci |
| `--blocksize` | 240 | ukuran blok callback (5 ms) |
| `--channels` | 2 | mono diduplikasi ke stereo (VB-CABLE default stereo) |
| `--tray` | off | ikon tray (pystray + pillow) |
| `--log file` | – | simpan log dan statistik ke file |
| `--no-audio` | off | hanya terima paket + statistik (debug jaringan) |

Arti statistik: `lost` = gap nomor urut, `plc` = slot yang diisi hening, `late` = paket datang setelah gilirannya,
`underrun` = buffer kosong, `ovf` = paket dibuang karena buffer penuh, `jitter` = jitter kedatangan (RFC 3550),
`rtt` = round trip PING/PONG lewat USB, `est_pc_side` = buffer + latensi output + RTT/2 (sisi PC saja).

Tanpa HP, `python sim_sender.py --host 127.0.0.1 --seconds 10` mengirim nada uji 440 Hz (`--loss` dan `--jitter-ms`
menyimulasikan link buruk).

Untuk hasil terbaik, set VB-CABLE ke 48 kHz: *Control Panel → Sound → Playback → CABLE Input → Properties →
Advanced* dan *Recording → CABLE Output → Advanced*. Jangan jadikan CABLE Input perangkat playback default.

## Upgrade dari UsbMic

CordMic sebelumnya bernama **UsbMic**. Protokol audionya tidak berubah (header `UM`, UDP 50005).

- **Windows:** jalankan installer CordMic. Installer menutup `UsbMic.exe` dan menghapus aturan firewall lama,
  scheduled task `\UsbMic\SetTetherMetric`, entri startup, shortcut, `C:\Program Files\UsbMic`, dan entri uninstall
  lama. Setting di `%APPDATA%\UsbMic` disalin ke `%APPDATA%\CordMic` saat pertama jalan.
- **Android:** nama paket berubah (`dev.usbmic` → `dev.cordmic`), jadi CordMic terpasang sebagai aplikasi baru.
  Beri izin lagi, lalu uninstall aplikasi UsbMic lama supaya keduanya tidak berebut mic.

## Roadmap

1. **Opus** (48 kHz, frame 2,5/5 ms, FEC) sebagai opsi; PCM tetap default.
2. **Kompensasi clock drift** – sisipkan atau buang satu sampel, bukan satu paket.
3. **Driver audio virtual sendiri** ("CordMic Microphone" tanpa VB-CABLE), berbasis contoh SysVAD Microsoft atau ACX
   di Windows 11. Driver kernel wajib ditandatangani Microsoft (sertifikat EV + attestation signing), karena itu
   menjadi tahap tersendiri. Windows tidak punya API user-mode untuk mikrofon virtual tingkat sistem.
4. **Code signing** (Authenticode) untuk installer dan aplikasi agar SmartScreen tidak memperingatkan.
5. Packet-loss concealment yang lebih baik.

## Lisensi

[MIT](LICENSE) © CordMic contributors. VB-CABLE adalah produk terpisah dari VB-Audio dan tidak disertakan.

Aset desain: logo CordMic adalah aset asli proyek ini (dibuat oleh `tools/brand/make_logo.py`); ikon di dalam
aplikasi memakai [Material Icons](https://fonts.google.com/icons) (gaya Round, Apache 2.0) dari Google; font UI adalah [Plus Jakarta Sans](https://github.com/tokotype/PlusJakartaSans)
(SIL Open Font License 1.1). Rincian dan pustaka lain ada di
[docs/THIRD_PARTY_NOTICES.md](docs/THIRD_PARTY_NOTICES.md).

## Pembuat

CordMic dikembangkan oleh [Lukecinate (Rozan Abid)](https://github.com/Lukecinate).
