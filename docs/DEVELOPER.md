# HeapScope Developer Guide

Status: living document, updated by every sprint.

## 1. Prerequisites
- Windows 11 x64 (Windows 10 x64 also supported).
- .NET 10 SDK (10.0.100 or later). `global.json` pins the feature band and rolls forward to the latest patch.
- An IDE supporting .NET 10: Visual Studio 2026, JetBrains Rider 2025.3+, or VS Code with C# Dev Kit.
- RAM: 16 GB is the baseline (Medium snapshots open in mapped mode when memory is tight); 32 GB lets Medium
  snapshots open fully in memory. Generating the Medium sample dump needs about 6 GB free RAM and 5 GB disk.
- Admin rights only for capturing or attaching to elevated processes (ADR-014).

## 2. Clone, build, test, run
```
git clone <repo> heapScope
cd heapScope
dotnet build HeapScope.sln
dotnet test HeapScope.sln
```
- Fast tests only: `dotnet test HeapScope.sln --filter "Category!=Integration&Category!=Slow"`.
- Run the UI: `dotnet run --project src/HeapScope.UI.Wpf`.
- Run the CLI: `dotnet run --project src/HeapScope.Cli -- --version`.
- Run the worker standalone (for debugging): `dotnet run --project src/HeapScope.Worker -- --pipe <name>`
  (without `--parent-pid` it does not watch a parent). Attach a client with
  `dotnet run --project src/HeapScope.Cli -- worker ping --pipe <name>`.
- Benchmarks: `dotnet run -c Release --project perf/HeapScope.Benchmarks -- <benchmark> [options]`. See
  section 6.

Build output: UI and CLI get the worker deployed into a `worker/` subfolder of their output by
`build/Worker.Deploy.targets`.

## 3. Fixtures and sample dumps (planned, sprint 0.4)
- `fixtures/LeakyApi`: run with `dotnet run --project fixtures/LeakyApi`. Leaks are switched on and sized via
  configuration or the `/leaks/*` endpoints (see the fixture README).
- `fixtures/LeakyConsole`: `dotnet run --project fixtures/LeakyConsole -- --leak <name> --count <n>`.
- `fixtures/LoadGen`: drives LeakyApi and can capture a dump when done.
- `fixtures/scripts/New-SampleDump.ps1 -Tier Small|Medium` produces sample dumps into `artifacts/dumps/`
  (git-ignored).
- `fixtures/KnownAnswers/*.json`: what the analysis must find for each leak.

## 4. Coding conventions
- **Naming**: .NET conventions. `PascalCase` types and members, `_camelCase` private fields, `s_camelCase`
  private static fields, `I` prefix for interfaces, `Async` suffix on async methods. One public type per file.
- **Nullable** is enabled everywhere; no `!` suppressions without a comment explaining why.
- **Warnings are errors**; do not disable a warning without a comment and reviewer agreement.
- **Async**: every async method takes a `CancellationToken` last (after `IProgress<T>` when present). Library
  code uses `ConfigureAwait(false)`. Never `.Result`, `.Wait()` or `async void` (except WPF event handlers,
  which must catch everything).
- **MVVM**: CommunityToolkit.Mvvm source generators (`[ObservableProperty]`, `[RelayCommand]`). No code-behind
  logic beyond view-only concerns. View models get services by constructor injection and never reference WPF
  controls. Long work uses async relay commands with cancellation.
- **Logging**: `ILogger<T>` via DI. Use structured templates, not string interpolation. Never log snapshot
  field values or string contents.
- **Exceptions**: throw specific exception types; catch only at boundaries (RPC methods, commands, CLI
  handlers). User-facing messages say what happened and what to do next.
- **Hot paths** (loaders, graph, analysis, storage):
  - Struct-of-arrays and `Column<T>`; work with `ReadOnlySpan<T>` and indices, never per-object classes.
  - No LINQ, lambdas capturing state, boxing or `foreach` over interfaces inside per-object loops.
  - Temporary buffers from `ArrayPool<T>.Shared`, returned in `finally`.
  - Allocate final arrays once at exact size; grow with chunked buffers, not `List<T>` doubling.
  - Check the `CancellationToken` at least every 64K iterations.
