# ADR-027 Dominator children kept; the tree served flattened (added in sprint 2.4)

- Context: 2.4's retention explorer walks the dominator tree from the top (the biggest retainers) and below any object.
  2.3 kept only the immediate dominators and retained sizes; `GroupRetention` rebuilt the children (12 bytes per object)
  on every run, and the 2.3.7 review (M9) asked for one kept form. A tree view of millions of nodes cannot be a UI tree
  (8; the 2.3.6 review).
- Decision: the dominators keep the tree's children as a CSR in object index order, computed with them and stored in
  their sidecar as two more sections (`DominatorChildStarts`, `DominatorChildren`); `DominatorTree.AlgorithmVersion` 2,
  so older sidecars are rebuilt once. Kept bytes go from 12 to 20 per object (Medium, 57.5M objects: about 440 MB more,
  mapped when the snapshot is). A query orders only the rows it shows (`RetainedOrder.Top`, largest retained first,
  ties by index): storing every node's children sorted took Medium's dominators from 8 to 13 s and scattered the walk
  of `GroupRetention` (the 2.4 review). The worker serves the tree flattened and paged (`QueryDominatorTreeAsync`): the
  client sends the keys it expanded, and the row to keep its place on, and gets rows in display order; it keeps no tree.
- Consequences: biggest retainers, expansion, focus and the icicle read a node's children as a slice and pick the
  largest in O(m log k); `GroupRetention` works in 4 bytes per object, in index order. Each expansion is a new query
  (0.03 s on Medium) whose keys survive a worker restart; Contracts owns the keys, kinds and limits. A future streaming
  or lazily expanded tree can use the same CSR.
