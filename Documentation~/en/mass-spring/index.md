# Mass-Spring System

In one line: **abstract a body into point masses, connect them with springs, and each step compute how much the springs push, turning force into velocity and position.**

Ropes, chains, swings, hanging signs, cloth and soft bodies are all this same machinery with a different topology plus different constraints.

## 1. What one step actually does

```text
span = min(dt, maxDeltaTime)          # dt clamp: a frame hitch or a long pause cannot inject a huge step
h    = span / substeps                # sub-step length
repeat substeps times:
    ApplyForces()                     # zero accumulators → gravity → all spring internal forces
    integrate every particle by h     # semi-implicit Euler with implicit damping
```

### Forces

```text
# gravity (accumulated on pinned particles too — inverseMass = 0 turns it into zero acceleration)
force_i = gravity * mass_i

# per spring: dir is the unit vector from a to b
d   = x_b - x_a
l   = |d|
dir = l > 1e-8 ? d / l : 0
F_b = -( k * (l - restLength) + c_spring * dot(v_b - v_a, dir) ) * dir
F_a = -F_b                            # strictly equal and opposite ⇒ internal forces cannot move the centre of mass
```

- `k * (l - restLength)` — **Hooke's law**; when stretched (`l > restLength`) it pulls both ends together.
- `c_spring * dot(v_b - v_a, dir)` — **axial relative-velocity damping**. Without it the spring oscillates forever; with it, approach/separation motion is absorbed.
- `F_a = -F_b` is **hard-coded**, not a coincidence. If the two ends were not exactly opposite, the centre of mass would drift on its own — the test `Step_StretchedSpring_PullsEndsTogetherAndKeepsCenterOfMass` pins exactly that.

### Integration (semi-implicit Euler + implicit damping)

```text
a     = force * inverseMass
v_new = (v + a * dt) / (1 + c_global * dt)        # implicit division
v_new = v_new * clamp(1 - c_particle * dt, 0, 1)  # per-particle damping
x_new = x + v_new * dt                            # advance position with the NEW velocity
```

The order is what makes it *semi-implicit* (symplectic): velocity first, then position with the new velocity. Writing `x += v*dt; v += a*dt` degrades it to explicit Euler, which effectively **injects** energy — the spring swings wider and wider.

::: tip Why damping is a division
The explicit form `v -= c * v * dt` **flips the sign** of velocity once `c * dt > 1` and diverges past `c * dt > 2`. The divisor form `v / (1 + c*dt)` can only shrink velocity towards zero and never crosses it — immune to huge damping, large sub-steps, and typos like `1e5`.
:::

Pinned particles need no special case: with `inverseMass = 0` the acceleration is always zero, the velocity keeps its value (normally zero), and the position never moves.

## 2. The "correct answer" for free fall is not ½gt²

After `n` steps the discrete closed form of semi-implicit Euler is

```text
x_n = g * dt² * n * (n + 1) / 2
```

while the continuous solution is `½ g t²` with `t = n·dt`. They differ by `½ g t · dt` — a **first-order** error that shrinks with dt but never vanishes.

Asserting the continuous formula against a numerical integrator makes you "fix" a bug that does not exist. This package asserts the discrete closed form (`Step_FreeFallMatchesDiscreteClosedForm`) and additionally verifies first-order convergence: quartering dt quarters the error (`Step_FreeFallErrorQuartersWhenDtQuarters`).

## 3. Stability: one number to watch

For a single spring-mass pair, semi-implicit Euler is stable while

```text
ω * h < 2      with ω = sqrt(k / m_reduced)
equivalently   k * h² / m < 4
```

`h` is the **sub-step** length (`span / substeps`), not `dt`. So:

| Want stiffer springs (k↑) | Do this | Not this |
| --- | --- | --- |
| ✅ | raise `substeps` so `h` shrinks | ❌ raise `globalDamping` to suppress the explosion (it eats the motion too — your rope becomes spaghetti) |
| ✅ | remember it is the ratio `k/m` that matters | ❌ raise `maxDeltaTime` "because it clamps anyway" |

The hardest link of the demo chain: `k = 1600`, `m = 0.8`, `substeps = 8`, `dt = 1/60`:

```text
h = (1/60) / 8 = 0.002083 s
k * h² / m = 1600 * 4.34e-6 / 0.8 = 0.0087   ≪ 4   ✅
```

The test `BuildChain_SubstepsKeepHardestLinkStable` recomputes that number and requires `< 1`, so anyone who later tunes the demo to the edge turns the suite red.

