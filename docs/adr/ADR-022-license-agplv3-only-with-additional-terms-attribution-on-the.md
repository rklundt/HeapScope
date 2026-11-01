# ADR-022 License: AGPLv3 only with additional terms, attribution on the main screen (added before sprint 1.8)

- Context: HeapScope is owned by Ray Klundt and is to be published with forced attribution, while staying available
  under separate commercial licenses.
- Decision: GNU AGPL v3 only (`LICENSE`) with additional terms under its Section 7 (`ADDITIONAL-TERMS.md`): an
  Attribution Notice, marking of modified versions, no trademark rights, and terms on AI use; commercial licenses per
  `COMMERCIAL.md`. Every source file we own carries the header of `docs/licensing/SOURCE-HEADER.txt`. The notice
  "Powered by HeapScope by Ray Klundt", with HeapScope linked to the repository, is docked at the bottom of the main
  window as selectable text in the body font size, theme text on the opaque surface color (contrast above 13:1 in both
  themes); the CLI prints it, with the URL, after help and version output and, on a console, to stderr at startup.
  `ProductInfo` holds the text and URL. Third-party code and packages keep their own licenses, listed in
  `THIRD-PARTY-NOTICES.md`; vendored code lives in a `ThirdParty` project or folder (the gcdump code, sprint 1.8).
- Alternatives: MIT (no attribution on screen, no copyleft); GPLv3 (no network clause); a splash screen instead of the
  main screen (ADDITIONAL-TERMS.md 1.2 allows either; the main screen costs no start-up time).
- Consequences: dependencies must be AGPLv3 compatible (DEVELOPER.md 4); outside contributions need a contributor
  agreement to keep commercial licensing possible; tests check the headers and the notice's text, link and contrast.
