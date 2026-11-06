# Soft Body Parameter Reference

Namespace `PhysicsSimulation` (flat — there is no `PhysicsSimulation.SoftBody`).
`SoftBodyParameters.Validate()` runs automatically when a `SoftBodySimulation` is constructed; invalid
values **throw immediately** rather than silently handing you a simulation that has already overflowed.

Since v1.6.0 every field carries exactly one marker — `[RuntimeTunable]` (edit during Play, effective next step) or
`[RequiresRebuild("why")]` (baked in at build time) — and a reflection test fails if a new field carries neither. The
class is also `[Serializable]` since v1.6.0, so all sixteen knobs finally show up in the Inspector.

## Input mesh: `SoftBodyMeshData`

| Member | Notes |
| --- | --- |
| `SoftBodyMeshData(Vector3[] vertices, int[] triangles)` | Raw vertices + triangle indices. Validated in the constructor: non-null arrays, triangle count divisible by three, indices in range |
| `static FromMesh(Mesh mesh)` | Pulls `vertices` / `triangles` off a Unity mesh. Empty meshes throw (message in Chinese) |
| `Vertices` / `Triangles` | Read-only |
| `VertexCount` / `TriangleCount` | Sizes |

## `SoftBodyParameters`

| Field | Default | Constraint | Notes |
| --- | --- | --- | --- |
| `mass` | 1 | finite, `> 0` | Per-particle mass (kg). Spring and volume forces convert to acceleration via `1/mass`. **Rebuild-only**: `AddParticle` writes it into the particle at build time |
| `gravity` | `(0, -9.81, 0)` | finite | Constant gravity, local space. Live — since v1.6.0 it is synced at the top of `Step()` (it used to be copied once in `Build()` and ignored forever) |
| `damping` | 0.5 | `>= 0` | Global velocity decay, `v /= 1 + damping·h` per substep. Live, same fix as `gravity`. Lower it to keep it wobbling |
| `maxSpeed` | 40 | `>= 0` (0 = unlimited) | Velocity cap. **Last line of defence** — correct parameters never reach it |
| `springStiffness` | 1200 | finite, `>= 0` | Structural spring constant in N/m (a real constant, not the PBD ratio cloth uses). Turn it down for softness; the demo jelly uses 130. **Rebuild-only**: `Spring.stiffness` is `readonly` and baked in by `Build()` |
| `springDamping` | 6 | `>= 0` | Axial damping on structural springs, kills ringing. **Rebuild-only** (also `readonly`) |
| `bendStiffness` | 150 | `>= 0` | Bend spring stiffness (opposite-vertex distance). **Rebuild-only**: at `0` the bend springs are not generated at all, so the topology is decided at build time |
| `bendDamping` | 2 | `>= 0` | Bend spring damping. **Rebuild-only** |
| `volumeStiffness` | 4000 | `>= 0` | Volume constraint gain: `F = -k_v·(V-V₀)·∇V`. 0 disables it (body goes flat). Live |
| `volumeDamping` | 20 | `>= 0` | Damps `dV/dt` so it doesn't pump like a balloon. Live |
| `substeps` | 4 | `>= 1` | Substeps inside one `Step(dt)`. **Cheaper and more stable than raising stiffness**. Live; floored at 1 at run time |
| `maxDeltaTime` | 1/15 | finite, `> 0` | Ceiling for `ClampDeltaTime(dt)` — one hitched frame can't explode the body. Live |
| `collisionThickness` | 0.01 | finite, `>= 0` | **(v1.3.0)** Particle skin. When a soft body lands, "how far the lowest particle sits above the ground" is exactly this number. Live |
| `weldTolerance` | 1e-4 | finite, `> 0` | Vertex weld tolerance in metres — a **radius, not a diameter**: seam gaps within it merge, gaps beyond it split into two particle sets, the mesh stops being closed and the volume constraint is silently skipped. **Rebuild-only** (welding runs once per build). See [From Mesh to Particles](/en/soft-body/mesh-to-particles) |
| `enableStretchLimit` | true | — | Whether to enforce the max stretch ratio. Live |
| `maxStretchRatio` | 2 | `> 1` | Longest a structural spring may get, enforced by 8 Gauss-Seidel projection passes. Live |

Methods: `Validate()` (throws on bad values), `ClampDeltaTime(dt)`, `EffectiveSubsteps` (clamps `substeps < 1` to 1).

## Component fields: `SoftBodyBehaviour`

