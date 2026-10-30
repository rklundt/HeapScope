# ADR-001 Platform: .NET 10, WPF, Windows x64

Status: amended by [ADR-025](ADR-025-ui-platform-avalonia-on-windows-linux-and-macos.md) (the UI is Avalonia on Windows, Linux and macOS, not WPF).

- Context: the users are Windows .NET developers; a desktop app gives rich, fast grids and charts.
- Decision: .NET 10, WPF, Windows 10/11 x64 only.
- Consequences: no macOS or Linux UI. Core libraries stay platform neutral where cheap, which keeps Linux
  dump analysis possible later.
- Amended by ADR-025 (sprint 2.3.5): the UI moves to Avalonia on Windows, Linux and macOS; the engine, worker and CLI
  already run on Linux. Since sprint 2.3.6 the assemblies are AnyCPU, so x64 and arm64 machines run them natively; WPF
  is retired.
