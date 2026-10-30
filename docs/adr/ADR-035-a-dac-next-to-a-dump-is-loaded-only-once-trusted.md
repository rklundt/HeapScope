# ADR-035 A DAC next to a dump is loaded only once trusted (added in sprint 3.6, amends ADR-017)

- Context: the peer review of 3.5.0 (P2) found that a DAC in the dump's folder was loaded as native code into the
  worker, matched only on values the dump supplies (PE time stamp and size, or build id and version). A zip holding a
  dump and a planted DAC ran its code; Linux and macOS have no signature check at all. ADR-017 listed the dump's folder
  with the installed runtimes as if it were as trustworthy.
- Decision: the DAC search has two parts. Installed runtimes, the dumped runtime's folder (when installed),
  `Symbols:DacFolder` and HeapScope's own runtime are trusted as before (`DacLocator.CandidateDirectories`). The dump's
  folder and `dac/<version>` beside it (`BesideDumpDirectories`) are searched only when those find nothing, and a match
  there is loaded only when its SHA-256 is trusted (`ClrMdLoaderOptions.IsTrustedDac`); otherwise the load fails with
  `UntrustedDacException`, which the worker answers as `DacNotTrusted` (1014) with the file and its hash
  (`DacTrustDetails`). A trusted DAC is copied into a private folder under its hash (`%LOCALAPPDATA%/HeapScope/dac`) and
  loaded from the copy after checking it, so the file loaded is the file hashed. A full dump opened from its index cache
  loads no DAC at open, so the worker runs the same lookup and hash then, before it reads the cache and without loading
  (review H1 and re-review), and refuses the same way: the question comes at open whether the dump is loaded or its
  cache is used. Only a DAC that needs trust stops a cache open; a missing one, or a dump of another system, is reported
  by the field reader when used, as before. The request's `CheckDac` (default true) turns the check off: after the user
  declines, the app opens the cache anyway with field values and strings refused, and the CLI never checks, so its
  commands that need no DAC (summary, diff, trend, retention) work on any cached dump while the ones that read with it
  refuse and name `--trust-dac`. A reopen after a worker restart never checks (nobody is there to answer). Trust lives in Application
  (`TrustedDacs`): the app asks when an open is refused and keeps a yes in `trusted-dacs.json` in the user's own data
  folder; the CLI's `--trust-dac <sha256>` trusts for one run; HeapScope's capture trusts the DAC it copies from the
  target's runtime when the copy equals the runtime's file and HeapScope is not elevated (elevated, the target's runtime
  folder may belong to a less privileged user, so the open asks). Every open request carries the trusted hashes
  (protocol 20); the worker keeps them for its lifetime, so the field reader, which opens the dump again later, has the
  same trust.
- Consequences: a dump received with its DAC asks once per DAC, for a load or a cache open; a "no" still opens its cache
  without field values, but a dump that must be loaded cannot be read at all. Trust is by content, not by path, so a
  different file at the same path is asked about again. On Windows ClrMD's own signature check
  (`VerifyDacOnWindows`, on by default) is expected to refuse an unsigned DAC before loading it; that is not yet
  confirmed by a test (Backlog), and on Linux and macOS the trust step is the only check. Every full-dump cache open now
  opens the dump with ClrMD and runs the DAC lookup; its cost on a large dump is to be measured (Backlog).
