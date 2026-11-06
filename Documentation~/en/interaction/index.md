# Particle Interaction (v1.6.0)

Through v1.5.0 the three solvers worked alone: a soft body only knew about scene `Collider`s, cloth only knew about
scene `Collider`s, and two bodies placed on top of each other passed straight through one another. v1.6.0 adds a
**body-to-body** contact channel.

## How to use it

Add one `ParticleInteractionGroup` and put the bodies that should collide into `Members` — or fill `interactionGroup`
on the body's own Inspector. Both routes go through the same `Add`, so "the group remembers a name that the body
itself does not know" cannot happen.

```csharp
var group = gameObject.AddComponent<ParticleInteractionGroup>();
group.Add(jellyA);      // SoftBodyBehaviour
group.Add(jellyB);
group.Add(scarf);       // ClothBehaviour — crossing solvers needs no extra configuration
```

**No members means the released behaviour, bit for bit.** That is deliberate: contact is opt-in, and there is no
"everything collides with everything" hidden global that would change the behaviour of scenes that already work.

Membership is explicit on both sides: `Add` writes `CloudGroup` back onto the body, `Rebuild()` re-registers the body
(`Reattach`), and leaving the group removes every proxy from every other member, so a rebuilt body never leaves behind
a cloud that no longer updates.

## Contact geometry: a cloud of spheres, not triangles

Every body hands the others its particles as a **cloud of spheres**. Why not fit a triangle surface: under deep
penetration the "closest face" normal flips sign, which is the corner-conveyor trap the fluid tank hit in v1.5.0. A
cloud has no direction ambiguity; the price is a grainy contact surface, so the radius has to be at least half the
particle spacing or points slip between two spheres.

One consequence runs against intuition:

> The trigger distance is **the other body's radius + skin**, not `r_A + r_B`.

Because one body's particle is a **point** as far as the proxy is concerned, not a sphere. Two surfaces therefore have
to interpenetrate by roughly half a particle spacing before contact does anything at all.

The resolution is sequential (`ICollisionProxy.PushOut(point, skin)`, unchanged since v1.3.0): each body projects itself
out of the others in arrival order, so momentum is **not** conserved, and which body gives way depends on the step order.
That is a known approximation, not a bug — distributing the correction by inverse mass would break the proxy signature.

## `cloudRadiusFactor`

| Solver | Default | Where the radius comes from |
| --- | --- | --- |
| Soft body | 0.5 | mean structural rest length × factor, additionally capped by the rest size |
| Cloth | 0.5 | `spacing` × factor |
| Mass-spring | 1.0 | `particleRadiusBase` × factor (that field is already an absolute radius — 1.0 keeps the released look) |

0.5 means "two neighbouring spheres exactly touch". Measured (0.6 m cube, 3 subdivisions, zero gravity, the two bodies
placed 0.08 m face to face, 360 steps, factor applied to both):

| Factor | Sphere radius | Trigger distance | Result |
| --- | --- | --- | --- |
| 0.25 | 0.057 m | 0.067 m | **No contact at all**: nearest sphere-centre distance frozen at 0.080 m for 360 steps, velocity exactly 0 |
| 0.35 | 0.080 m | 0.090 m | They do separate (1.64 m), but the trigger is below the particle spacing (0.228 m) ⇒ points slip through the gaps |
| **0.50** | 0.114 m | 0.124 m | The default; final separation 3.78 m |
| 0.75 | 0.171 m | 0.181 m | Both clouds start pushing half a spacing early and rebound harder (4.02 m) |

Turning the factor down is not "finer contact", it is **missing contact**. To make contact follow the surface, densify
the mesh — 3 → 6 subdivisions halves the spacing from 0.228 to 0.114 m — not the radius.

The radius also has a rest-size ceiling, `cbrt(restVolume) × 0.25`: a coarse grid with a large factor would otherwise
inflate the cloud into a balloon bigger than the body, and two such clouds can never resolve their own overlap.

## Known limits (all measured)

**Two equal-mass bodies do not stack.** Across four stiffness recipes the end state was always "both bodies lying side by
side on the floor" (final nearest distance 0.58~0.91 m). The cause is the contract itself: each body projects itself out
of the other, neither yields, and the escape direction carries a sideways component, so the upper body slides off.
**Light pressing on heavy does work** — in the demo scene the scarf really is supported by a jelly, because the scarf's
particles are the only ones that can move.

**Deep interpenetration produces a one-off recoil, and it cannot be trimmed away.** The demo scene used to start with
0.18 m of overlap (≈ one particle spacing): total translation 4.61 m at factor 0.5, but **decaying** — 1.03 → 0.18 m per
60 steps, not divergent.

Following the v1.5.0 `maxSpeed` lesson, a "at most 25% of `need` per query" cap was implemented and then **measured to be
ineffective**: 4.61 → 4.15 m, and at factor 0.75 it made things *worse* (4.53 → 5.65 m). The energy lives in the springs
and the volume constraint, not in how far a single projection moves a point — a jelly squashed 28% narrower really does
bounce back. The mechanism was therefore **reverted** rather than shipped as unverified API, and the demo now starts at a
0.06 m face gap (still inside the 0.105 m trigger distance, so contact still has something to show, without the launch).

**Cost is linear in body count**, not quadratic: each body builds its index once per step, and the group only wires
proxies (`Group_CostScalesLinearlyWithBodyCount`).

## Demo scene

`Tools / Physics Simulation / Interaction / Create Interaction Demo Scene` → `Assets/Scenes/ParticleInteractionDemo.unity`.
Two jellies and a scarf in one group. `Dump State` prints nearest sphere-centre distance / radii / penetration depth to
the Console — deciding whether contact is working is a matter of numbers, not of eyeballing a screenshot (that rule was
earned by the v1.5.0 tank leak, which took three attempts to find).

## Next

- [Collision proxies](/en/collision/) — the proxy contract this is built on
- [Soft body](/en/soft-body/) · [Cloth](/en/cloth/) · [Mass-spring](/en/mass-spring/)
- [Editor tools](/en/tools/) · [Changelog](/en/changelog)
