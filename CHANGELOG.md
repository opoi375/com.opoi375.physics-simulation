# Changelog

## [1.6.0] - 2026-10-08

### Added
- **Multi-body particle interaction** under `Runtime/Interaction/`: `IParticleBody` (a body that can offer its particles as a sphere cloud *and* be pushed by others), `ParticleCloudProxy` (uniform hash grid over that cloud, refresh stamped by `CloudVersion` rather than `Time.frameCount`, so an EditMode loop and a Play frame behave identically) and `ParticleInteractionGroup` (explicit membership - no members means released behaviour is bit-identical). All three solvers implement `IParticleBody`, so soft↔soft, cloth↔cloth and mass-spring↔anything need no per-type branch.
- **`cloudRadiusFactor` on all three behaviours** (soft body and cloth default 0.5 = neighbouring spheres exactly touch; mass-spring defaults to 1.0 because its `particleRadiusBase` was already an absolute radius). The radius is additionally capped by the body's rest size, because a coarse mesh otherwise inflates the cloud into a balloon larger than the body itself.
- **Collision-proxy ownership**: `CollisionEntry.Owner`, `CollisionSet.Add(proxy, space, owner)` and `ClearWorldSpaceOwnedBy(owner)`. `ColliderProxies.RefreshInto` now clears only its own entries, which is what lets interaction proxies survive the per-frame scene-collider refresh.
- **Interaction demo tools (130~132)** and `Assets/Scenes/ParticleInteractionDemo.unity`: two jellies plus a scarf in one group, with `Dump State` printing the nearest particle distance so "did they actually separate" is a number rather than a screenshot opinion.
- **`SoftBodyParameters` is now `[Serializable]`**. `ClothParameters`, `FluidParameters` and `MassSpringConfig` always were; the soft body was the only one missing, and Unity does not draw a non-serializable class field at all - so all 16 soft-body mechanics knobs were invisible in the Inspector and there was no entry point for tuning firmness.
- **46 new EditMode tests** (36 interaction + 10 contact/radius/serialisability), **316 passing** in the package (270 at v1.5.0 + 46); 722 passed / 2 skipped project-wide.

### Fixed
- **`parameters.gravity` and `parameters.damping` were silently ignored after `Rebuild()`** (since v1.2.0). `Build()` copied them once into the inner `MassSpringSystem` while every other field is read live, so changing gravity on a built body did nothing and reported no error. They are now synced at the top of `Step()`. This was surfaced by the `[Serializable]` fix: the same knob in the Inspector and in code had different effects.
- **Pinned particles were moved by collision proxies.** `CollisionPass.ResolvePositions` now takes the per-particle `inverseMass` array and skips immovable particles, so a pinned cloth corner acts as a static obstacle instead of being shoved by whatever touches it.
- **Cloud radius exploded on coarse meshes** (a 3x3 grid produced spheres bigger than the body, so two clouds could never resolve their own overlap). Capped by `cbrt(restVolume) x 0.25`.

