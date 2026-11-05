# Installing HeapScope

HeapScope ships on [GitHub Releases](https://github.com/rklundt/HeapScope/releases) for Windows and Linux (x64), plus untested macOS packages. Each
release has:

| File | What it is |
|---|---|
| `HeapScope-vX.Y.Z-win-x64.zip` | The app (`HeapScope.exe`) and the CLI (`heapscope-cli.exe`) for Windows. No .NET install needed. |
| `HeapScope-vX.Y.Z-linux-x64.tar.gz` | The app (`HeapScope`) and the CLI (`heapscope-cli`) for Linux. No .NET install needed. |
| `HeapScope-vX.Y.Z-osx-arm64.tar.gz`, `HeapScope-vX.Y.Z-osx-x64.tar.gz` | The app and the CLI for macOS (Apple silicon, Intel). **Untested: built on Linux, not on a Mac.** See [macOS](#macos-untested). |
| `HeapScope.Cli.X.Y.Z.nupkg` | The CLI alone as a `dotnet tool`, for systems with the .NET 10 SDK. |
| `HeapScope-vX.Y.Z-sbom-*.cdx.json` | Software bills of materials (CycloneDX) of the app, the CLI and the worker. |
| `SHA256SUMS` | The SHA-256 checksum of every file above. |

Capturing a snapshot of a running process works on Windows only. On Linux, HeapScope opens dumps taken with
`dotnet-dump` (see [Your first snapshot](#your-first-snapshot)).

## Check the download

HeapScope's files are not signed. Check them against the release's checksums and its build provenance instead.

- Linux: in the download folder, `sha256sum --check --ignore-missing SHA256SUMS`.
- macOS: `shasum -a 256 HeapScope-vX.Y.Z-osx-arm64.tar.gz`, and compare the hash with its line in `SHA256SUMS`.
- Windows (PowerShell): `Get-FileHash .\HeapScope-vX.Y.Z-win-x64.zip`, and compare the hash with its line in
  `SHA256SUMS` (PowerShell prints it in capitals).
- Provenance, with the [GitHub CLI](https://cli.github.com/): `gh attestation verify <file> --repo rklundt/HeapScope`
  confirms that GitHub Actions built the file from this repository.

## Windows

1. Download `HeapScope-vX.Y.Z-win-x64.zip`.
2. Extract it where you keep programs, for example `%LOCALAPPDATA%\Programs`. It holds one folder,
   `HeapScope-vX.Y.Z-win-x64`. Keep the folder whole: the `worker` folder in it does the analysis.
3. Run `HeapScope.exe`. The first time, Windows SmartScreen may say "Windows protected your PC", because the files are
   not signed: choose **More info**, then **Run anyway**. To skip that for every file at once, unblock the zip before
   extracting it (right-click it, **Properties**, tick **Unblock**), or in PowerShell: `Unblock-File .\HeapScope-vX.Y.Z-win-x64.zip`.
4. For the CLI, run `heapscope-cli.exe` from that folder, or add the folder to your `PATH`.

## Linux

1. Install the two libraries HeapScope needs, if your system does not have them: ICU and fontconfig.
   - Debian and Ubuntu: `sudo apt install libicu74 libfontconfig1` (the ICU package's number follows the release, for
     example `libicu70` on Ubuntu 22.04; `apt search libicu` shows yours).
   - Fedora: `sudo dnf install libicu fontconfig`.

   The app also needs an X11 or XWayland desktop session, which desktop systems have.
2. Download `HeapScope-vX.Y.Z-linux-x64.tar.gz` and extract it where you keep programs:

   ```bash
   mkdir -p ~/.local/opt && tar -xzf HeapScope-vX.Y.Z-linux-x64.tar.gz -C ~/.local/opt
   ```

3. Put the commands on your path (keep the folder whole: the `worker` folder in it does the analysis):

   ```bash
   mkdir -p ~/.local/bin
   ln -sf ~/.local/opt/HeapScope-vX.Y.Z-linux-x64/HeapScope ~/.local/bin/heapscope
   ln -sf ~/.local/opt/HeapScope-vX.Y.Z-linux-x64/heapscope-cli ~/.local/bin/heapscope-cli
   ```

4. Run `heapscope` for the app, `heapscope-cli --help` for the CLI.

## macOS (untested)

**These packages are built on Linux, not on a Mac, and have not been run.** They are not signed or notarized, so macOS may
refuse to open them. The steps below are what should work; nobody has confirmed them yet.

1. Download `HeapScope-vX.Y.Z-osx-arm64.tar.gz` (Apple silicon) or `HeapScope-vX.Y.Z-osx-x64.tar.gz` (Intel).
2. Extract it in Terminal: `tar -xzf HeapScope-vX.Y.Z-osx-arm64.tar.gz`. Keep the folder whole: the `worker` folder in it
   does the analysis.
3. Run `./HeapScope` from that folder, or `./heapscope-cli` for the CLI.

Capturing needs Windows, so take a dump with `dotnet-dump` and open the file in the app or the CLI (see
[Your first snapshot](#your-first-snapshot)).

### If macOS refuses it

Also unconfirmed. Both steps turn off a macOS check for this folder, so [check the download](#check-the-download) first.

- It says the app "cannot be opened" or "cannot be verified": remove the download mark with
  `xattr -dr com.apple.quarantine <folder>`, or allow it under System Settings, Privacy & Security, Open Anyway.
- Terminal says `killed` on Apple silicon: give the files a local signature, the `worker` folder included:
  `find <folder> -type f \( -perm +111 -o -name '*.dylib' \) -exec codesign --force --sign - {} \;`

If you try a macOS package, please say how it went in an [issue](https://github.com/rklundt/HeapScope/issues): nobody has
reported yet.

To build on your Mac instead, with the .NET 10 SDK, run `bash build/publish.sh osx-arm64 <folder>` (or `osx-x64`) in a clone
of the repository. That build is untested too.

## The CLI as a dotnet tool

If you have the .NET 10 SDK, the CLI also installs as a `dotnet tool` on Windows and Linux (x64; it reads dumps of its
own architecture). Installing needs the SDK (`dotnet tool` is an SDK command); running it needs the .NET 10 runtime,
which the SDK includes. Without the .NET 10 runtime, `heapscope-cli` stops with .NET's own "You must install or update
.NET" message and a download link. Without the SDK, use the archive for your system instead: it needs no .NET.

The tool is not on nuget.org yet, so install it from the downloaded file, and from that file only: a package of the
same name on nuget.org would not be HeapScope's. If `~/.nuget/packages/heapscope.cli` exists and you did not put it
there, delete it first, so no earlier download of that name is reused.

1. Make a folder of its own, for example `heapscope-packages` in your home folder, and put `HeapScope.Cli.X.Y.Z.nupkg`
   in it.
2. In that folder, save this as `nuget.config`. It tells `dotnet` to use this folder and no other package source:

   ```xml
   <?xml version="1.0" encoding="utf-8"?>
   <configuration>
     <packageSources>
       <clear />
       <add key="heapscope" value="." />
     </packageSources>
   </configuration>
   ```

3. Install it, naming the version and that file (Linux shown; in PowerShell use `$HOME\heapscope-packages\nuget.config`):

   ```bash
   dotnet tool install --global HeapScope.Cli --version X.Y.Z --configfile ~/heapscope-packages/nuget.config
   ```

4. Run `heapscope-cli --help`.

To update, put the new version's file in that folder and run `dotnet tool update --global HeapScope.Cli --version X.Y.Z
--configfile ~/heapscope-packages/nuget.config` with the new version (in PowerShell,
`$HOME\heapscope-packages\nuget.config`). To remove it: `dotnet tool uninstall --global
HeapScope.Cli`. The tool works like the CLI in the archives (capture on Windows only); it is not tested on macOS.

## Your first snapshot

- Windows, in the app: the Processes page lists the .NET processes you can capture; pick one and capture a full dump
  or a gcdump. The CLI does the same: `heapscope-cli list`, then `heapscope-cli capture --pid <pid> --out app.dmp`.
  Then `heapscope-cli summary app.dmp` shows what is on its heap. Capturing a process that runs as administrator needs
  HeapScope run as administrator too.
- Linux: take a dump with `dotnet-dump collect --process-id <pid> --type Full`, then open the file in the app or run
  `heapscope-cli summary <dump>`. Without .NET, use dotnet-dump's standalone download
  (https://aka.ms/dotnet-dump/linux-x64; make it executable with `chmod +x`); with the .NET SDK, `dotnet tool install
  --global dotnet-dump` installs it.
- macOS (untested): the same `dotnet-dump collect` command, with `dotnet-dump` from `dotnet tool install --global dotnet-dump`.

To open a dump, HeapScope needs the dumped runtime's data access component (DAC), which must match the .NET version
the dumped process ran on. HeapScope finds it in the .NET runtimes installed on the machine and in its own runtime when
the versions match, so a dump from a server with another .NET version usually needs that runtime installed, or its DAC
in a folder you name (`--dac-folder`, or the `Symbols:DacFolder` setting). A DAC that came with the dump (in its folder)
is native code from whoever made the dump: the app asks before using it and remembers a yes, and the CLI uses it only
with `--trust-dac <sha256>`, which its refusal names. A DAC that HeapScope's own capture copied is trusted. When none is
found, the message says what to do, including downloading it from Microsoft's symbol server if you allow that.

## Where HeapScope keeps its data

Sessions and logs go to `%LOCALAPPDATA%\HeapScope` on Windows, `~/.local/share/HeapScope` on Linux (under `$XDG_DATA_HOME` when it is set) and
`~/Library/Application Support/HeapScope` on macOS, or to the folder the `HEAPSCOPE_DATA_FOLDER` environment variable names. Sessions hold
full dumps, which can contain anything the dumped process held (passwords and keys included). A snapshot's index cache
goes next to its dump, or into that folder when the dump's folder cannot be written. Logs are described in the [user guide](docs/USER-GUIDE.md#logs-and-getting-help),
under "Logs and getting help".

## Settings

HeapScope reads its settings from an `appsettings.json` file you create beside `HeapScope` and `heapscope-cli`, and from
environment variables that name the setting with `__` between its parts. For example, a DAC folder:

```json
{ "Symbols": { "DacFolder": "/srv/dacs" } }
```

or `Symbols__DacFolder=/srv/dacs` in the environment. An `appsettings.json` stays behind when you extract a new
release, so copy it across; for the dotnet tool, use environment variables, since an update replaces the tool's folder.

## If HeapScope does not start

- Run it from a terminal to see its message, and look in the `logs` folder of the data folder above. For a more
  detailed log of one run, set `Logging__File__MinimumLevel=Debug` (the app also takes
  `--Logging:File:MinimumLevel Debug`).
- Windows SmartScreen: see the Windows steps above. Windows 11's Smart App Control blocks programs that are not signed
  and has no "Run anyway"; HeapScope's builds are not signed, so they cannot run where it is on.
- macOS: see [If macOS refuses it](#if-macos-refuses-it).
- Linux, a message about ICU ("Couldn't find a valid ICU package"): install ICU (see the Linux steps above).
- Linux, the app stops while starting its display (SkiaSharp cannot load `libSkiaSharp`): install fontconfig.
- The dotnet tool, "You must install or update .NET": install the .NET 10 runtime, or use the archive.

## Updating and removing

There is no installer and nothing in the registry. To update, extract the new release next to the old one (and point
the Linux links at it, and copy any `appsettings.json`); to remove HeapScope, delete its folder and the links. Your
data folder, with its dumps, stays until you delete it; on Windows, trace conversions may also leave files in
`%TEMP%\HeapScope`. The [releases page](https://github.com/rklundt/HeapScope/releases) lists every version; watch the repository's
releases on GitHub to hear of new ones.
