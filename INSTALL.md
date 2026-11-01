# Installing HeapScope

HeapScope ships on [GitHub Releases](https://github.com/rklundt/HeapScope/releases) for Windows and Linux (x64). Each
release has:

| File | What it is |
|---|---|
| `HeapScope-vX.Y.Z-win-x64.zip` | The app (`HeapScope.exe`) and the CLI (`heapscope-cli.exe`) for Windows. No .NET install needed. |
| `HeapScope-vX.Y.Z-linux-x64.tar.gz` | The app (`HeapScope`) and the CLI (`heapscope-cli`) for Linux. No .NET install needed. |
| `HeapScope.Cli.X.Y.Z.nupkg` | The CLI alone as a `dotnet tool`, for any system with the .NET 10 runtime. |
| `HeapScope-vX.Y.Z-sbom-*.cdx.json` | Software bills of materials (CycloneDX) of the app, the CLI and the worker. |
| `SHA256SUMS` | The SHA-256 checksum of every file above. |

Capturing a snapshot of a running process works on Windows only. On Linux, HeapScope opens dumps taken with
`dotnet-dump` (see [Your first snapshot](#your-first-snapshot)). macOS is not shipped yet; it can be built from source
(see the README).

## Check the download

HeapScope's files are not signed. Check them against the release's checksums and its build provenance instead.

- Linux: in the download folder, `sha256sum --check --ignore-missing SHA256SUMS`.
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

## The CLI as a dotnet tool

If you have the .NET 10 runtime (or SDK), the CLI also installs as a `dotnet tool` on Windows and Linux. It is not on
nuget.org yet, so install it from the downloaded file:

1. Put `HeapScope.Cli.X.Y.Z.nupkg` in a folder of its own, for example `~/heapscope-packages`.
2. `dotnet tool install --global HeapScope.Cli --add-source ~/heapscope-packages`
3. Run `heapscope-cli --help`.

To update, put the new version's file in that folder and run `dotnet tool update --global HeapScope.Cli --add-source
~/heapscope-packages`. To remove it: `dotnet tool uninstall --global HeapScope.Cli`. The tool works like the CLI in the
archives (capture on Windows only); it is not tested on macOS.

## Your first snapshot

- Windows, in the app: the Processes page lists the .NET processes you can capture; pick one and capture a full dump
  or a gcdump. The CLI does the same: `heapscope-cli list`, then `heapscope-cli capture --pid <pid> --out app.dmp`.
- Linux: take a dump with `dotnet-dump collect --process-id <pid> --type Full` (install it with `dotnet tool install
  --global dotnet-dump`), then open the file in the app or run `heapscope-cli summary <dump>`.

To open a dump, HeapScope needs the dumped runtime's data access component (DAC). It finds it in the .NET runtimes
installed on the machine, in its own runtime when the versions match, or next to a dump that HeapScope captured. When it
cannot, the message says what to do (`--dac-folder`, or downloading it from Microsoft's symbol server when you allow it).

## Where HeapScope keeps its data

Sessions, logs and settings go to `%LOCALAPPDATA%\HeapScope` on Windows and `~/.local/share/HeapScope` on Linux, or
to the folder the `HEAPSCOPE_DATA_FOLDER` environment variable names. A snapshot's index cache goes next to its dump,
or into that folder when the dump's folder cannot be written. Logs are described in the README,
under "Logs and getting help".

## Updating and removing

There is no installer and nothing in the registry. To update, extract the new release next to the old one (and point
the Linux links at it); to remove HeapScope, delete its folder and the links. Your data folder stays until you delete
it. The [releases page](https://github.com/rklundt/HeapScope/releases) lists every version; watch the repository's
releases on GitHub to hear of new ones.
