# HeapScope architecture: Error handling and logging

Part of [ARCHITECTURE.md](../ARCHITECTURE.md); the section numbers are the same as before the split (sprint 3.8), so
references such as "ARCHITECTURE.md 11" still name the right section.

## 11. Error handling and logging
- Microsoft.Extensions.Logging everywhere. The UI and the CLI each write a file per process and day,
  `heapscope-yyyyMMdd-<ui|cli>-<pid>.log` (`FileLoggerProvider` in Application, sprint 2.3.7), in
  `%LOCALAPPDATA%\HeapScope\logs` on Windows, `~/.local/share/HeapScope/logs` on Linux and
  `~/Library/Application Support/HeapScope/logs` on macOS (`Logging:File:Directory` overrides it). A file per process
  means two HeapScope processes never write over each other's lines (sprint 2.3.7 review H1). Each file starts with a
  header: the build with its source revision, the system, architecture, runtime, self-contained or not, the process and
  the log folder. Lines carry an ISO 8601 time with offset and the active scopes. A file rolls at 50 MB
  (`-2`, `-3`...); files older than 14 days are removed when a process opens its first file. `Logging:File:MinimumLevel`
  (Information by default) raises the detail for one run, and is passed to the worker (`--log-level`). The UI also logs
  to debug output, Avalonia's warnings (area, template and source type only) and failures before its host exists; the
  CLI also writes Warning and up to stderr. The worker logs single-line records to stderr, which the client forwards
  into its own log.
- **Correlation (sprint 2.3.7)**: every job (`JobTracker`: open, capture, inspections) and every CLI command runs under
  an `Activity`; its trace id is a scope of every line it causes, in the front end and, through StreamJsonRpc's activity
  tracing (W3C trace context in each request), in the worker. Job start and end lines carry the job id, title, time and
  outcome; the CLI logs its command line (paths and numbers as given, other values by their length) and exit code.
- **Provenance of results (sprint 2.3.7, review H2 to H4)**: the log backs up any result a user contests.
  - Each process's start: versions, system, architecture, runtime, options; the worker adds the protocol, its path, the
    GC settings and a versions line (loaders, index cache format, retention semantics, ClrMD, each inspection's version).
  - Each snapshot open: one identity line that ties the worker's snapshot id to the file: path, size, fingerprint,
    kind, capabilities, facts, process, runtime, capture time and its source, from the cache or the dump (or a reopen),
    memory mode, cache path and identity, writer and loader versions, counts, dropped references and roots, unreadable
    objects, incomplete segments, static roots, the DAC and why it was trusted, and the notes shown. A load from the dump
    also logs its phase times. The client logs its handle with the worker id on open, reopen and a changed-file refusal.
  - Each DAC choice (`DacChoice`): the file, the rule that accepted it (time stamp and size, file version, own build
    id, runtime build id with the installed-folder or version-string pairing), every file looked at with why it was not
    used; a symbol-server download is a warning. Kept in the cache metadata, so a reopen still says it.
  - Each answered query: `WorkerErrors.GuardAsync` logs the operation, the request and the result (`Provenance`), with
    page reads and probes at Debug. Inspections add their version, thresholds (`IInspection.Parameters`) and basis (what
    was scanned or skipped). Retention paths log each root shown (its kind and description) and each key path (root,
    type steps, objects, bytes), with stopped-early, truncated and the root path fallback. Compare logs how its pair was
    chosen; result set re-runs are logged. The dominator tree logs its focus, grouping, row count, total, the trail
    (types, addresses, retained), the first 20 open keys, the revealed key and its row, and the first ten rows of the
    list just opened (or the top); the icicle its widest bars; set retention the type, all and reachable members, what
    they retain, the lower bound and the first ten retained types. Each user action on the Retainers tab (expand,
    collapse, focus, grouping, a link from another tab, a set) logs one line under its own activity, which its tree,
    icicle and set queries join (sprint 2.4 and its review). Grid queries and panel loads run under an activity too, so their worker lines
    carry their trace id.
  - Monitoring logs each run's rules and, when a rule fires, the metric and its source, the threshold after steps, the
    baseline and the rearm mode (`RuleDecision`); the CLI logs its growth gate's decision.
- **What a log may hold (sprint 2.3.7 review M6)**: paths, file and process names, type, module and field names, counts,
  sizes, addresses, ids, versions, flags and HeapScope's own notes. Never field values or string contents from a dump,
  and never the user's filter text (logged by its shape: length and term counts): `Provenance` writes any other text by its length only, and
  error messages that would echo a filter leave it out. Logs are local evidence for support; they are not meant to be
  published, since paths name the user and the machine. A result meant to be shared goes through the shared analysis
  export (ADR-026), which strips names by default.
- Exceptions are caught at boundaries only: worker RPC methods, view model commands, CLI command handlers.
  Inner code throws specific exceptions (`CapabilityNotSupportedException`, `SnapshotFormatException`,
  `ElevationRequiredException`, `WorkerCrashedException`) and does not swallow them.
- Worker errors cross the pipe as JSON-RPC errors with a `HeapScopeErrorCode` as the JSON-RPC error code so clients
  can show a precise message.
- Users always see a plain-language message and a next step. Stack traces go to the log only.
- Navigation and polish (sprint 2.7): the analysis path logs each step opened, shown or closed by kind (Types,
  Instances, Object, TypeReferrers, TypePaths, Retainers, RetainedBySet, and Set and SetInstances since 3.1), its id on the path, its parent's id, its depth,
  the worker snapshot id and the step count, and each switch of snapshot, never a type name or an address; a CSV export
  logs the grid's fixed name, the worker result set it reads (whose query the worker logged), the rows expected and
  written, the columns, whether values were included and the time, never the file's path or a cell (a failure logs its
  exception type and HResult); bookmarks log counts, opens and file problems only. Provenance logs a filter by its shape
  (length and counts of terms, excluding terms and module terms), never its text.
