# Physics Simulation

适用于 Unity 的质点弹簧 / 布料 / 软体物理模拟工具包，支持场景碰撞，逻辑层可单测、结果可复现。

📚 **文档站（中英双语）**：<https://opoi375.github.io/com.opoi375.physics-simulation/>

## 功能

- **质点弹簧系统** — `Particle`（位置 / 速度 / 质量 / `inverseMass` / 力累积器）+ `Spring`（端点索引、原长、刚度 k、轴向阻尼 c）
- **半隐式欧拉 + 隐式阻尼** — 阻尼写成除数 `(1 + c·dt)`，任意 `c·dt` 都不会把速度反号；粒子级阻尼乘数钳到 `[0,1]`
- **子步与 dt 钳制** — `Step(dt)` 内部均分 `substeps` 份逐步积分，dt 默认钳到 1/15 秒，掉帧与暂停回来都不会炸
- **确定性** — 相同参数、相同步数跑两次逐位一致（无 `Random` / 无 `Time` / 无并行）
- **纯逻辑层与 Unity 层分离** — `MassSpringSystem` 不依赖 MonoBehaviour，可直接在编辑器测试里断言闭式解
- **运行时组件** — `MassSpringBehaviour` 在 `FixedUpdate` 里驱动，Inspector 配置质点与弹簧，Gizmos 画质点与按应变着色的弹簧（拉伸偏红、压缩偏蓝）
- **编辑器工具** — `Tools > Physics Simulation > …`（质点弹簧 100~103）、`… > Cloth > …`（布料 110~112）、`… > Soft Body > …`（软体 120~122），全部静默存盘
- **布料模拟（v1.1.0）** — `ClothSimulation` 用 PBD / XPBD 风格距离约束求解：结构 / 剪切 / 弯曲三类边，硬度与步长解耦，`stiffness = 1` 也不炸
- **布料稳定性保险** — `maxStretchRatio` 多轮限幅、`maxDeltaTime` 钳制、球体障碍碰撞（`collisionThickness` 控制布料厚度）、`AddWindImpulse` 风
- **布料 Unity 层** — `ClothBehaviour`（局部空间模拟、四边钉住、自动从 `SphereCollider` 取障碍、Gizmos 线框）+ `ClothMeshBuilder`（顶点即质点，自动生成网格与 UV）
- **布料性能实测（托管，无 Burst）** — 32×32（1024 质点 / 5826 约束）**4.65 ms/步**；64×64（4096 质点 / 23938 约束）**20.2 ms/步**，基准用例本身就是回归门槛
- **软体模拟（v1.2.0）** — `SoftBodySimulation` 把**任意网格**软体化：空间哈希焊接顶点成质点、三角形边成结构弹簧、共边对顶点成弯曲弹簧，再加一条散度定理体积约束 `F = -(k_v·(V-V₀) + c_v·dV/dt)·∇V`，被压扁会自己鼓回来
- **软体复用质点弹簧内核** — 不写第二套求解器，体积力作为外力注入 `MassSpringSystem`，所以确定性、子步、dt 钳制这些性质一并继承
- **软体 Unity 层** — `SoftBodyBehaviour`（局部空间模拟、实例网格拓扑照抄源网格、四种钉法 `SoftBodyPinMode`、可序列化的 `initialVelocity`、构建失败写 `LastBuildError` 不抛异常）
- **软体性能实测（托管，无 Burst）** — 642 质点（1920 结构 + 1920 弯曲弹簧、1280 三角形、子步 4）**2.745 ms/步**
- **碰撞代理（v1.3.0）** — 三个求解器共用一套 `ICollisionProxy`：球 / OBB 盒 / 胶囊 / **半空间**平面，纯解析最近点，不查 `PhysicsScene`；`ColliderProxies` 负责从场景 `Collider` 采样成代理，`Simulation` 与 `World` 两种登记空间可以在同一个物体上共存
- **半空间语义** — 平面没有"体内多深"的概念：穿到地面以下 3 米也一律顶回面上 + `skin`，软体不会被子地面吞掉；`MeshCollider` / `Terrain` 明确不支持，也**不拿包围盒冒充**（`TryFrom` 返回 `null`）
- **两种积分器要分开处理碰撞** — 布料是 PBD，位置被修正后速度自动跟着修正；质点弹簧与软体是半隐式欧拉，没有这种回算，所以只顶位置会让法向速度一路累积 ⇒ 同时削掉穿入方向的速度分量（切向保留，所以会沿斜面滑而不粘）
- **默认关、关了逐位一致** — `collideWithSceneColliders` 默认 `false`：不注入变换矩阵、不生成代理，于是质点坐标压根不进 `Matrix4x4`；布料的球障碍算术另有逐位对照测试（把 v1.2.0 那段抄进测试，13 个样本比 `SingleToInt32Bits`）
- **测试** — 170 个 EditMode 测试（30 质点弹簧 + 36 布料 + 34 软体 + 33 碰撞 + 22 模型审计 + 15 扫描工具）
- **模型审计（v1.4.0）** — `SoftBodyMeshAudit` 用**真实求解器**跑 90 步、落地在世界空间地面上，给任意网格出判定：闭合 / 体积保持 / 翻面 / 零厚度壳 / 退化 / 构建失败，附焊接比与每步耗时；`永不抛异常`，脏输入也照样成行
- **编辑器扫描** — `Tools/Physics Simulation/Soft Body/Audit Mesh Assets In Folder (124)` 一次扫全项目，输出"默认参数 + 按尺寸放大的推荐参数"两张 Markdown 表与好转/变差对照（预算线：单次 120 个、顶点 4000）