- **Pinned particles were still integrated.** `MassSpringIntegrator` only zeroed the acceleration (`force * inverseMass`), then ran `position += velocity * dt` unconditionally. A pinned particle that carried a velocity kept drifting at constant speed (measured: 1 m/s pinned point moved 0.1667 m in 10 steps; pinning an already-moving particle added another 0.50 m). `Pin()` now clears the velocity and the integrator early-returns for `inverseMass <= 0`. This also makes the comment in `SoftBodySimulation` ("the solver zeroes pinned velocities every step") true — it was not.
- **`FluidBehaviour.ResetToInitialLayout()` dropped every collision proxy, including the tank container.** It created a new `FluidSimulation` (and therefore a new `CollisionSet`) and only re-attached the scene-collider bridge, so `BoxContainerProxy` vanished — the exact v1.5.0 failure (0 proxies, water free-falling to y = -92, kinetic energy 16904), half-fixed: `Rebuild` was repaired, `Reset` was not. Both now go through one `WireSimulation()`.
- **`MassSpringBehaviour` runtime parameters were frozen at `Rebuild()`.** `gravity`, `globalDamping`, `substeps` and `maxDeltaTime` were copied into `MassSpringSystem` once, while `OnValidate` deliberately skips rebuilding in Play mode — so editing them during Play did nothing and reported nothing. They are now synced at the top of `Step()` (same fix `SoftBodySimulation` received earlier in this release).
- **Fluid `substeps` / `solverIterations` had no runtime lower bound.** `Validate()` only runs at construction/Rebuild, and the Inspector exposes the same parameter object, so `substeps = 0` produced `subStep = dt / 0 = +Infinity` and a loop that never entered: the water froze with no exception and no log. Both now go through `EffectiveSubsteps` / `EffectiveSolverIterations` (floor 1), matching the other three solvers.
- **Cloth inverse mass was half-frozen and could become infinity.** The constructor baked `1 / mass` while `SetPinned` re-read the live `Parameters.mass`. Changing mass at runtime gave one cloth two different masses, and `mass = 0` wrote `+Infinity`, which the projection turned into NaN within three steps. The free-particle inverse mass is now cached once and reused; `mass` is classified as rebuild-only.
- **Fluid sphere volume was centred on `transform.localPosition`.** The simulation runs in the component's own local space, so the centre must be the origin; a component at (3,0,0) generated its sphere at local (3,0,0) — world (6,0,0). Box and dam-break always generated at the origin, only the sphere branch was wrong.
- **`ClampStretch` spun forever on a fully pinned over-stretched edge.** `clean = false` was set before the `a.pinned && b.pinned` early-out, so every substep ran all 8 passes without changing anything and nothing reported that the edge was irreparable. The skip now happens first and the count is exposed as `IrreparableStretchEdges`.
- **`ClothBehaviour` destroyed its mesh with `Destroy` in EditMode**, which logs an Error that the Unity test framework counts as a failure — the exact reason `FluidBehaviour` had a `DisposeAsset` helper. Both now use the shared `RuntimeAssets.Dispose`; `FluidBehaviour` also finally disposes `_surfaceMaterial`, which it created but never released.

### Documentation corrections
- v1.5.0 release notes claimed "particles live in **world space**". They do not: the simulation runs in the component's local space and `localToWorld` is applied at render time (the component's own doc comment says so). Historical entries are left as written; this note is the correction.
- `FluidVolume` never had a **cylinder** sampler and the solver never had **double-density (s_corr) correction** — both were claimed in README and in the v1.5.0 notes. README now lists box / sphere / dam-break and the tensile clamp + compliance term that actually exist.
- README claimed "**no surface reconstruction**" two paragraphs after describing the marching-tetrahedra surface. It is `FluidRenderMode.Particles` that has no surface mesh; `Surface` mode rebuilds an isosurface every frame.
- Cloth clamping was documented as "up to **32** passes"; the loop is **16**. `ClothParameters.Validate()` was documented as rejecting bad `substeps` / `iterations` / `maxDeltaTime`; it did not check any of them, and now does.
- Test count corrected from 270 to 344. Counting note: the test bridge's namespace-level filter returns a stale subset (316) — the authoritative number comes from enumerating every class, which is what the release was verified with.

### Notes
- **Granular contact is a deliberate, measured limitation.** Contact is tested as *point against the other body's spheres*, so the trigger distance is `other radius + skin`, not `r_A + r_B`. Two surfaces must interpenetrate by up to about half a particle spacing before anything reacts; at `cloudRadiusFactor = 0.25` with spacing 0.2 the clouds are see-through and **no contact is detected at all** (nearest distance frozen at 0.080 m for 360 steps, velocity exactly 0). Denser meshes are the fix: spacing 0.228 -> 0.114 halves the visible gap at contact.
- **Equal-mass bodies do not stack; light-on-heavy does.** Across four stiffness recipes every equal-mass pair ends side by side on the floor (final nearest distance 0.58-0.91 m), because sequential projection gives neither body priority and the escape direction has a sideways component. The demo's scarf *is* supported by a jelly, because the scarf's particles are the only ones that can move.
- **A per-query correction cap was implemented, measured, and reverted.** Deep initial interpenetration (0.18 m ~ one particle spacing) produces a one-time recoil: total translation 4.61 m at factor 0.5, decaying from 1.03 to 0.18 m per 60 steps, never divergent. Clamping each query to 25% of `need` changed it by 4.61 -> 4.15 m and made factor 0.75 *worse* (4.53 -> 5.65 m) - the energy is stored in the springs and the volume constraint, not in how far one projection moves a point. The mechanism was removed rather than shipped as unproven API, and the demo now starts 0.06 m apart (inside the 0.105 m trigger distance, so contact still happens without the recoil).
- **Firmness recipe, measured** (0.6 m box jelly onto a floor, 420 steps, centroid height): `volumeStiffness` 4000 -> 40000 lifts the body from 0.02 m to 0.16 m at subdivisions 6, and from 0.11 m to 0.30 m at subdivisions 3. `volumeStiffness` / `volumeDamping` / `damping` / `substeps` / `maxSpeed` are read live; `springStiffness`, `bendStiffness` and `mass` are baked into `Spring` (`stiffness` is `readonly`) and need a `Rebuild()`. Raising stiffness without raising `substeps` diverges to NaN - explicit integration stability is bought almost entirely there.


