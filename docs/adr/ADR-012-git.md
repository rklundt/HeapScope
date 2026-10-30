# ADR-012 Git

- Decision: local repository, work on `main`, annotated tag `sprint-X.Y` per closed sprint. Remote,
  branches and CI are backlog items.
- Amended 2026-10-25 (after 2.2): the repository is published at https://github.com/rklundt/HeapScope. `main` holds
  releases and `develop` collects sprints; each sprint works on `sprint/X.Y` off `develop` and lands by a squash-merged
  PR, tagged `sprint-X.Y` on `develop`. A release is a PR from `develop` to `main` (merge commit), tagged `vX.Y.0`
  with a GitHub Release (pre-release for now). A ruleset on both branches requires a PR and blocks direct and force
  pushes and deletion. CI is still a backlog item; `./build/gate.ps1` is the local gate.
- Amended in sprint 2.3.5: CI runs on GitHub's hosted runners (`.github/workflows/ci.yml`; DEVELOPER.md 7) for every PR
  into `develop` or `main`; it is not yet a required check. The local gate stays the full check before closing a
  sprint.
- Amended in sprint 3.6 (peer review P4): CI also runs the integration tier on Windows (about three minutes) and keeps
  every job's test results (TRX) for 14 days; CI and the local gate (`build/gate.sh`, bash, which replaces `gate.ps1`)
  share `build/test.sh` and `build/fixture-dump.sh`. Making the CI jobs required checks is a repository setting the
  product owner applies.
- Amended in sprint 3.7 (peer review P13, P16, P17): a nightly fuzz workflow (`fuzz.yml`, on `develop`), a release
  workflow run by a published release or by hand as a dry run (`release.yml`), Actions pinned to commits, locked
  restores, Dependabot; the Linux job's DAC trust check fails rather than skips (`HEAPSCOPE_REQUIRE_FIXTURE`).
