# Architecture Decision Records

Each ADR: context, decision, consequences. ADR-001 to ADR-014 were decided with the product owner before sprint 0.1.
One file per ADR since sprint 3.8; amendments stay inside the ADR they amend, and its title and line here say "amended
by" the later ADR. A new ADR takes the next number and a line in this index ([ARCHITECTURE.md](../ARCHITECTURE.md)
section 13 links here).

- [ADR-001](ADR-001-platform-net-10-wpf-windows-x64.md) Platform: .NET 10, WPF, Windows x64 (amended by ADR-025: Avalonia on Windows, Linux and macOS)
- [ADR-002](ADR-002-target-processes-modern-net-only.md) Target processes: modern .NET only
- [ADR-003](ADR-003-snapshot-types-full-dumps-and-gcdumps-behind-one-abstraction.md) Snapshot types: full dumps and gcdumps behind one abstraction
- [ADR-004](ADR-004-scale-target-medium.md) Scale target: Medium (amended 2026-10-22)
- [ADR-005](ADR-005-compact-struct-of-arrays-object-graph.md) Compact, struct-of-arrays object graph
- [ADR-006](ADR-006-on-disk-index-cache.md) On-disk index cache
- [ADR-007](ADR-007-out-of-process-worker.md) Out-of-process worker (amended by ADR-025)
- [ADR-008](ADR-008-headless-core-and-cli.md) Headless core and CLI
- [ADR-009](ADR-009-application-stack.md) Application stack
- [ADR-010](ADR-010-diagnostics-libraries.md) Diagnostics libraries
- [ADR-011](ADR-011-build-hygiene.md) Build hygiene
- [ADR-012](ADR-012-git.md) Git
- [ADR-013](ADR-013-leaky-fixtures-as-source-of-truth.md) Leaky fixtures as source of truth
- [ADR-014](ADR-014-elevation.md) Elevation
- [ADR-015](ADR-015-adaptive-memory-modes.md) Adaptive memory modes (added 2026-10-22, follows from the ADR-004 amendment)
- [ADR-016](ADR-016-contracts-owns-the-wire-vocabulary.md) Contracts owns the wire vocabulary (added in sprint 0.1)
- [ADR-017](ADR-017-no-network-access-for-symbols-or-dac-by-default.md) No network access for symbols or DAC by default (added in sprint 0.1; amended by ADR-035)
- [ADR-018](ADR-018-diffs-compare-per-type-totals-no-object-identity-across-dump.md) Diffs compare per-type totals; no object identity across dumps (added in sprint 1.4; amended by ADR-032)
- [ADR-019](ADR-019-the-ui-process-is-never-power-throttled.md) The UI process is never power-throttled (added in sprint 1.5)
- [ADR-020](ADR-020-inspections-are-analysis-plugins-with-declared-capabilities.md) Inspections are Analysis plugins with declared capabilities (added in sprint 1.6)
- [ADR-021](ADR-021-live-monitoring-reads-eventcounters-in-the-front-end-s-proce.md) Live monitoring reads EventCounters in the front end's process (added in sprint 1.7)
- [ADR-022](ADR-022-license-agplv3-only-with-additional-terms-attribution-on-the.md) License: AGPLv3 only with additional terms, attribution on the main screen (added before sprint 1.8)
- [ADR-023](ADR-023-gcdump-capture-moves-to-the-worker.md) gcdump capture moves to the worker (added in sprint 1.9, amended by ADR-037: it stays in the front end)
- [ADR-024](ADR-024-retention-semantics-shared-by-paths-and-dominators.md) Retention semantics shared by paths and dominators (added in sprint 2.3)
- [ADR-025](ADR-025-ui-platform-avalonia-on-windows-linux-and-macos.md) UI platform: Avalonia on Windows, Linux and macOS (added in sprint 2.3.5, amends ADR-001)
- [ADR-026](ADR-026-sharing-results-an-exported-analysis-analysis-only-by-defaul.md) Sharing results: an exported analysis, analysis only by default (added in sprint 2.3.5)
- [ADR-027](ADR-027-dominator-children-kept-the-tree-served-flattened.md) Dominator children kept; the tree served flattened (added in sprint 2.4)
- [ADR-028](ADR-028-allocation-traces-read-in-the-worker-from-eventpipe-allocati.md) Allocation traces read in the worker from EventPipe allocation ticks (added in sprint 2.5)
- [ADR-029](ADR-029-analysis-path-per-snapshot-bookmarks-by-snapshot-file.md) Analysis path per snapshot; bookmarks by snapshot file (added in sprint 2.7)
- [ADR-030](ADR-030-object-sets-as-the-analysis-subject.md) Object sets as the analysis subject (added in sprint 3.1)
- [ADR-031](ADR-031-similar-retention-and-the-key-paths-diagram.md) Similar Retention and the key paths diagram (added in sprint 3.2)
- [ADR-032](ADR-032-objects-of-two-snapshots-matched-by-address.md) Objects of two snapshots matched by address (added in sprint 3.3, amends ADR-018)
- [ADR-033](ADR-033-one-capture-per-process-at-a-time-runs-recorded-in-their-ses.md) One capture per process at a time; runs recorded in their session (added in sprint 3.4)
- [ADR-034](ADR-034-runtime-leak-detectors-and-the-unreachable-set.md) Runtime leak detectors and the unreachable set (added in sprint 3.5)
- [ADR-035](ADR-035-a-dac-next-to-a-dump-is-loaded-only-once-trusted.md) A DAC next to a dump is loaded only once trusted (added in sprint 3.6, amends ADR-017)
- [ADR-036](ADR-036-index-caches-signed-with-a-per-user-key.md) Index caches signed with a per-user key (added in sprint 3.7)
- [ADR-037](ADR-037-live-sources.md) Live sources (added in sprint 3.8, amends ADR-023)
- [ADR-038](ADR-038-skiasharp-native-library-without-the-dng-sdk.md) SkiaSharp's native library without the DNG SDK (added 2026-10-30, outside a sprint)
- [ADR-039](ADR-039-distribution.md) Distribution (added in sprint 3.9)
