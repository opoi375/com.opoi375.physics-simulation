# Physics Simulation

适用于 Unity 的质点弹簧与布料物理模拟工具包，逻辑层可单测、结果可复现。

📚 **文档站（中英双语）**：<https://opoi375.github.io/com.opoi375.physics-simulation/>

## 功能

- **质点弹簧系统** — `Particle`（位置 / 速度 / 质量 / `inverseMass` / 力累积器）+ `Spring`（端点索引、原长、刚度 k、轴向阻尼 c）
- **半隐式欧拉 + 隐式阻尼** — 阻尼写成除数 `(1 + c·dt)`，任意 `c·dt` 都不会把速度反号；粒子级阻尼乘数钳到 `[0,1]`
- **子步与 dt 钳制** — `Step(dt)` 内部均分 `substeps` 份逐步积分，dt 默认钳到 1/15 秒，掉帧与暂停回来都不会炸
- **确定性** — 相同参数、相同步数跑两次逐位一致（无 `Random` / 无 `Time` / 无并行）
- **纯逻辑层与 Unity 层分离** — `MassSpringSystem` 不依赖 MonoBehaviour，可直接在编辑器测试里断言闭式解
- **运行时组件** — `MassSpringBehaviour` 在 `FixedUpdate` 里驱动，Inspector 配置质点与弹簧，Gizmos 画质点与按应变着色的弹簧（拉伸偏红、压缩偏蓝）
- **编辑器工具** — `Tools > Physics Simulation > …`（质点弹簧 100~103）与 `Tools > Physics Simulation > Cloth > …`（布料 110~112），全部静默存盘
- **布料模拟（v1.1.0）** — `ClothSimulation` 用 PBD / XPBD 风格距离约束求解：结构 / 剪切 / 弯曲三类边，硬度与步长解耦，`stiffness = 1` 也不炸
- **布料稳定性保险** — `maxStretchRatio` 多轮限幅、`maxDeltaTime` 钳制、球体障碍碰撞（`collisionThickness` 控制布料厚度）、`AddWindImpulse` 风
- **布料 Unity 层** — `ClothBehaviour`（局部空间模拟、四边钉住、自动从 `SphereCollider` 取障碍、Gizmos 线框）+ `ClothMeshBuilder`（顶点即质点，自动生成网格与 UV）
- **布料性能实测（托管，无 Burst）** — 32×32（1024 质点 / 5826 约束）**4.65 ms/步**；64×64（4096 质点 / 23938 约束）**20.2 ms/步**，基准用例本身就是回归门槛
- **测试** — 66 个 EditMode 测试（30 质点弹簧 + 36 布料：16 核心求解器 / 14 Unity 层 / 6 编辑器工具）

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

## 目录结构

| 目录 | 说明 |
| --- | --- |
| `Runtime/MassSpring/` | 质点弹簧纯逻辑层 + `MassSpringBehaviour` / `MassSpringParticleLink` 驱动组件 |
| `Runtime/Cloth/` | 布料求解器 `ClothSimulation` / `ClothParameters` + `ClothBehaviour` / `ClothMeshBuilder` |
| `Editor/MassSpring/`、`Editor/Cloth/` | 编辑器工具（演示场景生成、当前场景重建、状态打印） |
| `Tests/Editor/` | EditMode 测试 |
| `Documentation~/` | VitePress 中英双语文档站（Unity 忽略该目录） |
| `.github/workflows/` | 文档站部署工作流 |

## Roadmap

| 版本 | 内容 |
| --- | --- |
| **1.0.0** | 质点弹簧：质点 / 弹簧 / 半隐式欧拉 / 隐式阻尼 / 子步 / dt 钳制 / Gizmos / 编辑器工具 / 文档站 |
| **1.1.0** | **布料**：结构 / 剪切 / 弯曲三类邻居约束，PBD/XPBD 距离约束求解（硬度与步长解耦）、风、球体障碍碰撞、`ClothBehaviour` 组件、演示场景、66 个测试 |
| 1.2.0 | **软体**：一个把**任意网格**软体化的组件 —— 按网格顶点建质点、按三角形边建约束，再加四面体体积约束，直接复用质点弹簧系统 |
| 1.3.0 | **性能**：`Jobs + Burst` 并行求解器，放在**可选程序集**里（不装 Burst 自动退回托管路径，包核心依赖保持为零）+ 基准数字 |

### 明确不做（至少在本包的这几个版本里）

与 Unity 自带 `Rigidbody` / `ConfigurableJoint` 的互操作、网络与回放、自适应时间步、布料自碰撞。
碰撞方面目前只提供"质点 vs 球体障碍"的推出，不做三角形级相交。

## License

MIT，见 `LICENSE.md`。
