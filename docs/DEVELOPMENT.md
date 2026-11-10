# Pengembangan Noct

## Struktur proyek

| Lokasi | Isi |
| --- | --- |
| `src/Noct.App` | Aplikasi WPF: views, view models, tema, tray, window, dan integrasi Windows. |
| `src/Noct.Core` | Decoder FLAC, output audio, playback, antrean, library, lirik, pengaturan, dan logika island. |
| `tests/Noct.Tests` | Test PCM, decoder melalui track provider, antrean, volume, library, album, lirik, dan island. |
| `tools/Noct.Diagnostics` | Utilitas command line untuk decoder, perangkat output, playback, dan pengukuran. |
| `build` | Skrip build, packaging, generator data uji, dan pemeriksaan UI/performa. |
| `installer/Noct.iss` | Installer per pengguna dan aturan update/uninstall. |
| `licenses` | Salinan lisensi komponen pihak ketiga. |

`Noct.slnx` mencakup app, core, tests, dan diagnostics. Versi aplikasi berada di `Directory.Build.props`; versi NuGet dipatok di masing-masing `.csproj`.

## Batas yang perlu dijaga

- Jendela utama dan island memakai satu `PlaybackEngine`. Minimize, hover, dan restore tidak boleh membuat sesi playback baru atau menghentikan musik.
- Logika state/geometry island berada di Core; integrasi window, monitor, dan tray berada di `Noct.App/Platform`. Warna, ukuran, dan durasi UI dipusatkan di `Themes/Tokens.xaml`.
- Library membaca folder musik tanpa mengubah file. Penghapusan album atau sumber hanya mengubah indeks/pengaturan Noct.
- Pengaturan dan indeks JSON memiliki versi schema serta ditulis melalui `JsonStore`. Data aplikasi berada di `%LocalAppData%\Noct`, bukan folder instalasi.
- Decoder FLAC menghasilkan sampel integer. Noct memakai decoder managed sendiri agar jalur PCM tidak memerlukan konversi melalui decoder float atau DLL decoder eksternal.
- Metadata source dan format output yang diterima perangkat adalah dua hal berbeda. Klaim kualitas audio harus didukung pemeriksaan pada tingkat yang sesuai. Uji kesamaan PCM software tidak menguji driver atau DAC.
- Mode Transparent memakai WASAPI exclusive dengan unity gain. Gapless berlaku untuk format yang cocok; perubahan format dapat membuka ulang perangkat.
- Close dan tray Exit harus menghentikan audio, menutup window/tray, menyimpan pengaturan, dan melepas perangkat. Instance kedua mengaktifkan instance pertama.

## Build dan test

Perintah utama tersedia di [README](../README.md#build-from-source). Data referensi FLAC sengaja tidak disimpan di Git: `build/make-test-audio.ps1` membuat ulang WAV, FLAC, dan PCM deterministik di `artifacts/testaudio`. Jika folder tersebut tidak ada, test gagal dengan petunjuk generator, bukan melewati pemeriksaan audio.

Tool `flac.exe` dan `metaflac.exe` dicari di `PATH`, atau di folder `-ToolDirectory` yang diberikan. Test yang ada menggunakan hasil reference encoder Xiph untuk membandingkan sampel, packing PCM, seeking, dan batas lagu. Dependensi output memakai NAudio 3.3.0.

Untuk menjalankan aplikasi dengan data terpisah selama pengembangan:

```powershell
$env:NOCT_DATA_DIR = Join-Path (Get-Location) 'artifacts/dev-data'
dotnet run --project src/Noct.App/Noct.App.csproj -c Release
Remove-Item Env:NOCT_DATA_DIR
```

`NOCT_DATA_DIR` memisahkan pengaturan dan library; mutex single-instance tetap berlaku untuk sesi Windows yang sama. Tutup instance yang sedang berjalan sebelum memeriksa build lain.

## Diagnostics dan skrip UI

`Noct.Diagnostics` memiliki perintah `api`, `decode`, `devices`, `play`, `tone`, `meter`, dan `scan`. Untuk melihat bantuan, jalankan project tanpa argumen; bantuan tersebut mengembalikan exit code 1. Contoh pemeriksaan decoder setelah data uji dibuat:

```powershell
dotnet run --project tools/Noct.Diagnostics/Noct.Diagnostics.csproj -c Release -- decode artifacts/testaudio
```

Skrip `build/*-check.ps1` dan probe lain digunakan untuk diagnosis desktop serta regresi UI, bukan bagian dari aplikasi yang dikirim ke pengguna. Sebagiannya membuat library sintetis, menggerakkan pointer, atau menutup proses Noct. Baca skrip sebelum menjalankannya; siapkan fixture yang disebut di dalamnya. `make-perf-library.ps1` membuat library performa sintetis. `make-icon.ps1` memakai `Noct.png` di root untuk menghasilkan aset aplikasi; kedua gambar bernama `Noct.png` memiliki isi dan tujuan berbeda.

`build/test-installer.ps1` memeriksa instalasi pengguna yang sesungguhnya dan dapat mengubah instalasi Noct. Opsi `-IncludePurge` juga memindahkan data pengguna sementara. Jalankan pemeriksaan tersebut hanya dalam lingkungan uji yang disiapkan untuk instalasi/uninstall, seperti VM terpisah.

## Paket release

`build/package.ps1` membuat publish self-contained, ZIP, installer, dan SHA-256 dari source saat ini. `build/build-installer.ps1` membuat installer saja. `build/publish.ps1` menyiapkan folder aplikasi. Skrip packaging menyertakan petunjuk pengguna dan lisensi dependensi; source, data uji, PDB, serta log lokal tidak termasuk paket aplikasi.

Workflow Windows di `.github/workflows` menjalankan build, membuat data uji, menjalankan test, dan menyimpan paket sebagai workflow artifact. Workflow artifact dapat dipakai untuk memeriksa hasil CI; pengguna umum sebaiknya mengunduh aset GitHub Releases.

Sebelum menerbitkan release, pastikan repository tujuan sudah benar, buat paket dari commit yang akan ditandai, periksa [VALIDATION.md](VALIDATION.md), dan unggah ZIP, installer, serta `SHA256SUMS.txt` ke GitHub Releases. Perbarui bagian unduh README dengan tautan release yang sudah ada. Jangan menaruh paket biner di riwayat source.