- Object sets (sprint 3.1): a set is logged by its shape (`ObjectSetSpecs.Shape`: the kind, the first 20 type keys with
  their modules, the namespace or assembly, the inspection, row and baseline, and for objects their count and a short
  hash of the sorted addresses, never the addresses), in every request's provenance too. A resolution logs the
  snapshot id, objects, bytes, types, time and how it matched (the type entries matched for the keys, and the keys that
  matched nothing, with their modules; for an inspection row the rule version and whether its own objects or its hidden type, module and scope were
  used, and a re-run of the inspection with its reason); a cache hit logs the set and its size; evictions log at Debug.
  A set's dominators log the top members' count, what they retain, the members no root reaches and the five largest by
  type; set retention logs the shape and "n/a" as the lower bound of a set that is not whole types. The front end opens
  a set and switches its views in a `HeapScope.Sets` activity (the view's query and its worker lines share its trace
  id), logging the snapshot and the shape. A disputed set view is traced to what the set held.
- Retention views (sprint 3.2): Similar Retention logs its rule version, the reverse index's memory mode, the set's
  shape, its members, groups, unreachable members, the members with more referrers than were looked at, the grouping's
  time, and the five largest groups (`Provenance.Describe(RetentionGroupDto)`: number from 1, pattern key, objects,
  bytes, types and first type, path count, the paths shown as roots and type names); every other listed group at Debug.
  A retention group set logs its number, pattern key and the inner set's shape and size. The interface table logs the
  type entries, how many were not read and the first 20 of them with the reason; an interface set logs the first 20
  matching type names and the unread count. A set's key paths log the set's shape in place of the type. A growth gate
  logs its grouping.
- Comparison (sprint 3.3): a diff logs whether it matched objects and why not, the New, Dead and survived totals, whether
  retained bytes were added and where each side's dominators came from, and its column count; a compared set logs its
  change, the other snapshot, how many of the inner set's objects it kept and the survivors in all; a trend logs its
  snapshots in order, grouping, rows and the first rows' growth; the CLI's growth gate logs what it checked (total or a
  heap kind, per row or summed) and how many rows exceeded.
- Live workflow (sprint 3.4): a forced collection logs the target, whether its end was seen and the time; a refused
  capture or collection logs what it was, why it was asked and what was running; a run logs its id at the start, the
  allocation trace's path, size and length, and each time it is recorded in its session (snapshots, forced collections);
  reopening a past run logs its id, timeline, samples, events and snapshots; a rule deferred by a busy process logs once
  per busy stretch, and a re-armed rule says why; the trace's early stop logs its reason; every deletion logs the
  question (files and bytes), the answer, the paths and bytes removed, the files left (with the reason) and kept (paths
  of the user's own files, never their contents), or why it was refused; cleanup logs its cutoff, what it proposed and
  what it kept and why.
- Leak detectors (sprint 2.6): the inspection line carries the rule's version and thresholds, the baseline used (or
  none), the elapsed time, and the basis: what was scanned (static collections counted and in the baseline, delegate
  types, subscribers and publishers read, timers by state, types probed for a disposed flag, clients and scopes by
  state), what was left out and why, counted by reason (unreachable publisher, held by its own target, framework
  callback field, field not an event, one-shot or paused timer, unreadable field), every reported row with its counts,
  and the largest below the threshold, so a disputed finding is traced to its rule and inputs; the front end logs each
  run with its snapshot and baseline, and each baseline choice.
