# Physics Simulation

适用于 Unity 的质点弹簧（mass-spring）物理模拟工具包，逻辑层可单测、结果可复现。

📚 **文档站（中英双语）**：<https://opoi375.github.io/com.opoi375.physics-simulation/>

## 功能

- **质点弹簧系统** — `Particle`（位置 / 速度 / 质量 / `inverseMass` / 力累积器）+ `Spring`（端点索引、原长、刚度 k、轴向阻尼 c）
- **半隐式欧拉 + 隐式阻尼** — 阻尼写成除数 `(1 + c·dt)`，任意 `c·dt` 都不会把速度反号；粒子级阻尼乘数钳到 `[0,1]`
- **子步与 dt 钳制** — `Step(dt)` 内部均分 `substeps` 份逐步积分，dt 默认钳到 1/15 秒，掉帧与暂停回来都不会炸
- **确定性** — 相同参数、相同步数跑两次逐位一致（无 `Random` / 无 `Time` / 无并行）
- **纯逻辑层与 Unity 层分离** — `MassSpringSystem` 不依赖 MonoBehaviour，可直接在编辑器测试里断言闭式解
- **运行时组件** — `MassSpringBehaviour` 在 `FixedUpdate` 里驱动，Inspector 配置质点与弹簧，Gizmos 画质点与按应变着色的弹簧（拉伸偏红、压缩偏蓝）
- **编辑器工具** — `Tools > Physics Simulation > Create Demo Scene / Build In Current Scene / Dump State / Build Chain Only`
- **测试** — 30 个 EditMode 测试（积分器、弹簧力、系统行为、配置翻译层、演示构建器）

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

**Tools → Physics Simulation → Create Demo Scene** → 按 Play：一条从吊点垂下、侧偏 38° 释放的 5 节链会摆起来，上面几节几乎不形变、下面几节明显被甩长。

## 目录结构

| 目录 | 说明 |
| --- | --- |
| `Runtime/MassSpring/` | 质点弹簧纯逻辑层 + `MassSpringBehaviour` / `MassSpringParticleLink` 驱动组件 |
| `Editor/MassSpring/` | 编辑器工具（演示场景生成、当前场景重建、状态打印） |
| `Tests/Editor/` | EditMode 测试 |
| `Documentation~/` | VitePress 中英双语文档站（Unity 忽略该目录） |
| `.github/workflows/` | 文档站部署工作流 |

## Roadmap

| 版本 | 内容 |
| --- | --- |
| **1.0.0** | 质点弹簧：质点 / 弹簧 / 半隐式欧拉 / 隐式阻尼 / 子步 / dt 钳制 / Gizmos / 编辑器工具 / 文档站 |
| 1.1.0 | **布料**：结构 / 剪切 / 弯曲三类邻居约束，改用 XPBD 距离约束求解（硬度与步长解耦） |
| 1.2.0 | **软体**：一个把**任意网格**软体化的组件 —— 按网格顶点建质点、按三角形边建约束，再加四面体体积约束，直接复用质点弹簧系统 |
| 1.3.0 | **性能**：`Jobs + Burst` 并行求解器，放在**可选程序集**里（不装 Burst 自动退回托管路径，包核心依赖保持为零）+ 基准数字 |

### v1 明确不做

刚体 / 碰撞检测与响应 / 距离与角度刚性约束 / 布料与自碰撞 / 隐式或 XPBD 求解器 / Jobs+Burst 并行 / 自适应时间步 /
软体体积约束 / 与 Unity 自带 `Rigidbody`、`ConfigurableJoint` 互操作 / 网络与回放。

## License

MIT，见 `LICENSE.md`。
