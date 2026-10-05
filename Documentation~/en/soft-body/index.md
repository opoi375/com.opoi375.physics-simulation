# Soft Body Simulation

Mass-spring gives you a chain, cloth gives you a sheet, soft body wants a *lump*: an arbitrary mesh
that squashes when it lands, wobbles when you shove it, and pops back to shape when you let go.
That is what v1.2.0 adds — **turn any Mesh into a volume-preserving jelly**.

The whole idea in one line: the topology grows out of the mesh. Welded vertices become particles,
triangle edges become structural springs, opposite vertex pairs of shared edges become bend springs,
and the signed volume of the closed triangle set becomes a volume constraint. Hand it a mesh and it
figures out what to connect.

## 1. Five minutes in

```csharp
using PhysicsSimulation;
using UnityEngine;

// Build a soft body from any mesh (imported FBX, Unity primitive, procedural — all fine)
var data = SoftBodyMeshData.FromMesh(MeshAsset);
var soft = new SoftBodySimulation(new SoftBodyParameters
{
    mass = 0.9f,
    springStiffness = 260f,
    volumeStiffness = 1400f,
    substeps = 4
});
soft.Build(data);

// Want it hanging rather than falling over entirely? Pin the top layer.
for (int i = 0; i < soft.ParticleCount; i++)
    if (soft.GetPosition(i).y > 0.45f) soft.SetPinned(i, true);

// Advance (metres and seconds; gravity already lives in the parameters)
soft.Step(Time.fixedDeltaTime);

// Expand particle positions back into original mesh vertex order
var vertices = soft.CaptureMeshVertices();
instanceMesh.vertices = vertices;
instanceMesh.RecalculateNormals();
```

