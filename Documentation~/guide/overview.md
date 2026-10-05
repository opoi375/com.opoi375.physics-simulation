# 概述

`com.opoi375.physics-simulation` 是一个**质点弹簧 + 布料物理模拟工具包**，面向"想要自己写物理、但需要一层靠得住的地基"的场景。

它**不是** Unity 自带物理引擎的替代品：没有刚体动力学、没有 `Rigidbody` 互驱、碰撞只到质点级。它只解决一件事——**把一堆质点用弹簧连起来，稳定地、可复现地算下去**。绳子、链条、摆动、布料、软体都是从这一块地基长出来的；v1.3.0 起，地基上的质点还能落在场景的碰撞体上。

## 设计原则

| 原则 | 具体做法 |
| --- | --- |
| **纯逻辑与 Unity 层彻底分开** | `MassSpringSystem` / `Particle` / `Spring` 不继承 `MonoBehaviour`、不读 `Time`、不碰场景；`MassSpringBehaviour` 只做"配置 → 系统 → 每帧推进 → 画 Gizmos" |
| **确定性优先** | 无 `Random`、无 `Time`、无并行归约。相同参数 + 相同步数 → **逐位一致**的结果，因此可以写闭式解断言、可以做回放 |
| **数值行为可预期** | 阻尼写成除数 `(1 + c·dt)` 而不是减项 `v -= c·v·dt`；粒子级阻尼乘数钳到 `[0,1]`；`dt` 有上限钳制 |
| **API 面窄** | 6 个方法就能用完整套系统（`AddParticle` / `AddSpring` / `Pin` / `Step` / `ResetToInitial` / `ApplyForces`），没有"配置对象套三层"的仪式 |
| **不绑渲染管线** | 包依赖只有 `com.unity.test-framework`，URP / Built-in / 无管线都能用 |

## 目录结构

| 路径 | 内容 |
| --- | --- |
| `Runtime/MassSpring/` | 纯逻辑层（`Particle`、`Spring`、`MassSpringParameters`、`MassSpringIntegrator`、`MassSpringSystem`）+ Unity 层（`MassSpringBehaviour`、`MassSpringConfig`、`MassSpringParticleLink`） |
| `Editor/MassSpring/` | 编辑器工具（`Tools > Physics Simulation` 菜单） |
| `Tests/Editor/` | 30 个 EditMode 测试（积分器、弹簧力、系统行为、配置翻译层、演示构建器） |
| `Documentation~/` | 本文档站（VitePress，中英双语）。`~` 后缀让 Unity 忽略该目录 |

程序集：`PhysicsSimulation.Runtime` → `PhysicsSimulation.Editor` → `PhysicsSimulation.Editor.Tests`，命名空间分别是 `PhysicsSimulation`、`PhysicsSimulation.EditorTools`、`PhysicsSimulation.Editor.Tests`。

## 版本切分与 Roadmap

发版按能力切分，每一版都自带测试与文档：

| 版本 | 内容 | 状态 |
| --- | --- | --- |
| 1.0.0 | 质点弹簧：质点 / 弹簧 / 半隐式欧拉 / 隐式阻尼 / 子步 / dt 钳制 / Gizmos / 编辑器工具 / 演示场景 | ✅ |
| 1.1.0 | 布料：结构 / 剪切 / 弯曲距离约束、PBD 投影、风、球体障碍、`ClothBehaviour` 与自动网格、演示场景、36 个测试 | ✅ |
| 1.2.0 | 软体：任意网格 → 焊接质点 + 三角形边结构弹簧 + 共边对顶点弯曲弹簧 + **散度定理体积约束**（不是四面体网格，也不需要 tet 化），复用质点弹簧内核；`SoftBodyBehaviour` 四种钉法与可序列化扰动、演示场景、31 个测试 | ✅ |
| **1.3.0** | 碰撞代理：球 / OBB 盒 / 胶囊 / 半空间四种纯几何，三个求解器共用一套 `ICollisionProxy`；`Simulation` 与 `World` 两种登记空间；`ColliderProxies` 从场景 Collider 采样；开关默认关，关掉时与 v1.2.0 逐位一致；软体终于落地不穿地 | ✅ 当前版本 |
| 1.4.0 | 性能：`Jobs + Burst` 并行求解器（**可选程序集**，不装 Burst 就退回托管路径，包核心依赖保持为零） | 🚧 计划 |

### 明确不做（至少在这几个版本里）

布料与软体的自碰撞、三角形级相交、与 Unity `Rigidbody` / `ConfigurableJoint` 的互操作（软体现在能压住箱子，箱子不会动）、网络与回放、自适应时间步。
v1.3.0 的碰撞是**质点级 + primitive 解析**：球 / OBB 盒 / 胶囊 / 半空间，`MeshCollider` 与 `Terrain` 明确不支持，
也**不会**拿包围盒冒充（宁可"这个碰撞体没生效"，也不要凭空多一面看不见的墙）。响应只有"削掉法向、保留切向"，
没有摩擦与恢复系数。体积约束是梯度恢复力而不是硬约束，剧烈形变下允许约 1% 偏差；弯曲弹簧数量随三角化方式变化，
测试只断言它的不变量（与结构弹簧不重叠、无重复、闭合网格上大于 0），不断言具体数字。

扩展点以 `TODO` 注释标在 `MassSpringSystem` 顶部（`IConstraint`、`IForceGenerator`、碰撞与睡眠），**v1 不声明空接口**——避免为了"将来可能用到"先造出一堆没人实现的抽象。

## 下一步

- 想直接看到东西：[快速上手](/guide/quickstart)
- 想搞清公式与稳定性：[质点弹簧](/mass-spring/) · [布料](/cloth/)
- 想查字段：[参数参考](/reference/mass-spring-parameters)
