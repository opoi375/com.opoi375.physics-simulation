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

// optional since v1.3.0: let the particles land on scene shapes
system.Collisions.Add(new PlaneCollisionProxy(Vector3.zero, Vector3.up), CollisionProxySpace.World);

system.Step(Time.fixedDeltaTime);
Debug.Log($"bob {system.Particles[bob].position}, max speed {system.MaxSpeed():F3} m/s");
```

## Landing on the floor (v1.3.0)

All three components share one set of switches:

```csharp
massSpring.collideWithSceneColliders = true;
cloth.collideWithSceneColliders = true;
soft.collideWithSceneColliders = true;
soft.sceneColliders = new List<Collider> { ground.GetComponent<BoxCollider>() };
```

::: warning The floor must not be a `CreatePrimitive(Plane)`
A Plane ships with a **MeshCollider**, and this release is primitive-analytic only (sphere / oriented box /
capsule / half-space) — `ColliderProxies.TryFrom` returns `null` for a MeshCollider and it gets skipped. The result
is the nastiest possible symptom: "the floor is right there, and it still fell through". Use a Cube with a
`BoxCollider` as the ground, or feed the logic layer a `PlaneCollisionProxy` directly.
:::

Off is the default: no transform matrix is injected and no proxies exist, so the simulation stays **bit-identical**
to v1.2.0. To check whether it actually took effect, run `Tools ▸ Physics Simulation ▸ Soft Body ▸ Dump State`, which
prints the proxy count and how far the lowest particle sits above the ground surface.

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

## The other two modules, one line each

Everything above is mass-spring. The shortest usable path for the other two:

```csharp
using PhysicsSimulation;
using UnityEngine;

// Cloth: a 20x14 sheet, top edge pinned, wind blowing towards +Z
var cloth = gameObject.AddComponent<ClothBehaviour>();
cloth.parameters = new ClothParameters { columns = 20, rows = 14, spacing = 0.1f };
cloth.pinEdges = ClothPinEdges.Top;     // pin the top edge only
cloth.Rebuild();

// Soft body: turn any mesh into a jelly that squashes and pops back
var soft = gameObject.AddComponent<SoftBodyBehaviour>();
soft.sourceMesh = someMesh;                       // vertices get welded, triangle edges become springs
soft.pinMode = SoftBodyPinMode.BottomVertices;    // pin the lowest layer
soft.initialVelocity = new Vector3(2.4f, 0f, 0f); // shove it (must be this field - see the soft body guide)
soft.Rebuild();
```

Both advance themselves in `FixedUpdate` (when `autoSimulate` is on) and both accept a manual `Step(dt)`.
No code needed either: **Tools → Physics Simulation → Cloth / Soft Body → Create … Demo Scene**.

## Next

- Formulas, parameters, stability: [Mass-Spring](/en/mass-spring/) · [Cloth](/en/cloth/) · [Soft Body](/en/soft-body/)
- Field lookup: [Mass-spring parameters](/en/reference/mass-spring-parameters) · [Cloth parameters](/en/reference/cloth-parameters) · [Soft body parameters](/en/reference/soft-body-parameters)
- Diagnosing problems: [Editor tools](/en/tools/)
