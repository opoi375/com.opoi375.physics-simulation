# Cloth Simulation

Available since `v1.1.0`. Turns a `columns × rows` grid into fabric that sags, flaps and drapes over a ball.

Unlike the mass-spring module, cloth is **not a force model** — it is a **position-based model (PBD / XPBD-style distance
constraints)**: each substep predicts positions, projects every constraint back into its valid range, and then derives
velocity from the position change. That is why a `stiffness` of 1 does not explode the way an explicit spring would, and
why the parameters are so easy to dial in.

> The solver `ClothSimulation` is plain C# plus `UnityEngine.Vector3`. It needs no Unity physics, no URP/HDRP, no Jobs or
> Burst, can run in EditMode without any GameObject, and produces **bitwise identical results for identical parameters**.

## 1. Five minutes in

```csharp
using UnityEngine;
using PhysicsSimulation;

public class Flag : MonoBehaviour
{
    public ClothParameters parameters = new ClothParameters { columns = 24, rows = 16, spacing = 0.06f };

    ClothSimulation _cloth;

    void Awake()
    {
        _cloth = new ClothSimulation(parameters);

        // Pin the whole top edge = a flag; pin only the top-left corner = a cape
        for (int col = 0; col < parameters.columns; col++)
            _cloth.SetPinned(_cloth.IndexOf(col, 0), true);
    }

    void Update()
    {
        _cloth.AddWindImpulse(new Vector3(0.35f, 0f, 1.1f), Time.deltaTime);
        _cloth.Step(Time.deltaTime);

        // Build your own mesh from _cloth.GetPosition(i), or just use the component below
    }
}
```

## 2. The `ClothBehaviour` component (recommended)

`ClothBehaviour` bundles solver + mesh + obstacles + wind into one mountable component. It simulates in the component's
**own local space**: the grid lies in the local XY plane, columns run along `+X`, rows along `-Y`, and the front face
normal points to `+Z` — so put your camera on the `+Z` side or you will look at an edge.

| Field | Purpose |
| --- | --- |
| `parameters` | The parameter block documented in the reference |
| `pinEdges` | Combinable `None / Top / Bottom / Left / Right` flags |
| `autoSimulate` | Turn off to drive `Step(dt)` yourself (fixed timestep, replays) |
| `windAcceleration` | Acceleration added to every free particle each frame (an approximation, not an area-pressure model) |
| `obtainObstaclesFromTransforms` | Automatically treat `obstacles` transforms as spheres |
| `obstacles` | Obstacle list; a `SphereCollider` radius (times world lossy scale) is used when present, else `obstacleRadiusFallback` |
| `generateMesh` / `recalculateNormals` | Whether to build/update the mesh and its normals |
| `drawGizmoWireframe` | Draw structural edges and particle dots in the Scene view |

Methods: `Rebuild()`, `Step(dt)`, `ResetToInitialLayout()`, `CaptureCurrentAsInitial()`,
`CollectStructuralEdges(...)`, plus `System` / `IsBuilt` / `LastBuildError` / `Mesh`.

::: tip Obstacles: the v1.1.0 sphere lives in local space, scene colliders in world space
`ClothSimulation.AddSphereObstacle(center, radius)` now registers a **`Simulation`-space** `SphereCollisionProxy`:
its coordinates are in cloth-local space, exactly like the particles, while `ObstacleCount` reports the **total proxy
count** (bridged world colliders included). The other route is `collideWithSceneColliders` + `sceneColliders`, where
`ClothBehaviour` samples the scene's `Collider`s into **`World`-space** proxies, each of which round-trips a particle
through world space to push it out and back. That makes boxes and capsules exact (before v1.3.0 only spheres worked,
and a non-uniformly scaled one was approximated with `lossyScale / √3`). Both routes coexist, `Step` walks
`Collisions` in insertion order, and determinism is untouched.
:::

## 3. Topology: three constraint families