## [1.5.0] - 2026-10-06

### Added
- **PBF fluid solver** under `Runtime/Fluid/`: `FluidKernel` (poly6 / spiky gradient / viscosity laplacian, with normalisation pinned by **analytic integral** tests), `FluidNeighborSearch` (uniform hash grid + CSR table, bit-deterministic rebuild), `FluidVolume` (box / sphere / cylinder / dam-break sampling with budget-truncation reporting), `FluidSimulation` (density-constraint projection, double-density correction, tensile clamping, XSPH viscosity, vorticity confinement, substepping and dt clamping; collision reuses the v1.3.0 `CollisionSet`)
- **`FluidBehaviour`**: world↔local conversion (particles live in **world space** — there is no velocity re-derivation after projection, so local space would add a g every frame), automatic Collider bridging, and **`Graphics.DrawMeshInstanced` rendering** (no 100k-triangle mesh; one shared `Mesh`, one shared `Material`)
- **Fluid editor tools (130~132)**: `Create Fluid Demo Scene` / `Build Fluid In Current Scene` / `Dump State`; the demo scene is a **sealed tank with a dam-break column**
- **`DemoSceneSave`**: the single save path for every demo tool — aborts with a clear error on untitled scenes instead of opening a dialog — guarded by a repository-wide source scan test
- **`BoxContainerProxy`** (inside-out box): points inside are returned bit-identical, points outside are pinned **per axis** back to the nearest inner wall (minimum-displacement projection onto a convex set). It is the only `ICollisionProxy` that deliberately inverts the "outside means unchanged" contract, and the reason is measured - see the leak entry below
- **`enableBoxContainer` / `containerCenter` / `containerHalfSize` on `FluidBehaviour`**: the container is re-added inside `Rebuild()`, because a proxy attached by hand is dropped when entering Play
- **Four documentation pages (zh + en)**: the fluid module page (PBF pipeline, kernel normalisation, the three fuses, rendering and materials, the tank-seam lesson, measured costs) and the fluid parameter reference
- **68 new EditMode tests** (9 kernel + 8 neighbour search + 6 volume shapes + 24 solver + 10 Unity layer + 9 demo tools + 2 save path), plus one `maxDeltaTime` case added for cloth; **270 passing** in total (170 at v1.4.0 + 68 fluid/save-path + 1 cloth dt case + 1 soft body scaling benchmark + 19 surface + 2 tank seal + 6 container proxy + 2 container layer + 1 camera framing)

### Fixed
- **Wrong kernel normalisation**: poly6 used h⁷ (should be h⁹) and the spiky gradient / viscosity laplacian used 45/(πh⁷) (should be 45/(πh⁶)). Consequence: a resting block reported a density ratio of 7.1 instead of ~1.0 and pancaked on frame one — all three integrals now have analytic tests
- **`_stamp` was not cleared when rebuilding the neighbour table**: last round's neighbours were read as this round's, so viscosity operated on a stale neighbour set
- **Wrong scale and relaxation form in the PBF projection**: the displacement was missing the mass factor, λ's denominator was missing the sum, and velocity was not recomputed as `(x − x_prev)/dt` after projection — roughly 420,000× too much movement per iteration
- **`FluidParameters.Validate()` branch order** silently discarded out-of-range inputs
- **The fluid had no `maxDeltaTime`** (cloth always did): the second-scale `Time.deltaTime` of the first Play-mode frame threw the entire tank 1,651 m — visually, "the tank exploded". All four solvers now share the same semantics
- **The demo tank had a seam that acted as a one-way trapdoor**: the box proxy pushed particles out under the wall through the wall/floor gap and, with no floor on the outside, they free-falled through the camera (`v ≈ √(2gh)` means leaking, not blowing up). Walls now reach into the floor slab and overlap in plan, pinned by a test
- **The water column touched the walls**: tangent to three walls and the floor at once, so several axes were equally shallow and the tie-break ejected a particle on frame one. Now 0.08 m of clearance per side
- **Eye-level camera plus a pool too shallow to be a pool**: the "empty box" screenshot was simply the front wall filling the frame, and the water spread into one layer. The camera now looks down and the pool is at least four particle rows deep, both constants pinned by tests
- **A closed six-plate tank still leaked (corner conveyor)**: one `BoxCollisionProxy` per plate left 120 of 1540 particles outside after 300 steps (squeezed sideways past the floor edge, then free-fall: `(-0.635, -0.529, -0.964)`, 3.3 -> 4.1 m/s). Six collider-only backstops stopped the escape but parked **150 particles inside the wall slabs, 0.486 m deep**, independent of `dt`. The plates are now visual only and one `BoxContainerProxy` holds the water: 0 outside in 300 steps, 12 proxies -> 1
- **The demo water column was born inside a wall** (560 of 1540 particles): `DamBreak` centres the body in depth while the offset assumed the minimum corner
- **Hand-added collision proxies vanished on entering Play** (`OnEnable -> Rebuild()` recreates the `CollisionSet`): the pool fell to `y = -92` with kinetic energy 16904
- **`EditorSceneManager.SaveOpenScenes()` opens the native Save Scene dialog when an open scene has no path**, dead-locking the main thread when reached from a menu API (the menu appeared to hang for 300 s). All call sites now go through `DemoSceneSave`

