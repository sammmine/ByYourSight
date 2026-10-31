# Panduan Koneksi & Deployment ke Microsoft HoloLens 2 (`By Your Sight`)

Dokumen ini menjelaskan langkah-langkah praktis untuk menyambungkan, menguji, dan melakukan *build/deploy* proyek **By Your Sight** dari tahap simulasi di MacBook ke perangkat fisik **Microsoft HoloLens 2**.

---

## Daftar Isi
1. [Persiapan & Kebutuhan Sistem (Mac ke Windows)](#1-persiapan--kebutuhan-sistem-mac-ke-windows)
2. [Konfigurasi Izin Perangkat (Capabilities) di Unity](#2-konfigurasi-izin-perangkat-capabilities-di-unity)
3. [Cara A: Holographic Remoting (Paling Cepat untuk Demo & Uji Coba)](#3-cara-a-holographic-remoting-paling-cepat-untuk-demo--uji-coba)
4. [Cara B: Build & Deploy Standalone ke HoloLens 2 (Tanpa Kabel)](#4-cara-b-build--deploy-standalone-ke-hololens-2-tanpa-kabel)
5. [Penyesuaian dari Simulasi Ruangan ke Dunia Nyata (Spatial Mesh)](#5-penyesuaian-dari-simulasi-ruangan-ke-dunia-nyata-spatial-mesh)
6. [Checklist Sebelum Demo di Perangkat HoloLens 2](#6-checklist-sebelum-demo-di-perangkat-hololens-2)

---

## 1. Persiapan & Kebutuhan Sistem (Mac ke Windows)

Karena **Microsoft HoloLens 2** berjalan di atas **Windows Holographic OS (UWP ARM64)**, proses *build* akhir aplikasi UWP atau *streaming* langsung ke HoloLens membutuhkan **PC/Laptop Windows 10/11**.

### Alur Kerja Lintas Platform:
1. **Di MacBook:** Lakukan pengembangan skrip C#, pengaturan logika KF01–KF08, dan pengujian simulasi di Unity Editor.
2. **Saat Akan Menyambung ke HoloLens 2:** Pindahkan folder proyek `ByYourSight` (melalui Git/GitHub, Google Drive, atau Flashdisk/SSD eksternal) ke **laptop Windows 10/11**.

### Perangkat Lunak yang Wajib Terinstal di Laptop Windows:
* **Unity Hub & Unity 2022.3 LTS** dengan modul tambahan:
  * ✅ *Universal Windows Platform (UWP) Build Support*
* **Visual Studio 2022 (Community / Professional)** dengan beban kerja (*Workloads*):
  * ✅ *Universal Windows Platform development*
  * ✅ *Desktop development with C++*
  * ✅ Komponen individual: *Windows 10/11 SDK (10.0.19041.0 atau lebih baru)*, *USB Device Connectivity*, dan *MSVC v143 - VS 2022 C++ ARM64 build tools*.

---

## 2. Konfigurasi Izin Perangkat (Capabilities) di Unity

Sebelum menyambungkan atau melakukan *build* ke HoloLens 2, pastikan izin sensor dan mikrofon sudah diaktifkan di Unity Editor:

1. Buka **`Edit > Project Settings > Player`**.
2. Klik tab **ikon Windows Store (UWP)**, lalu buka bagian **`Publishing Settings > Capabilities`**.
3. Pastikan kotak-kotak berikut **dicentang**:
   * ✅ **`Microphone`** — Wajib agar perintah suara `KeywordRecognizer` (KF04) dan sensor kebisingan (KF06) berfungsi.
   * ✅ **`SpatialPerception`** — Wajib agar sensor *depth* HoloLens 2 dapat memindai dinding, lantai, dan tiang di dunia nyata (KF05 & KF07).
   * ✅ **`Webcam`** — Digunakan jika fitur kamera *Photo/Video (PV)* diaktifkan untuk pengenalan objek (KF01, KF02, KF08).
4. Buka **`Edit > Project Settings > XR Plug-in Management`**:
   * Pada tab **UWP**, pastikan **`OpenXR`** dicentang beserta *feature group* **Microsoft HoloLens**.

---

## 3. Cara A: Holographic Remoting (Paling Cepat untuk Demo & Uji Coba)

**Holographic Remoting** memungkinkan Anda menekan tombol **Play (▶)** di Unity Editor pada laptop Windows, lalu gambar hologram, suara 3D, pergerakan kepala (*Head Gaze*), dan mikrofon langsung di-*stream* secara *real-time* ke kacamata HoloLens 2 melalui Wi-Fi atau kabel USB-C **tanpa perlu menunggu proses *build* Visual Studio yang lama**.

### Langkah-langkah:
1. Nyalakan **HoloLens 2**, buka aplikasi **Microsoft Store** di dalam perangkat, cari dan instal aplikasi resmi gratis bernama **Holographic Remoting Player**.
2. Pastikan laptop Windows dan HoloLens 2 terhubung ke **jaringan Wi-Fi yang sama** (atau hubungkan keduanya langsung menggunakan kabel USB-C).
3. Buka aplikasi **Holographic Remoting Player** di HoloLens 2. Anda akan melihat **IP Address** tampil di layar lensa (contoh: `192.168.1.25`).
4. Di Unity Editor (laptop Windows), buka menu:
   **`Edit > Project Settings > XR Plug-in Management > OpenXR`** (atau **`Window > XR > Holographic Remoting for Play Mode`**).
5. Centang **`Holographic Remoting for Play Mode`**, masukkan **IP Address** yang tertera di HoloLens 2 pada kolom *Remote Host Name*.
6. Klik tombol **Play (▶)** di Unity Editor!
   * Tampilan dan audio 3D **By Your Sight** akan langsung muncul di lensa HoloLens 2 Anda.

---

## 4. Cara B: Build & Deploy Standalone ke HoloLens 2 (Tanpa Kabel)

Gunakan cara ini jika Anda ingin menginstal aplikasi **By Your Sight** secara permanen ke dalam HoloLens 2 agar bisa berjalan mandiri tanpa terhubung ke laptop.

### Langkah 1: Aktifkan Developer Mode di HoloLens 2
1. Di dalam HoloLens 2, buka **Settings > Update & Security > For developers**.
2. Aktifkan tombol **Use developer features (Developer Mode)**.
3. *(Opsional)* Aktifkan **Device Portal** jika ingin melakukan instalasi nirkabel lewat Wi-Fi.

### Langkah 2: Build Solution dari Unity Editor
1. Di Unity Editor (laptop Windows), buka **`File > Build Settings`**.
2. Pastikan `Assets/Scenes/SampleScene.unity` sudah masuk di daftar **Scenes In Build** (klik **Add Open Scenes** jika belum).
3. Pilih platform **Universal Windows Platform**, lalu klik **Switch Platform**.
4. Atur konfigurasi berikut:
   * **Target Device:** `HoloLens`
   * **Architecture:** `ARM64`
   * **Build Type:** `D3D Project`
   * **Target SDK Version:** `Latest Installed`
   * **Minimum Platform Version:** `10.0.10240.0` (atau `10.0.18362.0`)
   * **Visual Studio Version:** `Latest Installed`
5. Klik tombol **Build**, buat folder baru bernama `Builds`, lalu pilih folder tersebut.

### Langkah 3: Deploy dari Visual Studio 2022 ke HoloLens 2
1. Setelah proses *build* Unity selesai, buka folder `Builds` dan klik dua kali file **`ByYourSight.sln`** untuk membukanya di **Visual Studio 2022**.
2. Hubungkan HoloLens 2 ke laptop menggunakan **kabel USB-C** (pastikan HoloLens dalam keadaan menyala dan tidak terkunci).
3. Pada toolbar bagian atas Visual Studio, ubah pengaturan menjadi:
   * Konfigurasi: **`Release`** (atau `Master`)
   * Arsitektur: **`ARM64`**
   * Target: **`Device`** *(jika memakai kabel USB-C)* atau **`Remote Machine`** *(jika lewat Wi-Fi)*
4. Klik menu **`Debug > Start Without Debugging`** (atau tekan **`Ctrl + F5`**).
   * *(Jika pertama kali menyambungkan HoloLens, Visual Studio akan meminta **PIN Pairing**. Di HoloLens 2, buka **Settings > Update & Security > For developers > Pair**, lalu ketik angka PIN yang muncul ke Visual Studio).*
5. Tunggu hingga proses *compile* dan *deploy* selesai. Aplikasi **By Your Sight** akan otomatis terbuka di HoloLens 2 dan tersimpan di menu *All Apps*.

---

## 5. Penyesuaian dari Simulasi Ruangan ke Dunia Nyata (Spatial Mesh)

Kode C# pada proyek ini sudah dirancang agar kompatibel secara *native* ketika dijalankan di HoloLens 2:

1. **TTS & Speech Recognition Otomatis Beralih ke Native UWP:**
   * Blok `#if WINDOWS_UWP` pada `PriorityAudioManager.cs` otomatis mengaktifkan `Windows.Media.SpeechSynthesis.SpeechSynthesizer` bawaan HoloLens 2.
   * Blok `KeywordRecognizer` pada `VoiceAndGestureController.cs` otomatis mendengarkan mikrofon perangkat keras HoloLens 2 secara *offline*.
2. **Transparansi Layar Lensa (Additive Display):**
   * `Main Camera` sudah diatur menggunakan latar belakang gelap (`Solid Color`). Pada layar *additive* HoloLens 2, warna gelap dirender menjadi **100% transparan**, sehingga pengguna tetap dapat melihat dunia nyata di sekitarnya, sementara objek dengan highlight warna **Kuning**, **Merah/Oranye**, dan **Hijau** akan tampak menyala terang.
3. **Menghubungkan Deteksi Rintangan (KF07) ke Tembok & Tiang Asli:**
   * Saat digunakan di ruangan nyata, Anda dapat menonaktifkan objek lantai/objek dummy (`Indoor_Floor`) di *Hierarchy* agar tidak menghalangi pandangan.
   * Tambahkan komponen **`ARMeshManager`** (dari paket `AR Foundation` yang sudah terinstal) untuk memproyeksikan *Spatial Mesh* beserta `MeshCollider` pada dinding, tiang, meja, dan kursi asli di ruangan.
   * Skrip `ObstacleDetector.cs` telah dikonfigurasi agar menganggap setiap *collider* struktural tanpa tag khusus (`trackable == null`) sebagai rintangan bahaya (`HazardObstacle`), sehingga akan langsung memicu alarm `P0` (*"Warning!"*) ketika pengguna berjalan mendekati dinding atau tiang nyata dalam jarak `< 1.6 meter`.
4. **Menyembunyikan Panel Simulator Mouse/Keyboard di HoloLens:**
   * Saat dijalankan di HoloLens 2 asli, Anda dapat mematikan centang `enableEditorWalkSimulation` pada komponen `VoiceAndGestureController` di Inspector karena pergerakan kamera sepenuhnya dikendalikan oleh sensor kepala (*Head Tracking*) HoloLens 2.

---

## 6. Checklist Sebelum Demo di Perangkat HoloLens 2

- [ ] Kalibrasi mata (*Eye Calibration*) atau posisi visor HoloLens 2 sudah pas di kepala pengguna.
- [ ] Volume speaker *open-ear* HoloLens 2 diatur minimal di angka **70%–80%** melalui tombol fisik di sisi kiri atas visor.
- [ ] Bahasa sistem (*System Language*) di HoloLens 2 diatur ke **English (United States)** agar *Windows Speech Recognition* aktif secara *offline*.
- [ ] Pengujian dilakukan di **ruangan indoor** dengan pencahayaan yang cukup (menghindari pantulan cahaya matahari langsung ke sensor inframerah sesuai *Technical Constraints* T3).
