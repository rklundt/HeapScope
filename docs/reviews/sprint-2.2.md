# Review: Sprint 2.2 Retention paths

Reviewers: solution-architect, senior-dev, gc-specialist (parallel, the change set since `sprint-2.1`). Phase 2 runs
autonomously one sprint at a time (the product owner said "go" for 2.2 on 2026-10-24), so Moderate findings were decided
by the lead engineer with their recommendation and are marked "decided autonomously".

Totals after dedupe: 3 High, 9 Moderate, 19 Low (findings raised by more than one reviewer are merged and keep the
highest severity).

The reviewers confirmed:
- the CLR semantics: weak handles never retain, collectible statics, ref-counted handles, dependent-handle edges from an
  unreachable primary, gcdump dependent-handle values as roots, the finalizer queue;
- the tie rules in both searches;
- the signature interning;
- layering, the protocol and the enum mirror;
- the format 2 migration;
- that logs carry no snapshot contents.

## High / Urgent

- **H1 An object's tree had no size or depth limit** (solution-architect, senior-dev). A path through a long list sent one
  node per object (tens of MB of JSON) and the UI nested one TreeViewItem per step, which can overflow WPF's layout stack
  and kill the process; the CLI indented quadratically. **Fixed:** the worker builds the wire tree path by path
  (`RetentionMapping.TreeBuilder`): a run of one type is one node with `RetentionNodeDto.Folded`; a path over 64 segments
  keeps its ends around a "(N objects not shown)" gap node; the tree stops before 2,000 nodes and sets
  `ObjectPathsResult.Truncated`. The UI and CLI show folded counts and gaps. Tests: a 100,000-object list becomes 3
  nodes, 1,000 alternating objects become 64 with a gap of 938, the node limit, and (found while testing the fix) an
  object held by a root itself. Confirmation below.
- **H2 A search stopped at its limit called a live object unreachable** (gc-specialist). With no root within 1,000,000
  visited objects the UI and CLI said "garbage". **Fixed:** reachability comes from the root path tree (exact); the
  backward search skips objects the tree does not reach (an unreachable object is answered at once) and falls back to
  the tree's own path when it stops before any root (`RetentionSearch.FromRootPath`, with StoppedEarly); cancellation
  counts dequeues. Test `A_search_that_stops_before_any_root_falls_back_to_the_tree_and_skips_garbage`. Confirmed by
  gc-specialist.
- **H3 The CLI printed holders under the wrong parent when an object had several roots** (senior-dev as High;
  solution-architect, gc-specialist as Moderate). **Fixed:** a preorder walk (`WriteHoldersAsync`, `NodeText`); test
  `PathsTextTests` asserts the exact lines for two roots, a folded node and a gap. Confirmation below.

### Confirmations

- H1: CONFIRMED by solution-architect (payload and depth bounded, fields on the right edges, tests). senior-dev found the
  fix itself merged segments on their first object only, so two paths branching inside a run of one type could show a
  holder on the wrong object; solution-architect noted key path steps were still unbounded on alternating paths. Both
  fixed: segments merge only when identical (first, last and count), and key path steps are cut to 64 around one
  "(N steps not shown)" step. Tests `Paths_that_branch_inside_a_run_of_one_type_never_show_a_holder_on_the_wrong_object`
  and `A_key_path_over_an_alternating_chain_keeps_its_ends_around_one_step`.
- H3: CONFIRMED by senior-dev and solution-architect (preorder walk, exact-line test).
- H2: CONFIRMED by gc-specialist on re-reading the code: pruning is sound (an object no root reaches cannot lead to one),
  frozen objects are sources so never pruned, `FromRootPath` matches the tree's root choice and reports StoppedEarly,
  the kind-aware unreachable text is used by the UI and CLI.

## Moderate (decided autonomously)

- **M1 The root path tree was not checked against the memory budget and left its queue as garbage**
  (solution-architect; gc-specialist as Low). **Fix now:** the worker refuses with InsufficientMemory when its 8 bytes per
  object would not fit what is left of its share, and returns the queue with a non-compacting gen2 collection.
- **M2 Two definitions of where paths start** (solution-architect). **Fix now:** `PathRoots.Sources` (best first, frozen
  last) and `PathRoots.SemanticsVersion`; the root path tree uses them, 2.3's dominators will.
- **M3 "Unreachable" was explained as garbage for every kind** (solution-architect, gc-specialist). **Fix now:**
  `UnreachableNote` on both results from the snapshot's kind (a gcdump holds only survivors, so it means an unrecorded
  root); UI and CLI show it.
