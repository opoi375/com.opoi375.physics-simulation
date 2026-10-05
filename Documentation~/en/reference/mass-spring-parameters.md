# Parameter Reference

This page is a **field lookup table**. For the reasoning behind each value see [Mass-Spring System](/en/mass-spring/).

## `MassSpringBehaviour` (Inspector)

The driver component (`Add Component → Physics Simulation / Mass Spring Behaviour`).

### Integration

| Field | Type | Default | Unit | Notes |
| --- | --- | --- | --- | --- |
| `gravity` | Vector3 | `(0, -9.81, 0)` | m/s² | set to zero to isolate spring behaviour |
| `globalDamping` | float | `0.5` | 1/s | global linear damping, applied as the divisor `(1 + c·dt)`, can never flip the sign |
| `substeps` | int | `8` | count | how many sub-steps one `FixedUpdate` is split into; the main stability knob, cost is linear |
| `maxDeltaTime` | float | `1/15` | s | dt clamp |

### `particles` list

| Field | Type | Default | Notes |
| --- | --- | --- | --- |
| `position` | Vector3 | — | initial world position; also where `Reset To Initial Layout` returns to |
| `mass` | float | `1` | kg, must be a finite positive number |
| `pinned` | bool | `false` | fixed point (`inverseMass` becomes 0) |
| `initialVelocity` | Vector3 | `zero` | initial velocity |
| `damping` | float | `0` | per-particle `c_particle` (1/s), factor clamped to `[0,1]` |

### `springs` list

| Field | Type | Default | Notes |
| --- | --- | --- | --- |
| `a` | int | `0` | endpoint A index (in a chain: the link above) |
| `b` | int | `0` | endpoint B index; must differ from `a` and be in range |
| `stiffness` | float | `200` | k (N/m) |
| `damping` | float | `1` | axial c (N·s/m) |
| `restLength` | float | `0` | **0 or negative = auto, using the initial endpoint distance** |

### Gizmos

| Field | Type | Default | Notes |
| --- | --- | --- | --- |
| `groundReferenceSize` | float | `0` | half-length (m) of the ground reference lines drawn at world Y = 0; 0 disables |
| `particleRadiusBase` | float | `0.06` | base radius; actual radius `= base * (1 + ln(1 + 4m))`, clamped to `[0.01, 2]` |

Colours:

| Element | Colour |
| --- | --- |
| free particle | cyan wire sphere |
| pinned particle | amber wire sphere |
| spring near rest length | pale green |
| stretched spring | blends towards red (`|strain| * 2` saturates) |
| compressed spring | blends towards blue |
| ground reference lines | translucent grey |

### Read-only members at runtime

| Member | Type | Notes |
| --- | --- | --- |
| `System` | `MassSpringSystem` | the built system; `null` when the configuration is invalid |
| `IsBuilt` | bool | `System != null` |
| `LastBuildError` | string | why the last build failed (set when `Rebuild()` returns `false`) |

### Methods / context menus

| Member | Notes |
| --- | --- |
| `Rebuild()` | rebuild from the current configuration; returns `false` on invalid input (reason in `LastBuildError`) instead of throwing |
| `Capture Current As Rest` | context menu: write the **simulated** positions, velocities and spring lengths back into the configuration as the new initial layout |
| `Reset To Initial Layout` | context menu: call `System.ResetToInitial()` |

Lifecycle: `Awake` / `OnEnable` build the system; in edit mode `OnValidate` rebuilds whenever you change a value (it never rebuilds during play); `FixedUpdate` calls `Step(Time.fixedDeltaTime)`.

## `MassSpringParticleLink`

| Field | Type | Notes |
| --- | --- | --- |
| `target` | `MassSpringBehaviour` | if empty, takes the parent's `MassSpringBehaviour` |
| `particleIndex` | int | particle to follow; out-of-range indices are skipped silently (visualisation must never break the simulation) |
| `offset` | Vector3 | offset from the particle position, metres |

Syncs `transform.position` in `LateUpdate`. Purely cosmetic — removing it does not change the simulation at all.

## `MassSpringSystem` (pure logic)

| Member | Signature | Notes |
| --- | --- | --- |
| `Parameters` | `MassSpringParameters` | mutable system parameters |
| `Particles` | `IReadOnlyList<Particle>` | read-only view (elements themselves are mutable) |
| `Springs` | `IReadOnlyList<Spring>` | read-only view (elements are immutable) |
| `AddParticle` | `int AddParticle(Vector3 position, float mass, bool pinned = false, float damping = 0f)` | returns the index; throws `ArgumentOutOfRangeException` on non-finite positive mass, NaN/Inf position, negative damping |
| `AddSpring` | `int AddSpring(int a, int b, float restLength, float stiffness, float damping)` | returns the index; `restLength <= 0` auto-measures; throws on out-of-range indices, `a == b`, invalid parameters |
| `Pin` / `Unpin` | `void Pin(int index)` | toggles `inverseMass` between `0` and `1/mass`; throws on bad index |
| `ApplyForces` | `void ApplyForces()` | zero accumulators → gravity → spring internal forces |
| `Step` | `void Step(float deltaTime)` | clamp dt → sub-steps, each `ApplyForces()` + integrate; throws on `dt <= 0`, NaN or Inf |
| `ResetToInitial` | `void ResetToInitial()` | restores position, velocity, pinned state and per-particle damping; zeroes forces |
| `MaxSpeed` | `float MaxSpeed()` | largest speed right now (diagnostics) |
| `HasNonFiniteState` | `bool HasNonFiniteState()` | NaN / Infinity detector (diagnostics) |

## `Particle` / `Spring`

See [Mass-Spring §4](/en/mass-spring/). Key points: `force` is a **per-step accumulator**, `inverseMass` is owned by `Pin`/`Unpin`, and every `Spring` field is `readonly` (rebuild to change a spring).

## Default parameters of the demo chain

What `Tools → Physics Simulation → Create Demo Scene` produces:

| Item | Value |
| --- | --- |
| Particles | 1 pinned anchor (`y = 3`, `m = 1`) + 5 links (spacing `0.45 m`, `m = 0.8`, last one `m = 1.2`) |
| Springs | 5, `k = 1600 → 1100 → 750 → 500 → 320` (stiff on top, soft below), `c = 1.5`, auto rest length |
| Integration | `gravity = (0,-9.81,0)`, `globalDamping = 0.6`, `substeps = 8`, `maxDeltaTime = 1/15` |
| Start pose | whole chain tilted 38° and released (hanging perfectly still would show nothing) |
| Scene | `Main Camera` aimed at the chain centre (FOV 42), one directional light at 42°/-28° with soft shadows, ground reference lines with half-length 5 |
| Visuals | one `MassSpringParticleLink` + sphere per free particle (the last one bigger), a cube for the anchor |

Stability of the hardest link: `k·h²/m = 1600 × (1/480)² / 0.8 ≈ 0.0087 ≪ 4`.