| Field | Default | Notes |
| --- | --- | --- |
| `sourceMesh` | null | The source mesh. **Never rewritten** — write-back goes to the component's own instance mesh |
| `parameters` | defaults | The table above |
| `pinMode` | `None` | `None` / `TopVertices` / `BottomVertices` / `ExplicitIndices` |
| `pinVertexIndices` | empty | Used by `ExplicitIndices`; these are **source mesh vertex indices** (mapped through the weld table internally) |
| `pinLayerThickness` | 1e-3 | Height tolerance for "this layer" in Top/Bottom modes (metres) |
| `initialVelocity` | `zero` | Applied to every unpinned particle on each `Rebuild`. **Perturbations must go here** — pushing particles directly is lost when Play rebuilds from the source mesh |
| `autoSimulate` | true | Turn off to drive `Step(dt)` yourself (fixed timestep, replay, netcode) |
| `generateMesh` | true | Turn off for solver-only mode: no instance mesh is created |
| `recalculateNormals` | true | Recompute normals each write-back. Off saves a little time, lighting follows stale normals |
| `drawGizmoWireframe` | true | Draws structural springs and particles in the Scene view (pinned ones larger) |

## Topology rules

- **Welding**: a spatial hash (cell size = `weldTolerance`, 27-cell neighbourhood, squared-distance test)
  merges coincident vertices into one particle. A box subdivided 2×2 per face: 54 mesh vertices ⇒
  26 particles (8 corners + 12 edge midpoints + 6 face centres).
- **Structural springs** = unique triangle edges. A triangulated box therefore has **18**, not 12 —
  the face diagonals are real edges.
- **Bend springs** = one per shared edge, connecting the two opposite vertices; de-duplicated against
  structural springs. Counts depend on the triangulation, so only invariants are guaranteed: disjoint
  from structural springs, no duplicates, and always greater than zero on a closed mesh.
- **Closedness** (`IsClosed`): every edge is used by exactly two triangles. Open meshes have no
  meaningful volume ⇒ `Volume()` returns 0 and the volume force is skipped.

## Read-only and diagnostic API (`SoftBodySimulation`)

| Member | Notes |
| --- | --- |
| `Build(SoftBodyMeshData)` | Builds topology and solver; invalid input throws and leaves existing state untouched |
| `IsBuilt`, `Parameters`, `System` | `System` is the underlying `MassSpringSystem` (source of `MaxSpeed()` etc.) |
| `ParticleCount` / `MeshVertexCount` / `StructuralSpringCount` / `BendSpringCount` / `TriangleCount` / `IsClosed` | Sizes |
| `IndexOfVertex(int)` / `SetVertexPinned(int, bool)` | Mesh vertex index ⇄ particle index |
| `GetPosition` / `GetVelocity` / `SetVelocity` | Per-particle access. `SetVelocity` touches velocity only, never position |
| `SetPinned` / `IsPinned` | Pin by particle index. A pinned particle is **excluded from integration entirely**: its velocity is zeroed and its position does not move, so it acts as a static obstacle rather than a force-free point (the old behaviour only zeroed the acceleration and then ran `position += velocity·dt`, drifting a 1 m/s pinned point 0.1667 m in 10 steps) |
| `CapturePositions` / `SetPositions` | Bulk read / pose. **`SetPositions` zeroes velocities** |
| `WritePositionsTo(Vector3[])` / `CaptureMeshVertices()` | Expand particles back into source-mesh vertex order |
| `GetStructuralSpring(i)` / `GetBendSpring(i)` | Return `SoftBodyEdge { a, b, restLength }` |
| `Volume()` / `RestVolume()` | Current / rest signed volume (positive only when winding faces outward) |
| `Step(float dt)` | Advance (applies `ClampDeltaTime` and substepping internally) |
| `ResetToInitial()` / `CaptureInitialLayout()` | Restore the initial layout / make the current pose the initial one |
| `MaxStretchRatio()` | Largest `length/restLength` across structural springs |
| `IrreparableStretchEdges` | Edges that are over `maxStretchRatio` **and** pinned at both ends, so no position projection can fix them. Non-zero is not an error (a pinned shell can legitimately be stretched by an external push) — but it is the only signal that the clamp is spinning without effect, which is why it is now reported instead of silently burning all 8 passes every substep |
| `HasNonFiniteState()` | True if any particle holds NaN or Infinity |
| `CollectEdges(List<ValueTuple<Vector3,Vector3>>, bool includeBend)` | Line segments for gizmos/debug, in local space |

## Related pages

- [Soft Body Simulation](/en/soft-body/) · [Cloth Parameter Reference](/en/reference/cloth-parameters) · [Mass-Spring Parameter Reference](/en/reference/mass-spring-parameters)