- Runtime leak detectors (sprint 3.5): each basis line counts what was scanned and left out (async state machines waiting,
  ended and unreadable, by the largest methods; finalizer queue objects by type; types reachable now and in the baseline,
  framework types left out, the largest growth below the thresholds); the unreachable set's line has its objects, bytes
  and the reachable count.
- DAC trust (sprint 3.6, ADR-035): every open and reopen logs, under its trace id, the DACs it trusts to load from next to
  a dump, each with its hash, file, reason (the user when opening which dump, HeapScope's capture, `--trust-dac` for this
  run) and since when; the worker's DAC choice line names the hash it loaded ("trusted by SHA-256 ... and loaded from a
  private copy"); a refusal lists the trusted folders searched and why each file was not used; the app logs its question
  and the answer; `TrustedDacs` logs each grant, a hash trusted already, a capture's copy left untrusted (elevated, or a
  copy that differs from its source) and a trust file it cannot read (kept as `.bak` before a new one is written).
- Session documents (sprint 3.6): `SessionLibrary` logs a current session replaced because its folder or document is
  gone, a current or opened session refused as unreadable with the reason, sessions left out of the list (a count at
  Information, each with its reason at Debug), a removed empty session and one kept because something else writes into
  it; a save that fails after its retries says so. A monitoring run refused in an unreadable session logs the reason.
- Allocation traces (sprint 2.5): opening a trace logs its file with its size and write time, the reader's version, the
  process (and that it came from the first allocation tick), the recording's start and duration, samples, frames, lost
  events, samples without a stack or with unresolved frames, whether the rundown arrived and TraceEvent's conversion
  problems; a re-read logs both file identities; a recording logs why it ended (duration, Stop, process exit, cancel);
  each query logs its interval, type and the rows it answered (type and method names with their bytes; for trees the
  open keys, the revealed row and the rows under it; for stacks every stack answered with its rank, innermost stack
  node and first 16 frames);
  user actions on the page (an interval, a type, an expansion, the tree kind) log under a `HeapScope.Allocations`
  activity whose trace id the worker's answer lines carry. Never event payloads beyond type names and sizes.
- Hardening (sprint 3.7): the worker logs where its index cache key came from (`Index cache key {Path}: Loaded`,
  `Created`, `ReplacedDamaged`) or, when the key file can be neither read nor created, a Warning naming the path, the
  error and the next step (this run signs with a key of its own; ADR-036). A cache not used logs its reason with the
  snapshot's note, for the base cache as for sidecars (`Untrusted` says whether it was unsigned or signed with another
  key). A gcdump refused as inconsistent names the reader's exception type in its message, and the worker logs a
  refusal's wrapped exception with its stack at Debug; any other error while building the graph is a HeapScope error,
  logged at Error with its stack. A capture whose dump could not be made private logs a Warning with the path and error,
  and the result says so. The app logs a session snapshot or Show in folder refused because the path is not on this
  machine. Paths and exception types only, never dump contents.
- Modularity (sprint 3.8): every view's query logs a worker refusal as shown ("{Section} shown as: {Message}") under the
  activity whose trace id the worker's answer lines carry, grids included (they showed refusals without logging them);
  an optional retained column the worker refused is logged once. An event listener that throws is logged as a Warning
  ("A listener of {Event} failed") and changes nothing else. A past run's timeline or trace that is not used logs which
  check refused it (not recorded, not local, outside the folder, through a link, missing, not a CSV file). A snapshot's
  open line names its unavailable features; the worker's log lines name the handler that answered (the operation name
  is unchanged; the shared services log under their own categories, such as `ObjectSetResolver` and `SnapshotStore`).
  The worker logs the reachability walks it keeps per snapshot. Every log file's header names the data folder and where
  it came from (the default, `HEAPSCOPE_DATA_FOLDER`), and the worker's start line names the folder it was given; the
  variable moves the logs too, so look for a run's log in `<data folder>/logs`.
