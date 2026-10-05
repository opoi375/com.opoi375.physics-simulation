# Collision proxies (v1.3.0)

Through v1.0.0 ~ v1.2.0 collision was simply absent: cloth could only be fed hand-typed spheres, soft bodies fell straight
through the floor, and mass-spring had no detection at all. v1.3.0 fills that gap — but the *way* it does it matters,
because the moment the solver starts reading the scene, the "bit-identical for the same parameters and step count"
promise that has held since v1.0.0 is gone.

## In one line

::: info
The solvers do **not** know what a `Collider` is. They only know about **collision proxies** that get injected.
The Unity layer samples the scene's `Collider`s and feeds them in; the pure logic layer keeps doing arithmetic on
`(position, geometry)`, so every assertion stays closed-form — no scene setup, no physics step to wait for,
no drift with Unity versions.
:::

```csharp
using PhysicsSimulation;
using UnityEngine;

// Logic layer: feed geometry by hand, fully unit-testable
soft.Collisions.Add(new PlaneCollisionProxy(Vector3.zero, Vector3.up), CollisionProxySpace.World);
soft.Step(Time.fixedDeltaTime);

// Unity layer: hand it the scene's colliders
behaviour.collideWithSceneColliders = true;
behaviour.sceneColliders = new List<Collider> { groundCollider };
behaviour.Rebuild();
```

## The contract: four rules for `PushOut`

A proxy has exactly one method:

```csharp
public interface ICollisionProxy
{
    Vector3 PushOut(Vector3 point, float skin);   // push the point at least `skin` outside
}
```

All four rules are pinned by tests, so changing the semantics goes red first:

1. **A point already outside is returned unchanged, bit for bit.** No "computed it, didn't change it" float drift —
   otherwise a resting soft body would jitter on its own.
2. **A point inside gets pushed out.** The direction is uniquely determined — it doesn't look at velocity or history,
   so the same input always gives the same output.
3. **When the direction is undefined (point at the sphere centre, on the capsule axis, exactly on a surface) a fixed
   fallback axis (`+Y`) is used and NaN is never produced.** This was cloth's sphere-obstacle convention since v1.1.0;
   v1.3.0 promotes it to an interface contract.
4. **A negative `skin` is treated as 0.** An invalid value must never shove the point deeper inside.

## Which shapes

| Proxy | Shape | Exit direction when inside | Notes |
| --- | --- | --- | --- |
| `SphereCollisionProxy` | sphere | radial | arithmetic **character-for-character** identical to v1.1.0's cloth obstacle, with a bit-identical comparison test |
| `BoxCollisionProxy` | oriented box (OBB) | along the **shallowest** penetrating face; outside → along the closest-surface normal | any rotation supported; a thin slab exits through the nearest face instead of being flung sideways |
| `CapsuleCollisionProxy` | segment + radius | radial on the cylinder, spherical on the caps | a degenerate zero-length axis falls back to a sphere instead of breaking |
| `PlaneCollisionProxy` | **half-space** | always back to the normal side | see below — this is what makes landing work |

::: warning The plane is a half-space, not an infinitely thin wall
`PlaneCollisionProxy` has no notion of "how deep inside" — a particle 3 m below the floor is still pushed back to
**surface + skin**. Implement it as "subtract the penetration depth" and one fast frame carries a particle past the
plane, where it stays buried: that is exactly how a soft body "gets swallowed by the ground".
Normals are normalized at construction, so a non-unit normal can't amplify the push (tested).
:::

::: danger MeshCollider and Terrain are not supported — and are not faked with a bounding box
This release is **primitive analytic only**, by decision. `ColliderProxies.TryFrom` returns `null` for
`MeshCollider`, `Terrain` and disabled colliders, and those entries are skipped.

That isn't laziness: substituting a bounding box for a wall with a door in it means the scene shows an opening while
the jelly bounces off thin air. "This collider had no effect" is far easier to diagnose than "an invisible wall appeared".
Dump State reports how many proxies actually arrived.
:::

## Space: `Simulation` or `World`