| Type | Connects | Rest length | Count (`c` columns, `r` rows) | Controls |
| --- | --- | --- | --- | --- |
| `Structural` | Horizontal / vertical neighbours | `spacing` | `r·(c-1) + c·(r-1)` | Fabric never stretches; defines area and silhouette |
| `Shear` | Both diagonals of each cell | `spacing·√2` | `2·(c-1)·(r-1)` | Resists skewing, stops the sheet collapsing sideways |
| `Bend` | Neighbours two apart | `2·spacing` | `r·(c-2) + c·(r-2)` | Resists folding — denim vs silk |

`enableShear` / `enableBend` drop whole families for performance. With both off you keep only structural edges — handy for
rope nets or small props where you need the budget back.

## 4. Pipeline and feel

Inside one `Step(dt)`:

1. `dt` is clamped by `maxDeltaTime`, then split into `substeps` smaller steps;
2. each substep: **predict** (gravity + implicit damping `1/(1+damping·h)`) → **project** constraints `iterations` times
   (structural → shear → bend) → **clamp stretch** → **resolve collisions** → **commit** velocity `(newPos - prevPos)/h`;
3. pinned particles have `inverseMass = 0` and are never moved by any stage.

Quick tuning guide:

- Fabric feels like a rubber band → raise `structuralStiffness` (1 = fully projected per pass).
- Fabric feels like sheet metal → lower `bendStiffness` (0.05–0.1 soft, 0.3+ canvas/leather).
- Jitter or tunnelling → raise `substeps` (better value than raising `iterations`).
- Need performance → `iterations = 1`, disable `enableBend`, lower grid resolution.
- Want a hard safety net under extreme parameters → `maxStretchRatio`, enforced by up to 32 clamping sweeps.

::: warning Stretch clamping is a Gauss-Seidel approximation
`ClampStretch()` sweeps up to 32 times; fixing one edge can over-stretch another, so the guarantee is convergence, not an
exact simultaneous solve. Collisions are resolved *after* clamping, which makes "no particle inside a sphere" a hard
guarantee while accepting that a constraint may momentarily exceed `maxStretchRatio`.
:::

## 5. Measured performance (managed solver, no Burst)

Measured by the EditMode benchmark tests in the package: Unity Editor, Mono managed execution, `substeps = 4`,
`iterations = 2`, best and mean over several batches.

| Grid | Particles | Constraints | Best | Mean | Notes |
| --- | --- | --- | --- | --- | --- |
| 32 × 32 | 1,024 | 5,826 | **4.65 ms/step** | 4.95 ms/step | Cape/flag tier, fits inside one frame |
| 64 × 64 | 4,096 | 23,938 | **20.2 ms/step** | 20.5 ms/step | Stress tier, roughly two frames per step at 60 fps |

The benchmarks double as regression gates: `Benchmark_32x32_ManagedSolverFitsInsideOneFrame` (< 8 ms) and
`Benchmark_64x64_ManagedSolverStaysWithinTwoFrames` (< 33 ms). The optional Jobs + Burst assembly planned for `v1.4.0`
exists to push 64 × 64 back under a single frame.

## 6. Editor tools

- `Tools/Physics Simulation/Cloth/Create Cloth Demo Scene` — writes `Assets/Scenes/ClothDemo.unity`: a 20×14 grid, pinned top edge, wind blowing towards the camera and a ball the cloth wraps around. Saves silently, no modal dialogs.
- `Tools/Physics Simulation/Cloth/Build In Current Scene` — adds one cloth to the scene you already have open.
- `Tools/Physics Simulation/Cloth/Dump State` — logs particle/constraint/pinned counts, max stretch ratio and whether any non-finite state appeared.

## 7. Next

- [Cloth parameter reference](/en/reference/cloth-parameters)
- [Mass-spring module](/en/mass-spring/)
- [Changelog](/en/changelog)