### Notes
- **Burst moved to v1.6.0**: this release shipped fluids, not parallelism. The fluid runs on the managed solver — measured with `d = 0.05 / h = 0.1` fixed: 13.3~21.2 ms/step at 1,000 particles and 64.7~97.7 ms/step at 4,096 (the same case varied 1.6x between runs, hence ratio gates of 4.59~4.88 instead of absolute milliseconds); the demo scale of 1,456 particles (25.1 average neighbours) lands between those rows at roughly 20~60 ms/step, which will not fit 2 substeps x 2 iterations into one 60 fps frame
- **Benchmark gates changed shape**: the soft body absolute `8 ms` gate had been pierced by identical code on a loaded machine (9.4 / 9.5 ms, red) — it measured how idle the machine is, not whether the solver regressed. Fixed in this release: the two fluid benchmarks assert a *size ratio* only (measured 4.59~5.75 against a gate of 12), soft body keeps a 20 ms hard ceiling and gains `Benchmark_SoftBodyCostScalesNearLinearlyWithParticleCount` (162 vs 642 particles, ratio measured 5.99~6.33). The cloth 64×64 33 ms gate measured 32.36 ms on a loaded run — 0.6 ms of margin, a known brittle point whose conversion to a ratio is scheduled for v1.6.0
- Rendering has two modes: **particles** and an **isosurface water sheet** (marching tetrahedra, not marching cubes, no UF kernel blur); still no surface tension, XSPH is the only viscosity knob
- The container is **one box** (optionally rotated): the inner surface is a hard boundary, so you cannot model a leak with it, and stacking several containers reintroduces the per-proxy adjudication problem above
- Still no self-collision (cloth, soft body and fluid alike); scale stays bounded by the managed O(n·k) cost, which is why the demo budget is 1,500 particles
- `MaxCorrectionPerIterationFactor` is a **mutable static rail**, not a tuning parameter: move it only for diagnostics

## [1.4.0] - 2026-10-05

### Added
- **模型审计** `Runtime/SoftBody/SoftBodyMeshAudit.cs`：`SoftBodyMeshAudit.Audit(data, parameters, steps, addGround)` 用**真实** `SoftBodySimulation` 跑固定 1/60 步长、落地在世界空间 `PlaneCollisionProxy` 地面上，产出 `SoftBodyAuditResult`（焊接比、质点/弹簧/三角形数、闭合性、有向静止体积、体积保持率、最大拉伸、最低质点世界 y、每步耗时、判定）。**对脏输入永不抛异常** —— 构建失败记成 `BuildFailed` 并把原因留在 `BuildError`
- **七档判定** `SoftBodyAuditVerdict`：`Healthy` / `OpenMesh` / `InvertedWinding` / `DegenerateVolume` / `DegenerateWeld` / `Unstable` / `BuildFailed`。后两档是**扫描真实资产后补的**：绕序整体朝内的网格旧分类报 `Healthy`（两个负体积一除保持率就是正的），零厚度壳与"真的塌成 0"都印成 `0.000` 分不开
- **编辑器扫描工具** `Tools/Physics Simulation/Soft Body/Audit Selected Meshes (123)` 与 `Audit Mesh Assets In Folder (124)`：一次扫全项目，输出「默认参数」+「按包围盒对角线放大的推荐参数」两张 Markdown 表和一份好转/变差对照，落盘 `Logs/SoftBodyMeshAudit.md`；预算线（单次 120 个、顶点 4000）+ `StringComparer.Ordinal` 排序保证两次扫描逐字一致
- **`RecommendedParameters(diagonal)` / `Diagonal(data)` / `BuildComparison()`**：把"这类模型该怎么调"变成可执行的对照，而不是文档里一句空话
- **文档两页（中英）**：[从任意网格到质点](/soft-body/mesh-to-particles) 讲清焊接**真的是均匀空间哈希不是八叉树**（格边长 = 容差、27 邻居探查、精确平方距离裁决、为什么不用八叉树、容差是半径、构建拒绝清单）；[真实模型实测](/soft-body/model-audit) 给出本项目 **104 个网格**的逐行判定与成本
- **40 个新 EditMode 测试**（22 审计 + 15 扫描工具 + 3 焊接边界），全量 **170 通过**；整个工程 578 项 576 通过 2 跳过

