# Claude Code Licensing Setup

Drop these files in the repo root: `LICENSE`, `ADDITIONAL-TERMS.md`, `COMMERCIAL.md`, `SOURCE-HEADER.txt`, and this file. Fill in the values below, then paste the setup prompt into Claude Code.

## 1. One-time setup prompt

```
Set up licensing for this repo using LICENSE, ADDITIONAL-TERMS.md, SOURCE-HEADER.txt, COMMERCIAL.md and CLAUDE-SETUP.md in the root.

Values: APP NAME = [App Name], YEAR = [2026], REPO URL = [https://github.com/...], CONTACT EMAIL = [email].

1. Replace all placeholders in ADDITIONAL-TERMS.md, COMMERCIAL.md and SOURCE-HEADER.txt with these values. Do not otherwise change their wording.
2. Do not modify LICENSE in any way. It is the verbatim GNU AGPL v3 text.
3. Add the header from SOURCE-HEADER.txt to the top of every source file I own, adapted to each language's comment syntax. Skip third-party, vendored and generated files.
4. Create THIRD-PARTY-NOTICES listing every dependency and vendored code (e.g. PerfView) with its license and copyright notice. Flag any dependency whose license may be incompatible with AGPLv3, and do not remove anything without asking me.
5. Implement the Attribution Notice per Section 1 of ADDITIONAL-TERMS.md: "Powered by [App Name] by Ray Klundt" with [App Name] linked to the repo, on the main screen, as real text on a solid opaque background with at least 4.5:1 contrast and no smaller than body text. For CLI or library entry points, add it to --help, --version and startup output where applicable, and to the README.
6. Add a License section to README.md: AGPLv3 only with additional terms, the attribution requirement, the AI terms in Section 6 of ADDITIONAL-TERMS.md, and a link to COMMERCIAL.md.
7. Move SOURCE-HEADER.txt and CLAUDE-SETUP.md to docs/licensing/ once headers are applied.
8. Add the CLAUDE.md block from section 2 of CLAUDE-SETUP.md to CLAUDE.md (create it if missing), with [App Name] filled in.
9. Show me a summary of changes and any license concerns before committing.
```

## 2. CLAUDE.md block

```markdown
## Licensing (required)

This project is AGPLv3 only with additional terms (see LICENSE, ADDITIONAL-TERMS.md, COMMERCIAL.md). Owner: Ray Klundt.

- Never edit LICENSE, ADDITIONAL-TERMS.md or COMMERCIAL.md unless I explicitly ask.
- Every new source file I own gets the standard license header (copy from an existing file or docs/licensing/SOURCE-HEADER.txt).
- Never remove, hide, shrink, restyle or move the "Powered by [App Name] by Ray Klundt" attribution, or lower its contrast below 4.5:1. It must stay real text on a solid opaque background on the main screen. Check every UI change against this.
- Before adding a dependency, check its license. MIT, BSD, Apache-2.0, LGPL, GPLv3 and AGPLv3 are fine. Stop and ask me before adding anything GPLv2-only, proprietary, SSPL, "non-commercial" or unlicensed.
- When adding, removing or vendoring a dependency, update THIRD-PARTY-NOTICES.
- When copying code from outside sources, record its origin and license in THIRD-PARTY-NOTICES. Do not paste code from incompatible licenses.
```