- **Text**: no em dashes or en dashes in code, comments, docs or UI text. Use a hyphen or a comma.

## 5. Testing strategy
- **Unit tests** (`*.Tests`): fast, deterministic. Analysis is tested on synthetic graphs built with
  `SyntheticGraphBuilder`, where the right answer is known by construction.
- **Architecture test**: `HeapScope.Architecture.Tests` enforces the dependency table in ARCHITECTURE.md.
- **Integration tests** (`[Trait("Category", "Integration")]`): start real worker processes and fixture apps,
  capture real dumps, and assert the known answers.
- **Slow tests** (`[Trait("Category", "Slow")]`): large synthetic graphs; part of the full run.
- **Performance checks**: `perf/HeapScope.Benchmarks` measures against the budgets in ARCHITECTURE.md
  section 10; sprints that touch hot paths record results in their review file.
- Assertions: Shouldly or plain xUnit asserts. FluentAssertions is not allowed (commercial license).

## 6. Benchmarks
`perf/HeapScope.Benchmarks` is a plain console app (no BenchmarkDotNet, because the interesting cases use
gigabytes of memory and run once). Each benchmark prints elapsed time, managed memory and process working set,
and compares against the budget. Run in Release.

## 7. Git conventions
- Work on `main`; no branches yet (ADR-012).
- Commits happen only through `/close-sprint`, one per sprint, message `Sprint X.Y: <title>` plus a short body.
- Each closed sprint gets an annotated tag `sprint-X.Y`.
- Never commit dumps, index caches (`*.hsidx`) or logs; `.gitignore` covers them.

## 8. Sprint workflow
1. `/start-sprint [id]`: checks the previous sprint is Closed and the tree is clean, reads the docs, analyzes
   the sprint, asks clarifying questions in one numbered batch with defaults, writes the task breakdown and
   decisions into SPRINTS.md, sets In Progress, and implements after go-ahead.
2. Implement task by task, tests alongside code, build green at every step.
3. `/wrap-sprint`: full build and tests, three reviewer subagents (solution-architect, senior-dev,
   gc-specialist) in parallel, consolidated review file in `docs/reviews/sprint-X.Y.md`, High findings fixed
   and confirmed, Moderate findings decided by the human, docs updated, status Wrapping.
4. `/close-sprint`: verifies the Definition of Done, sets Closed, commits, tags, summarizes, offers the next
   sprint.

**Autonomous mode.** When the product owner grants autonomy for a phase, the skills do not stop for
questions: each question is answered with the recommended default and recorded under "Decisions" in the
sprint's section; Moderate findings are decided by the lead engineer and marked "decided autonomously" in the
review file; sprints close and the next one starts without asking. Escalation conditions set by the product
owner still apply.

## 9. Definition of Done (every sprint)
- Solution builds with zero warnings.
- All tests pass, including integration tests relevant to the sprint.
- Acceptance criteria in SPRINTS.md are all met, or explicitly deferred by the human.
- Review file exists in `docs/reviews/`, with all High findings resolved and every Moderate finding decided.
- ARCHITECTURE.md, DEVELOPER.md, SPRINTS.md and CLAUDE.md reflect what was actually built.
- No layering violations (architecture test passes).

## 10. Severity rubric
- **High / Urgent**: incorrect analysis results, crashes, data loss, security or privacy issues, layering
  violations, UI freezes, memory or time budgets missed by more than 2x, anything that violates the
  Definition of Done, design choices that will force a rewrite later.
- **Moderate**: maintainability or design concerns with real future cost, missing tests for meaningful edge
  cases, budgets missed by less than 2x, confusing UX, weak error messages.
- **Low / Trivial**: style, naming, minor refactors, nits, optional improvements.

Every finding includes: severity, reviewer, file and line (or area), what is wrong, why it matters, and a
suggested fix.