### Fixed
- **扫描报告的合计行把新判定塞进 `BuildFailed`**：旧 `switch` 用 `default: failed++` 兜底，导致 104 行报告里 9 行（`InvertedWinding` 4 + `DegenerateVolume` 3 + 真 `BuildFailed` 2）被一起报成"构建失败 9"。现在七档各自计数，并加了**各档之和必须等于总数**的对账异常 —— 以后再加枚举值会走 `default` 计入"未知判定"而不是污染某一档
- **`note` 列对每个健康行都印 `(unnamed)`**：`Sanitize` 的空串兜底本来是给模型名字用的，串到了 note 上。现在健康行是明确的 `-`，`Note()` 无话可说时返回空串
- **`BuildFailed` 行没有原因**：报告只有表格，而表格里不带 `BuildError` 就等于让读的人去猜。现在 note 列带上原因原文（并做单行化，异常消息里的换行与竖线不会撕坏表格）

### Notes
- **本版不做性能工作**（并行求解器改排 v1.5.0）。审计的 `ms/step` 只是"这个量级能不能用"的判据
- `RecommendedParameters` 是**诊断建议，不是新默认值**：实测救回 6 个（树、草、蘑菇、单胞盒子），同时打坏 1 个（`Tunnel_Mesh` 1.323 → 0.208 被过冲压扁）。默认值保持保守
- 审计只跑 90 步、只测单体、地面是世界空间半空间；`Healthy` 只代表数值健康，不代表观感好

## [1.3.0] - 2026-10-05

### Added
- **碰撞代理** `Runtime/Collision/`：`ICollisionProxy.PushOut(point, skin)` 一个方法就是全部契约，四种纯解析几何 —— `SphereCollisionProxy` / `BoxCollisionProxy`（OBB，任意旋转）/ `CapsuleCollisionProxy`（退化轴自动降为球）/ `PlaneCollisionProxy`（**半空间**）
- **三条契约**（改语义会先红）：体外原样返回逐位不变；体内沿穿透最浅方向顶出；方向未定义时用固定备用轴 `+Y` 且**绝不产生 NaN**；`skin` 为负按 0 处理
- **`CollisionSet`** 按插入顺序遍历 —— 顺序本身就是确定性契约的一部分；`CollisionPass` 提供位置版（布料，PBD 只修位置）与速度版（质点弹簧/软体）两种推进方式
- **两种登记空间**：`CollisionProxySpace.Simulation`（与求解器局部坐标直接比算）与 `World`（质点变到世界→推出→变回局部）。同一物体上两种可以共存，布料 v1.1.0 的 `obstacles` 因此原封不动继续有效
- **`ColliderProxies`** 桥接层：从场景 `Collider` 采样成代理。`MeshCollider` / `Terrain` / 被禁用的碰撞体返回 `null` 并跳过，**不拿包围盒冒充**
- **三个求解器统一接入**：`MassSpringSystem`、`ClothSimulation`、`SoftBodySimulation` 都有 `Collisions` / `HasColliders` / `SetSimulationToWorld(Matrix4x4)`；`MassSpringParameters` 与 `SoftBodyParameters` 新增 `collisionThickness`（默认 0.01 m）
- **Unity 层开关**：三个 Behaviour 新增 `collideWithSceneColliders`（默认 `false`）、`sceneColliders`、`updateCollidersEveryStep`（默认 `false`）
- **软体演示场景改造**：地面从 `CreatePrimitive(Plane)`（自带 `MeshCollider`，正好是不支持的那种）换成 Cube + `BoxCollider`；果冻**一个质点都不钉**，整块自由落体砸在地面上
- **Dump State 新增诊断**：碰撞代理个数、`collideWithSceneColliders` 与列表填充数、质点世界 y 最低/最高值、以及"最低质点高出盒子上表面多少"
- **33 个新 EditMode 测试**（21 几何契约 + 12 三求解器/桥接集成），含一条把 v1.2.0 布料球障碍算术逐字抄进测试、对 13 个样本比 `BitConverter.SingleToInt32Bits` 的逐位对照；全量 **130 通过**

