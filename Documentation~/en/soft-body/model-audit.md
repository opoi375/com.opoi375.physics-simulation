# Real-Model Audit: Soft Bodies on This Project's Art Assets

The jelly icosphere in the demo scene is a clean, procedurally generated mesh. Its health says nothing about
an arbitrary `.fbx`. This page is the result of **scanning every mesh asset in the Unity project that hosts
this package**; every number comes from the generated `Logs/SoftBodyMeshAudit.md`, not from estimates.

- Runtime API: [`Runtime/SoftBody/SoftBodyMeshAudit.cs`](https://github.com/opoi375/com.opoi375.physics-simulation/blob/main/Runtime/SoftBody/SoftBodyMeshAudit.cs)
- Editor tool: [`Editor/SoftBody/SoftBodyModelAuditTools.cs`](https://github.com/opoi375/com.opoi375.physics-simulation/blob/main/Editor/SoftBody/SoftBodyModelAuditTools.cs)
- How particles get generated in the first place: [From Arbitrary Mesh to Particles](/en/soft-body/mesh-to-particles)

---

## 1. How to run it

| Menu | Purpose |
| --- | --- |
| `Tools/Physics Simulation/Soft Body/Audit Selected Meshes (123)` | audits the Project-window selection (`GameObject`, `Mesh` or model — multi-sub-mesh assets produce one row each) |
| `Tools/Physics Simulation/Soft Body/Audit Mesh Assets In Folder (124)` | scans a whole folder (default `Assets`) and emits **two tables plus a comparison summary** |

The audit is deliberately plain: run the **real** `SoftBodySimulation` for **90 steps at 1/60 s**, put a
world-space `PlaneCollisionProxy` half-space at `y = 0` underneath as the ground, then write down whether the
body landed, kept its volume, turned inside out, and what it costs per step. The report goes to
`Logs/SoftBodyMeshAudit.md` (to the Console when no `Logs/` folder exists).

**Budget guards** (a scan must never hang the editor):

- at most **120** meshes per scan; anything above **4000** vertices is skipped and counted separately.
- asset paths are sorted with `StringComparer.Ordinal`, so two scans of the same assets produce a
  byte-identical table.

---

## 2. Overview: 61 of 104 meshes work out of the box

Scanning `Assets` (93 `.fbx` files, many with several sub-meshes, plus standalone mesh assets):
**107 meshes found**, 3 skipped for exceeding the vertex budget, **104 audited**.

| Verdict | Default parameters | Recommended (size-scaled) | Meaning |
| --- | --- | --- | --- |
| `Healthy` | **61** (58.7%) | **66** | closed, correct winding, volume retention inside 0.60–1.40 after 90 steps |
| `OpenMesh` | 11 | 11 | not closed => **the volume constraint is silently skipped** |
| `Unstable` | 23 | 18 | collapsed, or inverted mid-run |
| `InvertedWinding` | 4 | 4 | closed but signed volume negative — the whole model is inside-out |
| `DegenerateVolume` | 3 | 3 | closed but encloses a negligible volume (zero-thickness shell) |
| `DegenerateWeld` | 0 | 0 | welding collapsed the mesh below 4 particles |
| `BuildFailed` | 2 | 2 | rejected at build time with a reason (see §5) |

Topological closure rate: **91 / 104 = 87.5%**.

---

## 3. The dominant failure is *inverting*, not *collapsing*

Classifying all 104 rows by their note:

| Actual death mode | Count | Comment |
| --- | --- | --- |
| **Volume sign flipped during the run** | 9 | one volume-constraint step crosses zero and the body turns inside out — the single largest group |
| Collapsed out of the retention band (no flip) | 14 | self-weight beats the springs and the volume rebound |
| Winding already inverted on import | 4 | negative rest volume; the solver runs symmetrically, but every face is inside-out |
| Zero-thickness shell (enclosed volume < 1 cm³) | 3 | no headroom for the volume constraint |
| Broken geometry (build rejected) | 2 | see §5 |
| Open by design | 11 | open crates, ground, ramp, staircase |

A negative `volume retention` is the numeric fingerprint of an inversion. The earlier report printed both an
inversion and a "rest volume is essentially zero" case as `0.000`; `InvertedWinding` and `DegenerateVolume`
are now separate verdicts with the reason written into the note column.

---

## 4. Representative rows (default parameters, abridged)

| Model | verts | particles | weld x | closed | retention | max stretch | ms/step | verdict |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| prop_barrel | 180 | 60 | 3.00 | yes | 0.982 | 1.043 | 0.142 | Healthy |
| prop_mine_track_curve | 648 | 216 | 3.00 | yes | 0.819 | 1.107 | 0.426 | Healthy |
| prop_flower_pot | 662 | 180 | 3.68 | yes | 0.784 | 1.204 | 0.449 | Healthy |
| prop_chest | 240 | 68 | 3.53 | no | 0.000 | 1.202 | 0.122 | OpenMesh |
| prop_crate_open | 120 | 32 | 3.75 | no | 0.000 | 1.025 | 0.052 | OpenMesh |
| prop_tree_sakura | 482 | 123 | 3.92 | yes | 0.514 | 2.000 | 0.478 | Unstable |
| prop_clock_tower | 1684 | 473 | 3.56 | yes | **-1.308** | 2.000 | 2.093 | Unstable |
| Stairs_650_400_300_Mesh[1] | 240 | 80 | 3.00 | yes | -0.049 | 2.420 | 0.366 | Unstable |
| Box_350x250x200_Mesh | 24 | 8 | 3.00 | yes | **5.518** | 2.000 | 0.024 | Unstable |
| FactoryRoom[7] | 1656 | 552 | 3.00 | yes | 0.000 | 2.000 | 2.198 | Unstable |
| FactoryRoom[10] | 1560 | 520 | 3.00 | yes | 0.000 | 1.193 | 1.077 | DegenerateVolume |
| prop_cave_entrance | 806 | 0 | 0.00 | no | — | — | — | BuildFailed |

Two instructive extremes:

- `Box_350x250x200_Mesh` has only **8 particles** (24 verts welded into 8 corners). A single-cell box can
  turn inside out entirely, or balloon to **5.5x** its volume. **With 8 corners the volume constraint has no
  spatial resolution to push back** — a box like that should not be a soft body; subdivide it, or use cloth or
  the mass-spring system.
- `prop_clock_tower` (3.9 x 7.6 x 3.9 m, 473 particles) reaches retention **-1.308** at default parameters.
  That is not coarseness, it is **size**: particle mass is fixed at 1 kg, so a seven-metre jelly crushes its
  own volume constraint under self-weight.

---

## 5. Verbatim build rejections (note column)

These two are not "mediocre results", the geometry simply cannot stand:

```text
prop_cave_entrance  三角形 83 在焊接容差 0.0001 下退化成一条线，请缩小 weldTolerance 或简化网格
Structure_Mesh      三角形 58 面积为 0（三点共线），体积梯度会失效
```

The first line is worth reading carefully: at a tolerance of **0.0001 m** triangle 83 already degenerates,
i.e. the asset contains near-coincident vertex pairs. Usually that is duplicated vertices on export; it can
also mean the tolerance is large for this model's scale — shrinking `weldTolerance` and re-running separates
the two explanations.

---

## 6. Scaling parameters with size: 6 saved, 1 broken

`RecommendedParameters(diagonal)` scales stiffness linearly with the bounding-box diagonal (1–8x) and uses
`4 + (diagonal - 1)` substeps. Re-scanning the same 104 meshes:

| Model | default retention | recommended retention | verdict change |
| --- | --- | --- | --- |
| prop_tree_pine_tall | 0.447 | 1.044 | Unstable -> **Healthy** |
| prop_tree_round | 0.353 | 1.185 | Unstable -> **Healthy** |
| prop_tree_sakura | 0.514 | 1.153 | Unstable -> **Healthy** |
| prop_mushroom_cluster | 0.352 | 0.734 | Unstable -> **Healthy** |
| prop_reed | 0.245 | 0.693 | Unstable -> **Healthy** |
| Box_350x250x200_Mesh | 5.518 | 0.709 | Unstable -> **Healthy** |
| **Tunnel_Mesh** | **1.323** | **0.208** | **Healthy -> Unstable** ⚠️ |

Summary line: **improved 6 | unchanged 97 | worse 1 | still unhealthy after retuning 20**.

This is a **trade, not a free win**, and the docs must say so:

- The tall-and-thin death mode (trees, reeds, mushrooms) really is cured by "stiffer + more substeps".
- But `Tunnel_Mesh` (a 2.5 x 6.0 x 2.5 m tube) gets **squashed by the over-stiff combination**: a harder volume
  constraint plus harder springs at the same step size overshoots through zero more easily.
- **Therefore it stays a diagnostic suggestion, not the package default.** Defaults remain conservative; the
  report's job is to tell you which knob to turn.

The 20 meshes that stay unhealthy are almost all `FactoryRoom[*]` zero-thickness or inverted sub-meshes plus
the open-by-design props: **the bottleneck is the art, not the solver**.

---

## 7. Cost: why the vertex budget sits at 4000

| Sample | particles | ms/step |
| --- | --- | --- |
| the 104 in-budget meshes | median 67 | median **0.140**, p90 **0.394**, max **2.198** (`FactoryRoom[7]`, 552 particles) |
| `terrain_island` | 2497 | **5.374** |
| `IslandMesh_Showcase` | 2497 | **5.394` |
| `WaterPlane_Showcase` | 5329 (weld ratio 1.00, no duplicates at all) | **12.359** |

The last three are exactly the meshes the budget now skips; their numbers come from the pre-budget full scan.
**12.4 ms for a single step at 5329 particles** is 74% of one 60 fps frame — and it is an open terrain sheet
with no volume rebound at all. The budget line is not fastidiousness, it blocks magnitudes that cannot work.

---

## 8. API usage

```csharp
using PhysicsSimulation;
using UnityEngine;

var data = SoftBodyMeshData.FromMesh(mesh);          // any Mesh -> vertices + triangles
var result = SoftBodyMeshAudit.Audit(data, steps: 90, addGround: true);

Debug.Log(result);                                   // one line, verdict plus every number
if (result.Verdict != SoftBodyAuditVerdict.Healthy)
    Debug.LogWarning(SoftBodyMeshAudit.Note(result)); // the reason, verbatim
```

`SoftBodyMeshAudit` never throws on dirty input — a build failure becomes `BuildFailed` with the message
stored in `BuildError`. That is precisely what lets one scan chew through 104 meshes without aborting.

---

## 9. Limits of this audit (don't read it as a quality guarantee)

- **Only 90 steps (0.6 s of simulated time).** Slow-onset instabilities are invisible here.
- **The ground is an infinite half-space plane**, not the demo scene's `BoxCollider`; re-run for other setups.
- **Single body only** — no body-to-body contact (the package does not provide self-collision either).
- Parameters are either **runtime defaults** or the single fixed heuristic of §6, never per-model tuning.
- The verdicts look at **closure / volume / stretch / numeric sanity**, not at aesthetics.
  `Healthy` does not mean "looks like a tasty jelly".
- The sample is **this one project's** assets (low-poly props plus stair-generator output), not a general benchmark.
- 104 rows contain **near-duplicate geometry**: `terrain_island` and `IslandMesh_Showcase` have identical
  vertex/particle counts and bounds (140 x 14.35 x 140, 2497 particles) — almost certainly the same terrain
  imported twice. Percentages are per row, not per unique geometry.
- Different from duplicates: the seven rock variants all report 60 verts / 12 particles, and `prop_bench` and
  `prop_fence` both report 96 / 32. Those are distinct assets that merely share a topology family.
