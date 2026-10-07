# Fluid Simulation (PBF)

Available since `v1.5.0`. Turns a blob of water (a box, a sphere, a dam-break column) into a particle fluid that
spreads, flows around obstacles, gets stopped by walls — and does **not** turn itself into bullets.

The implementation is **PBF (Position Based Fluids, Macklin et al. 2016)**: like the cloth module it is a
*position constraint model* — each substep predicts positions, projects them back onto the constraint
"density equals rest density", and derives velocity from the position change. There is no explicit pressure term
and no stiffness knob; `restDensity` is a physical quantity, not a tuning handle. Stability comes from
**substeps + projection iterations + three fuses**.

> The core solver `FluidSimulation` is plain C# + `UnityEngine.Vector3`: no Unity physics, no URP/HDRP, no Jobs/Burst.
> It runs in EditMode without any GameObject, and **the same parameters produce bit-identical results on any machine**.

## 1. Five minutes

```csharp
using UnityEngine;
using PhysicsSimulation;

public class WaterBucket : MonoBehaviour
{
    public FluidParameters parameters = new FluidParameters
    {
        particleSpacing = 0.05f,     // sets particle count and mass m = ρ0·d³
        kernelRadius    = 0.10f,     // must be > d; h = 2d gives ~21~25 (measured) neighbours in a 3D lattice
        substeps        = 2,
        solverIterations = 2,
        clampTensileLambda = true,   // tensile clamp: push apart only, never suck together
    };

    FluidSimulation _fluid;

    void Awake()
    {
        // one dam-break column, grown from min towards +x/+y/+z
        _fluid = new FluidSimulation(parameters, FluidVolume.DamBreak(0.5f, 1.0f, 0.5f, 0.05f));

        // scene colliders: resolved into analytic proxies, bypassing Unity physics
        _fluid.Collisions.Add(new BoxCollisionProxy(Vector3.zero, new Vector3(2f, 0.1f, 2f), Quaternion.identity));
    }

    void Update() => _fluid.Step(Time.deltaTime);
}
```

## 2. The `FluidBehaviour` component (recommended)

The component runs in **its own local space**: particle coordinates are local, and world proxies such as
`BoxCollisionProxy` do "transform to world → push out → transform back".

| Field | Purpose |
| --- | --- |
| `parameters` | see the [parameter reference](/en/reference/fluid-parameters) |
| `volumeShape` | initial lattice shape: `Box / DamBreak / Sphere` |
| `volumeSize` / `volumeRadius` | lattice dimensions (size for Box/DamBreak, radius for Sphere) |
| `collideWithSceneColliders` + `sceneColliders` | bridge scene `Collider`s into collision proxies |
| `updateCollidersEveryStep` | resample proxies every frame (needed for moving platforms, not for a static tank) |
| `autoSimulate` | turn off to drive `Step(dt)` externally (fixed timestep / replay) |
| `renderParticles` | draw particles with `Graphics.DrawMeshInstanced` (no generated mesh, no SkinnedMesh budget) |
| `autoParticleSize` / `particleRenderScale` | billboard quad size: automatic 1.6×spacing, or manual |
| `particleColor` / `emissionStrength` | colour and emission (material comes from the active pipeline, see §6) |

Common members: `Rebuild()`, `Step(dt)`, `ResetToInitialLayout()`, `Simulation`, `IsBuilt`, `LastBuildError`,
`ParticlePositions`, `EffectiveParticleRadius`, `RenderBatchCount(n, max)` (`DrawMeshInstanced` caps at 1023 per call,
so larger clouds are split automatically).

## 3. What happens inside one substep

