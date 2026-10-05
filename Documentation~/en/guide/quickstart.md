# Quick Start

## The 10-second version

1. **Tools → Physics Simulation → Create Demo Scene**
2. Press **Play**

A five-link chain hanging from a pinned anchor is released from a 38° tilt and starts swinging: the upper links barely deform while the lower ones stretch visibly — that is the "stiff on top, soft below" gradient. In the Scene view particles are wire spheres (amber = pinned, cyan = free) and springs are coloured by strain (**red when stretched, blue when compressed**).

## The 10-line version: attach one in a scene

1. **Add Component → Physics Simulation / Mass Spring Behaviour** on an empty GameObject
2. In **Particles**, add two rows: tick `pinned` on the first one (the anchor), give the second a mass and a position
3. In **Springs**, add one row: `a = 0`, `b = 1`, `stiffness = 200`, leave `restLength` at **0** (auto = initial distance)
4. Press **Play**

## Pure code, no MonoBehaviour

```csharp
using PhysicsSimulation;
using UnityEngine;

var system = new MassSpringSystem();
system.Parameters.gravity       = new Vector3(0f, -9.81f, 0f);
system.Parameters.substeps      = 8;                       // the main stability knob
system.Parameters.globalDamping = 0.6f;                    // 1/s

int anchor = system.AddParticle(new Vector3(0f, 3f, 0f), mass: 1f, pinned: true);
int bob    = system.AddParticle(new Vector3(0.6f, 2.6f, 0f), mass: 0.8f);
system.AddSpring(anchor, bob, restLength: 0f, stiffness: 900f, damping: 1.5f);  // restLength <= 0 → auto

system.Step(Time.fixedDeltaTime);
Debug.Log($"bob {system.Particles[bob].position}, max speed {system.MaxSpeed():F3} m/s");
```

::: tip Why `restLength = 0`
In `AddSpring`, `restLength <= 0` means "use the current distance between the two endpoints". Measuring and typing it by hand is the number-one cause of a chain that jiggles on its own from frame one.
:::

## Three ways to drive it

| Way | How | Use for |
| --- | --- | --- |
| `MassSpringBehaviour` | attach the component, fill the lists, press Play | a rope in a level, a hanging sign |
| Your own system | `new MassSpringSystem()` and call `Step(Time.fixedDeltaTime)` from any `FixedUpdate` | when you own the lifecycle (fixed-step replay, custom substepping) |
| `MassSpringParticleLink` | put it on a Transform, set `particleIndex`; it follows in `LateUpdate` | making a visible ball track a particle |

::: warning Feed it `Time.fixedDeltaTime`, never `Time.deltaTime`
Feeding a variable frame interval into the physics gives you the classic "it swings faster on a fast machine" bug. `Step()` also clamps dt (`maxDeltaTime`, default 1/15 s), but **the clamp is a safety net, not a licence**: always advance from `FixedUpdate`.
:::

## Next

- Formulas, parameters, stability: [Mass-Spring](/en/mass-spring/)
- Field lookup: [Parameter reference](/en/reference/mass-spring-parameters)
- Diagnosing problems: [Editor tools](/en/tools/)
