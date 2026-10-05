# Overview

`com.opoi375.physics-simulation` is a **mass-spring physics toolkit** for people who want to write their own simulation but need a trustworthy foundation.

It is **not** a replacement for Unity's built-in physics: v1 has no rigid bodies, no collision detection and no `Rigidbody` interop. It does one job — **connect a pile of point masses with springs and integrate them stably and reproducibly**. Ropes, chains, swings, cloth and soft bodies all grow out of this same foundation.

## Design principles

| Principle | How it is done |
| --- | --- |
| **Pure logic fully separated from the Unity layer** | `MassSpringSystem` / `Particle` / `Spring` do not inherit `MonoBehaviour`, never read `Time`, never touch the scene. `MassSpringBehaviour` only does "config → system → step → gizmos" |
| **Determinism first** | No `Random`, no `Time`, no parallel reductions. Same parameters + same step count ⇒ **bit-identical** results, which is what makes closed-form assertions and replay possible |
| **Predictable numerics** | Damping is a divisor `(1 + c·dt)` rather than a subtraction `v -= c·v·dt`; the per-particle damping factor is clamped to `[0,1]`; dt is clamped |
| **Narrow API surface** | Six methods cover the whole system (`AddParticle` / `AddSpring` / `Pin` / `Step` / `ResetToInitial` / `ApplyForces`) |
| **Pipeline-agnostic** | The only package dependency is `com.unity.test-framework` — no URP, no `Unity.Mathematics`, no Burst |

## Layout

| Path | Contents |
| --- | --- |
| `Runtime/MassSpring/` | Pure logic (`Particle`, `Spring`, `MassSpringParameters`, `MassSpringIntegrator`, `MassSpringSystem`) + Unity layer (`MassSpringBehaviour`, `MassSpringConfig`, `MassSpringParticleLink`) |
| `Editor/MassSpring/` | Editor tools (`Tools > Physics Simulation` menus) |
| `Tests/Editor/` | 30 EditMode tests (integrator, spring force, system behaviour, config translation, demo builder) |
| `Documentation~/` | This bilingual site. The `~` suffix keeps Unity from importing it |

Assemblies: `PhysicsSimulation.Runtime` → `PhysicsSimulation.Editor` → `PhysicsSimulation.Editor.Tests`, namespaces `PhysicsSimulation`, `PhysicsSimulation.EditorTools`, `PhysicsSimulation.Editor.Tests`.

## Versioning & roadmap

| Version | Contents | Status |
| --- | --- | --- |
| 1.0.0 | Mass-spring: particles, springs, semi-implicit Euler, implicit damping, substepping, dt clamping, gizmos, editor tools, demo scene | ✅ |
| 1.1.0 | Cloth: structural / shear / bend distance constraints solved with PBD projection, wind, sphere obstacles, `ClothBehaviour`, generated mesh, 36 tests | ✅ |
| 1.2.0 | Soft bodies: any mesh → welded particles + triangle-edge structural springs + opposite-vertex bend springs + a **divergence-theorem volume constraint** (no tetrahedralisation needed), reusing the mass-spring core; `SoftBodyBehaviour` with four pin modes and a serializable perturbation, demo scene, 31 tests | ✅ |
| **1.3.0** | Collision proxies: four pure shapes (sphere / OBB box / capsule / half-space) behind one `ICollisionProxy` shared by all three solvers; `Simulation` vs `World` registration spaces; `ColliderProxies` samples the scene's Colliders; off by default and bit-identical to v1.2.0 when off; soft bodies finally land instead of falling through | ✅ current |
| 1.4.0 | Performance: `Jobs + Burst` parallel solver as an **optional assembly** (fall back to the managed path without Burst; core package dependencies stay at zero) | 🚧 planned |

### Explicitly out of scope (for now)

Self-collision for cloth and soft bodies, triangle-level intersection tests, interop with Unity `Rigidbody` / `ConfigurableJoint` (the jelly can press on a crate, the crate will not move), networking and replays, adaptive timesteps. Since v1.3.0 collision is **particle level with analytic primitives**: sphere, oriented box, capsule, half-space. `MeshCollider` and `Terrain` are deliberately unsupported and are **not** faked with a bounding box — "this collider had no effect" beats "an invisible wall appeared out of nowhere". The response is only "cut the normal velocity component, keep the tangent": no friction, no restitution. The volume constraint is a gradient restoring force rather than a hard constraint, so roughly 1% deviation is tolerated under violent deformation; bend spring counts depend on the triangulation, so tests assert invariants (disjoint from structural springs, no duplicates, non-zero on a closed mesh) rather than specific numbers.

Extension points are marked as `TODO` comments at the top of `MassSpringSystem` (`IConstraint`, `IForceGenerator`, collision and sleeping). v1 deliberately does **not** declare empty interfaces — abstractions nobody implements are worse than none.

## Next

- See something move: [Quick Start](/en/guide/quickstart)
- Formulas and stability: [Mass-Spring](/en/mass-spring/)
- Field lookup: [Parameter reference](/en/reference/mass-spring-parameters)
