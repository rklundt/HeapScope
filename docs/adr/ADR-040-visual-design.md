# ADR-040 Visual design (added in sprint 3.10)

- Context: the product owner found the look "well organized, but utilitarian" (2026-11-03). A review of captures of every
  page in both themes found stock Fluent at its default settings, chrome above the data on every page, and real defects:
  name columns that collapse, selected text under 4.5:1, charts that keep the old theme's colors. HeapScope is a dense
  tool for developers and support engineers, used for hours next to an IDE.
- Decision: "a quiet instrument panel".
  - Color: slate neutrals, one indigo accent. Color appears only for selection, the primary action and data. The values
    are the theme files' (`src/HeapScope.UI/Themes/Light.axaml`, `Dark.axaml`), and nothing else in the app states a color:
    charts and converters read the theme's keys, and follow a theme change.
  - Readable: text colors meet 4.5:1 on every surface they sit on, the selection included, and an input's edge 3:1
    (`ThemeTests`). The attribution notice (ADR-022) keeps its wording, link, position and size; its colors follow the
    theme, at 16.6:1 light and 14.0:1 dark.
  - Type: Inter (SIL OFL 1.1, shipped; DEVELOPER.md 4 allows OFL for fonts only). Page titles 18 SemiBold; body, grids and
    tabs 13; section headings 13 SemiBold; a snapshot's stat values 15 SemiBold; column headers and labels 12; captions and chart labels 11, the smallest size;
    addresses in the system's monospace at 12. Number columns use tabular figures, so digits line up.
  - Controls: 28 px high, radius 4 (panels 6), spacing on a 4 px grid. Neutral buttons; one primary button per page, for
    what the page is for. Rows are 22 px in grids and trees.
  - Selection: the accent's tint behind normal text, in every list and grid; the sidebar adds a 3 px accent bar, so the
    current page shows without relying on color alone.
  - Icons: our own, Duotone (a 1.5 px outline in the text color plus an accent tint), on a 20 px grid, in
    `Themes/Icons.axaml`. No third-party icon set. The app's logo is logo A, a lens over stacked blocks; logos B to E stay
    in `docs/design/logo-options/` as options.
- Refused or deferred:
  - A third-party icon set: more to license and track, for a few dozen simple shapes.
  - Roboto and IBM Plex: also OFL; Inter reads best at 11 to 13 px and has tabular figures.
  - Backlog: inline data bars in grids, dimmed namespaces in type names, a collapsible snapshot rail, the icicle chart
    colored by application versus framework code.
- Consequences: a new color is a key in both theme files, or the theme test fails. A new page reuses the styles in
  `Themes/Styles.axaml`. The screenshots in `docs/images/` go stale when the look changes; `build/screenshots.sh` recaptures
  them by hand, and no test compares pictures.
