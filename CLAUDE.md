# HeapScope

HeapScope is a Windows desktop memory profiler for modern .NET applications (.NET 10, WPF, x64), replacing
the core of JetBrains dotMemory. Primary use case: find what is growing and why it is retained in an ASP.NET
Core API run locally under replayed traffic. Snapshots (full dumps, later gcdumps) are loaded by an
out-of-process x64 worker into a compact struct-of-arrays object graph, cached on disk, and explored through a
WPF app or an equivalent CLI. Baseline machine: 16 GB RAM; use more when available.

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
  Core; UI.Wpf references only Application and Contracts. The architecture test enforces it.
- No code without an active sprint (status In Progress in SPRINTS.md).
- Zero warnings; warnings are errors.
- No em dashes or en dashes in any docs, code or UI text. Use a hyphen or a comma.
- Never commit without `/close-sprint`. One commit and one annotated tag `sprint-X.Y` per sprint, on `main`.
- Never one managed object per heap object (ADR-005). Hot paths use spans, indices and pooled buffers.
- Never upload or log snapshot contents. Dumps and index caches are sensitive.
- Definition of Done (DEVELOPER.md 9): zero warnings, all tests pass, acceptance criteria met or deferred by
  the human, review file with High fixed and Moderate decided, docs current, architecture test passes.

## Sprint workflow
1. `/start-sprint [id]`: analyze, ask, plan the tasks in SPRINTS.md, set In Progress.
2. Implement task by task with tests, build green throughout.
3. `/wrap-sprint`: build, test, three reviewers, fix High, decide Moderate, update docs, set Wrapping.
4. `/close-sprint`: verify Definition of Done, set Closed, commit, tag.
5. Next sprint.

## Commands
- Build: `dotnet build HeapScope.sln`
- All tests: `dotnet test --solution HeapScope.sln`
- Fast tests: `dotnet test --solution HeapScope.sln --filter-not-trait "Category=Integration" --filter-not-trait "Category=Slow" --ignore-exit-code 8`
- UI: `dotnet run --project src/HeapScope.UI.Wpf`
- CLI: `dotnet run --project src/HeapScope.Cli -- --version` (also `list`, `capture --pid <id>`, `open <dump>`,
  `summary <dump> [--group] [--filter] [--sort] [--format table|csv|json]`, `diff <baseline> <current>
  [--fail-on-growth 50MB|10%]` (exit 2 when it grew more; usage errors exit 64), `worker ping`, `worker info`,
  `synthetic --objects N`)
- UI smoke runs: `HeapScope.exe --smoke-synthetic N` or `--smoke-open <dump> [--smoke-compare <dump>]` (exit 0 ok,
  3 lag, 4 failed)
- Benchmarks: `dotnet run -c Release --project perf/HeapScope.Benchmarks -- <name>` (`graph-build`, `dump-stats`,
  `stack-roots`, `index-cache`, `load-dump`)
