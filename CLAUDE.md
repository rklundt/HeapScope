# HeapScope

HeapScope is a desktop memory profiler for modern .NET applications (.NET 10, an Avalonia app on Windows, Linux and
macOS since sprint 2.3.6, ADR-025; capture is Windows only for now), replacing
the core of JetBrains dotMemory. Primary use case: find what is growing and why it is retained in an ASP.NET
Core API run locally under replayed traffic. Snapshots (full dumps and gcdumps) are loaded by an
out-of-process worker into a compact struct-of-arrays object graph, cached on disk, and explored through the
desktop app or an equivalent CLI. Baseline machine: 16 GB RAM; use more when available.

## Current context (read first after a compact)
`CURRENT-CONTEXT.md` at the repo root (git-ignored) holds the working state: the active sprint, what is done and not,
open findings, measurements, decisions made in conversation, and next steps. Read it before anything else when
resuming. Keep it current at milestones (task done, decision made, investigation concluded); `/close-sprint`
resets it for the next sprint.

## Docs (read before working)
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md): layers, process model, graph format, budgets, ADRs. Read
  before any design or code change.
- [docs/DEVELOPER.md](docs/DEVELOPER.md): build, run, conventions, testing, Definition of Done, severity
  rubric. Read before writing code.
- [docs/SPRINTS.md](docs/SPRINTS.md): status, sprint scopes, acceptance criteria, backlog. Read the active
  sprint's section before starting work.
- `docs/reviews/sprint-X.Y.md`: review findings and decisions per sprint.

## Non-negotiable rules
- Layering (ARCHITECTURE.md 3.1): Core references nothing; Analysis, Storage, Loaders, Capture reference only
  Core; UI.ViewModels references only Application and Contracts (no UI toolkit), and UI only those and
  UI.ViewModels. The architecture test enforces it.
- No code without an active sprint (status In Progress in SPRINTS.md).
- Zero warnings; warnings are errors.
- No em dashes or en dashes in any docs, code or UI text. Use a hyphen or a comma.
- Never commit without `/close-sprint`. One commit per sprint on `sprint/X.Y`, squash merged into `develop` by PR,
  and one annotated tag `sprint-X.Y` on `develop`. Work outside a sprint that the owner asks for uses a short
  `fix/...` or `chore/...` branch and a PR. `main` and `develop` change only by PR (a ruleset enforces it); releases
  (`develop` to `main`, tag `vX.Y.0`) happen only when the owner says so (DEVELOPER.md 7).
- The owner is the sole author: no Co-Authored-By or generated-by lines in commits, PR titles or PR bodies.
- Never one managed object per heap object (ADR-005). Hot paths use spans, indices and pooled buffers.
- Never upload or log snapshot contents. Dumps and index caches are sensitive.
- Definition of Done (DEVELOPER.md 9): zero warnings, all tests pass, acceptance criteria met or deferred by
  the human, review file with High fixed and Moderate decided, docs current, architecture test passes.

## Licensing (required)

This project is AGPLv3 only with additional terms (see LICENSE, ADDITIONAL-TERMS.md, COMMERCIAL.md). Owner: Ray Klundt.

- Never edit LICENSE, ADDITIONAL-TERMS.md or COMMERCIAL.md unless I explicitly ask.
- Every new source file I own gets the standard license header (copy from an existing file).
- Never remove, hide, shrink, restyle or move the "Powered by HeapScope by Ray Klundt" attribution, or lower its contrast below 4.5:1. It must stay real text on a solid opaque background on the main screen. Any UI change must be checked against this.
- Before adding a dependency, check its license. MIT, BSD, Apache-2.0, LGPL, GPLv3 and AGPLv3 are fine. Stop and ask me before adding anything GPLv2-only, proprietary, SSPL, "non-commercial" or unlicensed.
- When adding, removing or vendoring a dependency, update THIRD-PARTY-NOTICES.
- Before any Avalonia update (ADR-025), check that no Avalonia component HeapScope uses has moved to a paid license
  (Avalonia sells "Accelerate" components; TreeDataGrid moved there). Use only MIT (or other allowed) components; if one
  we use went paid, stop and ask me before updating.
- When copying code from outside sources, record its origin and license in THIRD-PARTY-NOTICES. Do not paste code from incompatible licenses.

## Sprint workflow
1. `/start-sprint [id]`: analyze, ask, plan the tasks in SPRINTS.md, set In Progress.
2. Implement task by task with tests, build green throughout.
3. `/wrap-sprint`: build, test, three reviewers, fix High, decide Moderate, update docs, set Wrapping.
4. `/close-sprint`: verify Definition of Done, check every doc describes what was built, set Closed and the
   version, commit on `sprint/X.Y`, PR into `develop`, merge (autonomous mode), tag, reset `CURRENT-CONTEXT.md`.
5. Next sprint.

## Commands
- Build: `dotnet build HeapScope.sln`
- All tests: `dotnet test --solution HeapScope.sln`
- Full gate: `./build/gate.ps1` (Debug and Release builds, all tests, history in `artifacts/gate/history.csv`)
- Fast tests: `dotnet test --solution HeapScope.sln --filter-not-trait "Category=Integration" --filter-not-trait "Category=Slow" --ignore-exit-code 8`
- UI: `dotnet run --project src/HeapScope.UI` (`--headless` runs it without a display)
- Builds per system: `bash build/publish.sh <win-x64|linux-x64|osx-arm64|osx-x64> <folder>`
- CLI: `dotnet run --project src/HeapScope.Cli -- --version` (also `list`, `capture --pid <id> [--kind full|gcdump]`, `open <dump>`,
  `summary <dump> [--group] [--filter] [--sort] [--retained] [--format table|csv|json]`, `diff <baseline> <current>
  [--fail-on-growth 50MB|10%]` (exit 2 when it grew more; usage errors exit 64), `instances <dump> --type <name>
  [--module] [--filter] [--sort] [--retained] [--format]`, `object <dump> <address> [--references N] [--retained] [--format text|json]`,
  `referrers <snapshot> --address <hex> | --type <name> [--module] [--sort] [--format]`,
  `paths <snapshot> --address <hex> | --type <name> [--module] [--max N] [--expand] [--format text|json]`,
  `inspect <dump> [<name>] [--top|--all] [--format]`, `monitor --pid <id> [--every] [--above] [--growth] [--rearm]
  [--max] [--duration] [--kind full|gcdump]`,
  `trace --pid <id> [--duration]
  [--profile cpu|gc] [--out]`, `worker ping`, `worker info`, `synthetic --objects N`)
- UI smoke runs: `HeapScope --smoke-synthetic N`, `--smoke-live N` or `--smoke-open <dump> [--smoke-compare <dump>]
  [--smoke-trace <file>]`, with `--headless` where there is no display (exit 0 ok, 3 lag, 4 failed, 5 first window
  over 30 s, 6 a grid walked a whole row list). Measure untraced; traces are for finding where time goes. Sample dumps
  of a runtime no longer installed need `--Symbols:DacFolder` (DEVELOPER.md 2).
- Benchmarks: `dotnet run -c Release --project perf/HeapScope.Benchmarks -- <name>` (`graph-build`, `dump-stats`,
  `stack-roots`, `index-cache`, `load-dump`, `phase2-indices`, `reverse-index --cache <hsidx>`, `retention --cache <hsidx>`, `dominators --cache <hsidx> [--mode mapped]`, `trace-report --trace <file>`)
