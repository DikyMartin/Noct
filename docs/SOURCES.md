# Referensi teknis

Stack yang digunakan Noct dapat diperiksa di `.csproj`, `Directory.Build.props`, dan source. Tautan berikut merupakan referensi implementasi, bukan bukti bahwa perangkat tertentu sudah diuji.

- [.NET 10 SDK dan Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/10.0): SDK untuk build dan runtime yang disertakan dalam paket self-contained.
- [Instalasi .NET di Windows](https://learn.microsoft.com/en-us/dotnet/core/install/windows): perbedaan SDK, runtime, dan Desktop Runtime untuk WPF.
- [NAudio](https://github.com/naudio/NAudio): dependensi `NAudio.Core`, `NAudio.Wasapi`, dan `NAudio.Asio` versi 3.3.0 untuk output audio Windows. Decoder FLAC Noct berada di source proyek ini.
- [Lisensi NAudio pada commit paket 3.3.0](https://github.com/naudio/NAudio/blob/4f8889e2bd34c3978a25652c6c172fa1502ccba3/LICENSE): salinan disimpan di `licenses/NAudio/LICENSE.txt`.
- [Tool FLAC resmi](https://xiph.org/flac/download.html): `flac` dan `metaflac` untuk menghasilkan data referensi test. Tool ini tidak dikirim bersama Noct.
- [Inno Setup](https://jrsoftware.org/isdl.php): compiler installer Windows; skrip proyek memakai Inno Setup 6.

Pilihan library yang pernah dievaluasi, prompt bootstrap, dan rencana implementasi lama tidak menjadi persyaratan build. Batas perilaku yang masih berlaku dicatat di [DEVELOPMENT.md](DEVELOPMENT.md), sedangkan pemeriksaan manual ada di [VALIDATION.md](VALIDATION.md).