::: warning Explicit integration always explodes past `k·dt²/m > 4`
`Step_HighStiffnessWithSubsteps_StaysFiniteAfterThousandSteps` deliberately uses `k = 50000` with `dt = 1/15` (`k·dt²/m = 222`, far above the threshold) and is rescued only by 16 sub-steps, then runs 1000 steps asserting "finite and bounded". Note what it asserts: **numerical boundedness**, not "looks physical" — the latter is not testable.
:::

## 4. Parameters

### `MassSpringParameters`

| Field | Unit | Default | Notes / tuning |
| --- | --- | --- | --- |
| `gravity` | m/s² | `(0, -9.81, 0)` | set to zero to isolate spring behaviour |
| `globalDamping` | 1/s | `0` | preferred "slow it down" knob, typically `0.2 ~ 2`; `0` = conservative, good for replay |
| `substeps` | count | `1` | **the stability knob**; `4 ~ 16` for stiff springs / short ropes; cost grows linearly |
| `maxDeltaTime` | s | `1/15` | below ~15 FPS the simulation slows down instead of exploding |
| `EffectiveSubsteps` | count | — | read-only: `substeps < 1` behaves as 1 |
| `ClampDeltaTime(dt)` | s | — | read-only helper (`maxDeltaTime <= 0` disables clamping) |

### `Particle`

| Field | Unit | Notes |
| --- | --- | --- |
| `position` | m | world position |
| `velocity` | m/s | velocity |
| `force` | N | accumulator for **this step**, recomputed by `ApplyForces()` — never persistent state |
| `mass` | kg | must be a finite positive number |
| `inverseMass` | 1/kg | `1/mass`; `0` = pinned. Maintained by `Pin`/`Unpin`, don't hand-edit |
| `damping` | 1/s | per-particle `c_particle`; the factor `1 - c·dt` is clamped to `[0,1]` |
| `pinned` | — | fixed point (equivalent to `inverseMass == 0`) |
| `InitialPosition` / `InitialVelocity` | m / m/s | read-only snapshot that `ResetToInitial()` restores |

### `Spring`

| Field | Unit | Notes |
| --- | --- | --- |
| `a` / `b` | index | endpoint particle indices; `a→b` defines the positive direction |
| `restLength` | m | passing `<= 0` to `AddSpring` means "use the current distance" |
| `stiffness` | N/m | `k`; higher is stiffer and less stable |
| `damping` | N·s/m | axial `c_spring` |
| `Strain(posA, posB)` | — | read-only `(l - restLength) / restLength`, drives the gizmo colour |

## 5. Common pitfalls (all of them actually happened)

| Symptom | Root cause | Fix |
| --- | --- | --- |
| The chain jiggles from frame one | `restLength` ≠ the real initial distance | pass `0`, or use `Capture Current As Rest` after posing |
| Springs grow until NaN | explicit integration order, or `k·h²/m > 4` | raise `substeps`; check the integration order |
| Velocity flips sign under heavy damping | damping written as `v -= c*v*dt` | use the divisor form `(v + a·dt) / (1 + c·dt)` (already done here) |
| It swings faster on a fast machine | fed `Time.deltaTime` | feed `Time.fixedDeltaTime`, or drive fixed steps yourself |
| A "pinned" point jitters | position written back each step while velocity kept accumulating | use `Pin()` (`inverseMass = 0`) so velocity never grows |
| Explosion after a long pause | one huge dt | `maxDeltaTime` clamp (default 1/15) |
| State-comparison tests that are always green | snapshots via `Vector3.ToString()`, which keeps 2 decimals | use the `"R"` round-trip format (as `CaptureState` does here) |
| The system resets itself during play | rebuilding inside `OnValidate` | guard with `if (Application.isPlaying) return;` (done in `MassSpringBehaviour`) |

## 6. What determinism does and does not mean

`MassSpringSystem` guarantees: **same configuration + same dt sequence ⇒ bit-identical results**. It never reads `Time`, never uses `Random`, never parallelises, and iterates `List`s in insertion order.

It does not guarantee bit-identical results across platforms (`sin/cos/sqrt` differ between libm implementations), nor after you mutate particle state yourself outside `Step`. For cross-platform *visual* consistency, fix the dt sequence (fixed steps) rather than relying on frame rate.

## 7. Next

- Put it in a scene: [Quick Start](/en/guide/quickstart)
- Visualisation and diagnostics: [Editor tools](/en/tools/)
