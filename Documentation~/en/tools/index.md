# Editor Tools

All menus live under **Tools → Physics Simulation** (priorities 100–103, same house style as the cartoon-rendering package).

| Menu | Priority | What it does | Writes to disk? |
| --- | --- | --- | --- |
| Create Demo Scene | 100 | new scene → demo chain + camera + directional light → saves `Assets/Scenes/PhysicsDemo.unity` | ✅ yes (silently) |
| Build In Current Scene | 101 | rebuilds the demo chain in the **current** scene (removes the existing one first) | ❌ marks the scene dirty only |
| Dump State | 102 | prints the system state to the Console (diagnostics) | ❌ |
| Build Chain Only | 103 | builds just the chain, leaves camera/lights alone and does not delete anything | ❌ |

## Create Demo Scene

Builds "one pinned anchor + five spring links" with a decreasing stiffness gradient (`1600 → 1100 → 750 → 500 → 320`) and releases it from a 38° tilt, so pressing Play immediately shows the stiff-on-top / soft-below layering.

See [Parameter reference §Default parameters of the demo chain](/en/reference/mass-spring-parameters#default-parameters-of-the-demo-chain).

::: warning Why every save goes through `DemoSceneSave`
Two real traps, both paid for:

1. `SaveCurrentModifiedScenesIfUserWantsTo()` pops a modal dialog. Called from a script or from automation (`ExecuteMenuItem`, CI) **nobody clicks it** and the editor main thread dead-locks forever.
2. Switching to `EditorSceneManager.SaveOpenScenes()` is **not safe either** — v1.5.0 found out the hard way: when an open scene has no path (never saved), `SaveOpenScenes()` opens the **native "Save Scene" dialog** for it, which dead-locks the main thread just the same when reached from a menu API. Symptom: "the menu item timed out after 300 s". nastier than trap 1 because that API looks silent.

So all four demo tools go through `Editor/DemoSceneSave.cs`: `AllOpenScenesHavePaths()` aborts (with a clear message, never a dialog) if any scene is unsaved, only scenes that have a path get saved, and the target scene is written with `SaveScene(active, "Assets/Scenes/XXXDemo.unity")`. `DemoSceneSaveTests` covers the `CanSaveSilently` edge (a path of only whitespace counts as unsaved — a test forced that) plus a **repository-wide source scan** that turns red the moment `EditorSceneManager.SaveOpenScenes(` or `SaveCurrentModifiedScenesIfUserWantsTo(` reappears anywhere under `Editor/**/*.cs`. The rule is worth more than any paragraph of prose.
:::

::: warning Why not `SaveCurrentModifiedScenesIfUserWantsTo()`
It opens a modal dialog. When invoked from script or automation (UnitySkills' `ExecuteMenuItem`, CI) **nobody clicks it** and the editor's main thread blocks forever. This tool therefore uses a silent path:

1. `EditorSceneManager.SaveOpenScenes()` first — if that fails (an untitled scene has no file path) it **aborts with an error** instead of prompting;
2. then `EditorSceneManager.SaveScene(active, "Assets/Scenes/PhysicsDemo.unity")`.

This is a lesson imported from the cartoon-rendering package (its CHANGELOG 1.4.0).
:::

## Build In Current Scene

Finds the existing `MassSpringBehaviour`, removes it with `Undo.DestroyObjectImmediate`, builds a fresh chain and calls `MarkSceneDirty`. **No scene file is written** — save it yourself if you want to keep it. Every creation goes through `Undo`, so Ctrl+Z cleans up completely.

## Dump State

Prints everything needed to answer "is this thing actually simulating?":

```text
[PhysicsSimulation] 系统状态
  粒子数        = 6
  弹簧数        = 5
  固定点数      = 1
  substeps      = 8（生效 8）
  maxDeltaTime  = 0.0667 s
  gravity       = (0.000, -9.810, 0.000)
  globalDamping = 0.600 1/s
  最大速度      = 1.2735 m/s
  出现 NaN/Inf  = 否
  质点位置：
    [0] (0.0000, 3.0000, 0.0000)  v=(0.0000, 0.0000, 0.0000)  m=1.000  (fixed)
    [1] (-0.0544, 2.5188, 0.0000)  v=(-0.2931, 0.0428, 0.0000)  m=0.800
    ...
    spring[0] 0→1 rest=0.4500 k=1600.0 c=1.50 strain=0.0612
```

Click it a few times while playing and watch **max speed** and **strain** — far more reliable than eyeballing the viewport. Three typical signatures:

| Signature | Meaning | Action |
| --- | --- | --- |
| `出现 NaN/Inf = 是` | already diverged | more `substeps`, less `stiffness`, or check for a near-zero mass |
| max speed grows monotonically | energy is being injected (wrong integration order / negative damping) | see [Mass-Spring §1](/en/mass-spring/) |
| strain stays large and never settles | too soft, or `restLength` does not match the real initial distance | use `Capture Current As Rest`, or pass `restLength = 0` |


## Cloth tools (v1.1.0)

Menus live under **Tools → Physics Simulation → Cloth**, priority 110–112.

| Menu | Priority | What it does | Writes to disk |
| --- | --- | --- | --- |
| Create Cloth Demo Scene | 110 | New scene → 20×14 cloth, pinned top edge, wind, obstacle ball, camera and light → saved as `Assets/Scenes/ClothDemo.unity` | ✅ silently |
| Build In Current Scene | 111 | Adds one demo cloth to the scene you already have open | ❌ marks dirty only |
| Dump State | 112 | Logs particle count, the three constraint family counts, pinned count, max stretch ratio and any non-finite state | ❌ |

Three deliberate choices in the demo scene:

- **The camera sits on the `+Z` side** — the grid lies in the local XY plane with its normal at `+Z`, so a `-Z` camera only sees an edge.
- **Wind blows purely towards `+Z`** (no `y` component) — an upward component lifts the whole flag above the ball and you lose the drape.
- **The material is cloned from the active pipeline's `defaultMaterial`** (fetched by reflection, so the package still does not depend on URP/HDRP), and it must **not** be marked `HideFlags.DontSave` — that reference is lost after the scene reloads for Play mode and the whole sheet renders magenta.

## Soft body tools (v1.2.0)

Menus live under **Tools → Physics Simulation → Soft Body**, priority 120–122.

| Menu | Priority | What it does | Writes to disk |
| --- | --- | --- | --- |
| Create Soft Body Demo Scene | 120 | New scene → blue jelly (**zero pinned particles**, free-falls onto the floor) + orange bag (top pinned, 2.2 m/s swing) + **ground as a Cube with a BoxCollider** + camera and light → saved as `Assets/Scenes/SoftBodyDemo.unity` | ✅ silently |
| Build In Current Scene | 121 | Adds both soft bodies to the scene you already have open | ❌ marks dirty only |
| Dump State | 122 | Logs, for **every** `SoftBodyBehaviour`: `enabled` / `autoSimulate` / `activeInHierarchy` / `IsBuilt`, particle and constraint counts, volume retention, max stretch ratio, max speed, **collision proxy count**, **lowest/highest particle world y** and how far the lowest sits above the box ground surface | ❌ |
| Audit Selected Meshes | 123 | audits the **selected** model/mesh assets with the real solver, 90 steps (ground = a world-space `PlaneCollisionProxy`), one row per mesh with weld ratio / closure / volume retention / ms per step / verdict | ❌ |
| Audit Mesh Assets In Folder | 124 | scans a whole folder (default `Assets`) and writes **two tables** (default vs size-scaled parameters) plus an improvement/regression comparison to `Logs/SoftBodyMeshAudit.md` | ❌ |

Deliberate choices, each one earned by a bug:

- **Perturbations go through `initialVelocity`, not by pushing particles.** Shearing the top with
  `SetPositions` in the editor looks right until you press Play — `Awake → Rebuild` reconstructs the
  simulation from the source mesh and the perturbation vanishes, leaving a frozen frame. The kick has to be serialized.
- **Stiffness is 130, not the 1200 default.** The default is genuinely stiff (0.8 kg sags 6 mm); a demo has to look soft.
- The material uses the same pipeline `defaultMaterial` logic as the cloth demo (previous section), and likewise must **not** be marked `HideFlags.DontSave`.

Dump State is the tool to reach for when "nothing is moving": it tells you whether the solver is running at all (is max speed zero?).

## Fluid tools (v1.5.0)

Menu items live under **Tools → Physics Simulation → Fluid**, priorities 130~132.

| Menu item | Priority | What it does | Writes to disk |
| --- | --- | --- | --- |
| Create Fluid Demo Scene | 130 | New scene → **six-sided sealed tank** (floor + four walls + lid, each with a `BoxCollider`) + a water column on one side (dam break) + camera and light → saved as `Assets/Scenes/FluidDemo.unity` | ✅ via `DemoSceneSave`; aborts if an open scene has no path |
| Build Fluid In Current Scene | 131 | Adds just the tank and the water column to the current scene | ❌ marks dirty only |
| Dump State | 132 | Per `FluidBehaviour`: particle count, smoothing length, average neighbours, particle mass, density ratio, max speed, bounds, whether the budget truncated the volume, number of collision proxies | ❌ |

The tank geometry is not arbitrary — `FluidDemoToolsTests` pins three things, each earned by a real failure:

- **Wall bottoms must reach into the floor slab and opposite walls must overlap in plan** (`BuildTank_WallsSealTheFloorSeamAndEachOther`). A single seam between wall and floor turns the box proxy's "shallowest penetration axis" into a one-way trapdoor: water is pushed out under the wall, there is no floor on the outside, and it free-falls through the camera. It looks exactly like an exploding tank; it was a leak. 850 particles reached 1,651 m before this was fixed.
- **The column must clear every wall** (`DemoConfig_ColumnClearsEveryWallSoTheProxyHasNoTies`, 0.08 m per side). A particle exactly between two faces has two equally shallow axes; the tie-break throws it to whichever side sorts first and it is gone on frame one.
- **The pool needs depth and the camera has to look down** (`DemoConfig_PoolIsDeepEnoughAndCameraLooksIntoTheTank`). An eye-level camera sees nothing but the front wall — that is why the "empty box" screenshot happened — and a pool shallower than four particle rows spreads into a sheet.

All of it lives in constants in `FluidDemoTools`, not in the scene, so every menu run rebuilds the same geometry.

## Running the tests

**Window → General → Test Runner → EditMode**. The package is embedded under `Packages/`, so its tests show up automatically — nothing needs to be added to `testables` in `Packages/manifest.json`.

| Test file | Count | Covers |
| --- | --- | --- |
| `MassSpringIntegratorTests.cs` | 5 | inertia, free-fall discrete closed form, monotone global damping, particle damping clamp, first-order convergence |
| `SpringForceTests.cs` | 4 | zero force at rest length, equal-and-opposite axial force, centre-of-mass conservation, acceleration split by 1/m with zero total momentum |
| `MassSpringSystemTests.cs` | 6 | pinned particles, determinism, argument validation, stiffness + sub-stepping, reset, dt clamping |
| `MassSpringUnityLayerTests.cs` | 10 | configuration → system translation, `MassSpringBehaviour` rebuild and error reporting, `Capture Current As Rest` |
| `MassSpringDemoToolsTests.cs` | 5 | demo chain structure, decreasing stiffness, auto rest lengths, sub-step safety margin, visual bindings |
| `FluidKernelTests.cs` | 9 | analytic integrals and boundary behaviour of poly6 / spiky gradient / viscosity laplacian (∫poly6=W(0), ∫spiky=W, ∇spiky=0 at the rim, negative radius, normalisation) |
| `FluidNeighborSearchTests.cs` | 8 | hash grid + CSR: self-match excluded, radius semantics, bit-identical rebuilds, **count table must be cleared on rebuild** (this one caught a hidden viscosity bug), no neighbours lost across cells |
| `FluidVolumeTests.cs` | 6 | box / sphere / cylinder sampling, count reconciliation, budget-truncation reporting, no duplicated corners |
| `FluidSimulationTests.cs` | 24 | rest density ≈ ρ0, compression, dam break pushes forward, XSPH adds no energy, vorticity creates nothing from nothing, tensile clamp, **the rail is a fuse, not the driver**, `maxDeltaTime`, **a second-scale first frame does not eject the water**, determinism, validation |
| `FluidSurfaceTests.cs` | 19 | scalar-field splat (peak, monotone falloff, empty input, bit-identical), isosurface geometry (**watertight: boundary edges must be 0**, vertices inside sampled bounds, normals outward, two blobs must not bridge, budget grows the voxel, low iso must envelope high iso), the three `FluidBehaviour` render modes and throttling, demo surface constants and the six-sided tank |
| `FluidTankSealTests.cs` | 2 | within 300 steps no particle leaves the six-sided tank (behaviour); the max single-step excursion (advection + correction rail) must fit inside the thinnest slab's distance to its mid-plane (structure) |
| `FluidUnityLayer.cs` → `FluidUnityLayerTests.cs` | 10 | world↔local, proxy resync, budget truncation, `DrawMeshInstanced` builds no mesh, `DumpState` never throws |
| `FluidDemoToolsTests.cs` | 9 | budget vs actual count, sealed tank geometry, wall clearance, pool depth and camera framing, over-budget must warn |
| `DemoSceneSaveTests.cs` | 2 | `CanSaveSilently` edges + **repo-wide scan forbidding dialog-popping save APIs** |
| `ClothSimulationTests.cs` | 18 | topology and indexing, three constraint families, stretch clamp, sphere obstacles never penetrated, wind and damping, determinism, validation |
| `ClothUnityLayerTests.cs` | 14 | `ClothBehaviour` local-space contract, instance mesh write-back, edge pinning, failures that do not throw |
| `ClothDemoToolsTests.cs` | 6 | demo cloth structure, obstacle world radius, long-run stability, material source, empty-scene dump |
| `SoftBodySimulationTests.cs` | 17 | vertex welding (plus three boundary cases on cell coordinates, tolerance and float precision), structural/bend topology, closedness and signed volume, volume retention, `SetVelocity`, validation and state immutability |
| `SoftBodyUnityLayerTests.cs` | 11 | `SoftBodyBehaviour` local space, verbatim topology copy, four pin modes, `initialVelocity`, silent build failures |
| `SoftBodyDemoToolsTests.cs` | 8 | demo jelly structure and kick, 180 steps without exploding, material source, procedural box winding and volume |

| `SoftBodyMeshAuditTests.cs` | 22 | audit contract: verdict tiering (including the `InvertedWinding` / `DegenerateVolume` mis-report regressions), the note column, table column count surviving arbitrary error text |
| `SoftBodyModelAuditToolsTests.cs` | 15 | mesh extraction, budget filtering, Markdown report with per-bucket total reconciliation, recommended-parameter scaling, the two-run comparison |

270 EditMode tests in total: 30 mass-spring, 38 cloth, 36 soft body, **38 collision**, 22 model audit + 15 scanning tools, **89 fluid** (9 kernels + 8 neighbour search + 6 volume shapes + 24 solver + 19 surface + 2 tank seal + **12 Unity layer** + 9 demo tools) and 2 save-path tests.
