# Logo options

Logo A is HeapScope's logo: a lens over stacked blocks. `logo-a-small.svg` is its form for 20 px and under. B to E were the other
candidates (product owner, 2026-11-04) and stay here in case the logo changes. ADR-040 has the visual design.

To change the app's icon, redraw it in `build/make-icon.cs` and `src/HeapScope.UI/Themes/Icons.axaml`, then:

```bash
dotnet run build/make-icon.cs -- src/HeapScope.UI/Assets/heapscope.ico docs/images/logo.png
```