### Changed
- **布料球障碍迁移到 `CollisionSet`**：`AddSphereObstacle` 现在登记一条 `Simulation` 空间的 `SphereCollisionProxy`，`ObstacleCount` 变成代理总数。算术逐位不变，`RemoveObstacle` 从 O(n) 扫描变成按索引删除
- **布料障碍物不再拿 `lossyScale.magnitude / √3` 近似椭球**：改用质点往返世界空间的路径，盒/胶囊是精确的，只有非均匀缩放的球仍取最大外接轴（宁厚勿漏，写在文档里）
- **软体演示果冻从"钉底面 + 横向初速"改为"不钉 + 落体"**：`initialVelocity` 仍然保留为可序列化字段，因为它同时充当"模拟到底在跑没有"的探针，而 `PinMode` 现在能表达"一块整果冻自由落地"这个最有说服力的用例

### Performance
- 本版**不做性能工作**（并行求解器仍在 v1.4.0）。碰撞开销为 `O(质点数 × 代理数)` 每子步，纯算术、`updateCollidersEveryStep` 关闭时无分配；代理数为 0 时整段跳过 ⇒ 默认关=零开销
- 软体 642 质点基准在同机不同负载下复测为 **4.399 ms/步**（均值 4.603），v1.2.0 低负载时记录为 2.745 / 2.814 ms/步 —— 代码路径未变（无代理时碰撞被跳过），差异全部来自机器负载；基准数字对负载敏感，回归门槛只断言"能跑完且不超一帧预算"

### Fixed
- **质点弹簧/软体碰撞会累积法向速度**：半隐式欧拉没有 PBD 那种 `v = (pos - prev)/h` 回算，只顶位置会让速度每秒加一个 g，跑久了飞走或变 NaN。现在削掉穿入方向的速度分量、保留切向（所以物体会沿斜面滑而不粘）
- **`PlaneCollisionProxy` 按"穿透深度"实现会被高速穿地**：改为半空间语义 —— 不管穿多深，一律顶回面上 + `skin`
- **软体被地面吞掉**：`CreatePrimitive(Plane)` 的 MeshCollider 不支持，"场景里明明有地面还是穿过去"是最难查的一类现象。现在 `TryFrom` 跳过它，Dump State 直接报出实际收到几个代理

## [1.2.0] - 2026-10-05

### Added
- **软体模拟（Soft Body）**：`SoftBodyMeshData` / `SoftBodyParameters` / `SoftBodySimulation` / `SoftBodyEdge` —— 把**任意网格**变成会形变又保体积的果冻，不需要你手搭拓扑
- **拓扑自己长出来**：空间哈希焊接重合顶点（格边长 = `weldTolerance`，27 格邻域 + 平方距离）→ 三角形边去重成结构弹簧 → 共边三角形的对顶点成弯曲弹簧 → 每条边恰好 2 个三角形即 `IsClosed`
- **体积约束**：散度定理有向体积 `V = Σ (1/6)·x0·(x1×x2)`，配梯度恢复力 `F_i = -(k_v·(V-V₀) + c_v·dV/dt)·∇_iV`。它是力而不是位置投影，所以和弹簧共用同一套显式积分与子步；开放网格体积无意义 ⇒ 自动跳过
- **复用 v1.0.0 内核**：软体不写第二套求解器，直接驱动 `MassSpringSystem.ApplyForces()` + 半隐式欧拉，体积力作为额外外力注入
- **稳定性保险**：`maxStretchRatio` 超限后 8 趟 Gauss-Seidel 位置投影、`maxSpeed` 速度封顶（默认 40 m/s，硬弹簧炸穿时的最后一道防线）、`maxDeltaTime` 钳制 + `substeps`
- **Unity 层**：`SoftBodyBehaviour`（局部空间模拟、实例网格拓扑照抄源网格、`SoftBodyPinMode` 四种钉法、可序列化的 `initialVelocity`、`generateMesh` / `recalculateNormals` / `drawGizmoWireframe`、失败写 `LastBuildError` 不抛异常）
- **编辑器工具**：`Tools > Physics Simulation > Soft Body > Create Soft Body Demo Scene / Build In Current Scene / Dump State`（优先级 120~122，静默存盘）；`Dump State` 会遍历场景内每个软体并打印 `enabled` / `autoSimulate` / `IsBuilt` / 体积保持率 / 最大拉伸比 / 最大速度
- **演示场景** `Assets/Scenes/SoftBodyDemo.unity`：底面钉住被推一把的蓝色果冻 + 顶面钉住荡摆的橙色袋子；Play 模式实测形变可见（帧间变化 5.2%）、体积保持率 0.998~1.000、无非有限值
- **共享演示材质工具** `DemoMaterialHelper`：反射取当前管线 `defaultMaterial` 作模板（布料/软体共用一套逻辑，避免 `HideFlags.DontSave` 那个坑被重复踩）
- **31 个软体 EditMode 测试**（13 核心求解器 + 11 Unity 层 + 7 编辑器工具），全量 97 个测试通过
- **性能基准**：642 质点（1920 结构 + 1920 弯曲弹簧、1280 三角形、子步 4）最佳 **2.745 ms/步**、均值 2.814 ms/步；同机复测布料 32×32 最佳 3.299 ms/步、64×64 最佳 19.447 ms/步
- **中英双语文档**：`/soft-body/` 模块指南与 `/reference/soft-body-parameters` 参数参考

