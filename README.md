# WinPDF

A modern, lightweight, 100% native PDF reader and annotator for Windows 11, inspired by
[GNOME Papers](https://gitlab.gnome.org/GNOME/papers).

- WinUI 3 (Windows App SDK) + C# / .NET 10, MVVM (`CommunityToolkit.Mvvm`)
- Open-source rendering: PDFium + Win2D (GPU), touch-first pan / pinch-to-zoom
- English and Brazilian Portuguese

## Build

```powershell
dotnet build WinPDF.sln -c Debug -p:Platform=x64
.\src\WinPDF\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\WinPDF.exe
```

See [AGENTS.md](AGENTS.md) for architecture, rendering pipeline and contribution rules.
