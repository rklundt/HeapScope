---
name: close-sprint
description: Close the HeapScope sprint that is Wrapping. Verifies the Definition of Done item by item, sets the sprint Closed, commits to main as "Sprint X.Y: title", creates the annotated tag sprint-X.Y, and offers the next sprint.
---

# /close-sprint

Follow these steps exactly.

1. **Verify the Definition of Done** (DEVELOPER.md section 9), item by item, and show the result:
   - `dotnet build HeapScope.sln -c Debug` and `-c Release`: zero warnings, zero errors.
   - `dotnet test HeapScope.sln`: all pass, including the sprint's integration tests.
   - Every acceptance criterion in the sprint's section is met (cite the test or measurement) or explicitly
     deferred by the human (or, in autonomous mode, deferred and recorded as "decided autonomously").
   - `docs/reviews/sprint-X.Y.md` exists; every High finding is Fixed and confirmed; every Moderate finding
     has a decision.
   - ARCHITECTURE.md, DEVELOPER.md, SPRINTS.md and CLAUDE.md describe what was built.
   - The architecture test passed in the run above.
   - No em dashes or en dashes in changed files (search for U+2014 and U+2013).
   If anything is unmet, list exactly what is missing and stop. Do not close.
2. **Status.** In SPRINTS.md set the sprint to Closed with today's date and the tag `sprint-X.Y`.
3. **Commit.** `git add -A`, check `git status` shows no dumps, caches or logs, then commit to `main` with the
   subject `Sprint X.Y: <title>` and a short body: what was delivered and any deferred items.
4. **Tag.** `git tag -a sprint-X.Y -m "Sprint X.Y: <title>"`.
5. **Summarize.** What shipped, deferred items, the commit hash and the tag.
6. **Next.** Ask the human whether to start the next Planned sprint, naming it. If yes, run `/start-sprint`
   for it.

## Autonomous mode
When the product owner has granted autonomy for the current phase, step 6 does not ask: start the next
Planned sprint of the phase directly. At the end of the phase, stop and report.