### Fixed
- 程序化长方体网格的三角形绕序 4 面朝外、2 面朝内 ⇒ 散度定理体积只剩解析值的 **1/3**（新增的 `BuildBoxMesh_WithSingleSubdivision_IsAWatertightBox` 抓到）
- 反射读管线 `defaultMaterial` 时漏传 `BindingFlags.Instance` ⇒ 永远取不到模板材质，演示材质悄悄退化成"猜着色器名字"
- 实例网格先设 `triangles` 再设 `vertices` ⇒ Unity 直接拒绝这批索引（`Failed setting triangles... VertexCount: 0`），网格没有面、法线全零
- 演示扰动只写在运行时质点位置上 ⇒ Play 时 `Awake → Rebuild` 用源网格重建模拟，扰动一瞬间丢光、画面静止 ⇒ 改为可序列化的 `initialVelocity`
- `SoftBodySimulation.BendSpringCount` 被当方法调用（CS1955）、`Object` 在 `using System;` 下二义（CS0104）等编译期问题

### Notes
- 包依赖仍然只有 `com.unity.test-framework`，核心不依赖 URP / Unity.Mathematics / Burst
- 已知限制：软体**不与场景求交**（没有碰撞体，会直接穿过地面，所以演示用"钉住"而不是"落地"）；无自碰撞；体积是梯度恢复力不是硬约束（剧烈形变下允许约 1% 偏差）；弯曲弹簧数量随三角化方式变化，因此只对它断言不变量而非具体数值
- 计划：1.3.0 Jobs + Burst 并行求解（可选程序集，托管实现保留为回退，本版本基准即对照基线）

## [1.1.0] - 2026-10-05

### Added
- **布料模拟（Cloth）**：`ClothParameters` / `ClothSimulation` / `DistanceConstraint` / `ClothConstraintType`，把 `columns × rows` 网格变成会下垂、会飘、会被球顶起的布
- **位置约束求解（PBD / XPBD 风格距离约束）**：每小步 预测 → 投影（结构 → 剪切 → 弯曲）→ 拉伸限幅 → 碰撞推出 → 由位置差回写速度；刚度用 `alpha = 1 - (1-k)^(1/iterations)` 折算，`stiffness = 1` 也不会炸
- **三类约束**：结构边（横竖相邻）、剪切边（每格两条对角线）、弯曲边（隔一个的同向邻居），可分别关断与调刚度
- **稳定性保险**：`maxStretchRatio` 多轮（≤32）限幅扫描、`maxDeltaTime` 钳制、`collisionThickness` 碰撞厚度
- **风与障碍**：`AddWindImpulse(acc, dt)`、`AddSphereObstacle` / `ClearObstacles` / `ObstacleCount`（局部空间球体）
- **确定性**：固定遍历顺序、无随机、无 `Time`、无并行 ⇒ 同参数同步数逐位一致（有测试锁死）
- **Unity 层**：`ClothBehaviour`（局部空间模拟、`ClothPinEdges` 四边钉住、自动从 `Transform`/`SphereCollider` 取障碍、`Rebuild` / `Step` / `ResetToInitialLayout` / `CaptureCurrentAsInitial` / `CollectStructuralEdges`、Gizmos 线框）+ `ClothMeshBuilder`（顶点=质点、每格两三角、UV 覆盖 `[0,1]²`、>65k 顶点自动 32 位索引）
- **编辑器工具**：`Tools > Physics Simulation > Cloth > Create Cloth Demo Scene / Build In Current Scene / Dump State`（优先级 110~112，静默存盘）
- **演示场景** `Assets/Scenes/ClothDemo.unity`：20×14 网格、顶边钉住、朝相机吹的风、会被裹住的障碍物球；Play 模式实测布料下缘在球两侧翻起
- **36 个布料 EditMode 测试**（16 核心求解器 + 14 Unity 层 + 6 编辑器工具），全量 66 个测试通过
- **性能基准用例（含回归门槛）**：托管求解器 32×32（1024 质点 / 5826 约束）最佳 **4.65 ms/步**、均值 4.95 ms/步；64×64（4096 质点 / 23938 约束）最佳 **20.2 ms/步**、均值 20.5 ms/步
- **中英双语文档**：`/cloth/` 模块指南与 `/reference/cloth-parameters` 参数参考

