# ADR-038 SkiaSharp's native library without the DNG SDK (added 2026-10-30, outside a sprint)

- Context: Avalonia draws only through Skia, so every HeapScope build ships SkiaSharp's native library. The stock one
  from NuGet (SkiaSharp 3.119.4, on Windows, Linux and macOS) carries Skia's raw camera image decoder: Adobe's DNG SDK
  and Google's piex. HeapScope never decodes raw camera images. The DNG SDK License (in SkiaSharp's
  `THIRD-PARTY-NOTICES.txt`) asks, in section 5, anyone who distributes it in a commercial product to defend and
  indemnify Adobe, without a cap, and grants no patents. The product owner does not want it in HeapScope's builds
  (2026-10-30), whatever a legal review of that clause would find.
- Spike (2026-10-30): SkiaSharp's own Windows build script at v3.119.4, with only `skia_use_dng_sdk` and
  `skia_use_piex` turned off, built `libSkiaSharp.dll` in about 19 minutes on a hosted runner. The library holds no DNG
  SDK or piex code (the stock one does), SkiaSharp's version check accepts it, and the app's full smoke run (open a dump,
  the type grid, a drill-down, all inspections, the retainers view) behaves as with the stock library. Skia builds its
  raw decoder only when both the DNG SDK and piex are on, so either switch drops it; both are turned off.
- Decision:
  - HeapScope builds `libSkiaSharp` itself for every shipped runtime (`.github/workflows/skia-native.yml`): from
    SkiaSharp's sources at the commit of the release HeapScope uses, with SkiaSharp's own build scripts (on Linux in
    SkiaSharp's own Debian 10 image, as SkiaSharp builds it), changing only those two switches. Each library is checked
    for DNG SDK and piex code (`build/check-no-dng.sh`), and the set is attested in GitHub's attestation store.
  - The libraries are attached to a pre-release of this repository (`skia-native-<version>-<n>`), not committed:
    binaries do not belong in git, and building Skia takes longer than a package job should. `build/skia-native.sh`
    pins the release, the SkiaSharp version and each library's SHA-256; `build/publish.sh` swaps the stock library for
    it, and `build/check-package.sh` refuses any build whose library holds DNG SDK or piex code.
  - Debug builds and tests keep the stock NuGet library: they are never distributed.
  - Only `libSkiaSharp` changes. HarfBuzzSharp's native library holds no DNG SDK or piex code and stays as shipped.
  - `THIRD-PARTY-NOTICES.md` says the DNG SDK and piex are not in HeapScope's builds, and drops the DNG credit line.
    SkiaSharp's own notices file still ships unchanged under `licenses/`.
- Consequences: every SkiaSharp update that Avalonia brings needs the libraries rebuilt and the pins updated before a
  package builds (publish.sh fails until then; DEVELOPER.md 7). Publishing needs network access to the release, or the
  files in a folder (`HEAPSCOPE_SKIA_NATIVE_DIR`). HeapScope cannot open raw camera images, which it never did. A
  future runtime (for example linux-arm64 or win-arm64) needs its own build in the workflow first.