```
dt ──clamped to maxDeltaTime──> split into substeps, each:
  1. predict   x̂ = x + v·h + g·h²
  2. neighbours  uniform hash grid → CSR (starts/indices), cell size = h
  3. collide     analytic proxies push x̂ out (contact normals recorded)
  4. density     ρᵢ = Σⱼ m·W(xᵢ−xⱼ)          (poly6, fused with the gradient pass)
  5. lambda      λᵢ = −Cᵢ / (Σ|∇C|² + ε),  Cᵢ = ρᵢ/ρ0 − 1
  6. project     Δxᵢ = (m/ρ0)·Σⱼ(λᵢ+λⱼ)∇Wᵢⱼ   × solverIterations, each move passes the rail
  7. collide     push out again (projection must not shove water into a wall)
  8. velocity    v = (x̂ − x)/h, then remove the outward component along the contact normal
  9. optional    vorticity confinement; XSPH velocity smoothing
```

Tuning shortcuts:

- Water spreads too thin / density drifts ⇒ raise `substeps` (cheaper than raising `solverIterations`);
- Water looks sticky / films over ⇒ make sure `clampTensileLambda = true` (see §5);
- Want it softer (foam, mud) ⇒ raise `complianceAlpha`; it is a **relaxation factor**: λ is scaled by `1/(1+α)`;
- Too many particles ⇒ `particleSpacing` is cubic: `0.05 → 0.06` removes 42% of them.

## 4. Kernels and normalisation (all the numeric detail)

| Kernel | Expression | Coefficient |
| --- | --- | --- |
| Poly6 (density) | `W(r) = 315/(64πh⁹)·(h²−r²)³` | `Poly6Coefficient(h)` |
| Spiky gradient (pressure) | `∇W = −45/(πh⁶)·(h−r)²·(δ/r)` | `SpikyGradientCoefficient(h)` |
| Viscosity laplacian | `∇²W = 45/(πh⁶)·(h−r)` | `ViscosityLaplacianCoefficient(h)` |

Two traps we fell into, now pinned by tests:

::: warning h⁹ and h⁶, not h⁷
`Poly6Coefficient` used to assemble its denominator as `h²·h²·h²·h = h⁷` (correct: `h⁹`), and both
`SpikyGradientCoefficient` and `ViscosityLaplacianCoefficient` were written as `45/(πh⁷)` (correct: `h⁶`).
The first is off by a factor of `h²` in every density; the second made pressure gradients **10× too large**.
`FluidKernelTests` pins each coefficient against the analytic expression.
:::

::: tip Gradient magnitude simplification
The `/r` inside `SpikyGradient` cancels against `|δ| = r`, so `|∇W| = 45/(πh⁶)·(h−r)²`.
Use that directly in the λ denominator — do not divide by `r` twice.
:::

Direction convention: `SpikyGradient(delta, r)` returns the vector pointing **from i towards j**
(`delta = xᵢ − x`, hence the minus sign).

## 5. Four fuses, and the fires each one put out

PBF projection produces absurd displacements in **degenerate configurations** (with too few neighbours the density
constraint is unsatisfiable and the projection runs away). Four independent fuses guard against it:

1. **Displacement rail** `MaxCorrectionPerIterationFactor = 0.25`
   At most `0.25·h` of movement per particle per iteration. It is a "don't fling the water to orbit" fuse, not a
   primary fluid parameter: measured stable across `0.02 ~ 0.25` (after 4 s, `v_max` 0.83~1.02 m/s). `<= 0` disables it.
2. **Tensile clamp** `clampTensileLambda`
   `λ > 0` means "this region is too sparse, pull neighbours in" — real liquids have no such force (surface tension is
   a separate model). Leaving it on makes sparse particles clump and films over the free surface. Default on: only
   repulsion (λ ≤ 0) is allowed. Cost: with it off, a lattice blob **shrinks on its own** even without gravity
   (pinned by `TensileClamp_OffShrinksALatticeBlob`).
