# HeapScope architecture: Scale targets and budgets

Part of [ARCHITECTURE.md](../ARCHITECTURE.md); the section numbers are the same as before the split (sprint 3.8), so
references such as "ARCHITECTURE.md 10" still name the right section.

## 10. Scale targets and budgets

Tiers:
- **Small**: dump under 1 GB, about 5M objects.
- **Medium**: dump up to about 8 GB, about 50M objects. Baseline developer laptop: 16 GB RAM (mapped mode
  when tight). With 32 GB or more, snapshots open fully in memory.
- **Large**: 8 GB+, 100M+ objects. Backlog.

Initial budgets (sprints refine them with measurements):

| Budget | Target | Measured |
|---|---|---|
| Medium (~4 GB, ~50M objects) first load on a 16 GB laptop | < 3 min | 15.8 s on the fixture Medium dump (1.2, `load-dump`, 32 GB machine); about 20 s end to end in the UI smoke run |
| Medium reopen from index cache | < 20 s in memory; < 2 s mapped | 0.6 s in memory, 0.6 s mapped warm; 0.8 s and 1.0 s cold (0.5, synthetic 50M); 0.6 s for the fixture Medium (1.2) |
| Worker peak memory, Medium, in-memory mode (machines with room, e.g. 32 GB) | < 1.5x the dump's managed heap size | 2.4 GB for a 3.36 GB managed heap, 0.71x (1.2) |
| Worker peak memory, Medium first load on a 16 GB laptop (graph, builder buffers and ClrMD caches) | < 4 GB | 2.4 GB (1.2, fixture Medium at 1.14 references per object) |
| Worker private memory, Medium, mapped mode (after load) | < 1 GB | +9 MB private, +202 MB working set for the mapped graph (sprint 0.5, synthetic 50M) |
| UI dispatcher block | never > 100 ms | every UI sprint |
| Type summary and diff on loaded Medium | < 5 s | type aggregation 0.05 s on Medium, 0.03 s on DenseHeap (54M objects); CLI `summary` end to end from the index cache 1.9 s and 2.0 s (1.3); CLI `diff` of Medium against DenseHeap end to end from the index caches 2.8 to 2.9 s, of which the aggregations 0.05 to 0.08 s each (1.4) |
| Phase 2 indices (reverse references, semi-NCA dominators, retained sizes; prototype, 1.9) | under 60 s and within the 4 GB worker budget on a Medium-scale real heap | Medium 9.5 s, peak 3,514 MB (met in memory); DenseHeap 12.9 s, peak 4,119 MB in memory (missed by 23 MB), 2,360 MB with the graph mapped (met); hence computation follows the memory mode (section 6.4) |
| Retention paths (2.2, `retention` on real index caches) | the root path tree within the 60 s Phase 2 budget; a type's key paths and an object's paths interactive (under 5 s) | Medium tree 0.8 to 1.2 s (219 MB), key paths up to 1.7 s (16M objects), an object's paths at most 6 ms, peak 2,802 MB in memory and 1,197 MB mapped; DenseHeap tree 1.6 to 2.3 s, key paths at most 0.4 s, an object's paths at most 176 ms, peak 3,320 MB in memory and 1,416 MB mapped. Met on the 32 GB machine; a 16 GB machine is not measured (section 6.4) |
| Dominators and retained sizes (2.3, `dominators` on real index caches, the worker's paths) | under 60 s for a Medium-scale real heap; Medium within the 4 GB worker budget in both memory modes | After the review: Medium reverse index and dominators 3.9 s in memory (peak 3,289 MB), 5.6 s mapped (peak 699 MB); DenseHeap 4.6 s in memory (peak 3,875 MB) and 7.0 s mapped (peak 661 MB). Met for both in both modes on the 32 GB machine (DenseHeap in memory missed by 189 MB before the review's M4). With the working set capped at 1 GB (a stand-in for memory pressure): Medium 7.9 s, DenseHeap 11.1 s, no thrashing; a real 16 GB machine is a Backlog check. With 2.4's tree children kept (20 bytes per object), on a slower day (2.3.7's code took 8.2 s for Medium's dominators): Medium 6.4 to 6.9 s in memory (peak 3,291 MB), 11.4 s mapped (697 MB); DenseHeap 7.3 s in memory (3,877 MB), 10.6 s mapped (658 MB). Met |
| Reverse references index (2.1, `reverse-index` on real index caches; the Release worker on DenseHeap) | built well inside the 60 s Phase 2 budget; worker within 4 GB | Medium 0.6 to 0.8 s, 470 MB, benchmark peak 2,423 MB in memory and 970 MB mapped; DenseHeap 0.8 to 1.7 s, 831 MB, benchmark peak 3,454 MB in memory and 1,695 MB mapped; worker peak commit 2,644 MB after building DenseHeap's index in memory, plus up to 512 MB of ClrMD cache when fields are named; reopen from the sidecar 0.1 to 1.1 s; releasing the built index 7 to 14 ms. Met on the 32 GB machine; a 16 GB machine is not measured (section 6.4) |
| Compact graph cost | ~30 to 38 bytes per object | 34.0 bytes per object at 3 references (sprint 0.2, synthetic 50M) |

