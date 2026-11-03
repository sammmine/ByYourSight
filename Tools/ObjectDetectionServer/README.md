# Camera AI — Object Detection Server (Opsi 2)

Server kecil yang berjalan di **laptop yang menjalankan Unity**. Server membaca kamera HoloLens 2 lewat
Windows Device Portal, menjalankan YOLO, lalu mengirim hasil deteksi ke Unity
(`Assets/Scripts/Perception/RealWorldObjectDetector.cs`) di `http://127.0.0.1:8765`.

```
HoloLens 2 kamera ──(Device Portal live stream)──► detection_server.py (YOLO) ──(JSON)──► Unity ──(Remoting)──► HoloLens 2
```

## 1. Instal (sekali saja)

Butuh **Python 3.10–3.12** (centang *Add python.exe to PATH* saat instal).

```powershell
cd Tools\ObjectDetectionServer
py -3.11 -m venv .venv
.venv\Scripts\activate
pip install -r requirements.txt
```

Model `yolo11n.pt` (±6 MB) diunduh otomatis saat pertama kali dijalankan. Butuh internet sekali.

## 2. Aktifkan Device Portal di HoloLens 2 (sekali saja)

1. HoloLens: **Settings › Update & Security › For developers** → nyalakan **Developer Mode** dan **Device Portal**.
2. Di browser laptop buka `https://<IP-HoloLens>` (IP dari *Settings › Network & Internet › Wi-Fi › Advanced options*).
   Abaikan peringatan sertifikat (*Advanced › Continue*).
3. Pertama kali, buat **user name dan password** Device Portal. Catat, dipakai di langkah 3.
4. Cek di menu **Views › Mixed Reality Capture**: pratinjau kamera harus tampil.

## 3. Jalankan (setiap sesi uji)

Jalankan **sebelum** menekan Play di Unity, di terminal terpisah:

```powershell
cd Tools\ObjectDetectionServer
.venv\Scripts\activate
python detection_server.py --portal https://192.168.1.25 --user NAMA --password SANDI
```

Lalu buka `http://127.0.0.1:8765/` di browser: kamu akan melihat apa yang dilihat kamera HoloLens beserta kotak deteksinya.
Di Unity, panel kiri layar Game menampilkan **Camera AI: ON | … objects | sees: …**.

### Pilihan lain

| Kebutuhan | Perintah |
|---|---|
| Uji tanpa HoloLens (webcam laptop) | `python detection_server.py --source 0` |
| HoloLens lewat kabel USB | `--portal http://127.0.0.1:10080` |
| Kenali juga pintu & tangga (lebih lambat, butuh Git terpasang) | tambah `--model yolov8s-worldv2.pt` |
| Laptop punya GPU NVIDIA | tambah `--device 0` |
| Terlalu banyak deteksi salah | tambah `--conf 0.5` |

## Benda yang dikenali

Model default (COCO, 80 kelas) — yang relevan dan nama yang diucapkan:
person, chair, table (*dining table*), sofa (*couch*), bed, toilet, television (*tv*), laptop, book, plant (*potted plant*),
bag (*backpack/handbag/suitcase*), phone (*cell phone*), water bottle (*bottle*), coffee mug (*cup*), dan lainnya.

**Tidak dikenali model default:** pintu, tangga, dinding, tiang. Untuk itu pakai `--model yolov8s-worldv2.pt`,
atau tetap pakai **"Save door"** dari Opsi 1.

## Kalau ada masalah

| Gejala | Coba ini |
|---|---|
| `cannot open camera stream` | IP/user/password salah, atau Device Portal belum aktif. Cek dengan membuka `https://<IP>` di browser. |
| Panel Unity: **Camera AI: OFF** | Server belum jalan, atau sudah ditutup. Lihat terminal server. |
| Kotak deteksi di preview benar, tapi di HoloLens posisinya bergeser ke arah kepala baru menoleh | Naikkan `Stream Latency Seconds` di Inspector `RealWorldObjectDetector` (mis. 1.0 → 1.5). Kalau bergeser ke arah sebaliknya, turunkan. |
| Benda terdeteksi di preview tapi "Find …" bilang *not saved yet* | Area belum terpindai (spatial mesh). Lihat ke sekeliling benda itu beberapa detik. |
| Lambat (`fps` < 2 di preview) | Pakai model default `yolo11n.pt`, atau `--imgsz 480`. |
