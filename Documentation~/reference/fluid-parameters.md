# 流体参数参考

`FluidParameters`（命名空间 `PhysicsSimulation`）。字段都能在 Inspector 里改，改完调 `FluidBehaviour.Rebuild()`。
构造 `FluidSimulation` 时自动 `Validate()`，非法值**立刻抛异常**（消息是中文的），不会悄悄给一个错的模拟。

## 分辨率三兄弟（互相牵制，改一个就要看另外两个）

| 字段 | 默认 | 约束 | 说明 |
| --- | --- | --- | --- |
| `particleSpacing` | 0.05 | 有限且 `> 0` | 初始点阵间距 `d`。**粒子数按 `1/d³` 增长**，`0.05 → 0.06` 少 42% 粒子 |
| `kernelRadius` | 0.10 | 有限且 **`> particleSpacing`** | 核支撑半径 `h`。`h = 2d` 时三维点阵约 21~25（实测） 邻居；`h = d` 会被 `Validate()` 直接拒绝（核支撑里几乎没邻居，密度恒等于自身项，水散成一团点） |
| `restDensity` | 1000 | 有限且 `> 0` | 静止密度 ρ0（水 = 1000 kg/m³）。它是**约束目标**，不是软硬度旋钮 |

派生量（只读）：

- `ParticleMass` = `ρ0 · d³` —— 设计密度下单个粒子的质量。`d = 0.05` → `0.125 kg`；演示的 `d = 0.07` → `0.343 kg`。
- 粒子数 ≈ `体积 / d³`。想估算预算，先把这条乘出来。

## 求解器

| 字段 | 默认 | 约束 | 说明 |
| --- | --- | --- | --- |
| `substeps` | 2 | `>= 1` | 一个 `Step(dt)` 切几个子步。**提它比提 `solverIterations` 更划算**（邻居表每子步重建一次，代价线性；迭代是每子步多扫一遍） |
| `solverIterations` | 2 | `>= 1` | 每个子步内密度约束投影几轮。0 等于不解密度 |
| `complianceAlpha` | 0 | `>= 0` | **松弛因子**：λ 乘 `1/(1+α)`。`0` = 严格不可压；`0.1~1` 得到软/泡沫感。它不是论文里那个量纲合规度 |
| `clampTensileLambda` | true | — | 拉力钳制：`λ > 0`（"太稀，把邻居吸过来"）置零。真实液体没有这个力；关掉会让稀疏粒子互相吸成团、自由水面结膜 |
| `maxParticles` | 8192 | `>= 1` | 构建时粒子数超限**直接拒绝**（`LastBuildError` 给出实际数量），而不是让帧率崩了再查 |
| `maxDeltaTime` | 1/30 | `>= 0`（`<= 0` = 不钳制） | 单步 dt 上限。见下面 warning —— 这是演示场景"水自己炸开"的真凶 |
| `maxSpeed` | 0 | `>= 0`（`0` = 不限制） | **平流速度上限（CFL 式）**。位移护栏只兜约束修正，而平流位移 = 速度 × 子步长，原先没有上限：PBF 在稀疏/深穿透构型下能给出十几米每秒，一步跨过整块薄板。演示取 8 m/s；`Validate()` 拒绝负数 |
| `maxSpeed` | 0 | `>= 0`（`0` = 不限制） | **平流速度上限（CFL 式）**。位移护栏只兜约束修正，而平流位移 = 速度 × 子步长，原先没有上限：PBF 在稀疏/深穿透构型下能给出十几米每秒，一步跨过整块薄板。演示取 8 m/s；`Validate()` 拒绝负数 |

::: warning `maxDeltaTime` 不是性能参数，是正确性参数
进 Play 第一帧 / 卡帧 / 切后台回来，`Time.deltaTime` 可能给到秒级。子步长直接进重力积分与投影，
不钳住的话一帧就能把整池水甩飞：实测质心 `y = −1394 m`、包围盒 `162×1492×115 m`。
布料/质弹簧/软体从一开始就有这个钳制，流体在 `v1.5.0` 补齐（同一套语义）。
:::

## 耗散与观感

