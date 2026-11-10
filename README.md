# Noct

Noct adalah pemutar FLAC lokal untuk Windows. Tambahkan folder musik, pilih album atau lagu, lalu putar dari jendela utama atau kontrol kecil di tepi atas layar saat aplikasi diminimalkan. Antarmuka aplikasi menggunakan bahasa Inggris.

## Unduh aplikasi

Unduh paket siap pakai dari [Releases Noct 0.5.1](https://github.com/DikyMartin/Noct/releases/tag/v0.5.1).

Pilih salah satu paket berikut:

| Paket | Cara memakai |
| --- | --- |
| [Installer Windows x64](https://github.com/DikyMartin/Noct/releases/download/v0.5.1/Noct-Setup-0.5.1-x64.exe) | Jalankan installer, lalu buka Noct dari Start Menu. |
| [ZIP Windows x64](https://github.com/DikyMartin/Noct/releases/download/v0.5.1/Noct-0.5.1-win-x64.zip) | Ekstrak seluruh ZIP ke folder pilihan, lalu jalankan `Noct.exe` di dalamnya. |

[SHA256SUMS.txt](https://github.com/DikyMartin/Noct/releases/download/v0.5.1/SHA256SUMS.txt) berisi checksum kedua paket untuk memeriksa kecocokan unduhan.

Paket installer dan ZIP menyertakan .NET Desktop Runtime. Pengguna tidak perlu memasang .NET atau SDK secara terpisah. ZIP **Source code** yang dibuat GitHub berisi source, bukan aplikasi siap jalan.

## Persyaratan

- Windows 11 x64. Paket yang disiapkan adalah `win-x64`; Windows 10, ARM64, dan Windows versi lain belum diuji.
- Perangkat output audio Windows. Driver ASIO diperlukan hanya jika memilih ASIO.
- File musik berformat FLAC. Format musik lain dan layanan streaming tidak tersedia.

## Memasang dan menjalankan

Installer memasang aplikasi untuk pengguna saat ini di `%LocalAppData%\Programs\Noct`, tanpa hak administrator. Shortcut desktop bersifat opsional. Instalasi versi baru menggunakan lokasi yang sama; tutup Noct sebelum memperbarui.

Paket ZIP dapat dijalankan tanpa instalasi. Pertahankan semua file di folder hasil ekstraksi bersama `Noct.exe`; menyalin EXE saja tidak cukup.

1. Buka Noct, lalu pilih **Add folder** di Library.
2. Pilih folder musik. Noct memindai FLAC di folder tersebut dan subfoldernya. Setiap folder yang langsung berisi FLAC tampil sebagai satu album, dengan nama folder sebagai nama album.
3. Pilih lagu atau album untuk memutar. Pencarian tersedia di Library; antrean dapat dilihat dan diubah di Queue.
4. Pilih perangkat dan mode output di Settings bila perlu. WASAPI shared adalah pengaturan awal.

Menghapus album atau sumber dari Library hanya menghapus catatannya di Noct. File musik tidak dihapus atau diubah.

## Fitur yang tersedia

- Home, Now Playing, Library, Queue, dan Settings; tampilan album dan semua lagu, pencarian, serta cover dari folder atau file FLAC.
- Play/pause, previous/next, seek, volume, shuffle album, repeat, **Play next**, dan **Add to queue**.
- Lirik `.lrc` lokal: letakkan `Song.lrc` di samping `Song.flac`. Lirik bertimestamp disorot saat diputar dan dapat diklik untuk berpindah posisi. Tidak ada pengunduh lirik.
- Kontrol kecil di tepi atas layar saat minimize, dengan auto-hide dan hover. Pilih **Open Noct** dari island atau tray untuk kembali. Fitur ini dapat dimatikan di Settings. Tombol Close dan tray **Exit** menutup aplikasi dan menghentikan playback.
- Penyimpanan library, antrean, pengaturan, dan posisi lagu. Saat dibuka kembali, antrean dipulihkan tanpa langsung memutar musik.
- WASAPI shared, WASAPI exclusive, dan pilihan ASIO bila driver tersedia. Mode Transparent memerlukan WASAPI exclusive dan mengunci volume software pada unity gain.
- Gapless untuk lagu dengan format yang sama. Pergantian sample rate atau format dapat memerlukan pembukaan ulang perangkat audio.

WASAPI exclusive atau mode Transparent tidak membuktikan hasil bit-perfect pada driver atau DAC. Pengujian perangkat DAC dan ASIO masih diperlukan; lihat [hasil pemeriksaan dan batasannya](docs/BUILD_NOTES.md).

## Data aplikasi dan uninstall

Pengaturan, indeks library, cache cover, dan `noct.log` disimpan di `%LocalAppData%\Noct`, termasuk saat menjalankan paket ZIP. Paket ZIP tidak menyimpan pengaturan di samping EXE.

Uninstall melalui Settings Windows menghapus program. Uninstaller menawarkan pilihan untuk menghapus data Noct; file musik dan lirik di folder library tidak termasuk. Untuk paket ZIP, tutup aplikasi lalu hapus folder hasil ekstraksinya. Data di `%LocalAppData%\Noct` dapat dihapus terpisah bila tidak lagi diperlukan.

## Membangun dari source

Gunakan Windows x64, PowerShell, dan [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0). Jalankan perintah dari folder yang berisi `Noct.slnx`:

```powershell
dotnet build Noct.slnx -c Release
dotnet run --project src/Noct.App/Noct.App.csproj -c Release --no-build
```

Untuk menjalankan test, pasang [tool FLAC resmi](https://xiph.org/flac/download.html) dan pastikan `flac.exe` serta `metaflac.exe` ada di `PATH`. Tool tersebut hanya diperlukan untuk membuat data uji, bukan untuk menjalankan Noct.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build/make-test-audio.ps1
dotnet test tests/Noct.Tests/Noct.Tests.csproj -c Release
```

Jika tool FLAC belum ada di `PATH`, berikan lokasi foldernya melalui parameter `-ToolDirectory` pada skrip pembuat data uji.

Untuk membuat installer dan ZIP dari source saat ini, pasang [Inno Setup 6](https://jrsoftware.org/isdl.php) dari bagian unduhan versi 6, lalu jalankan:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build/package.ps1
```

Hasilnya berada di `dist\release`, bersama `SHA256SUMS.txt`. Versi paket dibaca dari `Directory.Build.props`. Skrip selalu menjalankan publish baru; isi `dist` lama tidak digunakan sebagai dasar release. Untuk membuat ZIP saja tanpa Inno Setup:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build/package.ps1 -ZipOnly
```

Struktur source, utilitas diagnosis, dan cara memisahkan data pengujian dijelaskan di [panduan pengembangan](docs/DEVELOPMENT.md). Daftar pemeriksaan manual ada di [VALIDATION.md](docs/VALIDATION.md).

## Lisensi

Lisensi untuk source Noct belum ditetapkan. Lisensi dependensi tidak otomatis berlaku untuk source Noct. Pemberitahuan komponen yang digunakan ada di [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
