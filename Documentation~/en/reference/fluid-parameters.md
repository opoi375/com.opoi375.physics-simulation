# Fluid Parameter Reference

`FluidParameters` (namespace `PhysicsSimulation`). Every field is editable in the Inspector; call
`FluidBehaviour.Rebuild()` after changing them. `Validate()` runs automatically when a `FluidSimulation` is built and
**throws immediately** on invalid values — it never silently hands you a wrong simulation.

## The resolution trio (they constrain each other; change one, look at the other two)

| Field | Default | Constraint | Notes |
| --- | --- | --- | --- |
| `particleSpacing` | 0.05 | finite, `> 0` | initial lattice spacing `d`. **Particle count grows as `1/d³`**; `0.05 → 0.06` removes 42% of them |
| `kernelRadius` | 0.10 | finite, **`> particleSpacing`** | kernel support radius `h`. `h = 2d` gives ~21~25 (measured) neighbours in a 3D lattice; `h = d` is rejected by `Validate()` (the kernel support would contain almost no neighbour, density would equal the self term, and the water flies apart into a point cloud) |
| `restDensity` | 1000 | finite, `> 0` | rest density ρ0 (water = 1000 kg/m³). It is the **constraint target**, not a softness knob |

Derived (read-only):

- `ParticleMass` = `ρ0 · d³` — the mass of one particle at the design density. `d = 0.05` → `0.125 kg`; the demo's
  `d = 0.07` → `0.343 kg`.
- particle count ≈ `volume / d³`. Multiply this out before you budget.

## Solver

| Field | Default | Constraint | Notes |
| --- | --- | --- | --- |
| `substeps` | 2 | `>= 1` | substeps per `Step(dt)`. **Raising this is a better deal than raising `solverIterations`** (the neighbour table is rebuilt once per substep, linear cost; each extra iteration is another full sweep) |
| `solverIterations` | 2 | `>= 1` | density projections per substep. 0 means "don't solve incompressibility" |
| `complianceAlpha` | 0 | `>= 0` | **relaxation factor**: λ is scaled by `1/(1+α)`. `0` = strictly incompressible; `0.1~1` gives a soft/foamy feel. This is not the paper's dimensionless compliance |
| `clampTensileLambda` | true | — | zero out `λ > 0` ("too sparse, pull neighbours in"). Real liquids have no such force; leaving it on clumps sparse particles and films the free surface |
| `maxParticles` | 8192 | `>= 1` | building more particles is **refused** (`LastBuildError` reports the actual count) instead of letting the frame rate collapse |
| `maxDeltaTime` | 1/30 | `>= 0` (`<= 0` = no cap) | per-step dt cap — see the warning below, this was the real cause of the exploding demo |
| `maxSpeed` | 0 | `>= 0` (`0` = unlimited) | **advective velocity ceiling (CFL-style)**. The correction rail only bounds constraint motion; advection is velocity × substep and had no bound: PBF hands back ten-plus m/s on dilute or deeply penetrating configurations, enough to hop a whole thin slab in one step. The demo uses 8 m/s; `Validate()` rejects negatives |

::: warning `maxDeltaTime` is a correctness parameter, not a performance one
The first frame after entering Play, a hitch, or a return from background can produce second-scale `Time.deltaTime`.
The substep feeds straight into gravity integration and projection, so one frame can eject the whole pool: measured
centroid `y = −1394 m`, bounds `162×1492×115 m`. Cloth, mass-spring and soft body always had this cap; the fluid
got it in `v1.5.0` (identical semantics: `<= 0` disables the cap).
:::

## Dissipation and looks

| Field | Default | Constraint | Notes |
| --- | --- | --- | --- |
| `gravity` | `(0, -9.81, 0)` | finite | constant gravitational acceleration (simulation space) |
| `xsphViscosity` | 0.05 | `[0, 1]` | XSPH velocity smoothing weight: `vᵢ += c·Σⱼ (m/ρⱼ)·(vⱼ−vᵢ)·Wᵢⱼ`. `0` = inviscid (spray everywhere), `0.05~0.1` = water, `>0.3` starts looking oily. Above 1 overshoots backwards, so `Validate()` rejects it |
| `vorticityEpsilon` | 0 | `>= 0`, finite | vorticity confinement strength, restores swirl. Costs an extra neighbour sweep, so **off by default**; `0.1~0.5` is where it starts showing |
| `collisionThickness` | 0.005 | `>= 0`, finite | collision "fur": how far a particle is pushed outside a collider. Rule of thumb `0.2·d`. Too small jitters, too large leaves a gap along walls |

## The ε in the denominator (easy to misread)

`ComplianceEpsilon = (complianceAlpha · h)³ · ρ0`, floored at `1e-12`.

::: tip It only guards against division by zero; it is not the softness source
Softness comes from the `1/(1+α)` relaxation. With α = 0, ε is far below a typical `Σ|∇C|²` (order `1e5`) and only
prevents "isolated particle, zero denominator". To soften the fluid, move `complianceAlpha`, not ε.
:::

