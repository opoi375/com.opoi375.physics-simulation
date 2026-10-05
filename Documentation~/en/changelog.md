# Changelog

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
