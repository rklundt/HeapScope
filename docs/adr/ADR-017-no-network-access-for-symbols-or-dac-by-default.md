# ADR-017 No network access for symbols or DAC by default (added in sprint 0.1; amended by ADR-035)

- Context: ClrMD can download the data access component (DAC) and symbols from Microsoft's symbol server, and it
  ships Azure.Identity and MSAL for that purpose. A download sends module identifiers off the machine, which
  conflicts with "never send anything" (section 12), yet a dump from a runtime version not installed locally
  cannot be read without the matching DAC.
- Decision: the ClrMD loader configures its symbol locator explicitly with no remote servers. It looks for the
  DAC in the locally installed runtimes, next to the dump, and in a user-configured folder. When the DAC is
  missing, the user gets a message naming the exact runtime version needed and how to obtain it. A per-user
  opt-in setting (`Symbols:AllowMicrosoftSymbolServer`, default off) may later enable downloads, with a privacy
  note shown when it is turned on.
- Consequences: dumps from machines with other runtime versions need a manual step unless the user opts in.
- Implemented in sprint 1.2 and confirmed by the product owner: `DacLocator` searches the dumped runtime's folder
  (only inside an installed runtime folder, see the amendment), the installed shared runtimes, the dump's folder and
  `Symbols:DacFolder`, and
  accepts a file only when its PE time stamp and image size match the dump's index. `LocalOnlyFileLocator` replaces
  ClrMD's default locator. The opt-in `Symbols:AllowMicrosoftSymbolServer` restores ClrMD's own lookup. A missing DAC
  fails with `RuntimeComponentMissing` (1009) naming the runtime version, the file and the folders searched. The UI and
  CLI read the `Symbols` section (the CLI also takes `--dac-folder` and `--allow-symbol-server`) and pass it to the
  worker on its command line; the worker reads options from its command line only, never from environment variables
  or settings files.
- Amended in the 1.2 review: the opt-in is per installation (appsettings next to the executable, or the CLI options),
  not per user. The privacy note is a warning logged by the client (shown on the CLI's stderr) and by the worker
  whenever downloads are on. Candidate folders are local fixed-drive paths only; the dumped runtime's folder counts only
  inside an installed runtime folder; `dac\<runtime file version>` next to the dump is a candidate (where capture keeps
  the target's DAC, sprint 1.1); every installed runtime folder is a candidate (the dump records the file
  version, folders use the product version) and the PE match picks the right one; when the dump records no index the
  file version must equal the runtime version.
- Amended in sprint 2.3.5 (review H1, M1): on Linux and macOS a path is local when the mount that holds it, after
  symbolic links are resolved, is a local disk (fixed, container overlays included, or tmpfs); network mounts (NFS,
  SMB, autofs, FUSE), `/proc`, `/sys` and `/dev` are refused, and a mount is checked before anything on it is looked at
  (`UnixLocalPath`). On Linux the mount types are read as text from `/proc/self/mountinfo` against a list of local
  types, so no mount is ever asked (a hung network mount cannot block a lookup); a mount that cannot be described is
  not local. A candidate image must be a local file of at least a header's size (pipes and devices never are).
  A Linux or macOS DAC is matched by the dump's build id or UUID: its own, or its runtime library's (`libcoreclr`) when
  the dump archives it that way; then the pair is accepted from an installed runtime folder, or elsewhere only when
  both files embed the same `@(#)Version` string. A DAC matched by build id is the exact build, so ClrMD's own version
  check, which reads some core files' version as 0.0, is skipped for it.
- Amended in sprint 3.6 (ADR-035): the dump's folder and `dac\<runtime file version>` beside it are searched last, and
  a DAC there is loaded only once trusted by its SHA-256.