| 字段 | 默认 | 约束 | 说明 |
| --- | --- | --- | --- |
| `gravity` | `(0, -9.81, 0)` | 有限 | 常重力加速度（模拟空间） |
| `xsphViscosity` | 0.05 | `[0, 1]` | XSPH 速度平滑权重：`vᵢ += c·Σⱼ (m/ρⱼ)·(vⱼ−vᵢ)·Wᵢ`。`0` 无黏（水花四溅），`0.05~0.1` 是水，`>0.3` 开始像油。大于 1 会反向过冲，`Validate()` 拦住 |
| `vorticityEpsilon` | 0 | `>= 0`，有限 | 涡度约束强度，补"打旋"的观感。它要多扫一遍邻居表，**默认关**；`0.1~0.5` 起效 |
| `collisionThickness` | 0.005 | `>= 0`，有限 | 碰撞"皮毛"：粒子被推到碰撞体外多远。经验值 `0.2·d`。太小会抖，太大会在墙边留空隙 |

## 分母里的 ε（容易误解的一条）

`ComplianceEpsilon = (complianceAlpha · h)³ · ρ0`，下限 `1e-12`。

::: tip 它只是防除零，不是软度来源
软度走 `1/(1+α)` 的松弛。α = 0 时 ε 是一个远小于典型 `Σ|∇C|²`（`1e5` 量级）的值，
只挡"孤立粒子分母为零"这一种情况。想调软硬度，动 `complianceAlpha`，别动 ε。
:::

## 类常量（不在 Inspector 里）

| 名称 | 值 | 说明 |
| --- | --- | --- |
| `FluidSimulation.MaxCorrectionPerIterationFactor` | 0.25 | 位移护栏：单轮迭代每粒子最多移动 `0.25·h`。可调（`<= 0` = 关闭）以便复现调参扫描；实测 `0.02~0.25` 稳定段很宽（4 秒后 `v_max` 0.83~1.02 m/s），所以它只是保险丝，不是主参数 |

### 内侧盒子容器（v1.5.0）

| 字段 | 默认 | 说明 |
| --- | --- | --- |
| `enableBoxContainer` | `false` | 把水关进一个轴对齐（可带旋转）的长方体容器。**这是 `FluidBehaviour` 的序列化字段，不是运行时手动 `Collisions.Add`** —— 手动加的代理在 `Rebuild()` 之后会消失（进 Play 就重建，实测踩过），容器必须跟着组件走 |
| `containerCenter` | `Vector3.zero` | 容器中心（世界空间） |
| `containerHalfSize` | `Vector3.zero` | 容器半尺寸（米），三个分量必须为正，否则 `Validate` 报错、容器不生效 |

演示水箱就是这么来的：六块板只负责画，`ApplyTankContainer` 把内空尺寸写进上面三个字段，
`Dump State` 里 `碰撞代理 1` 就是它。

## 水面渲染参数（挂在 `FluidBehaviour` 上，不在 `FluidParameters` 里）

水面是**观感层**：它只读粒子位置，不参与密度约束，所以开不开它模拟结果逐位一致。

| 字段 | 默认 | 说明 |
| --- | --- | --- |
| `renderMode` | `Particles` | `Particles` 一堆实例化小球（v1.5.0 原本的观感）/ `Surface` 连续等值面 / `Both` 两个都提交 |
| `surfaceCellSize` | 0 | 等值面体素边长。**0 表示取 `particleSpacing`**；演示用 `0.75 × particleSpacing`（比间距小才不像积木） |
| `surfaceIsoLevel` | 0 | 阈值。**0 表示用 `FluidSurface.DefaultIsoLevel = 0.5`**：场被归一化成"静止水体内部 ≈ 1"，0.5 就是水面 |
| `surfaceRefreshEveryNFrames` | 1 | 几帧重建一次水面。演示取 2 —— 一次重建的代价高于若干次约束投影，先节流再谈优化 |
| `surfaceMaxCells` | 0 | 格子预算。**0 表示用 `FluidSurface.DefaultMaxCells = 262144`**；超了自动**放大体素**（`grid.Min` 与覆盖范围不动，只放大不缩小） |
| `surfaceColor` | 半透明蓝 | 水面颜色（alpha 必须小于 1，否则看着像蓝色塑料；`FluidSurfaceMaterial.IsTranslucent` 就是断言这一条） |

