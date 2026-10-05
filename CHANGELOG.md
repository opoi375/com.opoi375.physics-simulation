# Changelog

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