## Class constants (not in the Inspector)

| Name | Value | Notes |
| --- | --- | --- |
| `FluidSimulation.MaxCorrectionPerIterationFactor` | 0.25 | displacement rail: at most `0.25·h` per particle per iteration. Mutable (`<= 0` disables) so the tuning sweep can be reproduced; measured stable across `0.02~0.25` (after 4 s, `v_max` 0.83~1.02 m/s), i.e. a fuse rather than a primary parameter |

### Inside-out box container (v1.5.0)

| Field | Default | Notes |
| --- | --- | --- |
| `enableBoxContainer` | `false` | Confine the water to an axis-aligned (optionally rotated) box. **It is a serialized `FluidBehaviour` field, not a runtime `Collisions.Add` call** — proxies added by hand vanish after `Rebuild()` (which happens on entering Play， measured the hard way), so the container has to travel with the component |
| `containerCenter` | `Vector3.zero` | Container centre (world space) |
| `containerHalfSize` | `Vector3.zero` | Half size in metres， all three components must be positive， otherwise `Validate` reports it and the container is skipped |

That is how the demo tank works: six plates are drawn， `ApplyTankContainer` writes the cavity into those three
fields， and `碰撞代理 1` / `collision proxies 1` in Dump State is exactly it.

## Surface rendering parameters (on `FluidBehaviour`, not `FluidParameters`)

The surface is **presentation**: it reads particle positions and never joins the density constraint, so simulation
results are bit-identical whether it is on or off.

| Field | Default | Notes |
| --- | --- | --- |
| `renderMode` | `Particles` | `Particles` = instanced billboards (the v1.5.0 look) / `Surface` = one continuous isosurface / `Both` = two draw submissions |
| `surfaceCellSize` | 0 | voxel edge. **0 means "use `particleSpacing`"**; the demo uses `0.75 × particleSpacing` (below the spacing or the surface reads as blocks) |
| `surfaceIsoLevel` | 0 | threshold. **0 means `FluidSurface.DefaultIsoLevel = 0.5`**: the field is normalised so a still body reads ≈ 1 inside, so 0.5 *is* the surface |
| `surfaceRefreshEveryNFrames` | 1 | rebuild the surface every N frames. The demo uses 2 — one rebuild costs more than several constraint passes, so throttle before optimising |
| `surfaceMaxCells` | 0 | cell budget. **0 means `FluidSurface.DefaultMaxCells = 262144`**; past it the voxel is **grown** (never shrunk; `grid.Min` and the covered extent stay put) |
| `surfaceColor` | translucent blue | surface tint (alpha must be below 1 or it reads as blue plastic; `FluidSurfaceMaterial.IsTranslucent` asserts exactly that) |

Hard ceiling: one surface mesh carries at most `FluidSurface.MaxMeshVertices = 65000` vertices (`Mesh.triangles`
defaults to ushort indices, 65535). Past that it **throws** instead of handing you a torn surface.

Read-only observables: `SurfaceMesh` / `SurfaceMaterial` / `SurfaceRevision` (how many rebuilds) /
`SurfaceTriangleCount` / `ParticleBatchCount`.

## Read-only diagnostics

| Member | Meaning | What it tells you |
| --- | --- | --- |
| `ParticleCount` / `ParticleMass` / `TotalMass()` | particle count and mass | mass conservation (projection must not change total mass) |
| `GetDensity(i)` / `ComputeDensity(i)` | current density ρᵢ | `ρ/ρ0` in `0.85~1.05` means "this is water"; `> 1.6` is pancaked, `< 0.6` is a dust cloud |
| `GetLambda(i)` | density constraint multiplier | sign: negative = compression pushing apart, positive = tension pulling in (never positive while clamped) |
| `AverageNeighborDegree` / `MaxNeighborDegree` / `NeighborDegree(i)` / `NeighborAt(i, slot)` | neighbour degrees and the CSR table | an `h = 2d` 3D lattice should show 26~32; single digits mean the water already flew apart |
| `Bounds()` / `CenterOfMass()` | bounds and centroid | the direct evidence for a leaking tank (centroid below the floor) |
| `TotalKineticEnergy()` / `AverageVorticity()` / `MaxVorticity()` | energy and vorticity | after settling, energy should be small; monotonic growth means energy is being pumped in |
| `HasNonFiniteState()` | any NaN/Inf | hard divergence check |
| `Positions` / `GetPosition(i)` / `GetVelocity(i)` | state access | custom rendering or external coupling |
| `Collisions` | the `CollisionSet` | add/remove colliders without rebuilding particles |
| `SetSimulationToWorld(m)` | local ↔ world matrix | used by the component bridge; keep the identity default in hand-written solvers |

## Related pages

- [Fluid simulation](/en/fluid/) — pipeline, kernels, the four fuses, particle and surface rendering, demo configuration
- [Scene collision](/en/collision/) — analytic proxies and `CollisionSet`
- [Cloth parameter reference](/en/reference/cloth-parameters) — parameter feel for the same PBD family