3. **dt cap** `maxDeltaTime` (default 1/30 s) — see the warning below; it was the real cause of "the demo water exploded".
4. **Velocity ceiling (CFL-style)** `maxSpeed` (default 0 = unlimited, the demo uses 8 m/s)
   The correction rail bounds **constraint motion**, but advective displacement = velocity × substep and had no bound.
   PBF can hand back speeds above ten metres per second on dilute or deeply penetrating configurations, enough to hop
   a whole thin slab in one step — half of the demo tank's "trapdoor leak" (the other half is in §7).
   `Validate()` rejects negative values; 0 means unlimited.
   Test: `DemoTank_SingleStepMaxExcursionFitsInsideEveryProxySkin` pins "max single-step excursion < the thinnest
   slab's distance to its own mid-plane'.

::: warning A second-scale `Time.deltaTime` throws a whole pool a kilometre away
The first frame after entering Play, a hitch, or returning from background can hand Unity a **second-scale**
`deltaTime`. The substep size feeds straight into gravity integration and projection, so a single frame can eject the
water: measured centroid at `y = −1394 m`, bounds `162×1492×115 m`. Cloth, mass-spring and soft body had
`maxDeltaTime` from the start; the fluid got it in this release (same semantics: `<= 0` means no cap).
Regression test: `FirstFrameHugeDeltaTime_DoesNotLetTheFluidEscapeTheTank`.
:::

## 6. Rendering: particles (`DrawMeshInstanced`) and a water surface (isosurface)

### 6.1 Particle mode (`FluidRenderMode.Particles`, the default)

