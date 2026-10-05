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

## Running the tests

**Window → General → Test Runner → EditMode**. The package is embedded under `Packages/`, so its tests show up automatically — nothing needs to be added to `testables` in `Packages/manifest.json`.

| Test file | Count | Covers |
| --- | --- | --- |
| `MassSpringIntegratorTests.cs` | 5 | inertia, free-fall discrete closed form, monotone global damping, particle damping clamp, first-order convergence |
| `SpringForceTests.cs` | 4 | zero force at rest length, equal-and-opposite axial force, centre-of-mass conservation, acceleration split by 1/m with zero total momentum |
| `MassSpringSystemTests.cs` | 6 | pinned particles, determinism, argument validation, stiffness + sub-stepping, reset, dt clamping |
| `MassSpringUnityLayerTests.cs` | 10 | configuration → system translation, `MassSpringBehaviour` rebuild and error reporting, `Capture Current As Rest` |
| `MassSpringDemoToolsTests.cs` | 5 | demo chain structure, decreasing stiffness, auto rest lengths, sub-step safety margin, visual bindings |
