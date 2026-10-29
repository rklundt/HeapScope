---
name: close-sprint
description: Close the HeapScope sprint that is Wrapping. Verifies the Definition of Done item by item, sets the sprint Closed and the version, commits on sprint/X.Y as "Sprint X.Y: title", opens the PR into develop, merges it in autonomous mode, tags sprint-X.Y on develop, and offers the next sprint.
---

# /close-sprint

Follow these steps exactly.

1. **Verify the Definition of Done** (DEVELOPER.md section 9), item by item, and show the result:
   - `dotnet build HeapScope.sln -c Debug` and `-c Release`: zero warnings, zero errors.
   - `dotnet test --solution HeapScope.sln`: all pass, including the sprint's integration tests.
   - Every acceptance criterion in the sprint's section is met (cite the test or measurement) or explicitly
     deferred by the human (or, in autonomous mode, deferred and recorded as "decided autonomously").
   - `docs/reviews/sprint-X.Y.md` exists; every High finding is Fixed and confirmed; every Moderate finding
     has a decision.
   - The docs describe what was built. Check each against the change set (`git diff --stat <last sprint tag>`) and
     update it now if it lags: ARCHITECTURE.md (code map, protocol version, ADRs, measurements, privacy),
     DEVELOPER.md (commands, workflows), SPRINTS.md (tasks ticked, Decisions, Review summary, Backlog), CLAUDE.md
     (commands, rules), and any README the sprint touched (for example `fixtures/KnownAnswers/README.md`). The review
     file records `/wrap-sprint`'s Markdown sweep of every tracked .md file.
   - Anything in `CURRENT-CONTEXT.md` worth keeping (decisions, measurements, findings, backlog items) is in the
     committed docs, since step 6 resets it.
   - The architecture test passed in the run above.
   - No em dashes or en dashes in changed files (search for U+2014 and U+2013).
   If anything is unmet, list exactly what is missing and stop. Do not close.
2. **Status.** In SPRINTS.md set the sprint to Closed with today's date and the tag `sprint-X.Y`. Set
   `<Version>X.Y.0</Version>` in `Directory.Build.props`. Add the version's section at the top of `RELEASE-NOTES.md`
   ("## X.Y.0: <title> (sprint X.Y)"): what a user gets, in a few bullets, and anything refused or limited.
3. **Commit.** On `sprint/X.Y`: `git add -A`, check `git status` shows no dumps, caches, traces or logs, then commit with the
   subject `Sprint X.Y: <title>` and a short body: what was delivered and any deferred items.
   No Co-Authored-By or generated-by lines.
4. **PR.** `git push -u origin sprint/X.Y`, then `gh pr create --base develop --title "Sprint X.Y: <title>"` with a
   short body (what was delivered, deferred items, the gate result). No generated-by lines; no em or en dashes.
5. **Merge and tag.** Only in autonomous mode (otherwise stop and ask the owner to merge, then continue here):
   `gh pr merge --squash --delete-branch --subject "Sprint X.Y: <title>" --body "<the commit body>"`. Then
   `git checkout develop`, `git pull --ff-only`, `git tag -a sprint-X.Y -m "Sprint X.Y: <title>"` on that commit and
   `git push origin sprint-X.Y`. Never push to `main` or `develop` directly; releases to `main` happen only when the
   owner says so (DEVELOPER.md 7).
6. **Summarize.** What shipped, deferred items, the PR, the commit hash on `develop` and the tag.
7. **Reset the current context.** Rewrite `CURRENT-CONTEXT.md` (git-ignored, never committed) to the post-sprint
   state: the sprint just closed with its commit and tag, the next sprint by id and title, the phase's autonomy and
   standing constraints, open backlog items relevant to the next sprint, and anything the human said that the docs do
   not hold. Drop the closed sprint's working notes.
8. **Next.** Ask the human whether to start the next Planned sprint, naming it. If yes, run `/start-sprint`
   for it.

## Autonomous mode
When the product owner has granted autonomy for the current phase, step 7 does not ask: start the next
Planned sprint of the phase directly. At the end of the phase, stop and report.