## 安装

### 通过 Git URL

在 Package Manager 中选择 **Add package from git URL**：

```
https://github.com/opoi375/com.opoi375.physics-simulation.git
```

### 通过本地路径

**Add package from disk**，选择本目录下的 `package.json`。

## 要求

- Unity 6000.5 或更高版本
- Unity Test Framework 1.7.0+（自动作为依赖安装）
- 不依赖 URP / 任何渲染管线

## 十秒上手

- **质点弹簧**：**Tools → Physics Simulation → Create Demo Scene** → Play：一条从吊点垂下、侧偏 38° 释放的 5 节链会摆起来，上硬下软。
- **布料**：**Tools → Physics Simulation → Cloth → Create Cloth Demo Scene** → Play：一面 20×14 的红旗朝相机鼓起，并被障碍物球顶出裹球的褶皱。
- **软体**：**Tools → Physics Simulation → Soft Body → Create Soft Body Demo Scene** → Play：蓝色果冻**一个质点都不钉**，自由落体砸在地面盒子上压扁再弹回来（实测最低质点停在地面上表面之上正好 0.01 m = `collisionThickness`，体积保持率 0.981），橙色袋子像钟摆一样荡。画面不动就用 **Dump State**，它会报出碰撞代理个数、以及质点世界 y 最低值与地面上表面的差值。

## 目录结构

| 目录 | 说明 |
| --- | --- |
| `Runtime/MassSpring/` | 质点弹簧纯逻辑层 + `MassSpringBehaviour` / `MassSpringParticleLink` 驱动组件 |
| `Runtime/Cloth/` | 布料求解器 `ClothSimulation` / `ClothParameters` + `ClothBehaviour` / `ClothMeshBuilder` |
| `Runtime/SoftBody/` | 软体拓扑提取与求解 `SoftBodySimulation` / `SoftBodyMeshData` / `SoftBodyParameters` + `SoftBodyBehaviour`；v1.4.0 起还有 `SoftBodyMeshAudit`（拿真实求解器给任意网格打判定） |
| `Runtime/Collision/` | 碰撞代理 `ICollisionProxy` + 四种几何、`CollisionSet`（按插入顺序，顺序本身就是确定性契约）、`ColliderProxies`（从 `Collider` 采样） |
| `Editor/MassSpring/`、`Editor/Cloth/`、`Editor/SoftBody/` | 编辑器工具（演示场景生成、当前场景重建、状态打印），`Editor/DemoMaterialHelper.cs` 是共用的管线默认材质工具 |
| `Tests/Editor/` | EditMode 测试 |
| `Documentation~/` | VitePress 中英双语文档站（Unity 忽略该目录） |
| `.github/workflows/` | 文档站部署工作流 |

## Roadmap

| 版本 | 内容 |
| --- | --- |
| **1.0.0** | 质点弹簧：质点 / 弹簧 / 半隐式欧拉 / 隐式阻尼 / 子步 / dt 钳制 / Gizmos / 编辑器工具 / 文档站 |
| **1.1.0** | **布料**：结构 / 剪切 / 弯曲三类邻居约束，PBD/XPBD 距离约束求解（硬度与步长解耦）、风、球体障碍碰撞、`ClothBehaviour` 组件、演示场景、36 个测试 |
| 1.2.0 | **软体**：任意网格 → 焊接质点 + 三角形边结构弹簧 + 共边对顶点弯曲弹簧 + 散度定理体积约束，复用质点弹簧内核；`SoftBodyBehaviour` 四种钉法与可序列化扰动、演示场景、31 个测试 |
| **1.3.0** | **碰撞**：`ICollisionProxy`（球 / OBB 盒 / 胶囊 / 半空间）三求解器共用、`Simulation` 与 `World` 两种登记空间、`ColliderProxies` 桥接场景 Collider、软体终于落地；默认关且关掉时与 v1.2.0 逐位一致；33 个新测试，全量 130 |
| **1.4.0** | **模型审计（当前版本）**：`SoftBodyMeshAudit` 用真实求解器跑 90 步给每个网格出判定（Healthy / OpenMesh / InvertedWinding / DegenerateVolume / DegenerateWeld / Unstable / BuildFailed）、"焊接到底怎么做的"原理页、项目 104 个真实网格的实测表；+40 个测试，全量 170 |
| 1.5.0 | **性能**：`Jobs + Burst` 并行求解器，放在**可选程序集**里（不装 Burst 自动退回托管路径，包核心依赖保持为零）+ 基准数字 |

### 明确不做（至少在本包的这几个版本里）

与 Unity 自带 `Rigidbody` / `ConfigurableJoint` 的互操作（软体能压住箱子，箱子不会动）、网络与回放、自适应时间步、
自碰撞（布料与软体都没有）。v1.3.0 的碰撞是**质点级 + primitive 解析**：球 / OBB 盒 / 胶囊 / 半空间，
不做三角形级相交；`MeshCollider` 与 `Terrain` 明确不支持，也**不拿包围盒冒充**。
响应只有"削掉法向、保留切向"，没有摩擦系数与恢复系数。
体积约束是梯度恢复力而不是硬约束，剧烈形变下允许约 1% 偏差。

## License

MIT，见 `LICENSE.md`。
