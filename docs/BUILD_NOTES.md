# Pemeriksaan rilis 0.5.1

Pemeriksaan persiapan publikasi dijalankan pada 10 November 2026, di Windows x64 build 26300 dengan .NET SDK 10.0.403 dan runtime 10.0.14. Hasil di bawah hanya mencakup perintah yang dijalankan pada persiapan ini; catatan sesi pengembangan lama tidak dipakai sebagai hasil uji terbaru.

## Hasil saat ini

| Pemeriksaan | Hasil |
| --- | --- |
| `dotnet build Noct.slnx -c Release` | Berhasil, 0 warning dan 0 error. |
| `build/make-test-audio.ps1` | Berhasil membuat ulang data referensi dengan Xiph FLAC 1.5.0. |
| `dotnet test tests/Noct.Tests/Noct.Tests.csproj -c Release --no-build` | 173 lulus, 0 gagal, 0 dilewati. |
| `Noct.Diagnostics decode artifacts/testaudio` | 18 input diperiksa, 0 kegagalan: 15 FLAC valid cocok dengan reference PCM dan seek tepat; 3 input rusak/tidak valid ditolak. |
| Publish self-contained `win-x64` | Berhasil; runtime .NET dan WPF disertakan bersama petunjuk pengguna dan lisensi dependensi. |
| ZIP | 258 file dapat diekstrak; SHA-256 setiap file cocok dengan hasil publish. Tidak memuat PDB, data uji, log, settings, atau library pengguna. |
| Startup dari ZIP | Window merespons; aplikasi menutup dengan exit code 0 menggunakan `NOCT_DATA_DIR` terpisah. Folder aplikasi serta settings/library pengguna asli tidak berubah. |
| Installer | Berhasil dikompilasi dengan Inno Setup; metadata versi 0.5.1. Instalasi/update/uninstall belum dijalankan pada persiapan ini. |

Test otomatis mencakup PCM packing, volume, gapless pada track provider, antrean, library/album, lirik, serta state dan geometry island. Kesamaan sampel berlaku untuk jalur software yang diuji, bukan untuk output fisik perangkat.

Paket installer belum ditandatangani secara digital. Checksum unduhan berada di `SHA256SUMS.txt` bersama aset release. Checksum memeriksa kecocokan file, bukan identitas penerbit.

## Keputusan implementasi

Noct memakai decoder FLAC managed sendiri yang menghasilkan sampel integer. Jalur ini menghindari ketergantungan decoder native dan konversi float untuk decoding. NAudio 3.3.0 digunakan untuk WASAPI dan ASIO; versi dependensi ada di `.csproj`.

Island digambar dengan geometry WPF pada window transparan per piksel agar kurvanya mengikuti bentuk yang sama dengan clipping konten. Island tidak memakai backdrop blur DWM dan tidak memiliki drop shadow. Window utama meminta backdrop Windows; hasil akhirnya bergantung pada dukungan OS dan pengaturan aksesibilitas/transparansi. Perilaku yang harus dijaga dirangkum di [DEVELOPMENT.md](DEVELOPMENT.md).

## Belum diverifikasi pada persiapan ini

- Pengujian mesin bersih tanpa SDK/runtime terpasang dan pemeriksaan antivirus/SmartScreen.
- Seluruh alur wizard install, update, uninstall, shortcut, serta pilihan penghapusan data pada VM bersih.
- Playback fisik, DAC eksternal, driver ASIO, perangkat sibuk, disconnect/reconnect, serta klaim bit-perfect hardware.
- Inspeksi visual seluruh tampilan, DPI 100/150/200%, multi-monitor, perubahan monitor, high contrast, reduced motion, dan keyboard/accessibility.
- Eksekusi workflow pada GitHub Actions dan unduhan aset dari release publik.

Installer source menerima Windows mulai build 19041, tetapi itu hanya batas pemasangan, bukan bukti dukungan Windows 10. Dokumentasi pengguna menyebut Windows 11 x64; versi Windows dan arsitektur lain belum diuji.

Gunakan [VALIDATION.md](VALIDATION.md) untuk mencatat pemeriksaan manual berikutnya. Jangan menyimpulkan keberhasilan alur yang belum dijalankan dari build atau unit test saja.
