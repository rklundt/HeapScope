---
name: start-sprint
description: Start a HeapScope sprint. Verifies the previous sprint is closed and the tree is clean, analyzes the sprint, asks clarifying questions with defaults, writes the task breakdown into docs/SPRINTS.md, sets it In Progress, then implements after go-ahead. Usage /start-sprint [sprint-id]
---

# /start-sprint [sprint-id]

Follow these steps exactly.

1. **Pick the sprint.** If an id was given, use it. Otherwise take the first sprint in the status table of
   `docs/SPRINTS.md` with status Planned and confirm it with the human by name.
2. **Check preconditions.**
   - The previous sprint in the status table is Closed.
   - `git status --porcelain` is empty.
   - No other sprint is In Progress or Wrapping.
   - The previous sprint's PR is merged into `develop` (`gh pr list --state open` shows no `sprint/*` branch).
   If any check fails, stop and explain exactly which one and how to fix it.
   Then branch: `git checkout develop`, `git pull --ff-only`, `git checkout -b sprint/X.Y`.
3. **Read context.** `CURRENT-CONTEXT.md` (if present), `CLAUDE.md`, `docs/ARCHITECTURE.md`, `docs/DEVELOPER.md`, the sprint's section in
   `docs/SPRINTS.md`, the Backlog (`docs/BACKLOG.md`), and every `docs/reviews/*.md` with deferred items.
4. **Analyze.** Restate the goal in one sentence. List ambiguities, risks, dependencies, impacts on the
   architecture (does any ADR need adding or changing?), and backlog or deferred items to pull in or exclude
   explicitly.
5. **Ask.** Put all clarifying questions to the human in one batch, numbered, each with a recommended default.
   Wait for answers. Write no code before the answers arrive.
6. **Plan.** In the sprint's section of `docs/SPRINTS.md`:
   - Replace "to be filled by /start-sprint" with a numbered task list. Tasks are small and testable; each
     lists the tests that prove it.
   - Add a **Decisions** list recording each answer.
   - Set the status table row to In Progress.
7. **Go.** Present the plan briefly and ask for a go-ahead. After approval, implement task by task, writing
   tests alongside code, keeping `dotnet build HeapScope.sln` at zero warnings and tests green after each
   task. Tick tasks off in SPRINTS.md as they finish, and keep `CURRENT-CONTEXT.md` current (active sprint, task
   status, decisions in conversation, next steps).

## Autonomous mode
When the product owner has granted autonomy for the current phase:
- Step 1 does not ask for confirmation; step 5 does not wait. Each question is answered with its recommended
  default and recorded under **Decisions** as "decided autonomously".
- Step 7 starts implementation without asking.
- Still stop and ask if a decision contradicts or would change a locked ADR, if stuck after three genuinely
  different attempts, if admin rights, a paid license or access is needed, if an action is destructive beyond
  normal commits, or if a budget looks unachievable as designed.