`Build` throws with a Chinese-language reason when it fails (empty mesh, out-of-range indices,
a triangle count that isn't a multiple of three). Using the component hides all of that — next section.

## 2. The `SoftBodyBehaviour` component

```csharp
var go = new GameObject("Jelly");
go.AddComponent<MeshFilter>();
go.AddComponent<MeshRenderer>();
var soft = go.AddComponent<SoftBodyBehaviour>();
soft.sourceMesh = MeshAsset;
soft.pinMode = SoftBodyPinMode.BottomVertices;     // glued to the table
soft.initialVelocity = new Vector3(2.4f, 0f, 0f);  // give it a shove
soft.Rebuild();
```

Same contract as `ClothBehaviour`:

- **The simulation lives in the component's own local space.** Moving, rotating or scaling the
  GameObject applies no force whatsoever — so you can animate the transform and still let the body wobble.
- **Your source mesh asset is never modified.** Write-back goes to the component's own instance mesh,
  with vertex count and triangle indices copied verbatim.
- **A failed build does not throw during gameplay.** The reason lands in `LastBuildError`, `IsBuilt`
  stays false, `Step` returns silently.

`pinMode` has four values: `None`, `TopVertices` / `BottomVertices` (take one height layer, tolerance
`pinLayerThickness`), and `ExplicitIndices` (pin exact source-mesh vertex indices).

::: warning Perturb with `initialVelocity`, not by pushing particles
A perturbation that only rewrites runtime particle positions (say, shearing them with `SetPositions`
in the editor) is **lost the instant Play mode runs `Awake → Rebuild`** from the source mesh — the demo
scene will sit there perfectly still. That trap was hit for real, which is why `initialVelocity` exists:
it is serialized, and every `Rebuild` applies it to all unpinned particles.
:::

## 3. Three constraints that grow out of the mesh

| Constraint | Source | What it resists | If you switch it off |
| --- | --- | --- | --- |
| Structural spring | Triangle edges (each unique edge once) | Stretch and squash within a face | The mesh flies apart |
| Bend spring | Opposite vertex pair across a shared edge | The crease angle between faces | Goes limp like wet paper |
| Volume constraint | Signed volume of the closed triangle set | Being squashed flat | Squeezes down and never returns |

A box subdivided 2×2 per face welds down to 26 particles: 8 corners + 12 edge midpoints + 6 face
centres. Note that **the structural spring count is not the edge count** — a triangulated box has 18,
because each face diagonal is a genuine triangle edge. That is the mesh deciding, not a bug.

Bend spring counts depend on the triangulation, so the docs give no formula; the reference page gives
invariants instead (never overlaps structural springs, no duplicates, always > 0 on a closed mesh).

## 4. The volume math

Divergence theorem turns the volume into a sum over triangles:

```
V = Σ_t  (1/6) · x0 · (x1 × x2)
```

The sign follows the winding, so **winding must face outward** — otherwise V is negative and the
restoring force happily pushes the body inside out. Taking the gradient per particle (one sixth of the
weighted area-vector sum around it) gives

```
F_i = -( k_v · (V - V₀) + c_v · dV/dt ) · ∇_i V
```

with `dV/dt = Σ_j ∇_j V · v_j`. This is a **gradient restoring force**, not a position projection, so it
shares the same explicit integrator and substepping as the structural springs — no extra iteration loop.

Open meshes (some edge used by exactly one triangle) have no meaningful volume: `IsClosed` is false,
`Volume()` returns 0 and the volume force is skipped. You get a sloshing shell instead, which still works.

## 5. Parameter feel

- `springStiffness` is a real spring constant in N/m (not the PBD ratio used by cloth). The default
  1200 is stiff — 0.8 kg hangs 6 mm lower — so **turn it down to see softness**. The demo jelly runs at 130.
- Stiff springs plus explicit integration explode. Three safeguards: `substeps` (default 4),
  `maxDeltaTime` (default 1/15 s), `maxStretchRatio` (8 Gauss-Seidel position-projection passes once
  exceeded) and `maxSpeed` (default 40 m/s cap). `maxSpeed` is the last line of defence: if you ever
  hit it, your stiffness numbers are wrong.
- Raising `volumeStiffness` makes it behave like an inflated ball, but also makes it twitch under violent
  deformation. 4000 with `volumeDamping = 20` is a stable pairing.
- `damping` is global velocity decay (0.5 keeps roughly 60% per second). To make it wobble longer, lower
  `damping` — don't delete the bend springs.

## 6. Measured performance (managed solver, no Burst)

Unity 6000.5.6f1, managed single thread, EditMode benchmark, best/mean over 5 batches × 60 steps:

| Size | Configuration | Best | Mean |
| --- | --- | --- | --- |
| Soft body, 642 particles | 1920 structural + 1920 bend springs, 1280 triangles, 4 substeps | **2.745 ms/step** | 2.814 ms/step |
| Cloth 32×32 | 1024 particles, 5826 constraints, 4 substeps | 3.299 ms/step | 3.351 ms/step |
| Cloth 64×64 | 4096 particles, 23938 constraints, 4 substeps | 19.447 ms/step | 20.477 ms/step |

A two-level subdivided icosphere (642 particles) costs 2.7 ms per step — enough to share a frame with a
32×32 cloth. The benchmark is itself a test,
`Benchmark_IcoSphere642_ManagedSolverFitsInsideOneFrame`, gated at 8 ms so a regression goes red.

## 7. Editor tools

`Tools ▸ Physics Simulation ▸ Soft Body`:

- **Create Soft Body Demo Scene** (120): writes `Assets/Scenes/SoftBodyDemo.unity` — a blue jelly pinned at
  the bottom that just got shoved, plus an orange bag pinned at the top swinging like a pendulum.
- **Build In Current Scene** (121): adds both bodies to the open scene, no file written.
- **Dump State** (122): logs, for **every** `SoftBodyBehaviour` in the scene, particle/spring/triangle
  counts, closedness, volume retention, max stretch ratio, max speed, `IsBuilt` and the failure reason.

When the picture won't move, Dump State is the tool that answers it: it prints `autoSimulate`, `enabled`,
`activeInHierarchy`, `IsBuilt`, and whether max speed is zero.

See the [Editor Tools overview](/en/tools/).

## 8. What's next

- [Soft Body Parameter Reference](/en/reference/soft-body-parameters): defaults, semantics, out-of-range behaviour.
- v1.3.0 moves the solver into Jobs + Burst (optional assembly); this benchmark is the baseline to beat.
