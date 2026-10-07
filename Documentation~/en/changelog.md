# Changelog

## [1.5.0] - 2026-10-06

### Added
- **Fluid surface rendering (isosurface)** `Runtime/Fluid/FluidSurface.cs`: particles are splatted onto a regular lattice into a scalar field `α` (same kernel, normalised by the kernel's self-sum on the spawn lattice, so a still body reads ≈ 1 inside) and the `α = 0.5` isosurface is extracted with **marching tetrahedra** (each cube split Kuhn-style into 6 tetrahedra). No 256-case marching-cubes table: hand-copying four thousand integers fails as "the surface breaks open occasionally", the least testable bug there is. A Kuhn split is translation-invariant, so neighbouring cubes share the face diagonal and the surface is **closed by construction** — the test counts boundary edges (`CountBoundaryEdges` must be 0). Normals come from central differences of the field, not `Mesh.RecalculateNormals`
- **`FluidRenderMode` (`Particles` / `Surface` / `Both`, default `Particles`)** plus the surface fields on `FluidBehaviour` (`surfaceCellSize` / `surfaceIsoLevel` / `surfaceRefreshEveryNFrames` / `surfaceMaxCells` / `surfaceColor`) and read-only observables (`SurfaceMesh` / `SurfaceRevision` / `SurfaceTriangleCount` / `ParticleBatchCount`); `FluidSurfaceMaterial` is a pipeline-agnostic translucent water material
- **`FluidParameters.maxSpeed`** (default 0 = unlimited, demo 8 m/s): a CFL-style advective velocity ceiling, closing the hole where the correction rail bounded constraint motion but nothing bounded advection
- **A sixth slab on the demo tank: the lid** (`Tank_HasSixWallsFloorSidesAndCeiling`). Interior faces still bit-identical; thickness grows outward and upward only
- **19 `FluidSurfaceTests` + 2 `FluidTankSealTests`** plus one cloth scaling benchmark; **262 passing** (counted per file from `[Test] + [TestCase]` and reconciled against the runner — the old tool-page figures of 33 collision / 7 soft-body demo tests did not match the files and are corrected to 32 / 8)
- **PBF fluid solver** under `Runtime/Fluid/`: `FluidKernel` (poly6 / spiky gradient / viscosity laplacian, with normalisation pinned by **analytic integral** tests), `FluidNeighborSearch` (uniform hash grid + CSR table, bit-deterministic rebuild), `FluidVolume` (box / sphere / cylinder / dam-break sampling with budget-truncation reporting), `FluidSimulation` (density-constraint projection, double-density correction, tensile clamping, XSPH viscosity, vorticity confinement, substepping and dt clamping; collision reuses the v1.3.0 `CollisionSet`)
- **`FluidBehaviour`**: world↔local conversion (particles live in **world space** — there is no velocity re-derivation after projection, so local space would add a g every frame), automatic Collider bridging, and **`Graphics.DrawMeshInstanced` rendering** (no 100k-triangle mesh; one shared `Mesh`, one shared `Material`)
- **Fluid editor tools (130~132)**: `Create Fluid Demo Scene` / `Build Fluid In Current Scene` / `Dump State`; the demo scene is a **sealed tank with a dam-break column**
- **`DemoSceneSave`**: the single save path for every demo tool — aborts with a clear error on untitled scenes instead of opening a dialog — guarded by a repository-wide source scan test
- **Four documentation pages (zh + en)**: the fluid module page (PBF pipeline, kernel normalisation, the three fuses, rendering and materials, the tank-seam lesson, measured costs) and the fluid parameter reference
- **68 new EditMode tests** (9 kernel + 8 neighbour search + 6 volume shapes + 24 solver + 10 Unity layer + 9 demo tools + 2 save path), plus one `maxDeltaTime` case added for cloth; with the surface, tank-seal, container and framing tests added during the wrap-up, **270 passing** in total

### Fixed

- **`FluidSurface.PlanGrid` stored the cell count in an `int`, and 1e12 overflowed into a negative number**: "over budget" read as "within budget" and a million-cell grid was allocated in silence. `CellCount` / `NodeCount` are now `long`, and an excessive node count throws with a readable message
- **`FluidBehaviour` called `Object.Destroy` on the surface mesh in EditMode**: Unity logs `Destroy may not be called from edit mode!` as an Error, and the test framework treats any unexpected Error as a failure — two surface tests went red for nothing else. Now `DisposeAsset`: `Destroy` at runtime, `DestroyImmediate` in the editor
- **Wrong kernel normalisation**: poly6 used h⁷ (should be h⁹) and the spiky gradient / viscosity laplacian used 45/(πh⁷) (should be 45/(πh⁶)). Consequence: a resting block reported a density ratio of 7.1 instead of ~1.0 and pancaked on frame one — all three integrals now have analytic tests
- **`_stamp` was not cleared when rebuilding the neighbour table**: last round's neighbours were read as this round's, so viscosity operated on a stale neighbour set
- **Wrong scale and relaxation form in the PBF projection**: the displacement was missing the mass factor, λ's denominator was missing the sum, and velocity was not recomputed as `(x − x_prev)/dt` after projection — roughly 420,000× too much movement per iteration
- **`FluidParameters.Validate()` branch order** silently discarded out-of-range inputs
- **The fluid had no `maxDeltaTime`** (cloth always did): the second-scale `Time.deltaTime` of the first Play-mode frame threw the entire tank 1,651 m — visually, "the tank exploded". All four solvers now share the same semantics
- **A fully closed six-plate tank still leaked (corner conveyor)**: with one `BoxCollisionProxy` per plate, 120 of 1540 particles were squeezed sideways out of the floor edge within 300 steps, cleared both the floor and the wall outer faces and then free-felled (sample `(-0.635, -0.529, -0.964)`, accelerating 3.3 -> 4.1 m/s). The first mitigation was a round of six collider-only "backstop" plates (12 proxies, 0 particles past the outer envelope) - but containing is not the same as correct: measured **150 particles pressed into the wall slabs**, deepest **0.486 m** (parked 1.4 cm inside the outer face, dt-independent across 100/400 steps and dt = 1/60, 0.02, 1/30), which is what "water hugging the wall skin" looks like. Root cause is per-proxy shallowest-face projection, not the time step. **Final fix**: every plate became visual only and the water boundary moved to a single `BoxContainerProxy`; measured 0 particles outside after 300 steps with the bounds back inside the cavity, and the six backstops were deleted again (12 -> **1** proxies)
- **The water column was born inside a wall**: `FluidVolume.DamBreak` centres the body along depth, but the demo placed the column at `DemoColumnOffset.z = -0.37` assuming all three axes hug the minimum corner, so **560 of 1540 particles started inside the -z slab** (density spiked to 1392 in one frame). The offset is now 0 and the placement assertion measures the **actual generated particle bounds** instead of trusting what the shape arguments mean
- **A hand-attached container proxy vanished in Play**: proxies live in the `CollisionSet`, and `OnEnable -> Rebuild()` recreates it, so the scene looked fine in the editor but entering Play reported "collision proxies 0" and the whole pool fell to `y = -92` (kinetic energy 16904). The container is now a serialized field re-added inside `Rebuild()`, pinned by `BoxContainer_SurvivesEveryRebuild`
- **The line from the camera to the water crossed a wall top**: water in the tank, invisible in the screenshot. Camera-facing walls are now culled and the framing check is a hard geometric assertion - the camera-to-corner segment may not cross any plate that still has a Renderer
- **The demo tank had a seam that acted as a one-way trapdoor**: the box proxy pushed particles out under the wall through the wall/floor gap and, with no floor on the outside, they free-falled through the camera (`v ≈ √(2gh)` means leaking, not blowing up). Walls now reach into the floor slab and overlap in plan, pinned by a test
- **The water column touched the walls**: tangent to three walls and the floor at once, so several axes were equally shallow and the tie-break ejected a particle on frame one. Now 0.08 m of clearance per side
- **Eye-level camera plus a pool too shallow to be a pool**: the "empty box" screenshot was simply the front wall filling the frame, and the water spread into one layer. The camera now looks down and the pool is at least four particle rows deep, both constants pinned by tests
- **`EditorSceneManager.SaveOpenScenes()` opens the native Save Scene dialog when an open scene has no path**, dead-locking the main thread when reached from a menu API (the menu appeared to hang for 300 s). All call sites now go through `DemoSceneSave`

### Changed

- **The demo tank went from six solid plates to six visual plates plus one inside-out box container**: collision proxies 12 -> **1**, the lid and the two camera-facing walls are not rendered, and the water stays inside the cavity (measured over 300 steps)
- **`Assets/Scenes/FluidDemo.unity` is now surface mode with a six-sided sealed tank** (1540 particles / budget 1500 / `spacing 0.07`). The open top was meant to show splashing, but in Play the surge threw water clean over the 1.1 m wall and after 300 frames the bounds had grown to 10.3 × 9.1 × 6.7 m — a tank of water splashed across the scene while the body itself stayed healthy (mean density ratio 0.917, `v_rms` 2.6 m/s: ballistic, not divergent). The tank is colliders only, no mesh, so the lid costs nothing visually
- **`FluidTankSealTests` length raised from 10 to 300 steps**: trapdoor leaks show within 10 steps, ballistic splash only later — **too short a window is itself a testing hole**
- **Cloth benchmark gates changed shape**: `32×32` from 8 ms to a 20 ms ceiling, `64×64` from 33 ms to 66 ms, plus `Benchmark_ClothCostScalesNearLinearlyWithParticleCount` (ratio gate; linear is 4.00, threshold 12). Same reasoning already applied to soft body and fluid: absolute milliseconds measure how idle the machine is

### Notes
- **Burst moved to v1.6.0**: this release shipped fluids, not parallelism. The fluid runs on the managed solver — measured with `d = 0.05 / h = 0.1` fixed: 13.3~21.2 ms/step at 1,000 particles and 64.7~97.7 ms/step at 4,096 (the same case varied 1.6x between runs, hence ratio gates of 4.59~4.88 instead of absolute milliseconds); the demo scale of 1,456 particles (25.1 average neighbours) lands between those rows at roughly 20~60 ms/step, which will not fit 2 substeps x 2 iterations into one 60 fps frame
- **Benchmark gates changed shape**: the soft body absolute `8 ms` gate had been pierced by identical code on a loaded machine (9.4 / 9.5 ms, red) — it measured how idle the machine is, not whether the solver regressed. Fixed in this release: the two fluid benchmarks assert a *size ratio* only (measured 4.59~5.75 against a gate of 12), soft body keeps a 20 ms hard ceiling and gains `Benchmark_SoftBodyCostScalesNearLinearlyWithParticleCount` (162 vs 642 particles, ratio measured 5.99~6.33). The cloth 64×64 33 ms gate measured 32.36 ms on a loaded run — 0.6 ms of margin, a known brittle point whose conversion to a ratio is scheduled for v1.6.0
- Rendering has two modes: **particles** and an **isosurface water sheet** (marching tetrahedra, not marching cubes, and no UF kernel blur); still no surface tension, XSPH is the only viscosity knob
- The container is **one box** (optionally rotated): its inner surface is a hard boundary, so you cannot model a leak with it, and stacking several containers reintroduces the per-proxy adjudication problem described above
- Still no self-collision (cloth, soft body and fluid alike); scale stays bounded by the managed O(n·k) cost, which is why the demo budget is 1,500 particles
- `MaxCorrectionPerIterationFactor` is a **mutable static rail**, not a tuning parameter: move it only for diagnostics

## [1.4.0] - 2026-10-05

### Added
- **Model audit** `Runtime/SoftBody/SoftBodyMeshAudit.cs`: `Audit(data, parameters, steps, addGround)` runs the **real** `SoftBodySimulation` at a fixed 1/60 s step onto a world-space `PlaneCollisionProxy` ground and returns a `SoftBodyAuditResult` (weld ratio, particle/spring/triangle counts, closure, signed rest volume, volume retention, max stretch, lowest particle world y, ms per step, verdict). It **never throws on dirty input** — build failures become `BuildFailed` with the reason kept in `BuildError`
- **Seven verdicts** `SoftBodyAuditVerdict`: `Healthy` / `OpenMesh` / `InvertedWinding` / `DegenerateVolume` / `DegenerateWeld` / `Unstable` / `BuildFailed`. The middle two were added **after scanning real assets**: an inside-out mesh used to report `Healthy` (dividing two negative volumes yields a tidy retention of 1), and a zero-thickness shell was indistinguishable from a genuine collapse (both printed `0.000`)
- **Editor scanning** `Audit Selected Meshes (123)` and `Audit Mesh Assets In Folder (124)`: one pass over the whole project, emitting a "default parameters" table plus a "size-scaled recommended parameters" table and an improvement/regression comparison into `Logs/SoftBodyMeshAudit.md`; budget guards (120 meshes, 4000 vertices) and `StringComparer.Ordinal` sorting keep two scans byte-identical
- **`RecommendedParameters(diagonal)` / `Diagonal(data)` / `BuildComparison()`** turn "how should this class of model be tuned" into a runnable comparison instead of a doc hand-wave
- **Two documentation pages**: *From Mesh to Particles* states plainly that welding is a **uniform spatial hash, not an octree** (cell size = tolerance, 27-cell probe, exact squared-distance verdict, why not an octree, tolerance-is-a-radius, build-time rejections); *Real-Model Audit* publishes the per-row results for **104 meshes** of this project
- **40 new EditMode tests** (22 audit + 15 scan tool + 3 welding boundary cases), **170 passing**; whole project 578 total, 576 passed, 2 skipped

### Fixed
- **The report's summary folded new verdicts into `BuildFailed`**: the old `switch` ended in `default: failed++`, so 9 of 104 rows (`InvertedWinding` 4 + `DegenerateVolume` 3 + genuine `BuildFailed` 2) were reported as "9 build failures". All seven buckets now count separately and a reconciliation check asserts they sum to the total — a future enum member lands in "unknown" instead of corrupting a bucket
- **The note column printed `(unnamed)` on every healthy row**: `Sanitize`'s empty-string fallback was meant for model names. Healthy rows now show an explicit `-`, and `Note()` returns an empty string when there is nothing to say
- **`BuildFailed` rows carried no reason**: a report that is only a table must not say "failed" and nothing else. The note column now carries the reason verbatim, single-lined so newlines and pipes inside exception messages cannot tear the table

### Notes
- **No performance work in this release** (the parallel solver moved to v1.5.0). `ms/step` in the audit only answers "is this magnitude usable"
- `RecommendedParameters` is **a diagnostic suggestion, not the new default**: it saved 6 meshes (trees, reeds, mushrooms, the single-cell box) and broke 1 (`Tunnel_Mesh` 1.323 → 0.208, squashed by over-stiffening). Defaults stay conservative
- The audit runs 90 steps on one body against a world-space half-space. `Healthy` means numerically sound, not aesthetically good

## [1.3.0] - 2026-10-05

### Added
- **Collision proxies**: `ICollisionProxy.PushOut(point, skin)` — one method is the whole contract. Four analytic shapes: sphere, oriented box (any rotation), capsule (a degenerate axis falls back to a sphere) and a **half-space** plane. See [Collision proxies](/en/collision/)
- **Three contracts**, red-first if the semantics change: a point outside is returned bit-identical; a point inside exits along the shallowest penetration; undefined direction falls back to `+Y` and never produces NaN; negative `skin` is clamped to 0
- **Two registration spaces**: `Simulation` and `World` (particles round-trip through world space), which may coexist on one object — so cloth's v1.1.0 sphere obstacles keep working untouched
- **`ColliderProxies`** samples the scene's `Collider`s; `MeshCollider`, `Terrain` and disabled colliders return `null` and are skipped — **never faked with a bounding box**
- **All three solvers wired up**: `MassSpringSystem`, `ClothSimulation`, `SoftBodySimulation` each expose `Collisions`, `HasColliders`, `SetSimulationToWorld`; `collisionThickness` (0.01 m) is now a parameter in all three
- **Behaviour switches**: `collideWithSceneColliders` (off by default), `sceneColliders`, `updateCollidersEveryStep` (off by default)
- **Soft body demo reworked**: the ground is now a Cube + `BoxCollider`, and the jelly has **zero pinned particles** — it free-falls onto the floor (measured: the lowest particle rests exactly 0.01 m above the surface, volume retention 0.981)
- **33 new tests** (21 geometry contract + 12 integration), including a bit-for-bit comparison against v1.2.0's cloth sphere arithmetic; **130 passing** in total

### Changed
- Cloth's sphere obstacles moved into `CollisionSet` (arithmetic bit-identical; `ObstacleCount` now counts proxies); the `lossyScale / √3` ellipsoid approximation is gone
- The demo jelly went from "pinned bottom + sideways velocity" to "unpinned + free fall", so `PinMode` can now express the most convincing case of all

### Performance
- No performance work in this release (parallelism still v1.4.0). Collision costs `O(particles × proxies)` per substep and is skipped entirely with zero proxies — off by default means zero overhead

### Fixed
- Cutting only positions in the Euler integrators let normal velocity accumulate without bound ⇒ the inward component is now removed while the tangent survives
- The plane implemented as "subtract penetration" got punched through at speed ⇒ it is now a half-space
- `CreatePrimitive(Plane)` ships a MeshCollider, producing "the floor is right there, why did it fall through" ⇒ skipped explicitly, and Dump State reports how many proxies arrived

---

## [1.2.0] - 2026-10-05

### Added
- **Soft body simulation**: `SoftBodyMeshData` / `SoftBodyParameters` / `SoftBodySimulation` / `SoftBodyEdge` — turn **any mesh** into a deforming, volume-preserving jelly without hand-wiring topology
- **Topology grows out of the mesh**: spatial-hash welding of coincident vertices (cell size = `weldTolerance`, 27-cell neighbourhood, squared-distance test) → unique triangle edges become structural springs → opposite vertices across shared edges become bend springs → every edge used by exactly two triangles means `IsClosed`
- **Volume constraint**: signed volume via the divergence theorem, `V = Σ (1/6)·x0·(x1×x2)`, driven by a gradient restoring force `F_i = -(k_v·(V-V₀) + c_v·dV/dt)·∇_iV`. It is a force, not a position projection, so it shares the explicit integrator and substepping; open meshes have no meaningful volume and are skipped automatically
- **Reuses the v1.0.0 core**: no second solver — the soft body drives `MassSpringSystem.ApplyForces()` plus semi-implicit Euler and injects the volume force as an extra external force
- **Stability safeguards**: 8 Gauss-Seidel projection passes once `maxStretchRatio` is exceeded, a `maxSpeed` cap (40 m/s by default — the last line of defence when stiff springs blow up), plus `maxDeltaTime` clamping and `substeps`
- **Unity layer**: `SoftBodyBehaviour` (local-space simulation, instance mesh with verbatim topology, four `SoftBodyPinMode` options, serializable `initialVelocity`, `generateMesh` / `recalculateNormals` / `drawGizmoWireframe`, failures reported via `LastBuildError` instead of throwing)
- **Editor tools**: `Tools > Physics Simulation > Soft Body > Create Soft Body Demo Scene / Build In Current Scene / Dump State` (priority 120-122, silent save). Dump State walks every soft body in the scene and prints `enabled` / `autoSimulate` / `IsBuilt` / volume retention / max stretch ratio / max speed
- **Demo scene** `Assets/Scenes/SoftBodyDemo.unity`: a blue jelly pinned at the bottom given a shove, plus an orange bag pinned at the top swinging. Verified in Play mode: visible deformation (5.2% of pixels changing between frames), volume retention 0.998-1.000, no non-finite state
- **Shared demo material helper** `DemoMaterialHelper`: fetches the active pipeline's `defaultMaterial` by reflection and clones it (one implementation for cloth and soft body, so the `HideFlags.DontSave` trap cannot be re-trod)
- **31 soft body EditMode tests** (13 core solver + 11 Unity layer + 7 editor tools); the full suite is 97 tests
- **Benchmarks**: 642 particles (1920 structural + 1920 bend springs, 1280 triangles, 4 substeps) best **2.745 ms/step**, mean 2.814 ms/step; cloth re-measured on the same machine - 32x32 best 3.299 ms/step, 64x64 best 19.447 ms/step
- **Bilingual documentation**: `/soft-body/` module guide and `/reference/soft-body-parameters`

### Fixed
- Procedural box mesh had 4 faces wound outward and 2 inward ⇒ divergence-theorem volume came out at **1/3** of the analytic value (caught by the new `BuildBoxMesh_WithSingleSubdivision_IsAWatertightBox` test)
- Reflecting the pipeline's `defaultMaterial` without `BindingFlags.Instance` ⇒ the template was never found and demo materials silently fell back to guessing shader names
- Setting `triangles` before `vertices` on the instance mesh ⇒ Unity rejected the indices (`Failed setting triangles... VertexCount: 0`), leaving a faceless mesh with zero normals
- Demo perturbations were written only to runtime particle positions ⇒ `Awake → Rebuild` in Play mode rebuilt from the source mesh and the perturbation vanished, freezing the frame. Perturbations are now a serialized `initialVelocity`
- Compile-level issues such as calling `SoftBodySimulation.BendSpringCount` as a method (CS1955) and ambiguous `Object` under `using System;` (CS0104)

### Notes
- Dependencies are still only `com.unity.test-framework`; the core does not reference URP, Unity.Mathematics or Burst
- Known limits: soft bodies do **not** interact with colliders (they pass straight through the ground, which is why the demo pins layers instead of dropping them); no self-collision; the volume constraint is a gradient force rather than a hard constraint (about 1% deviation under violent deformation); bend spring counts depend on the triangulation, so tests assert invariants rather than specific numbers
- Planned: 1.3.0 Jobs + Burst parallel solver as an optional assembly, with the managed path kept as fallback and this benchmark as the baseline

## [1.1.0] - 2026-10-05

### Added
- **Cloth simulation**: `ClothParameters` / `ClothSimulation` / `DistanceConstraint` / `ClothConstraintType` turn a `columns × rows` grid into fabric that sags, flaps and drapes over obstacles
- **Position-based solver (PBD / XPBD-style distance constraints)**: each substep predicts, projects (structural → shear → bend), clamps stretch, resolves collisions and then commits velocity from the position delta; stiffness is mapped through `alpha = 1 - (1-k)^(1/iterations)` so even `stiffness = 1` stays stable
- **Three constraint families**: structural (grid neighbours), shear (both cell diagonals), bend (skip-one neighbours), each independently toggleable and tunable
- **Safety nets**: `maxStretchRatio` enforced by up to 32 clamping sweeps, `maxDeltaTime` clamping, `collisionThickness`
- **Wind and obstacles**: `AddWindImpulse(acc, dt)`, `AddSphereObstacle` / `ClearObstacles` / `ObstacleCount` (local-space spheres)
- **Determinism**: fixed traversal order, no randomness, no `Time`, no parallelism ⇒ identical parameters and step counts are bit-identical (locked by a test)
- **Unity layer**: `ClothBehaviour` (local-space simulation, `ClothPinEdges`, obstacles harvested from `Transform`/`SphereCollider`, `Rebuild` / `Step` / `ResetToInitialLayout` / `CaptureCurrentAsInitial` / `CollectStructuralEdges`, gizmo wireframe) plus `ClothMeshBuilder` (vertices = particles, two triangles per cell, UVs across `[0,1]²`, automatic 32-bit indices above 65k vertices)
- **Editor tools**: `Tools > Physics Simulation > Cloth > Create Cloth Demo Scene / Build In Current Scene / Dump State` (priority 110–112, silent saving)
- **Demo scene** `Assets/Scenes/ClothDemo.unity`: 20×14 grid, pinned top edge, wind blowing towards the camera, obstacle ball the cloth wraps around — verified in Play mode
- **36 cloth EditMode tests** (16 solver + 14 Unity layer + 6 editor tools); full suite 66/66 passing
- **Benchmark tests doubling as regression gates**: managed solver at 32×32 (1,024 particles / 5,826 constraints) best **4.65 ms/step**, mean 4.95; at 64×64 (4,096 particles / 23,938 constraints) best **20.2 ms/step**, mean 20.5
- **Bilingual documentation**: `/en/cloth/` module guide and `/en/reference/cloth-parameters`

### Notes
- The package still depends only on `com.unity.test-framework`; the core needs no URP, Unity.Mathematics or Burst
- Known limits: no self-collision; wind is an acceleration approximation rather than an area-pressure model; stretch clamping is a Gauss-Seidel approximation (~2% residual overshoot tolerated under extreme parameters); sphere obstacles only
- Plan: 1.2.0 soft bodies (arbitrary mesh → particles + constraints) · 1.3.0 Jobs + Burst solver (target: 64×64 inside one frame)

## [1.0.0] - 2026-10-05

### Added
- **Mass-spring system**: `Particle` (position / velocity / mass / `inverseMass` / force accumulator / per-particle damping / pinned flag), `Spring` (endpoint indices, rest length, stiffness k, axial damping c), `MassSpringSystem` (`AddParticle` / `AddSpring` / `Pin` / `Unpin` / `ApplyForces` / `Step` / `ResetToInitial` / `MaxSpeed` / `HasNonFiniteState`)
- **Semi-implicit (symplectic) Euler**: damping written as the divisor `(1 + c_global·dt)` so any magnitude only decays and never flips the sign; the per-particle factor `1 - c·dt` is clamped to `[0,1]`
- **Sub-stepping and dt clamping**: `Step(dt)` clamps to `maxDeltaTime` (default 1/15 s) then splits into `substeps` equal integrations
- **Determinism**: no `Random`, no `Time`, no parallelism — identical parameters and step counts give bit-identical results
- **Validation**: out-of-range indices, non-positive mass, `dt <= 0`, `a == b` all throw `ArgumentOutOfRangeException` before any state is touched
- **Unity layer**: `MassSpringBehaviour` (Inspector configuration, `FixedUpdate` driver, strain-coloured gizmos, `Capture Current As Rest` / `Reset To Initial Layout` context menus), `MassSpringParticleLink`, `MassSpringBuilder` (pure configuration → system translation layer)
- **Editor tools**: `Tools > Physics Simulation > Create Demo Scene / Build In Current Scene / Dump State / Build Chain Only`, silent scene saving (never the modal `SaveCurrentModifiedScenesIfUserWantsTo`)
- **Demo scene**: pinned anchor + 5 links with a decreasing stiffness gradient, released from a 38° tilt, camera, directional light and ground reference lines
- **30 EditMode tests**: integrator closed forms, spring symmetry and conservation, system determinism and stability, configuration layer, demo builder structure
- **Bilingual documentation site** (VitePress under `Documentation~`) with a GitHub Pages workflow

### Notes
- The package's only dependency is `com.unity.test-framework` — **no URP**, no render pipeline requirement
- Explicitly out of scope for v1: rigid bodies, collisions, rigid distance constraints, cloth, soft bodies, XPBD, Jobs/Burst, `Rigidbody` interop. Extension points are `TODO` comments at the top of `MassSpringSystem`
- Next releases: 1.1.0 cloth, 1.2.0 soft bodies (a component that turns an arbitrary mesh into a soft body), 1.3.0 Jobs + Burst