Baseline measured in sprint 0.4 on the fixture sample dumps (`dump-stats`, raw ClrMD walk, no HeapScope graph yet;
reference pass by address as the loader will do it; 24 logical cores, 32 GB RAM, Release). Memory is peak private
bytes (committed memory, the budget's metric); the benchmark's own sorted address list (8 bytes per object) is
included.

| Dump | Size | Objects | Managed heap (live + dead) | References per object | Object pass | Reference pass | Peak private bytes |
|---|---|---|---|---|---|---|---|
| Small | 568 MB | 5.6M | 317 MB | 1.23 | 1.5 s | 1.6 s | 189 MB |
| Medium, ClrMD cache default | 4,022 MB | 57.5M | 3,359 MB | 1.14 | 41 s | 42 s | 3,475 MB |
| Medium, ClrMD cache 512 MB | 4,022 MB | 57.5M | 3,359 MB | 1.14 | 30 s | 51 s | 2,079 MB |

Readings for 1.2: the two ClrMD passes take about 80 s, leaving about 100 s of the 3 minute first-load budget for
reference resolution and the cache write. Uncapped, ClrMD commits about as much as the managed heap; capped at
512 MB the total is about 2.1 GB at the same speed, so graph columns (about 1.55 GB at 1.14 references) plus a capped
ClrMD fit the 4 GB first-load budget. Caveat: the fixture heaps have 1.14 to 1.23 references per object, against the
2 to 4 assumed in 6.2 for real applications; 1.2 also measures a denser heap (for example a dump of a real
ASP.NET Core application under load) before signing off its budgets.

Measured in sprint 1.2 with HeapScope's loader (`load-dump`, Release, 24 logical cores, 32 GB RAM, ClrMD cache 512 MB,
memory-mapped dump reader):

| Dump | Objects | References per object | Object pass | Reference pass | First load (with cache write) | Peak private bytes | Graph columns |
|---|---|---|---|---|---|---|---|
| Small (568 MB) | 5.6M | 1.23 | 1.3 s | 1.0 s | 3.7 s | 227 MB | 142 MB |
| Medium (4,022 MB) | 57.5M | 1.14 | 7.0 s | 6.7 s | 15.8 s | 2,395 MB | 1,457 MB |
| Roslyn compiler server (423 MB, a real application) | 0.35M | 2.01 | 0.4 s | 0.2 s | 1.7 s | 130 MB | 10 MB |
| DenseHeap (4,440 MB; 3.4 GB heap of real Roslyn objects) | 54.1M | 3.03 | 27.5 s | 29.2 s | 64.4 s | 2,636 MB | 1,759 MB |

The leak fixture heaps are sparse (1.1 to 1.2 references per object). The DenseHeap fixture (`fixtures/DenseHeap`,
captured with `fixtures/scripts/New-DenseDump.ps1`) builds a Medium-size heap at real-application density out of the
Roslyn compiler's own objects: first load 64 s against the 3 minute budget, peak 2.6 GB (0.78x the managed heap)
against 4 GB, 34.1 bytes per object, nothing dropped, mapped transition 1.4 s down to 34 MB private. The benchmark ran
right after the capture (the dump file partly cold); opened through the UI a minute later the worker loaded it in
29.0 s, and reopened it from the index cache in 1.0 s, with the longest dispatcher operation 44 ms.

Cancellation (acceptance criterion: a load stops within 2 s), measured on the DenseHeap dump with
`load-dump --cancel-at <phase>`: canceled as "Opening dump" and "Reading objects" start 0.2 s, "Reading references"
0.8 s (it includes the collection after freezing), "Reading roots" 1.1 s, "Building graph" 1.1 s. `load-dump` checks
the budgets of this section (the 1.5x heap check passes any peak under 512 MB, so small dumps are not failed by fixed
costs) and returns 1 when one is missed; the DenseHeap run met all of them (first load 58.9 s,
peak 2.6 GB, mapped transition 1.0 s down to 33 MB private).