- **M4 Worker-level tests were missing** (solution-architect, senior-dev). **Fix now:** argument and unknown-type tests
  through the real worker (`Bad_requests_are_refused_and_an_unknown_type_has_no_paths`), the root path tree slot
  shared once and dropped on close.
- **M5 KeyPaths allocated a stack per object and its memo was unbounded** (gc-specialist; solution-architect,
  senior-dev as Low). **Fix now:** one reused stack, the memo capped at 8 million entries (ancestors only).
- **M6 Root kinds other than statics and the gcdump CLI were untested** (gc-specialist, senior-dev). **Fix now:** thread
  stack, interior pointer and pinned handle roots from the RuntimeKinds fixture end to end; `paths` on a gcdump with exit
  codes 1 and 64.
- **M7 Measurements predated the shipped code and skipped worst cases** (gc-specialist; others as Low). **Fix now:** re-run
  after every fix with an unreachable object added: Medium peak 2,802 MB in memory, 1,197 MB mapped; DenseHeap 3,320 /
  1,416 MB; an unreachable object 0 to 1 ms; the section 10 row qualified (32 GB machine).
- **M8 View model tests covered only the happy path** (senior-dev). **Fix now:** a worker error shown as the status, a
  tree with two roots in order with a folded node.
- **M9 Cancellation in the backward search could be skipped; no cancellation or deep tie tests** (senior-dev;
  gc-specialist as Low). **Fix now:** a dequeue counter; tests for all three computations and a depth-3 tie.

## Low / Trivial (listed, not fixed unless noted)

- L1 For 2.4: `PathRootDto` carries the root detail only in its description; steps carry no module (solution-architect).
- L2 `KeyPathDto.ExampleAddress` and `Bytes` mean the root's object and the target's size in an object's key paths
  (solution-architect).
- L3 `HeapGraph.FromColumns` does not check that dependent handles are sorted (solution-architect).
- L4 The CLI's `--max` defaults to 10 for `--type` where decision 3 and the UI use 20 (solution-architect, senior-dev).
- L5 The per-object total of all 2.x indices did not count the root path tree (solution-architect). Fixed in the docs
  (about 66 bytes per object).
- L6 No ADR for the retention semantics shared with 2.3 (solution-architect). `PathRoots` (M2) is the single source; an
  ADR can come with 2.3.
- L7 The search's dictionary and queue grow by doubling; nodes at the stop distance still expand (gc-specialist).
- L8 Thread statics are `Other` roots whose detail names the thread, so key paths split by thread, and they rank after
  the finalizer queue (gc-specialist, senior-dev).
- L9 A step whose field lookup was skipped by the budget can be labeled "(dependent handle)" when the primary also holds
  it in a field (gc-specialist).
- L10 gcdump `[static vars]` roots read "Static field " with no name (gc-specialist).
- L11 An unreachable finalizable object will be resurrected for its finalizer first (gc-specialist).
- L12 `Why alive` ignores an instances view's scope (senior-dev).
- L13 The incomplete-snapshot text is reused by string replacement; an unrequested cancel shows a bare message; the
  paths view's CancellationTokenSource is not disposed (senior-dev).
- L14 Type key path steps do not mark dependent-handle edges (senior-dev).
- L15 `types` is read through `IReadOnlyList<bool>` in the per-object loops (senior-dev).
- L16 Two doc comments still say unreachable means garbage for every kind (`RetentionTree.IsReachable`,
  `KeyPathSet.Unreachable`) (gc-specialist, after H2). Fixed, as docs must describe what was built.
- L17 The first object query of a snapshot now also builds the root path tree (1 to 2 s, 8 bytes per object while
  built) (gc-specialist, after H2). Noted in ARCHITECTURE 6.4.
- L18 The CLI tree printer and the UI tree folding duplicate the framework fold rule (senior-dev, from H3).
- L19 The sprint record overstated task 4's tests (solution-architect). Covered by M4.

## Test runs and measurements

- Gate before the review: pass, 819 tests, zero warnings.
- Smoke runs (`--smoke-open` Small) after the fixes: pass, maximum lag 52 to 61 ms, longest operation 58 to 59 ms; the
  drill shows the object's retention tab and the type's Why alive (root path tree 0.2 to 0.4 s).
- Measurements: ARCHITECTURE 6.4 and 10.
