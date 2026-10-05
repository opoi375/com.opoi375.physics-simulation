# Changelog

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
