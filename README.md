# HeapScope

Powered by [HeapScope](https://github.com/rklundt/HeapScope) by Ray Klundt.

HeapScope is a desktop memory profiler for modern .NET applications (.NET 10) on Windows, Linux and macOS. It opens
memory snapshots (and captures them on Windows), shows what is on the heap and what grew between two snapshots, and
explains why objects are retained, through a desktop app or an equivalent command line (`heapscope-cli`). Snapshots
never leave the machine.

- Build: `dotnet build HeapScope.sln`
- Test: `dotnet test --solution HeapScope.sln`
- UI: `dotnet run --project src/HeapScope.UI`
- CLI: `dotnet run --project src/HeapScope.Cli -- --help`

Developer documentation: [docs/DEVELOPER.md](docs/DEVELOPER.md) and [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## License

Copyright (C) 2026 Ray Klundt.

HeapScope is licensed under the [GNU Affero General Public License, version 3 only](LICENSE), with
[additional terms](ADDITIONAL-TERMS.md) under Section 7 of that license. In short, and the files themselves govern:

- **Copyleft, including over a network.** Modified versions, and services built on them, must be released under the
  same license with their source code.
- **Attribution.** Every work based on HeapScope must keep the notice "Powered by HeapScope by Ray Klundt" (or "Based
  on HeapScope by Ray Klundt"), with HeapScope linked to https://github.com/rklundt/HeapScope. In a user interface it
  must be readable on the main screen (or a splash screen shown for at least five seconds): real text on an opaque
  background, at least 4.5:1 contrast, no smaller than the body text. Command line tools show it in help, version and
  startup output. Modified versions must be marked as such.
- **AI and machine learning (Section 6).** Text and data mining rights are reserved. Models trained, fine-tuned or
  evaluated on HeapScope must credit it in their documentation, and code that an AI system produces from HeapScope is a
  work based on it, carrying the attribution and the same license.
- **Commercial licenses** that waive some or all of these requirements are available; see [COMMERCIAL.md](COMMERCIAL.md).

Third-party components keep their own licenses; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