- `FluidParticleMesh` caches one camera-facing billboard quad; `FluidBehaviour` fills a `Matrix4x4[]` per frame and
  calls `Graphics.DrawMeshInstanced` in batches of 1023 (Unity's per-call cap). Thousands of particles never build a
  hundred-thousand-triangle mesh.
- `FluidParticleMaterial.Get()` obtains the **active render pipeline's `defaultMaterial`** through reflection
  (including `BindingFlags.Instance`), so the package does not depend on URP/HDRP and still avoids magenta on
  Built-in; properties are written to both `_BaseColor`/`_EmissionColor` and `_Color`.
- The material must **not** be flagged `HideFlags.DontSave` — that breaks the reference on save, and the fluid renders
  magenta in Play mode (same trap as the cloth demo).

### 6.2 Surface mode (`FluidRenderMode.Surface` / `Both`)

A pile of dots reads as a particle effect, not as water. Surface mode turns the same particles into one continuous,
translucent mesh that follows the flow:

1. **splat into a scalar field**. Particles are deposited on a regular lattice with the same kernel `W`:
   `α(x) = Σⱼ W(|x − xⱼ|) / Σ_lattice W`. The denominator is the kernel's self-sum on the **spawn lattice at spacing
   `d`**, which is deliberate: inside a still body of water `α ≈ 1` and outside it falls to zero fast, so
   `isoLevel = 0.5` *is* the surface. Use a different kernel for the normalisation and the interior value stops
   being 1, dragging the threshold with it.
2. **extract the isosurface** with **marching tetrahedra**: every cube is split Kuhn-style into 6 tetrahedra, each
   emitting 1-2 triangles. No 256-case marching-cubes table — that table means hand-copying over four thousand
   integers, and a typo in it shows up as "the surface breaks open occasionally", the least testable failure there is.
   A Kuhn split is translation-invariant, so neighbouring cubes share the same face diagonal and the isosurface is
   **closed by construction**; ambiguous faces are absorbed by the tetrahedra themselves. The claim is tested, not
   asserted in prose: `CountBoundaryEdges` must return 0 (`BuildMesh_CompactBlob_ProducesWatertightSurface`).
3. **normals from the field gradient** (central differences), not `Mesh.RecalculateNormals`: the gradient points along
   fastest-decreasing `α`, which is the outward normal. A test requires at least 98% of vertices to agree with the
   "away from the blob centre" direction.

Four engineering bounds:

| Item | Value / behaviour | Why |
| --- | --- | --- |
| Voxel edge | `surfaceCellSize`; the demo uses `0.75 × particleSpacing` | smaller than the spacing or the surface looks voxelated; smaller still is wasted frame time |
| Cell budget | `surfaceMaxCells` (default `262144`) | when exceeded the voxel edge is **grown**, never shrunk; `grid.Min` and the covered extent stay put |
| Vertices per mesh | `MaxMeshVertices`, otherwise `InvalidOperationException` | `Mesh.triangles` defaults to ushort indices (65535); past that you get a broken surface, not an error |
| Refresh cadence | `surfaceRefreshEveryNFrames` (demo: 2) | one surface rebuild costs more than several constraint passes; throttle before optimising |

Bit-for-bit reproducibility still applies: `PlanGrid` traversal and tetrahedra enumeration orders are fixed, and
`BuildMesh_SameInput_ProducesBitIdenticalVertices` compares vertices and indices exactly (the package's determinism
floor: no randomness, no hash iteration, no parallel reductions).

::: warning `Destroy` logs an Error in EditMode, and the test framework counts any unexpected Error as a failure
Every surface rebuild disposes the previous `Mesh`. Calling `Object.Destroy` from the editor logs
`Destroy may not be called from edit mode!`, and passing assertions do not excuse that log line — two surface tests
went red exactly that way. Use `Destroy` at runtime, `DestroyImmediate` in the editor;
`FluidBehaviour.DisposeAsset` exists for nothing else.
:::

::: tip Surface mode adds no dependency and no accuracy
It only reads particle positions and never touches the solver, so **the simulation result is identical** whether the
surface is on or off; `Both` is two draw submissions, numerically identical to `Particles`
(pinned from the behaviour side by the three `RenderMode_*` tests).

## 7. Editor tools and the demo scene

- `Tools/Physics Simulation/Fluid/Create Fluid Demo Scene` (130) — writes `Assets/Scenes/FluidDemo.unity`:
  a six-sided sealed tank (floor + four walls + a lid, **drawn but not colliding**) + one dam-break column
  (1540 particles, budget 1500) + a **camera looking down into the tank** + **surface mode**
  (`FluidRenderMode.Surface`). What actually holds the water is **one inside-out box container proxy**
  (`BoxContainerProxy`, a serialized field on `FluidBehaviour`) rather than the six plates - the demo
  scene reports `碰撞代理 1` / "proxy count 1" and that is it.
- `Tools/Physics Simulation/Fluid/Build Fluid In Current Scene` (131) — adds a tank and a water column to the open scene.
- `Tools/Physics Simulation/Fluid/Dump State` (132) — per blob: particle count, average/max neighbour degree,
  density mean/min/max, bounds, centroid, kinetic energy, mass, proxy count, substeps/iterations/XSPH/vorticity/clamp.
  **The first tool to reach for when the picture looks wrong.**

Three demo settings exist because we tripped over them; each is pinned by a test:

::: warning Three leak warnings, one root cause: solid plates used as colliders
The first tank was six **solid plates** (floor + four walls + lid, one `BoxCollisionProxy` each), and it
leaked three separate ways. (1) The horizontal seam between wall bottoms and the floor was a **one-way
trapdoor**: water was pushed out along the shallowest axis and free-falled 65 m below the floor
(`v = 35.7 m/s = √(2g·65)`, falling, not exploding). (2) With the seam sealed it still leaked **vertically**:
on a 0.2 m slab the mid-plane sits 0.1 m under the top face, so once a particle is pushed past it the
shallowest axis flips to the other side of the plate and water is ejected through the floor's **underside** —
a 3D trapdoor, only suppressed by thickening to `2.5t`. (3) Even fully closed it leaked: after 300 steps, 120
particles were squeezed sideways out of the floor's `-z` edge, past both the floor's and the wall's outer
faces, then fell (`(-0.635, -0.529, -0.964)`, accelerating 3.3 → 4.1 m/s). That is a **corner conveyor**:
two overlapping "sealed" plates hand water out over their intersection line.
The common factor: `BoxCollisionProxy` means "push out along the **shallowest penetrated face**", while a tank
needs the opposite, and each plate adjudicates its own shallowest axis, so seams and mid-planes are always leaks.
**What it does now**: the plates are visual only, and the water boundary is a single `BoxContainerProxy` (an
inside-out box, out-of-range points are pinned **per axis** back to the nearest inner wall, i.e. minimum
displacement projection onto a convex set, so a point that leaves at a corner is pushed to the inner corner, 
not out along one axis). Measured: 0 particles outside after 300 steps, bounds back inside the cavity, pinned
by two `FluidTankSealTests`.
Tests: `BuildTank_WallsSealTheFloorSeamAndEachOther`, `Tank_HasSixWallsFloorSidesAndCeiling`,
`DemoTank_NoParticleEndsUpOutsideTheTankAfterTenSteps` (the "ten steps" in the name is history, **raised to 300**:
slab leaks show within 10, ballistic splash and the corner conveyor only appear late — **too short a window is
itself a testing hole**).
:::

::: warning Walls that block the camera are not drawn: water in the tank, invisible in the screenshot
The freedom this buys: since the plates no longer collide, they can be culled. With a 1.5 × 1.1 × 0.9 cavity
the dam-break flattens to 0.26 m of water sitting on a 1.1 m deep floor, and the measured camera at
`(1.33, 2.36, −2.21)` sees its line to the pool centre cross the `-z` wall's top face at `z = −0.713`
(`y = 1.1`, exactly the wall top), so the screenshot was an empty box with water in it. Raising the camera
above 5 m works but flattens the demo into a top-down view, losing the wave shape. `PlateBlocksCamera` now
culls plates whose outward normal faces the camera, `CreateTank` builds no Renderer for the lid or those walls, 
and there is a **hard geometric assertion**: the segment from the camera to each of the four water-surface
corners must not cross any plate that is still drawn (`SegmentIntersectsPlate`).
:::

::: warning Place spawn geometry from measured particles, not from shape parameters
`FluidVolume.DamBreak` centres the body along depth, but the demo placed the column at
`DemoColumnOffset.z = −0.37` assuming it measured from `-z`, so **560 of 1540 particles were born inside a wall**
(out of range → pinned back → density spiked to 1392 in one frame). The demo assertions now measure the
**actual generated particle bounds** instead of trusting what the shape arguments mean.
:::

::: warning The water column must not touch a wall at spawn time
If the initial lattice touches a wall and the floor at the same time (distance 0 to several faces，) the
shallowest axis degenerates into a tie-break and the ejection direction is arbitrary — whole batches ended up
under the floor. The demo keeps one particle spacing of clearance per side
(`DemoConfig_ColumnClearsEveryWallSoTheProxyHasNoTies`).
:::

::: warning Place spawn geometry from measured particles, not from shape parameters
If the initial lattice touches a wall and the floor at the same time (distance 0 to several faces, ) the
shallowest axis degenerates into a tie-break and the ejection direction is arbitrary — whole batches ended up
under the floor. The demo keeps one particle spacing of clearance per side
(`DemoConfig_ColumnClearsEveryWallSoTheProxyHasNoTies`).
:::

::: warning Place spawn geometry from measured particles, not from shape parameters
If the initial lattice touches a wall **and** the floor simultaneously (distance 0 to several faces), "shallowest axis"
degenerates into a tie-break and the push direction becomes arbitrary — entire batches got shoved under the floor.
The demo leaves one particle spacing on every side (`DemoConfig_ColumnClearsEveryWallSoTheProxyHasNoTies`).
:::

::: tip The camera must look down, and the water must be deep enough
At eye level the front wall hides the pool and the screenshot is just a wall (it happened). The framing values are
constants pinned by tests: `DemoPoolDepth / spacing >= 4` (otherwise the water flattens into one layer and
`size.y` is literally 0), the view axis points downward, and the water stays inside the frustum.
:::

## 8. Measured performance (managed solver, no Burst)

Measured inside the Unity Editor with EditMode benchmark cases, Mono managed execution, `substeps = 2`,
`iterations = 2`, with **`d = 0.05 m` and `h = 0.1 m` fixed — only the box size changes** (0.5 / 0.65 / 0.8 m cubes).
**The machine had background load during these runs** and the same case varied by up to 1.6x between two runs, so the
table lists both measurements; the benchmark gates assert *ratios*, not absolute milliseconds.

| Particles | ms/step (two runs) | Avg. neighbours | Note |
| --- | --- | --- | --- |
| 1 000 (0.5 m cube) | **13.3 ~ 21.2 ms** | 21.0 | already fills a 60 fps frame budget |
| 2 197 (0.65 m cube) | **41.7 ~ 69.4 ms** | — (not printed there) | this is the scale where the neighbour-table share was measured |
| 4 096 (0.8 m cube) | **64.7 ~ 105.5 ms** | 22.9 | upper end; one step per frame is out of reach on the managed path |

- Neighbour table rebuild: at 2 197 particles the whole step is 41.7 ~ 69.4 ms of which **10.8 ~ 20.4 ms (26 ~ 29%)**
  is the rebuild — the single largest cost, printed by `Benchmark_NeighbourTableBuildIsTheDominantCost`.
- The uniform-hash search scales near-linearly: `Benchmark_NeighbourCostScalesNearLinearlyWithParticleCount`
  requires `t(4N)/t(N) < 12` and measures **4.59 ~ 5.75** (perfectly linear would be 4; the excess comes from the
  neighbour degree growing from 21.0 to 22.9 with scale).
- **The demo configuration was never timed on its own**: 1 456 particles at `d = 0.07`, `h = 0.14`, average degree
  25.1 sits between the 1 000 and 2 197 rows above, so its order of magnitude is **20 ~ 70 ms per step**.
  `DemoParticleBudget = 1500` is taken from this table: beyond it you must step across frames or drop substeps —
  the managed solver will not finish a step inside 16.7 ms.
- The table times `Step()` only; `DrawMeshInstanced` cost is not included.

`v1.6.0` (Jobs + Burst optional assembly) targets an order of magnitude on these numbers.

## 9. Known limits

- **The surface is presentation, not physics**: `FluidSurface` reads particle positions and takes no part in the
  density constraint. Sheets thinner than one voxel, or spacings larger than the voxel, can have gaps "glued over" by
  the isosurface; two genuinely separated blobs never bridge (tested).
- **Isosurface resolution is bounded by the budget**: past `surfaceMaxCells` the voxel is grown and the surface starts
  looking blocky. Big tank + fine voxel means either lower density or tiling, and tiling is not implemented.
- **Single phase**: no surface tension, no multi-material, no two-way coupling with air. Vorticity confinement
  (`vorticityEpsilon`) only adds a bit of swirling look; off by default.
- **Free surface reads dilute**: after settling the mean density ratio is about **0.89 ~ 0.99** (surface particles have
  fewer neighbours). That is inherent SPH bias, not a bug.
- **Depth needs sampling**: a pool must be at least ~4 particle layers deep to look like water, otherwise it flattens
  into a membrane (see the tip in §7).
- **No MeshCollider**: same rule as the v1.3.0 collision layer — analytic proxies only
  (sphere/box/capsule/plane + inside-out box container).
- **The container is a single box**: `BoxContainerProxy` supports rotation, but it is **one box**, not
  an arbitrary basin. Non-rectangular tanks mean several container proxies, and stacking them reintroduces
  the same per-proxy adjudication problem from section 7.
- **One-way coupling with rigid bodies**: colliders affect the water, the water does not push colliders.

## 10. Next

- [Fluid parameter reference](/en/reference/fluid-parameters)
- [Scene collision](/en/collision/)
- [Cloth simulation](/en/cloth/) (same PBD family, different constraint)
- [Editor tools](/en/tools/)
- [Changelog](/en/changelog)
