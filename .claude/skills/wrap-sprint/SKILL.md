---
name: wrap-sprint
description: Wrap up the active HeapScope sprint. Builds and tests, runs the solution-architect, senior-dev, gc-specialist and support-engineer reviewers in parallel, writes docs/reviews/sprint-X.Y.md, fixes High findings, gets Moderate findings decided, then sweeps every tracked Markdown file so the docs match what was built, and sets the sprint to Wrapping.
---

# /wrap-sprint

Follow these steps exactly.

1. **Confirm state.** The sprint in `docs/SPRINTS.md` is In Progress. Run `dotnet build HeapScope.sln` (must
   be zero warnings) and `dotnet test --solution HeapScope.sln`. Fix any failure before continuing.
2. **Change set.** Find the latest `sprint-*` tag (`git describe --tags --abbrev=0 --match "sprint-*"`) and
   list the changes since it: `git diff --stat <tag>` plus untracked files (`git status --porcelain`).
3. **Review.** Launch the four reviewer subagents in parallel, in one message: `solution-architect`,
   `senior-dev`, `gc-specialist`, `support-engineer` (definitions in `.claude/agents/`). Give each: the sprint id,
   the list of changed and new files, the sprint section of SPRINTS.md, a pointer to ARCHITECTURE.md and
   DEVELOPER.md, and the severity rubric (DEVELOPER.md section 10). Reviewers report only; they never edit.
4. **Consolidate.** Dedupe overlapping findings, keep the highest severity on conflicts, number them
   (H1, M1, L1...), and write `docs/reviews/sprint-X.Y.md` grouped by severity. Each finding: severity,
   reviewer(s), file and line or area, what is wrong, why it matters, suggested fix, status.
5. **High / Urgent.** Fix all of them. Re-run build and tests. Send each fix back to the reviewer that raised
   it (resume that agent, or a new instance of the same type with the finding and the diff) and record its
   confirmation in the review file. Repeat until confirmed.
6. **Moderate.** Present them to the human as a numbered list, each with a one-line recommendation (fix now,
   defer to backlog, won't fix). Wait for decisions. Implement "fix now" items, add deferred items to the
   Backlog (`docs/BACKLOG.md`), record every decision in the review file.
7. **Low / Trivial.** List them in the review file only. Do not fix them and do not raise them.
8. **Docs.** Update ARCHITECTURE.md and its pages under `docs/architecture/` and `docs/adr/`, DEVELOPER.md, SPRINTS.md (including the sprint's Review summary: counts by
   severity, what was fixed, what was deferred, measured numbers) and CLAUDE.md so they match what was built.
   Add ADRs for significant decisions made during the sprint. Move anything durable from `CURRENT-CONTEXT.md`
   (decisions, measurements, findings) into these docs, and update `CURRENT-CONTEXT.md` to say the sprint is
   Wrapping.
9. **Markdown sweep.** Only after steps 5 to 8, so the sweep sees the code as corrected. List every tracked
   Markdown file (`git ls-files "*.md"`) and read each against the code as it now stands. Fix anything stale: names of
   projects, types, commands, options, files or folders that changed; counts and versions (app version, worker
   protocol, test totals); the UI toolkit and platforms; the review process itself, including the reviewer briefs in
   `.claude/agents/` and the skills in `.claude/skills/`. Check THIRD-PARTY-NOTICES.md against the packages and any
   vendored code. Never edit LICENSE, ADDITIONAL-TERMS.md or COMMERCIAL.md (report a problem there to the human).
   Past review files (`docs/reviews/sprint-*.md` other than this sprint's) are records: leave them as written. Search
   every changed file for em and en dashes (U+2014, U+2013). Record in the review file which files the sweep changed
   (or that none needed it).
10. **Finish.** Run build and tests one last time. Set the sprint status to Wrapping. Tell the human it is ready
    for `/close-sprint`.

## Autonomous mode
When the product owner has granted autonomy for the current phase, step 6 does not wait: apply the
recommendation for each Moderate finding and mark it "decided autonomously" in the review file. Escalation
conditions set by the product owner still apply.
