# Cloth Parameter Reference

`ClothParameters` (namespace `PhysicsSimulation`). Every field is Inspector-editable. `Validate()` runs automatically when a `ClothSimulation` is constructed — invalid values **throw immediately** instead of silently producing a wrong simulation.

Since v1.6.0 every field carries exactly one marker: `[RuntimeTunable]` (edit it during Play and it takes effect on the next step) or `[RequiresRebuild("why")]` (the solver copied it into an internal structure at build time). The two tables below split accordingly; a reflection test fails the build if a new field carries neither, so "I changed it and nothing happened" cannot come back silently. At run time `Step()` floors `substeps` / `iterations` at 1 and clamps `dt` to `maxDeltaTime`.

## Grid

| Field | Default | Constraint | Meaning |
| --- | --- | --- | --- |
| `columns` | 16 | `>= 2` | Column count (local `+X`) |
| `rows` | 16 | `>= 2` | Row count (local `-Y`) |
| `spacing` | 0.1 | finite, `> 0` | Rest distance between neighbours, in metres. Sheet size = `(columns-1)·spacing × (rows-1)·spacing` |
| `mass` | 1 | finite, `> 0` | Per-particle mass. Distance constraints distribute corrections with `inverseMass`. **Rebuild-only since v1.6.0**: the free-particle inverse mass is computed once at construction and reused by `SetPinned`, so editing `mass` on a built cloth does nothing until you `Rebuild()`. `mass = 0` is rejected by `Validate()` (it used to write `+Infinity`, which became NaN within three frames) |

## Forces and integration

| Field | Default | Constraint | Meaning |
| --- | --- | --- | --- |
| `gravity` | `(0, -9.81, 0)` | finite | Constant gravity |
| `damping` | 0.05 | `>= 0` | Implicit damping: velocity divided by `1 + damping·h` each substep. `0` = lossless, `0.05–0.4` typical for fabric |
| `substeps` | 4 | `>= 1` | Substeps inside one `Step(dt)`. **Raising this is cheaper and more stable than raising `iterations`**. Floored at 1 at run time |
| `iterations` | 2 | `>= 1` | Projection passes per substep. Floored at 1 at run time — a 0 does not disable projection, it silently becomes one pass |
| `maxDeltaTime` | 1/15 | finite, `> 0` | Ceiling applied by `ClampDeltaTime(dt)` so one hitch cannot destroy the sheet |

## Stiffness (PBD semantics, not a spring constant)

Projection ratio per pass is derived as `alpha = 1 - (1 - stiffness)^(1/iterations)`,
so `stiffness = 1` means "project fully every pass" and `stiffness = 0` means "no correction at all".

| Field | Default | Range | Meaning |
| --- | --- | --- | --- |
| `structuralStiffness` | 1 | `[0, 1]` | Structural edges. Below 1 you will see visible elongation |
| `shearStiffness` | 0.6 | `[0, 1]` | Diagonal edges; resists skew |
| `bendStiffness` | 0.2 | `[0, 1]` | Skip edges; silk 0.02–0.08, cotton 0.1–0.25, canvas/leather 0.3+ |
| `enableShear` | true | — | Off = no shear constraints (about a third fewer constraints) |
| `enableBend` | true | — | Off = no bend constraints |

## Safety nets

| Field | Default | Constraint | Meaning |
| --- | --- | --- | --- |
| `maxStretchRatio` | 2 | finite, `>= 1` | Hard ceiling on `length / restLength` per constraint, enforced by up to 16 clamping sweeps each substep (the sweep stops early once a pass changes nothing) |
| `collisionThickness` | 0.01 | finite, `>= 0` | Minimum particle-to-proxy distance — effectively the fabric's thickness. Since v1.3.0 it also applies to bridged scene colliders |

::: warning Two honest caveats
1. **Stretch clamping is approximate**: Gauss-Seidel style per-edge correction means fixing one edge can push another out of
   range, so ~2% residual overshoot is tolerated under extreme parameters (the test asserts
   `MaxStretchRatio() <= maxStretchRatio × 1.02`). The guaranteed part is the 16-sweep loop, not an exact solve.
2. **Collisions run after clamping**: the order is predict → project → clamp → collide → commit. "No particle inside a
   sphere" is therefore a hard guarantee, at the cost of a momentarily over-stretched constraint.
:::

::: tip Live vs rebuild, at a glance

| Live | Needs `Rebuild()` |
| --- | --- |
| `gravity`, `damping`, `structuralStiffness`, `shearStiffness`, `bendStiffness`, `enableShear`, `enableBend`, `substeps`, `iterations`, `maxDeltaTime`, `maxStretchRatio`, `collisionThickness` | `columns`, `rows`, `spacing`, `mass` |
:::

## Read-only diagnostics

| Member | Description |
| --- | --- |
| `ParticleCount` / `Columns` / `Rows` | Grid size |
| `ConstraintCount` / `Constraints` | Constraint count and list (structural → shear → bend, contiguous) |
| `IndexOf(col, row)` | Column/row → particle index (row-major `row * columns + col`), throws when out of range |
| `GetPosition(i)` / `GetVelocity(i)` / `CapturePositions()` | Read state |
| `SetPinned(i, bool)` / `IsPinned(i)` | Pinning |
| `AddWindImpulse(acc, dt)` | Add one frame of wind acceleration to all free particles |
| `AddSphereObstacle(center, radius)` / `ClearObstacles()` / `ObstacleCount` | Sphere obstacles (local space) |
| `MaxStretchRatio()` | Current max `length / restLength`; `1.0` = no stretch |
| `HasNonFiniteState()` | NaN / Infinity smoke check |
| `ResetToInitial()` / `CaptureInitialLayout()` | Restore / re-capture the initial layout |

## Constraint count formulas

With `c` columns and `r` rows:

```text
structural = r·(c-1) + c·(r-1)
shear      = 2·(c-1)·(r-1)
bend       = r·(c-2) + c·(r-2)
```

Example: `64 × 64` ⇒ 8064 structural + 7938 shear + 7936 bend = **23,938** constraints.

## Related

- [Cloth simulation](/en/cloth/)
- [Mass-spring parameter reference](/en/reference/mass-spring-parameters)