Before opening a dump without an index cache, the Application layer estimates the first-load peak
(`SnapshotMemoryEstimate`: 0.55 bytes per dump byte plus 768 MB, calibrated on the samples above; for the dense dump it
gives 3.2 GB against the measured 2.6 GB) and warns when it exceeds available physical memory: the UI asks to
continue, the CLI prints the warning (or fails with `--no-overcommit`). `SnapshotSession.GetOpenWarningAsync` holds the
rule for both front ends; it asks the worker (`ProbeSnapshotAsync`) whether a valid index cache exists where the
locator puts it, so a reopen from cache never warns.

Comparing (sprint 1.4): `diff` of Medium against DenseHeap from their index caches takes 2.8 to 2.9 s end to end.
Inspecting (sprint 1.5): the UI smoke drill on Medium opens the instances of `System.Byte[]` (16M objects), the largest
object and its references; after the responsiveness changes of section 8 the longest checked dispatcher operation is
36 to 63 ms and probe lag 49 to 128 ms across Medium and DenseHeap runs (Release and published ReadyToRun builds).
With the full test suite running at the same time (the 1.5 review re-measure), Debug smoke runs on Small and DenseHeap
pass every time: longest operation 43 to 53 ms, probe lag 80 to 158 ms. Instances of `System.String` on Medium
(6.6M strings, Release CLI, from the index cache): a text filter 3.8 s and a regular expression 4.5 s end to end,
against 1.9 s for opening alone; values are read and tested one at a time, and only a sort by value keeps text (at
most 250,000 strings).
Inspections (sprint 1.6, Release CLI `inspect`, all eight, from a freshly rebuilt index cache): Medium (57.5M objects)
7 s end to end, of which duplicate strings 2.7 s, finalizable and fragmentation 0.6 s each, the rest 0.1 to 0.2 s;
DenseHeap (54M objects) 15 s end to end, duplicate strings 5.1 s, collections 1.4 s, finalizable 1.4 s, fragmentation
1.5 s. In the UI the Small smoke run's inspections phase took 2.4 s with a longest dispatcher operation of 23 ms.
Rebuilding Medium's cache with free space and reference-array marks: 37 s (first load 19 s from the dump).
Live monitoring (sprint 1.7): the first counter sample arrives about one interval after the session starts; LeakyApi
leaking 20 requests of 2,000 items shows a GC heap rise of more than 10 MB within three samples; under load the heap
now climbed about 14 MB a second between collections while the after-GC size changed only at each GC. The Live smoke
run charts 1,800 samples (a full 30-minute window) in about 29 s with a longest dispatcher operation of 18 to 26 ms;
3,600 samples (an hour, the window sliding) stayed at 29 ms.
Leak detectors (sprint 2.6, Release CLI `inspect`, a fresh LeakyApi dump with every leak at 300,000 items each, 658 MB):
growing static collections 0.1 s, event handler leaks 1.5 s (300,000 subscribers on 1,024 publishers), timer leaks
0.4 s, disposed but referenced 0.3 s, HttpClient instances and service scopes 0.1 s each; all fourteen inspections
5.7 s end to end (measured before the review; reachability is now computed once per snapshot and kept, 0.125 bytes per
object, so later detectors skip that walk). The disposed probe reads each type's first reachable object, found in the
counting pass. On the clean LeakyApi run, which holds look-alike patterns above every threshold, none fires, with or
without a baseline.
Allocation traces (sprint 2.5, Release, the `allocations` benchmark with the worker's GC settings, Server GC with 12
heaps on the 24-thread machine; growth over private bytes before the read, the 2.5 review): a 54 s LeakyApi trace under
LoadGen whose process was killed before the rundown (5.6 MB, 45,110 samples) reads in 1.1 s with 40 MB of growth; 150 s
of LeakyApi under continuous load (25.6 MB, 168,073 samples) 1.1 s and 131 MB; 600 s (72 MB, 565,625 samples) 1.9 s and
227 MB, about 76 MB plus 2.1 bytes per file byte. The worker reserves 96 MB plus 3 bytes per file byte while reading
(`TraceStore.ReadReservation`, about 1.4 times the measured growth; sizes above 72 MB are extrapolated, and growth
follows samples, about 240 bytes each, more than file bytes); an open trace keeps 25 bytes per sample plus its names (14 MB for the 600 s trace).
Whole-trace queries take 2 to 13 ms (types 244 ms on the first call, JIT included). A 12 s trace of 5,000 requests
(4.1 MB, 5,415 samples) lists the five leaks' `Grow` methods as its top five call stacks. The UI smoke run's
allocations step (Release, the 150 s trace, after the Small-sized dump's phases): the cold first show 3.0 s with a
maximum lag of 74 ms; the whole run's longest dispatcher operation 66 ms, exit 0. TraceEvent's conversion cannot be canceled; it took under
2 s on these traces, and a much longer, busier trace would hold its reservation longer (Backlog: a streaming reader).