硬上限：单张水面网格最多 `FluidSurface.MaxMeshVertices = 65000` 个顶点（引擎的 `Mesh.triangles` 默认 ushort 索引 65535），超了**抛异常**而不是给你一张破面。

只读观察口：`SurfaceMesh` / `SurfaceMaterial` / `SurfaceRevision`（重建了几次）/ `SurfaceTriangleCount` / `ParticleBatchCount`。

## 水面渲染参数（挂在 `FluidBehaviour` 上，不在 `FluidParameters` 里）

水面是**观感层**：它只读粒子位置，不参与密度约束，所以开不开它模拟结果逐位一致。

| 字段 | 默认 | 说明 |
| --- | --- | --- |
| `renderMode` | `Particles` | `Particles` 一堆实例化小球（v1.5.0 原本的观感）/ `Surface` 连续等值面 / `Both` 两个都提交 |
| `surfaceCellSize` | 0 | 等值面体素边长。**0 表示取 `particleSpacing`**；演示用 `0.75 × particleSpacing`（比间距小才不像积木） |
| `surfaceIsoLevel` | 0 | 阈值。**0 表示用 `FluidSurface.DefaultIsoLevel = 0.5`**：场被归一化成"静止水体内部 ≈ 1"，0.5 就是水面 |
| `surfaceRefreshEveryNFrames` | 1 | 几帧重建一次水面。演示取 2 —— 一次重建的代价高于若干次约束投影，先节流再谈优化 |
| `surfaceMaxCells` | 0 | 格子预算。**0 表示用 `FluidSurface.DefaultMaxCells = 262144`**；超了自动**放大体素**（`grid.Min` 与覆盖范围不动，只放大不缩小） |
| `surfaceColor` | 半透明蓝 | 水面颜色（alpha 必须小于 1，否则看着像蓝色塑料；`FluidSurfaceMaterial.IsTranslucent` 就是断言这一条） |

硬上限：单张水面网格最多 `FluidSurface.MaxMeshVertices = 65000` 个顶点（引擎的 `Mesh.triangles` 默认 ushort 索引 65535），超了**抛异常**而不是给你一张破面。

只读观察口：`SurfaceMesh` / `SurfaceMaterial` / `SurfaceRevision`（重建了几次）/ `SurfaceTriangleCount` / `ParticleBatchCount`。

## 只读诊断 API

| 成员 | 含义 | 用来判断 |
| --- | --- | --- |
| `ParticleCount` / `ParticleMass` / `TotalMass()` | 粒子数与总质量 | 质量守恒（投影不该改总质量） |
| `GetDensity(i)` / `ComputeDensity(i)` | 当前密度 ρᵢ | `ρ/ρ0` 落在 `0.85~1.05` 才算"是水"；`> 1.6` 压成饼，`< 0.6` 散成云 |
| `GetLambda(i)` | 密度约束乘子 | 符号：负 = 压缩推开，正 = 拉伸吸引（钳制开时恒 ≤ 0） |
| `AverageNeighborDegree` / `MaxNeighborDegree` / `NeighborDegree(i)` / `NeighborAt(i, slot)` | 邻居度与 CSR 邻居表 | `h = 2d` 的三维点阵应有 21~25（实测）；只有个位数说明水已经散开 |
| `Bounds()` / `CenterOfMass()` | 包围盒与质心 | 漏水的直接证据（质心跑到地板以下） |
| `TotalKineticEnergy()` / `AverageVorticity()` / `MaxVorticity()` | 动能与涡量 | 稳定后动能应趋于小量；单调上涨说明能量在往里灌 |
| `HasNonFiniteState()` | 是否出现 NaN/Inf | 发散的硬判据 |
| `Positions` / `GetPosition(i)` / `GetVelocity(i)` | 状态读写 | 自建渲染或外部耦合 |
| `Collisions` | `CollisionSet` 代理列表 | 加/删碰撞体，不需要重建粒子 |
| `SetSimulationToWorld(m)` | 局部 ↔ 世界矩阵 | 组件桥接用，手写求解器时保持默认单位阵 |

## 相关页面

- [流体模拟](/fluid/) —— 求解流程、核函数、四道保险丝、粒子与水面两种渲染、演示配置
- [场景碰撞](/collision/) —— 解析代理与 `CollisionSet`
- [布料参数参考](/reference/cloth-parameters) —— 同为 PBD 家族的参数手感对照
