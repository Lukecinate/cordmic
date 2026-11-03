# CordMic — HP Android jadi mic PC lewat kabel USB (prototipe)

Status: **prototipe v0.3.0** (nama baru: **CordMic**, sebelumnya *UsbMic*). Sekarang ada **aplikasi Windows plug & play** (installer `.exe`, ikon tray, panduan
penyiapan) – lihat bagian 0. Logika receiver (Python dan C#) sudah dites otomatis di Linux (unit test + loopback UDP);
aplikasi Windows-nya **belum pernah dijalankan di Windows**.
Aplikasi Android sudah **berhasil di-build** jadi APK, tapi **belum pernah dijalankan di HP sungguhan**.
Bagian "Status verifikasi" di bawah menjelaskan apa yang sudah dan belum dites.

Target: Samsung S24 FE / Galaxy S (Android 14/15) → Windows 11 Pro, untuk Discord, game, Zoom.
Syarat: **hanya kabel USB**, tanpa Wi-Fi, **tanpa USB debugging/ADB**, latensi rendah, jalan di background.

---

## 0. Aplikasi Windows plug & play (v0.3.0) – cara paling mudah

Aplikasi Windows ini menggantikan receiver Python untuk pemakaian sehari-hari. Ia jalan diam-diam di tray,
mendeteksi HP sendiri saat kabel dicolok, dan menyambung ulang otomatis. Receiver Python tetap ada untuk debugging.

### Pasang (sekali saja)
1. Jalankan **`dist/CordMic-Setup-0.3.0.exe`** (klik dua kali, setujui UAC).
   * Windows SmartScreen bisa menampilkan "Windows protected your PC" karena installer belum
     ditandatangani (in-house). Klik **More info → Run anyway**.
   * Pilihan di installer: **Jalan otomatis saat Windows mulai** (default aktif) dan *Shortcut di Desktop*.
2. Installer otomatis:
   * memasang aplikasi ke `C:\Program Files\CordMic` + shortcut di Start menu;
   * membuat aturan firewall **CordMic (UDP 50005)** (masuk, hanya subnet lokal, semua profil – jaringan
     tethering biasanya dianggap "Public");
   * membuat scheduled task `\CordMic\SetTetherMetric` (jalan sebagai SYSTEM saat jaringan baru
     tersambung, saat login, dan saat boot) yang memberi adapter HP **metric 9999**, supaya
     internet/game tetap lewat LAN/Wi-Fi dan HP hanya membawa audio.
3. Pasang **HP**: APK `dist/CordMic-0.3.0-release.apk` (lihat bagian 3). Versi 0.2.0 ke atas diperlukan agar
   PC bisa "memanggil" HP langsung (lebih andal dari broadcast saja).

### Upgrade dari UsbMic 0.2.0 (ganti nama ke CordMic)
Produk ini sebelumnya bernama **UsbMic**. Protokol audionya **tidak berubah** (header `UM`, port UDP 50005),
jadi HP dan PC versi lama/baru tetap saling kompatibel.
* **Windows:** cukup jalankan `CordMic-Setup-0.3.0.exe`. Installer otomatis menutup `UsbMic.exe`, menghapus
  aturan firewall *UsbMic (UDP 50005)*, scheduled task `\UsbMic\SetTetherMetric`, entri startup `UsbMic`,
  shortcut, folder `C:\Program Files\UsbMic`, dan entri uninstall lama. Setting di `%APPDATA%\UsbMic` **tidak
  dihapus**: saat pertama jalan, CordMic menyalinnya ke `%APPDATA%\CordMic` (jitter, perangkat output,
  status panduan tetap sama). Log lama di `%LOCALAPPDATA%\UsbMic` tidak dipindah.
* **HP:** nama paket Android berubah (`dev.usbmic` → `dev.cordmic`), jadi CordMic terpasang sebagai aplikasi
  **baru**, bukan update. Pasang `CordMic-0.3.0-release.apk`, beri izin mikrofon dan notifikasi lagi, lalu
  **uninstall aplikasi UsbMic lama** supaya tidak ada dua aplikasi yang berebut mic.

### Pemakaian pertama (panduan 3 langkah muncul otomatis)
1. **Pasang mic virtual.** CordMic tidak membawa driver sendiri; ia memakai VB-CABLE (gratis) dari situs
   resminya. Tombol *Buka halaman unduhan VB-CABLE* membuka https://vb-audio.com/Cable/. Ekstrak,
   klik kanan `VBCABLE_Setup_x64.exe` → *Run as administrator* → *Install Driver*. Panduan mengecek
   ulang tiap 2 detik dan langsung memilih "CABLE Input" begitu terdeteksi (restart PC bila belum muncul).
2. **Sambungkan HP.** Colok kabel USB data → di HP: *Pengaturan → Koneksi → Hotspot Seluler dan
   Tethering → Tethering USB* ON → buka aplikasi CordMic di HP → *Start*. Status di panduan berubah
   hijau dan bar level bergerak saat Anda bicara.
   **Samsung mematikan Tethering USB setiap kabel dicabut**: setelah colok ulang, nyalakan lagi
   toggle-nya. PC menyambung sendiri, tidak perlu menyentuh aplikasi Windows.
3. **Pilih mic di aplikasi.** Discord: *User Settings → Voice & Video → Input Device →
   CABLE Output (VB-Audio Virtual Cable)*. Zoom: *Settings → Audio → Microphone → CABLE Output*.
   Game tanpa pilihan mic: jadikan CABLE Output perangkat rekaman default (tombol di panduan membuka
   pengaturan Sound Windows).

### Ikon tray
| Warna | Arti |
|---|---|
| abu-abu | menunggu HP (kabel/tethering belum ada) atau HP terdeteksi tapi belum *Start* |
| hijau | terhubung, suara masuk |
| kuning | perlu perhatian: mic virtual belum ada, HP terhubung tapi tidak mengirim suara, atau internet PC lewat HP |
| merah | error: port UDP 50005 dipakai aplikasi lain, atau perangkat audio gagal dibuka (dicoba ulang otomatis) |

Arahkan mouse ke ikon untuk melihat **latensi perkiraan sisi PC** (½ RTT USB + jitter buffer + buffer
WASAPI 10 ms), RTT, loss 10 detik terakhir, dan isi buffer. Klik kanan untuk menu:
* **Perangkat output** – *Otomatis* (kabel virtual terbaik) atau pilih manual (VB-CABLE A–D, VAC, VoiceMeeter).
* **Jitter buffer** – Rendah 5 ms / Normal 10 ms (default) / Aman 20 ms (kalau suara putus-putus).
* **Mode mic** – penjelasan mode di HP (dipilih di aplikasi HP).
* **Panduan penyiapan**, **Perbaiki prioritas jaringan HP** (muncul hanya bila internet lewat HP; minta UAC),
  **Buka log**, **Jalan saat Windows mulai**, **Keluar**.

Setting: `%APPDATA%\CordMic\settings.json`. Log: `%LOCALAPPDATA%\CordMic\logs\cordmic.log` (statistik tiap 10 detik
saat terhubung). Log installer/metric: `C:\ProgramData\CordMic\`.

### Troubleshooting
| Gejala | Coba |
|---|---|
| Ikon abu-abu terus, "Menunggu HP" | Kabel harus kabel **data**. Nyalakan lagi *Tethering USB* (mati tiap kabel dicabut). Di Windows *Settings → Network* harus muncul adapter "Remote NDIS…"/"UsbNcm…". |
| "HP terdeteksi" tapi tidak tersambung | Buka aplikasi CordMic di HP dan tekan *Start*. Pastikan versi APK 0.2.0 ke atas (CordMic 0.3.0 disarankan). Cek aturan firewall *CordMic (UDP 50005)* ada (pasang ulang installer bila hilang). |
| Merah, "Port sibuk" | Receiver Python (`cordmic_receiver`) masih jalan. Tutup, CordMic mencoba lagi tiap 5 detik. |
| Kuning, "Mic virtual belum ada" | Pasang VB-CABLE (langkah 1), restart PC bila perlu. |
| Suara putus-putus / robot | Menu *Jitter buffer → Aman (20 ms)*. Lihat `lost`/`underrun` di log. Hindari hub USB, pakai port langsung. |
| Ping game naik setelah colok HP | Menu *Perbaiki prioritas jaringan HP* (atau jalankan `C:\Program Files\CordMic\tools\set-tether-metric.ps1` sebagai admin). Sebagai alternatif, matikan data seluler di HP. |
| Discord tidak dengar apa-apa | Input Device harus **CABLE Output** (bukan CABLE Input). Bar level di panduan langkah 3 harus bergerak. |
| Suara terlalu diproses / ada delay | Di HP pilih *Voice Recognition*; kalau memakai *Voice Communication*, matikan Noise Suppression/Echo Cancellation di Discord. |

### Copot pemasangan
*Settings → Apps → CordMic → Uninstall*: menghapus aplikasi, shortcut, startup, aturan firewall dan
scheduled task. Setting dan log di `%APPDATA%`/`%LOCALAPPDATA%` sengaja dibiarkan.

### Build sendiri (Linux)
```
cd windows-app && ./build.sh     # butuh .NET 8 SDK, NSIS 3 (makensis), python3
```
Hasil: `windows-app/publish/CordMic.exe` (win-x64 self-contained single-file) dan `dist/CordMic-Setup-0.3.0.exe`.

### Kenapa masih perlu VB-CABLE? (mic virtual sendiri = pekerjaan lanjutan)
Windows hanya menampilkan "mikrofon" yang berasal dari **driver audio**. Aplikasi biasa (user-mode) tidak
bisa membuat perangkat rekaman baru, jadi CordMic memutar suara ke kabel virtual yang sudah ada.
Untuk "CordMic Microphone" sendiri tanpa VB-CABLE ada dua jalur:
1. **Driver audio virtual sendiri** berbasis contoh Microsoft *SysVAD* atau *SimpleAudioSample*
   (Windows-driver-samples). Driver kernel ini wajib ditandatangani Microsoft: perlu **sertifikat EV**
   dan *attestation signing* lewat Partner Center (untuk distribusi umum: WHQL/HLK). Tanpa itu hanya bisa
   jalan dengan *test signing* (tidak cocok untuk PC gaming dengan anti-cheat).
2. **ACX (Audio Class eXtensions, Windows 11)** – kerangka driver audio baru di atas KMDF yang lebih
   sederhana dari PortCls/SysVAD. Tetap **driver kernel**, jadi syarat tanda tangan sama (EV + attestation).
Catatan: Windows **tidak** punya API user-mode untuk membuat mikrofon virtual yang terlihat oleh semua aplikasi
(API kamera virtual Media Foundation hanya untuk video). Jadi jalur "tanpa VB-CABLE" pasti lewat driver
bertanda tangan; bagian streaming (UDP, jitter buffer) di CordMic.Core tetap bisa dipakai, driver hanya
menerima PCM lewat IOCTL/ring buffer dari aplikasi tray.
Keputusan driver ditunda sesuai rencana ("prototipe dulu, driver diputuskan nanti").

---

## 1. Arsitektur

```
┌──────────────── HP Android ────────────────┐   kabel USB    ┌──────────────── Windows 11 ─────────────────┐
│ Mic → AAudio (LOW_LATENCY, 48 kHz, mono,   │  (USB tethering│ cordmic_receiver (Python)                    │
│       16-bit, frame 5 ms = 240 sampel)     │   RNDIS/NCM =  │  UDP :50005 → jitter buffer (default 10 ms) │
│   → header 20 byte (seq + timestamp)       │   kartu jaringan│  → WASAPI (sounddevice/PortAudio)           │
│   → UDP ke IP PC                           │──────────────▶ │  → "CABLE Input" (VB-Cable)                 │
│ Foreground service type=microphone         │                │  Discord/Zoom/game pakai "CABLE Output"     │
│ + notifikasi permanen + wakelock           │ ◀── PING/HELLO │  sebagai mikrofon                            │
└────────────────────────────────────────────┘                └─────────────────────────────────────────────┘
```

* **Transport**: USB tethering membuat HP muncul sebagai kartu jaringan kabel di Windows
  (driver RNDIS/NCM bawaan Windows 11). Tidak perlu ADB, tidak perlu Wi-Fi, dan tidak bergantung
  pada mode USB debugging yang sering berpindah atau terputus.
* **Catatan alamat IP**: saat USB tethering aktif, **HP itulah gateway/DHCP server-nya** (contoh: HP
  `192.168.42.129`, PC dapat `192.168.42.x`). Jadi aplikasi tidak mengirim ke gateway. Aplikasi
  mencari interface tethering (`rndis0`/`ncm0`/`usb0`), lalu mengirim paket **DISCOVER** broadcast di
  subnet itu. Receiver PC membalas **HELLO**, lalu HP mengirim audio langsung (unicast) ke IP PC.
  IP PC juga bisa diisi manual.
* **Auto-reconnect sederhana**: PC mengirim PING tiap 500 ms dan HP membalas PONG (dipakai juga untuk
  mengukur RTT). Kalau HP tidak mendengar PC selama 3 detik, atau kabel dicabut lalu socket error,
  HP kembali ke mode discovery. Kalau stream AAudio putus (misalnya karena route audio berubah),
  stream dibuka ulang otomatis.
* **Protokol** (little-endian, 20 byte, sama persis di `Protocol.kt` dan `protocol.py`):

  | offset | ukuran | field |
  |---|---|---|
  | 0 | 2 | magic `"UM"` |
  | 2 | 1 | versi (1) |
  | 3 | 1 | tipe: 1 AUDIO, 2 DISCOVER, 3 HELLO, 4 PING, 5 PONG, 6 BYE |
  | 4 | 4 | seq (uint32, wrap) |
  | 8 | 8 | timestamp µs (jam monotonic pengirim) |
  | 16 | 2 | jumlah frame PCM |
  | 18 | 2 | flags: bit 0-3 codec (0 = PCM s16le), bit 4-7 channel-1, bit 8-11 sample rate (0 = 48 kHz) |

  Payload AUDIO berisi PCM mentah: 240 frame × 2 byte = 480 byte per 5 ms (~100 kbit/s).
* **Jitter buffer** (`jitter.py`): menyusun ulang paket berdasarkan seq. Playback mulai setelah
  target terisi (default 10 ms = 2 paket). Paket yang hilang diganti hening (PLC sederhana).
  Paket terlambat dibuang. Kalau buffer melebihi batas maksimum, paket terlama dibuang supaya
  latensi tidak terus naik (misalnya karena clock drift).

### Struktur folder

```
usbmic/                           (nama folder proyek tidak diubah)
├── README.md
├── dist/                         CordMic-Setup-0.3.0.exe (installer Windows) + APK (debug + release)
├── windows-app/                  aplikasi Windows C#/.NET 8 (tray + wizard + installer NSIS)
│   ├── src/CordMic.Core/          protokol, jitter buffer, receiver UDP (port dari Python, byte-kompatibel)
│   ├── src/CordMic.App/           aplikasi tray WinForms + WASAPI (NAudio) + panduan penyiapan
│   ├── src/CordMic.Harness/       receiver console untuk tes loopback di Linux
│   ├── tests/CordMic.Tests/       33 unit/loopback test (xUnit), termasuk interop dengan sim_sender.py
│   ├── installer/                cordmic.nsi + tools/*.ps1 (firewall, prioritas adapter HP)
│   └── build.sh                  test → publish win-x64 → installer
├── android/                      proyek Gradle (Kotlin + C++/AAudio)
│   └── app/src/main/
│       ├── AndroidManifest.xml   izin + service foregroundServiceType="microphone"
│       ├── cpp/aaudio_capture.cpp  capture AAudio low-latency (JNI)
│       ├── java/dev/cordmic/
│       │   ├── MainActivity.kt   UI: IP/port, mode mic, Start/Stop, status
│       │   ├── MicService.kt     foreground service + notifikasi + wakelock
│       │   ├── Streamer.kt       loop capture→UDP, discovery, PING/PONG, reconnect
│       │   ├── Capture.kt        AAudio (utama) + AudioRecord (cadangan)
│       │   ├── TetherNet.kt      deteksi interface USB tethering
│       │   └── Protocol.kt       header 20 byte
│       └── res/…
└── windows-receiver/
    ├── cordmic_receiver/          protocol, jitter, server (UDP), audio_out (WASAPI), tray, CLI
    ├── tests/                    unit test + loopback test
    ├── sim_sender.py             "HP palsu" (nada sinus) untuk tes tanpa HP
    ├── cordmic_tray.pyw           launcher klik-ganda (tanpa console, ikon tray)
    ├── run_receiver.bat
    ├── setup_firewall.ps1
    └── requirements.txt
```

---

## 2. Mengaktifkan USB tethering (Samsung)

1. Colokkan HP ke PC dengan kabel USB **data** (bukan kabel charge-only).
2. HP: **Pengaturan → Koneksi → Hotspot Seluler dan Tethering → Tethering USB → ON**.
   (Toggle ini abu-abu sampai kabel tersambung.) USB debugging **tidak** perlu dinyalakan.
3. Windows: di *Settings → Network & internet* muncul adapter baru (biasanya "Ethernet X",
   *Remote NDIS based Internet Sharing Device* atau *UsbNcm*).
4. **Penting untuk gaming: jangan sampai internet PC lewat HP.** Windows bisa memakai HP sebagai
   jalur internet, sehingga ping game naik dan kuota HP terpakai. Jalankan di PowerShell (Admin):
   ```powershell
   Get-NetAdapter                    # cari nama adapter HP, mis. "Ethernet 3"
   Set-NetIPInterface -InterfaceAlias "Ethernet 3" -InterfaceMetric 9999
   ```
   Dengan metric tinggi, Windows tetap memakai LAN/Wi-Fi utama untuk internet, sedangkan HP hanya
   dipakai untuk link audio. Cek dengan `Get-NetRoute 0.0.0.0/0`. Catatan: kalau Windows membuat
   adapter baru (misalnya port USB berbeda), metric perlu diset lagi.
5. Firewall: jaringan tethering biasanya dianggap "Public". Izinkan UDP 50005 dengan menjalankan
   `windows-receiver\setup_firewall.ps1` sebagai Admin.

> Keterbatasan Android: aplikasi biasa **tidak bisa** menyalakan USB tethering sendiri (butuh izin
> sistem). Setelah kabel dicabut lalu dicolok lagi, Samsung biasanya mematikan tethering, jadi toggle
> perlu dinyalakan ulang manual. Ide otomatisasi ada di roadmap.

---

## 3. Aplikasi Android

### Install APK siap pakai
APK ada di `dist/`:
* `dist/CordMic-0.3.0-release.apk` (tanpa debug, **ditandatangani dengan debug key**, khusus prototipe)
* `dist/CordMic-0.3.0-debug.apk`

Cara install: salin APK ke HP (kabel/MTP atau cara lain), buka file di My Files, lalu izinkan
"Install unknown apps" untuk My Files. Samsung Auto Blocker harus dimatikan sementara jika aktif.
Tidak perlu ADB.

### Build sendiri
Kebutuhan: JDK 17, Android SDK Platform 34, Build-Tools 34, **NDK 26.1.10909125**, **CMake 3.22.1**
(semuanya terpasang otomatis lewat Android Studio, atau `sdkmanager`).
```bash
cd android
echo "sdk.dir=/path/ke/Android/Sdk" > local.properties     # Android Studio membuat ini otomatis
./gradlew assembleRelease        # → app/build/outputs/apk/release/app-release.apk
./gradlew testDebugUnitTest lint # unit test protokol + lint
```
Prototipe ini sudah di-build di Linux (JDK 17.0.21, AGP 8.5.2, Gradle 8.7, Kotlin 1.9.24).
Hasilnya: build sukses, lint 0 error, dan 3 unit test Kotlin lulus.

### Pemakaian
1. Nyalakan USB tethering (lihat bagian 2) dan jalankan receiver di PC (bagian 4).
2. Buka **CordMic**. Kolom "IP PC" boleh dikosongkan (auto-detect), port default `50005`.
3. Pilih mode mic:
   * **Voice Recognition** (default): proses minimal, biasanya jalur paling low-latency.
   * **Unprocessed**: suara mentah tanpa filter.
   * **Voice Communication**: noise suppression/echo cancel aktif, latensi lebih tinggi. Cocok kalau
     tidak memakai headset.
4. Tekan **Start**, lalu izinkan Mikrofon dan Notifikasi. Notifikasi "CordMic aktif" (dengan tombol
   Stop) tetap tampil dan aplikasi terus berjalan saat layar mati atau saat pindah aplikasi.
5. Agar tidak dimatikan One UI: **Pengaturan → Aplikasi → CordMic → Baterai → Tidak dibatasi**, dan
   pastikan CordMic tidak ada di daftar "Aplikasi tidur".

> Aturan Android 14+: foreground service mikrofon hanya boleh dimulai saat aplikasi terlihat.
> Karena itu Start harus ditekan dari layar aplikasi, dan service tidak auto-start setelah reboot.

---

## 4. Receiver Windows

Kebutuhan: Python 3.11+ (python.org, centang "Add to PATH").
```bat
cd windows-receiver
pip install -r requirements.txt
python -m cordmic_receiver --list-devices            REM daftar output WASAPI
python -m cordmic_receiver --device "CABLE Input" --jitter-ms 10
```
Opsi penting:

| opsi | default | arti |
|---|---|---|
| `--device` | default output | index atau sebagian nama device (contoh `"CABLE Input"`) |
| `--jitter-ms` | 10 | target jitter buffer. Turunkan ke 5 kalau stats bersih, naikkan kalau sering `plc`/`underrun` |
| `--exclusive` | off | WASAPI exclusive: latensi output paling rendah, tapi device dikunci oleh app ini |
| `--blocksize` | 240 | ukuran blok callback (5 ms) |
| `--channels` | 2 | mono diduplikasi ke stereo (VB-Cable default stereo) |
| `--tray` | off | ikon tray (pystray + pillow). Hijau = HP terhubung |
| `--log file` | - | simpan log dan statistik ke file |
| `--no-audio` | off | hanya terima paket + statistik (debug jaringan) |

**Jalan di background**: klik ganda `cordmic_tray.pyw` (berjalan dengan pythonw, tanpa jendela console,
ikon di tray, log di `cordmic.log`). Ubah `DEFAULT_ARGS` di file itu kalau perlu. Agar ikut jalan
saat login, buat shortcut ke file tersebut di `shell:startup`. Untuk `.exe` tunggal:
`pip install pyinstaller && pyinstaller --noconsole --onefile cordmic_tray.pyw`.

**Statistik** (dicetak tiap detik):
```
peer=192.168.42.129:40123 rx=… lost=0 (0.00%) reord=0 late=0 plc=0 underrun=0 ovf=0
buf=10.0ms jitter=0.10ms rtt=0.80ms (min 0.50) pkt=5.0ms out=…ms xruns=0 est_pc_side=…ms
```
`lost` = gap seq (gaya RTP), `plc` = slot yang diisi hening, `late` = paket datang setelah gilirannya,
`underrun` = buffer kosong, `ovf` = paket dibuang karena buffer kepenuhan, `jitter` = jitter
kedatangan (RFC 3550), `rtt` = round trip PING/PONG lewat USB, `est_pc_side` = buffer + latensi
output + RTT/2 (**perkiraan sisi PC saja**, tanpa latensi capture HP).

---

## 5. Virtual cable dan Discord

1. Download **VB-CABLE Driver** dari https://vb-audio.com/Cable/ (donationware). Ekstrak, klik kanan
   `VBCABLE_Setup_x64.exe` → *Run as administrator* → Install, lalu reboot.
2. *Control Panel → Sound*:
   * tab **Playback → CABLE Input → Properties → Advanced**: pilih 2 channel, 16 bit, **48000 Hz**.
   * tab **Recording → CABLE Output → Advanced**: 48000 Hz juga.
   * Jangan jadikan CABLE Input sebagai default playback (supaya suara game tidak masuk ke mic).
3. Jalankan receiver dengan `--device "CABLE Input"`.
4. **Discord**: *User Settings → Voice & Video → Input Device = "CABLE Output (VB-Audio Virtual Cable)"*.
   Untuk latensi minimum, matikan Noise Suppression/Krisp dan Echo Cancellation di Discord kalau mode
   mic HP sudah Voice Communication (supaya pemrosesan tidak dobel). Gunakan Voice Activity atau PTT.
5. **Zoom / game**: pilih "CABLE Output" sebagai mikrofon. Kalau game tidak punya pilihan device,
   jadikan CABLE Output sebagai *Default Recording Device* di Windows.

---

## 6. Menguji latensi

1. **Tanpa HP (tes PC + jaringan lokal)**:
   ```bat
   python -m cordmic_receiver --device "CABLE Input"
   python sim_sender.py --host 127.0.0.1 --seconds 10 --loss 0.01 --jitter-ms 2
   ```
   Nada 440 Hz harus terdengar di CABLE Output (cek di *Sound → Recording → CABLE Output → Listen*).
2. **Link USB**: jalankan receiver, lalu Start di HP. Lihat `rtt` (link USB biasanya hanya beberapa
   ms atau kurang; ukur sendiri) dan pastikan `lost`/`plc`/`underrun` tetap 0 selama beberapa menit.
3. **End-to-end (akustik)**: buka OBS, tambahkan dua *Audio Input Capture*: (a) "CABLE Output" dan
   (b) mic fisik lain (headset/webcam). Rekam, lalu tepuk tangan tepat di dekat **kedua** mic. Buka
   rekaman di Audacity dan ukur jarak antar puncak tepukan. Selisih itu adalah latensi CordMic
   dikurangi latensi mic pembanding. Ulangi dengan `--jitter-ms 5` dan `--exclusive`.
4. **Estimasi teoritis** (belum diukur): capture AAudio ~5–10 ms + paket 5 ms + USB <1–2 ms
   + jitter buffer 10 ms + WASAPI/VB-Cable ~10–20 ms ≈ **30–45 ms**. Nilai sebenarnya harus diukur
   dengan langkah 3.

---

## 7. Status verifikasi (jujur)

Sudah dijalankan (di box Linux, bukan Windows/HP):
* `python -m unittest discover -s tests` (folder `windows-receiver`): **17 test lulus**. Isinya
  protokol, jitter buffer (reorder, loss, late, underrun, overflow, wrap seq, restart pengirim),
  duplikasi channel di callback output, dan **2 loopback test UDP** (simulator → receiver → konsumen
  real-time 5 ms): link bersih 0 loss, link dengan 2% loss/jitter 3 ms/reorder di mana `lost`
  receiver = paket yang dibuang simulator.
* CLI receiver `--no-audio` + `sim_sender.py` via 127.0.0.1: discovery HELLO, PING/PONG RTT, statistik.
* Build Android: `assembleDebug`, `assembleRelease`, `lint` (0 error), `testDebugUnitTest`
  (3 test; header Kotlin byte-identik dengan Python). APK berisi `libcordmic_native.so`
  (arm64-v8a, armeabi-v7a, x86_64), minSdk 29, targetSdk 34, dan semua izin yang dibutuhkan.

* Aplikasi Windows C# (`windows-app/`, di-cross-build dari Linux): `dotnet test` **34 test lulus** (v0.3.0, termasuk migrasi setting dari `%APPDATA%\UsbMic`)
  (protokol termasuk golden vector byte, jitter buffer port 1:1 dari Python, aturan adapter/kabel virtual,
  setting, loopback UDP bersih/lossy, HELLO proaktif, timeout peer, dan **interop dengan `sim_sender.py`**).
  Harness console + `sim_sender.py`: 600/600 paket, 0 loss, RTT ~0,4 ms; link lossy: lost = paket yang dibuang.
* `CordMic.exe` (win-x64, self-contained, single-file) dan installer NSIS berhasil di-build; isi installer
  dicek dengan 7-Zip (exe identik dengan hasil publish, skrip PowerShell ikut). Skrip PowerShell lolos
  parser PowerShell 7 dan XML scheduled task-nya valid. **Semua ini dicek di Linux, bukan di Windows.**
* Android 0.2.0 (bind port 50005 di interface tethering): build debug/release, lint 0 error, 3 unit test lulus.
* **v0.3.0 (ganti nama UsbMic → CordMic)**: build ulang semuanya; APK `dev.cordmic` versi 0.3.0 (versionCode 3) berisi
  `libcordmic_native.so`, lint 0 error, 3 unit test Kotlin lulus; 17 test Python lulus; 34 test .NET lulus.
  Langkah upgrade installer (hapus UsbMic lama) dan migrasi setting/startup **belum dicoba di Windows sungguhan**.

**Belum dites**:
* Aplikasi Windows di Windows sungguhan: tampilan panduan/ikon tray, WASAPI ke VB-CABLE (NAudio),
  deteksi adapter, UAC/runas, installer + uninstaller, aturan firewall, scheduled task metric.
* Aplikasi di HP sungguhan (AAudio di S24 FE, foreground service, notifikasi, discovery lewat
  rndis/ncm, perilaku saat kabel dicabut-colok).
* Output WASAPI/VB-Cable sungguhan (PortAudio tidak tersedia di box; `audio_out.py` belum pernah
  memutar suara), mode exclusive, ikon tray, dan Windows Firewall.
* Latensi end-to-end nyata dan clock drift jangka panjang HP vs PC.

---

## 8. Roadmap

1. **Uji lapangan** di S24 FE + Windows 11 dan tuning `--jitter-ms`/blocksize berdasarkan statistik.
2. **Kompensasi clock drift**: buang atau sisipkan 1 sampel saat buffer menjauh dari target,
   bukan drop paket.
3. **Opus** (mode 48 kHz, frame 5 ms/2.5 ms, FEC) sebagai opsi; PCM tetap default.
4. **Auto-reconnect di HP**: deteksi kabel/tethering lewat broadcast `ACTION_USB_STATE` dan notifikasi
   "nyalakan tethering". (Sisi Windows sudah: v0.2.0 memantau adapter RNDIS/NCM dan mengirim HELLO
   proaktif ke HP.)
5. ~~Receiver native~~ **selesai di v0.2.0** (`windows-app/`, tray + installer). Berikutnya: tanda tangan
   kode (Authenticode) untuk installer agar SmartScreen tidak memperingatkan.
6. **Driver audio virtual sendiri** (misalnya berbasis contoh SysVAD/"Virtual Audio Driver") agar
   muncul langsung sebagai "CordMic Microphone" tanpa VB-Cable. Butuh penandatanganan driver
   (EV cert / attestation signing Microsoft).
7. PLC lebih baik (ulang paket terakhir dengan fade) dan indikator level mic di app/tray.