### Notes
- 包依赖仍然只有 `com.unity.test-framework`，核心不依赖 URP / Unity.Mathematics / Burst
- 已知限制：无自碰撞、无三角形-三角形相交；风是"给质点加加速度"的近似，不是面积压力模型；拉伸限幅是 Gauss-Seidel 收敛近似（极端参数允许约 2% 残留超限）；碰撞只有球体
- 计划：1.2.0 软体（把任意网格软体化的组件）· 1.3.0 Jobs + Burst 并行求解（目标把 64×64 压进单帧）

## [1.0.0] - 2026-10-05

### Added
- **质点弹簧系统**：`Particle`（位置 / 速度 / 质量 / `inverseMass` / 力累积器 / 粒子阻尼 / 固定标志）、`Spring`（端点索引、原长、刚度 k、轴向阻尼 c）、`MassSpringSystem`（`AddParticle` / `AddSpring` / `Pin` / `Unpin` / `ApplyForces` / `Step` / `ResetToInitial` / `MaxSpeed` / `HasNonFiniteState`）
- **半隐式（辛）欧拉积分器**：阻尼写成除数 `(1 + c_global·dt)`，任意 `c_global·dt` 都只衰减、绝不反号；粒子级阻尼乘数 `1 - c·dt` 钳到 `[0,1]`
- **子步与 dt 钳制**：`Step(dt)` 先把 dt 钳到 `maxDeltaTime`（默认 1/15 秒），再均分成 `substeps` 份逐步积分
- **确定性**：无 `Random` / 无 `Time` / 无并行；同参数、同步数跑两次结果逐位一致
- **参数校验**：索引越界、质量非有限正数、`dt <= 0`、弹簧两端相同 → 一律 `ArgumentOutOfRangeException`，且抛出前不改动系统状态
- **Unity 层**：`MassSpringBehaviour`（Inspector 配置、`FixedUpdate` 驱动、Gizmos 按应变着色、右键菜单 `Capture Current As Rest` / `Reset To Initial Layout`）、`MassSpringParticleLink`、`MassSpringBuilder`（配置 → 系统的纯翻译层）
- **编辑器工具**：`Tools > Physics Simulation > Create Demo Scene / Build In Current Scene / Dump State / Build Chain Only`，静默存盘（不用会弹模态框、把编辑器主线程堵死的 `SaveCurrentModifiedScenesIfUserWantsTo`）
- **演示场景**：固定吊点 + 5 节链（k 逐节递减 `1600 → 320`）、侧偏 38° 释放、相机与方向光、地面参考线
- **30 个 EditMode 测试**：积分器闭式解与收敛阶、弹簧力对称性与守恒、系统确定性与稳定性、配置翻译层、演示构建器结构
- **中英双语文档站**（VitePress，`Documentation~`）+ GitHub Pages 部署工作流

### Notes
- 包依赖只有 `com.unity.test-framework`，**不依赖 URP** 或任何渲染管线
- v1 明确不做：刚体、碰撞、刚性距离约束、布料、软体、XPBD、Jobs/Burst 并行、与 `Rigidbody` 互操作；扩展点以 `TODO` 注释标在 `MassSpringSystem` 顶部
- 计划：1.1.0 布料（XPBD 距离约束）· 1.2.0 软体（把任意网格软体化的组件）· 1.3.0 Jobs + Burst 并行求解
