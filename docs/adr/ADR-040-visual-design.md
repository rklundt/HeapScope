# ADR-040 Visual design (added in sprint 3.10)

- Context: the product owner found the look "well organized, but utilitarian" (2026-11-03). A review of captures of every
  page in both themes found stock Fluent at its default settings, chrome above the data on every page, and real defects:
  name columns that collapse, selected text under 4.5:1, charts that keep the old theme's colors. HeapScope is a dense
  tool for developers and support engineers, used for hours next to an IDE.
- Decision: "a quiet instrument panel".
  - Color: slate neutrals, one indigo accent. Color appears only for selection, the primary action and data. The values
    are the theme files' (`src/HeapScope.UI/Themes/Light.axaml`, `Dark.axaml`), and no view or control states a color
    (`ThemeTests` scans their code and markup): charts and converters read the theme's keys, and follow a theme change. A control's plain
    fallback only draws if a key is missing, and `Icons.axaml`'s logo keeps its fixed colors. `App.axaml` repeats the accent
    for Fluent's palette, and the theme files repeat tokens under Fluent's own key names; tests hold both to the tokens and
    check those names still exist in Fluent.
  - Info icons (amended in sprint 3.11): a screen says what it is for. Every page and tab has an `InfoIcon` beside its title, and groups
    of settings have one beside their heading; single buttons keep a tooltip. Hovering shows the text, and a click, Enter or Space opens it
    to stay, so it answers beside a disabled control. The page texts are in `InfoTexts`, approved by the product owner and checked against the code.
  - Readable: text colors meet 4.5:1 on every surface they sit on, the selection included, and an input's edge 3:1
    (`ThemeTests`). The attribution notice (ADR-022) keeps its wording, link, position and size; its colors follow the
    theme, at 16.6:1 light and 14.0:1 dark.
  - Type: Inter (SIL OFL 1.1, shipped; DEVELOPER.md 4 allows OFL for fonts only). Page titles 18 SemiBold; body, grids and
    tabs 13; section headings 13 SemiBold; a snapshot's stat values 15 SemiBold; column headers and labels 12; captions and chart labels 11, the smallest size (a diagram the user fits to its width scales down to 0.75, so its labels can draw at about 8 px; past that it scrolls);
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
- Consequences: a new color is a key in both theme files, or a theme test fails. A new page reuses the styles in
  `Themes/Styles.axaml`. A new page, tab, view or group of settings gets an info icon whose text the owner approved first, in
  `docs/design/info-text.md` (DEVELOPER.md 4, Info icons); a view with none fails `InfoIconTests`. The screenshots in `docs/images/` go stale when the look changes; `build/screenshots.sh` recaptures
  them by hand, and no test compares pictures.