Solvers always run in their own **simulation space** (mass-spring uses bare coordinates, cloth and soft bodies use the
component's **local** space). Every proxy states which space it lives in:

```csharp
set.Add(sphere, CollisionProxySpace.Simulation);  // compared against local coordinates directly
set.Add(box,    CollisionProxySpace.World);       // particle → world → push out → back to local
```

Both sources can coexist on one object: cloth's legacy `obstacles` (local spheres, v1.1.0 semantics untouched)
alongside bridged world-space colliders.

::: tip Why world proxies take the round trip
Because the alternative is worse: folding a world collider *into* local space turns a sphere into an ellipsoid (under
non-uniform scale) and a box into an oblique parallelepiped (rotated parent plus non-uniform scale). Up to v1.2.0 cloth
did exactly that — approximating an ellipsoid with `lossyScale.magnitude / √3`, so a slightly scaled rock would have
cloth sink half a metre into it. Round-tripping the particles keeps boxes and capsules **exact**.
:::

Spheres are still an approximation: a non-uniformly scaled `SphereCollider` is mathematically an ellipsoid, and the
bridge takes the **largest axis**. Deliberately a bit too fat rather than letting particles poke through what you can see.
Documented, not hidden.

## Semi-implicit Euler and PBD need different collision handling

This is the one point in v1.3.0 you cannot guess without reading the code:

- **Cloth (PBD)** recomputes velocity as `v = (pos - prev) / h` at the end of each substep, so a positional correction
  fixes velocity **for free**.
- **Mass-spring / soft body (semi-implicit Euler)** have no such recomputation. Push a particle out without touching its
  velocity and the normal component **accumulates forever**: the position stays stuck on the floor while velocity grows
  by 9.81 m/s every second. So it eventually launches or turns into NaN.

Hence the particle path does one extra thing — **it removes the velocity component that still points into the shape**:

```csharp
Vector3 normal = correction.normalized;            // the correction *is* the outward normal
float intoSurface = Vector3.Dot(velocity, normal);
if (intoSurface < 0f) velocity -= normal * intoSurface;
```

Only the normal part is cut; the tangential part survives ⇒ objects **slide** down slopes instead of sticking.
There is no friction coefficient (not in v1).

Measured: a single particle dropped onto a plane for 1.5 s stays under 6 m/s and never sinks below `plane + skin`.
That's `MassSpring_ParticleFallingOnGroundPlaneRestsInsteadOfSinking`, and note it asserts the **velocity bound**, not
the position — because if you push positions but forget the velocity, the position still *looks* right.

## Bit-identical with the old behaviour (off by default, and off means "as if unreleased")

`collideWithSceneColliders` defaults to **false**. When off:

- no transform matrix is injected (identity), so particle coordinates never go through a `Matrix4x4` multiply;
- no proxies exist, so the collision pass is skipped entirely.

Cloth's sphere-obstacle path has its own bit-comparison test: v1.2.0's arithmetic is transcribed verbatim into the test
and compared via `BitConverter.SingleToInt32Bits` on 13 deliberately chosen samples
(inside / outside / on the surface / at the centre / hovering right by the threshold).

::: info The demo scene's ground became a flat box with a BoxCollider instead of a Plane
`GameObject.CreatePrimitive(PrimitiveType.Plane)` ships a **MeshCollider** — precisely the shape this release skips.
Keeping a Plane in the demo would produce the single most confusing failure mode there is: "the floor is clearly in the
scene, why does the jelly fall through it?" So `SoftBodyDemoTools` now builds the ground from a Cube + `BoxCollider`.
Same rule for your own project: if you want soft bodies to land, the floor needs a box / sphere / capsule collider.
:::

Measured (`Tools ▸ Physics Simulation ▸ Soft Body ▸ Dump State`, during Play):

```
SoftBodyJelly: pinned 0 | collision proxies 1
particle world y lowest -0.0100 | box ground top surface -0.0200 ⇒ lowest particle sits 0.0100 above
volume retention 0.981 | max stretch ratio 1.0893 | non-finite False
```

The lowest particle sits exactly `collisionThickness` (0.01) above the floor, with **zero** pinned particles: the whole
body free-falls, squashes 1.9% on impact and the volume constraint pops it back.

## Parameters and cost

| Field | Where | Default | Meaning |
| --- | --- | --- | --- |
| `collisionThickness` | `ClothParameters` / `MassSpringParameters` / `SoftBodyParameters` | 0.01 m | skin; scaled for world-space proxies |
| `collideWithSceneColliders` | all three behaviours | `false` | injects matrices and reads colliders only when on |
| `sceneColliders` | all three behaviours | empty | the colliders that participate |
| `updateCollidersEveryStep` | all three behaviours | `false` | turn on when colliders move or get enabled — rebuilds the proxy list each step (allocates) |

Cost is `O(particles × proxies)` per substep, pure arithmetic, allocation-free while
`updateCollidersEveryStep` stays off. v1.3.0 does **no** performance work (nor does v1.4.0, which shipped the model audit): the parallel solver is now scheduled for v1.5.0.

## Still not in this release

- `MeshCollider` / `Terrain` / arbitrary convex polytopes;
- friction, restitution, elasticity — the response is only "cut the normal component, keep the tangent";
- self-collision (cloth passing through itself, soft bodies passing through themselves);
- triangle / tetrahedron level intersection — everything here is **particle** level;
- driving or being driven by a `Rigidbody`: the jelly can press on a crate, the crate will not move;
- collision event callbacks (`OnCollisionEnter`-style).

::: warning The solver never queries the scene, so toggling a collider off does not take effect by itself
Proxies are a geometry snapshot taken at `Rebuild()` (or every step when `updateCollidersEveryStep` is on).
Flipping `Collider.enabled` at runtime needs either that flag on, or a manual `Rebuild()`.
:::
