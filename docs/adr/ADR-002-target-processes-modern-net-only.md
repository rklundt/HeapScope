# ADR-002 Target processes: modern .NET only

- Context: the primary use case is ASP.NET Core; .NET Framework adds a second runtime model and attach path.
- Decision: support .NET 5 and later targets. .NET Framework is a backlog item.
- Consequences: process discovery uses the diagnostics IPC of modern runtimes only.
