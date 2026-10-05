# From Arbitrary Mesh to Particles: How Welding Actually Works

> Bottom line first: **there is no octree**. Welding uses a **uniform spatial hash** whose cell size equals
> `weldTolerance`, followed by an exact squared-distance test. This page documents what the code really does,
> including its failure modes and measured numbers.

Implementation: `Build()` / `FindWelded()` / `InsertIntoBucket()` / `CellKey()` in
[`Runtime/SoftBody/SoftBodySimulation.cs`](https://github.com/opoi375/com.opoi375.physics-simulation/blob/main/Runtime/SoftBody/SoftBodySimulation.cs).

---

## 1. The three-stage pipeline

```text
mesh vertices + triangle indices
      |
      +-- (1) weld by position      vertices -> particles (_weldMap: vertex index -> particle index)
      +-- (2) extract edges         triangle edges  -> structural springs
      |                             shared-edge opposite vertices -> bend springs
      +-- (3) closedness + volume   every edge used by exactly 2 triangles => closed
                                    => signed rest volume V0 via the divergence theorem
                                    not closed => the volume constraint is silently skipped
```

`SoftBodyMeshData` is only a carrier for "vertex array + triangle indices"; it does not depend on `Mesh`,
`MonoBehaviour` or the physics engine, which is what makes the whole pipeline unit-testable bit-for-bit.

---

## 2. What welding actually does

Vertices are processed in the array's **original order**. For each vertex `p`:

1. **Cell coordinates** (the cell edge length *is* the tolerance):

   ```csharp
   int cx = Mathf.FloorToInt(p.x / tolerance);   // tolerance = parameters.weldTolerance
   ```

2. **Probe the 27 neighbouring cells** (own cell plus +/-1 on each axis):

   ```csharp
   for (dx = -1..1) for (dy = -1..1) for (dz = -1..1)
       if (buckets.TryGetValue(CellKey(cx+dx, cy+dy, cz+dz), out bucket))
           for (int b : bucket)
               if ((welded[b] - p).sqrMagnitude <= tolerance * tolerance) return b;  // first hit wins
   ```

3. **On a miss, create a particle** and insert its index into **its own single cell** (not into all 27).

4. `CellKey` mixes three prime-ish constants into an `unchecked long`:

   ```csharp
   long h = (long)x * 73856093L ^ (long)y * 19349663L ^ (long)z * 83492791L;
   ```

**Why 27 cells is exactly enough and no more**: if two points are within the tolerance and the cell edge
equals the tolerance, their cell indices can differ by at most 1 along any axis. So every possible match lies
inside the 27-cell neighbourhood, which is the smallest cubic neighbourhood covering a radius of one cell.

**Key collisions are harmless.** `CellKey` is a hash, so distinct cells may share a bucket. Buckets store
particle indices and every candidate still has to pass the exact `sqrMagnitude <= tolerance^2` test, so a
collision only costs extra comparisons — it can never weld far-apart points together.

**Determinism.** `FindWelded` returns the first hit. When `p` is within tolerance of two already-welded
particles that are themselves farther apart than the tolerance (a chain), the representative depends on
traversal and bucket insertion order — both of which are fixed, so the same input always yields the same
particles, which is what makes bit-for-bit assertions possible. The cost: **welding is not an equivalence
relation** (A~B and B~C can hold while A and C stay separate).

---

## 3. Why a hash and not an octree

| | Uniform spatial hash (current) | Octree |
|---|---|---|
| Prior information needed | none, built while iterating | a bounds pre-pass plus a depth cap |
| Degenerate distributions | unaffected | thin shells / long strips refuse to subdivide, degrade to linear |
| Insert cost | amortised O(1), no recursion | may split on every insert |
| Determinism | comes free from the fixed traversal | split/rebalance order becomes a new dependency |
| Accuracy | the decision is an exact distance test; a tree **only reduces candidates** | same — a tree buys zero accuracy |
| Code size | ~40 lines, no recursion, one `Dictionary<long, List<int>>` | node struct + recursion + depth policy |

The last two rows carry the argument: **result accuracy is independent of the index structure**, because the
final verdict is one `sqrMagnitude` comparison. A tree only reduces the candidate count, and the candidate
count in a hash bucket is already proportional to local density x tolerance^3.

An octree **starts to pay off** when tens of thousands of vertices land in a single cell (a wildly
over-welding tolerance relative to feature size); the bucket then degrades into a linear scan. This project's
meshes are nowhere near that (median weld ratio 3.00, see the next page). **Honest caveat: per-bucket
occupancy was never instrumented**, so this page only states *when* you would need a different structure —
it does not claim "measured fast".

---

## 4. What the tolerance really means (three tests pin this down)

`weldTolerance` is a **radius** — not a diameter, not a hint about cell size. All three consequences are
covered by [`SoftBodySimulationTests`](https://github.com/opoi375/com.opoi375.physics-simulation/blob/main/Tests/Editor/SoftBodySimulationTests.cs):

| Situation | Outcome | Test |
|---|---|---|
| Vertices **exactly coincident** (the usual FBX seam) | welds at any positive tolerance | `Weld_CoincidentVerticesMergeEvenAtTinyTolerance` (24 verts -> 8 particles at 1e-6) |
| Seam gap <= tolerance | welds, topology closed, volume constraint active | `Weld_ToleranceIsARadius_...` (0.01 gap + 0.02 tol -> 8 particles, closed, V0 ~ 1) |
| Seam gap > tolerance | **splits into two particle sets -> not closed -> volume constraint silently skipped** | same test (0.01 gap + 0.005 tol -> 12 particles, open, `RestVolume() == 0`) |

Row three is the expensive one in practice: a box that *looks* closed ends up running as **a bag of springs
with no volume rebound**, with no error raised. Only the audit verdict `OpenMesh` reveals it — 11 of the 104
scanned meshes in this project are exactly that case.

**Choosing the tolerance**:

- Lower bound: at least the largest seam gap you can tolerate. The default `1e-4` m (0.1 mm) only suits
  assets whose seam vertices are strictly coincident.
- Upper bound: no more than 1/3 of the model's smallest feature, or distinct corners merge and the rest volume
  inflates or degenerates.
- How to check: run `Tools/Physics Simulation/Soft Body/Audit Selected Meshes (123)` and read the `weld x`
  and `closed` columns. A weld ratio clearly below that of comparable assets plus `closed = no` means the
  tolerance cannot reach the seams.

---

## 5. The real precision wall: float resolution, not cell-index overflow

A hypothesis that once made it into the design notes: "cell coordinate = position / tolerance, and `CellKey`
takes `int`s, so large coordinates with a tiny tolerance overflow 32 bits and welding fails silently — add a
guard."

**The tests disproved it**, and the way it was disproved is more useful than the guard would have been:

- The float gap at magnitude `|c|` is about `|c| * 2^-23`. At `c = 1e7` that is ~1.2 m; at `5e7`, ~6 m.
- So a 1 m cube moved to `x = 5e7` cannot even be *represented*: its edges collapse onto identical floats,
  triangle areas become zero, and the build rejects it outright
  (`三角形 0 面积为 0（三点共线），体积梯度会失效`).
- Inside the range where floats still resolve the geometry, coincident vertices overflow to the *same*
  garbage cell index (same input, same overflow) and therefore still weld; non-coincident vertices differ by
  less than that magnitude's resolution, so they are coincident anyway.

Conclusion: **the hash's `int` cell index cannot break before float precision does**, so no speculative guard
was added. The measured contract is what the test pins down
(`Weld_MeshFarFromOrigin_IsRejectedWithAClearGeometricError`).

Practical rule: **keep soft bodies near the world origin**; the problem is vertex data carrying tens of
kilometres of import offset, not the behaviour's transform. Using `ulp ≈ |c| * 1.19e-7`:

| Feature size you need to keep | Coordinate magnitude limit |
| --- | --- |
| 1 mm | ~8.4e3 m (ulp ~1 mm there) |
| 1 cm | ~8.4e4 m |
| 1 dm | ~8.4e5 m |

---

## 6. Rejections raised at build time

`Build()` never tolerates broken geometry "just to get something running". Each case throws an
`ArgumentException` whose message names the parameter:

| Rejected when | Why |
|---|---|
| empty vertex or triangle array, index count not a multiple of 3 | cannot build |
| a vertex is non-finite (NaN / Infinity) | one NaN poisons every spring chain |
| triangle area 0 (three collinear points) | the volume gradient sums area vectors; collinear gives zero |
| a triangle degenerates **after welding** | same reason; the message names the triangle and the active tolerance |
| invalid parameters (mass <= 0, tolerance <= 0, `maxStretchRatio` < 1, ...) | `SoftBodyParameters.Validate` |

In the audit report these appear as `BuildFailed`, and the **note column carries the reason verbatim**
(e.g. `三角形 83 在焊接容差 0.0001 下退化成一条线，请缩小 weldTolerance 或简化网格`).

---

## 7. Welding measured on 104 real meshes

| Metric | Measured |
|---|---|
| weld ratio `mesh verts / particles` | median **3.00**, max **5.00** |
| implication | roughly **two thirds** of art vertices are duplicates; welding is a necessity, not an optimisation |
| particle count | median **67**, min 0 (build failures), max 576 within budget |
| topologically closed | **91 / 104 = 87.5%** |

Full distribution, failure taxonomy and the parameter comparison live on
[Real-Model Audit](/en/soft-body/model-audit).
