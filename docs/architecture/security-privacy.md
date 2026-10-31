# HeapScope architecture: Security and privacy

Part of [ARCHITECTURE.md](../ARCHITECTURE.md); the section numbers are the same as before the split (sprint 3.8), so
references such as "ARCHITECTURE.md 12" still name the right section.

## 12. Security and privacy
- Full dumps contain everything in the process memory: connection strings, tokens, personal data. HeapScope
  treats every dump and every index cache as sensitive.
- Nothing is ever uploaded. There is no telemetry. Symbol and DAC downloads are off by default (ADR-017).
- A dump is untrusted input. Paths recorded in it (the runtime's folder, module paths) never lead the worker to a
  network share: the DAC lookup and ClrMD's file locator accept local fixed-drive paths only, and the dumped runtime's
  folder only when it is an installed shared runtime folder. A DAC in `Symbols:DacFolder` is native code the user chose
  to trust by putting it there. A DAC next to the dump came with the dump, so it is native code from whoever made the
  dump: since 3.6 (ADR-035) it is searched last and loaded only when trusted by its SHA-256 (the app asks, naming the file
  and its hash, and keeps a yes in `trusted-dacs.json`; the CLI takes `--trust-dac <sha256>` for one run; HeapScope's own
  capture trusts the DAC it copies from the target's runtime), and then from a private copy checked against the hash.
  Any DAC is loaded only when its PE image matches the dump's index (or, when the dump records none, its file version
  equals the runtime's). This local-only guarantee holds while the symbol server opt-in is off; with it on, ClrMD's own
  locator is used. `trusted-dacs.json` (in the user's own HeapScope data folder, never beside a configured session root,
  which may be shared) holds per trusted DAC its SHA-256, the path it was found at, when, and why (which names the dump
  it was opened with); the private copies sit under `%LOCALAPPDATA%/HeapScope/dac/<sha256>/`. Deleting an entry, or the
  file, revokes the trust.
- Crafted input (sprint 3.7, peer review P13): every reader of a file HeapScope did not write bounds what the file states.
  The address index cannot loop on a span of 2^63 or more; the minidump truncation check reads each memory list once and
  is cancellable; the vendored gcdump reader refuses counts its stream cannot hold before allocating (`CHANGES.md` change
  5; the address table must cover every node), and the loader's object, reference and root walks share one budget of a
  child per byte of the file, check every child and type index and duplicate addresses, and refuse an inconsistency the
  reader throws with a reason; HeapScope's own errors are never reported as a bad file (3.7 review H5). Mutation tests
  run in every CI run and nightly with more iterations (`fuzz.yml`).
- Index caches are signed with a per-user key (sprint 3.7, ADR-036): a cache shipped next to a dump, written by another
  user or machine, or forged with recomputed checksums is never used.
- Private files (sprint 3.7, peer review P15): on Windows a file HeapScope creates is created with an owner-only access
  list in one step (the profile's list does not cover a dump's cache in a folder the user picked), and a finished dump is
  made owner-only (`PrivateFiles.Protect`; the runtime writes a dump with the folder's permissions, so until the capture
  ends the folder's list applies; a dump that cannot be protected is logged and the capture says so); off Windows,
  HeapScope's own data folders (the cache key, trusted DACs and their copies, trace conversion) are made owner-only
  (0700) when they exist with a wider mode, and refused when they belong to another user
  (`CreatePrivateDirectory`); trace conversion (`.etlx`) uses HeapScope's data folder instead of the shared `/tmp` off
  Windows; partial traces and CSV exports are created new and private, never written into a file someone made first.
  The partial-file sweep skips links and never sweeps a folder the user picked.
- Paths a received `session.json` names (sprint 3.7, peer review S3): a run's timeline is opened only when it is a
  regular `.csv` file inside the session folder, reached through no folder link (the rule deleting a session uses); its
  trace only inside the folder; a snapshot on a network path (any two leading separators, `\??\`, a mapped network
  drive) is never looked at or opened.
- Supply chain (sprint 3.7, peer review P16): packages come from nuget.org only (`nuget.config`, `<clear />` and source
  mapping), every project has a `packages.lock.json` and CI's builds restore in locked mode (a publish for a runtime
  identifier, including the release builds, restores unlocked: the gap is in the Backlog), Actions are pinned to release
  commits with no token left in checkouts, Dependabot proposes NuGet and Actions updates, and `SECURITY.md` says how to
  report a vulnerability privately.
- Releases (sprint 3.7, peer review P17): publishing a GitHub release builds its tag for every runtime identifier and
  attaches the tarballs, an SBOM (CycloneDX) of the app, the CLI and the worker, and `SHA256SUMS`, all with build
  provenance attestations in GitHub's attestation store (`release.yml`; the builds hold read access only, one job that
  builds nothing attests and attaches; a manual run is a dry run by default); CI and release builds are deterministic (`ContinuousIntegrationBuild`).
- Published builds (sprint 3.6, peer review P3; CI uploads them as artifacts) carry `LICENSE`, `ADDITIONAL-TERMS.md`,
  `COMMERCIAL.md`, `THIRD-PARTY-NOTICES.md` and every shipped package's own license and notice files under `licenses/`;
  the TraceEvent native binaries (`KernelTraceControl.dll`, `msdia140.dll`), which nothing calls and whose redistribution
  terms are not reviewed, are left out of them. Since 2026-10-30 SkiaSharp's native library in them is HeapScope's own
  build without the DNG SDK and piex (ADR-038), downloaded by pinned checksum and built, checked and attested by
  `skia-native.yml`. `build/check-package.sh` checks all three on every package job.
- The pipe is restricted to the current user. The worker accepts no other connection.
- The index cache stores type names, static field names, addresses and sizes, not field values or string
  contents. Its sidecars (sprint 2.1: `<snapshot>.rev.hsidx`, the reverse references; sprint 2.3: `<snapshot>.dom.hsidx`,
  immediate dominators and retained sizes, and since 2.4 the tree's children) store object indices and sizes only and
  sit next to it, beside the dump or under the fallback cache root; deleting a snapshot in the app deletes them with it
  (sprint 3.4).
- The UI and the CLI say once per capture that the dump contains secrets and where it was written.
- Dumps live in the session folders under `%LOCALAPPDATA%\HeapScope\sessions` (each dump made owner-only when it is
  finished, above) unless `--out` puts them elsewhere; HeapScope never moves them. The app deletes a snapshot with its
  caches, a session, or the sessions not opened for a chosen number of days, after showing the files (sprint 3.4); the
  CLI deletes nothing.
- Elevated targets require HeapScope to run elevated (ADR-014). HeapScope never tries to elevate silently.
- Traces (`trace`, `--smoke-trace`, sprint 1.5) hold method and type names and event data such as exception messages,
  not heap contents; the CLI says so after each one, and they are written where the user asks (never uploaded).
- Inspecting an object reads its field values and string text from the dump on demand (sprint 1.5); they are shown,
  never written to the index cache or the log. The same holds for inspections (sprint 1.6): duplicate strings shows
  the first 100 characters of each duplicated value in the UI and the CLI only; logs record counts and sizes.
- Monitoring (sprint 1.7) reads the target's runtime counters (sizes, counts, rates), never its memory; the timeline
  file in the session folder holds those numbers and the snapshot file names.
- Shared results (ADR-026, sprint 2.3.5; built by the shared analysis export): what a team shares is an exported
  analysis, never a dump or an index cache. By default it holds type, field and module names, counts, sizes and paths;
  string text and field values only when the person exporting opts in for that export, which the export then states on
  its first line. Exports are written where the user asks and never uploaded. Sprint 2.7's CSV export of a grid is the
  first form: value columns (`ResultColumn.HasValues`) are left out unless the person answers yes for that export.
- Bookmarks (sprint 2.7) are kept in `bookmarks.json` in HeapScope's data folder (owner-only): the snapshot
  file's full path, size and write time, the object's address and type name, a color and a time; no field values or
  string text.
- CI (sprint 2.3.5) dumps only the LeakyConsole fixture on GitHub's runners and never uploads the dump. The builds it
  uploads (sprint 2.3.6) hold build output only.
- Off Windows, HeapScope's own folders and files are the user's alone (sprint 2.3.6, `PrivateFiles`): sessions, logs,
  index caches and their sidecars, monitoring timelines and traces are created 0700 (folders) and 0600 (files) instead
  of the process umask, which often lets other users read them. A folder the user picked keeps its permissions; on
  Windows the files get an owner-only access list (sprint 3.7, above).

- Allocation traces (sprint 2.5) hold type and method names, times and sizes, not heap contents. Record allocations writes
  them into the session folder (user-only, like dumps); the CLI writes them where `--out` says. Reading one writes
  TraceEvent's `.etlx` (the same names, indexed) into a private folder (`HeapScope/etlx` under the user's temp folder on
  Windows, under HeapScope's data folder elsewhere since 3.7, 0700) and deletes it when the read ends, failed or canceled; nothing is written next to the
  trace. A worker killed mid-read leaves its `.etlx`: the next worker deletes those over an hour old that no reader
  holds, and logs it; a canceled recording's file is deleted, and the session sweep removes `*.nettrace.partial` files.
  Methods are named from the trace's own events, never a symbol server (ADR-017).
