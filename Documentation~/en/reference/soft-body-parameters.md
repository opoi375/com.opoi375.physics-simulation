# Soft Body Parameter Reference

Namespace `PhysicsSimulation` (flat — there is no `PhysicsSimulation.SoftBody`).
`SoftBodyParameters.Validate()` runs automatically when a `SoftBodySimulation` is constructed; invalid
values **throw immediately** rather than silently handing you a simulation that has already overflowed.

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
| `mass` | 1 | finite, `> 0` | Per-particle mass (kg). Spring and volume forces convert to acceleration via `1/mass` |
| `gravity` | `(0, -9.81, 0)` | finite | Constant gravity, local space |
| `damping` | 0.5 | `>= 0` | Global velocity decay, `v /= 1 + damping·h` per substep. Lower it to keep it wobbling |
| `maxSpeed` | 40 | `>= 0` (0 = unlimited) | Velocity cap. **Last line of defence** — correct parameters never reach it |
| `springStiffness` | 1200 | finite, `>= 0` | Structural spring constant in N/m (a real constant, not the PBD ratio cloth uses). Turn it down for softness; the demo jelly uses 130 |
| `springDamping` | 6 | `>= 0` | Axial damping on structural springs, kills ringing |
| `bendStiffness` | 150 | `>= 0` | Bend spring stiffness (opposite-vertex distance) |
| `bendDamping` | 2 | `>= 0` | Bend spring damping |
| `volumeStiffness` | 4000 | `>= 0` | Volume constraint gain: `F = -k_v·(V-V₀)·∇V`. 0 disables it (body goes flat) |
| `volumeDamping` | 20 | `>= 0` | Damps `dV/dt` so it doesn't pump like a balloon |
| `substeps` | 4 | `>= 1` | Substeps inside one `Step(dt)`. **Cheaper and more stable than raising stiffness** |
| `maxDeltaTime` | 1/15 | finite, `> 0` | Ceiling for `ClampDeltaTime(dt)` — one hitched frame can't explode the body |
| `weldTolerance` | 1e-4 | finite, `> 0` | Vertex weld tolerance in metres; coincident vertices closer than this become one particle |
| `enableStretchLimit` | true | — | Whether to enforce the max stretch ratio |
| `maxStretchRatio` | 2 | `> 1` | Longest a structural spring may get, enforced by 8 Gauss-Seidel projection passes |

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
| `SetPinned` / `IsPinned` | Pin by particle index |
| `CapturePositions` / `SetPositions` | Bulk read / pose. **`SetPositions` zeroes velocities** |
| `WritePositionsTo(Vector3[])` / `CaptureMeshVertices()` | Expand particles back into source-mesh vertex order |
| `GetStructuralSpring(i)` / `GetBendSpring(i)` | Return `SoftBodyEdge { a, b, restLength }` |
| `Volume()` / `RestVolume()` | Current / rest signed volume (positive only when winding faces outward) |
| `Step(float dt)` | Advance (applies `ClampDeltaTime` and substepping internally) |
| `ResetToInitial()` / `CaptureInitialLayout()` | Restore the initial layout / make the current pose the initial one |
| `MaxStretchRatio()` | Largest `length/restLength` across structural springs |
| `HasNonFiniteState()` | True if any particle holds NaN or Infinity |
| `CollectEdges(List<ValueTuple<Vector3,Vector3>>, bool includeBend)` | Line segments for gizmos/debug, in local space |

## Related pages

- [Soft Body Simulation](/en/soft-body/) · [Cloth Parameter Reference](/en/reference/cloth-parameters) · [Mass-Spring Parameter Reference](/en/reference/mass-spring-parameters)
