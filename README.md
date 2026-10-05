# Physics Simulation

适用于 Unity 的质点 / 弹簧物理模拟工具包（纯 C# 可单测的逻辑层 + MonoBehaviour 驱动层 + 编辑器工具）。

## 功能

- **质点弹簧系统（Mass-Spring）** — 质点（位置 / 速度 / 质量 / `inverseMass`，0 为固定点）与弹簧（端点索引、静长、刚度 k、阻尼 c）
- **半隐式欧拉积分 + 线性阻尼** — 阻尼写成隐式除法，任意 `c·dt` 都不会让速度反号或发散；粒子级阻尼另加 `[0,1]` 钳制
- **子步（substeps）与 dt 钳制** — `Step(dt)` 内部均分子步逐步积分，默认 dt 上限 1/15 s，用稳定手段换掉「看起来会炸」的高刚度参数
- **参数校验** — 索引越界、质量 ≤ 0、`dt ≤ 0` 一律抛异常，且抛出前不改变系统状态
- **确定性** — 同参数同步数两次运行结果逐位一致（无 `Random` / 无 `Time` / 无并行）
- **场景可视化** — `OnDrawGizmos` 画质点（固定点另一种颜色、半径按质量对数缩放）与弹簧（按应变着色：压缩偏蓝、拉伸偏红）
- **编辑器工具** — `Tools > Physics Simulation > …`：一键生成演示场景 / 在当前场景重建 / 打印系统状态

## 安装

### 通过 Git URL

在 Package Manager 中选择 **Add package from git URL**：

```
https://github.com/<your-org>/com.opoi375.physics-simulation.git
```

### 通过本地路径

**Add package from disk**，选择本目录下的 `package.json`。

## 要求

- Unity 6000.5 或更高版本
- Unity Test Framework 1.7.0+（自动作为依赖安装）
- 不依赖 URP 或其他渲染管线

## 目录结构

| 目录 | 说明 |
| --- | --- |
| `Runtime/` | 运行时脚本：`MassSpring/` 纯逻辑层（质点、弹簧、积分器、系统）与 MonoBehaviour 驱动层 |
| `Editor/` | 编辑器工具：演示场景生成、当前场景重建、状态导出 |
| `Tests/Editor/` | 编辑器测试（EditMode）：积分器、弹簧力、系统行为 |
| `Documentation~/` | 文档站（VitePress 中英双语，`~` 后缀让 Unity 忽略该目录） |
| `.github/workflows/` | 文档站部署流水线 |

## Roadmap（v1 明确不做）

v1 只有质点弹簧。以下能力**不在**本版本：刚体、碰撞检测与响应、距离 / 角度刚性约束、布料与自碰撞、隐式或 XPBD 求解器、Jobs + Burst 并行、自适应时间步、软体体积约束、与 Unity 自带 `Rigidbody` / `ConfigurableJoint` 互操作、网络与回放。

公共 API 会为此预留扩展点（`IForceGenerator` / `IConstraint`），但不提前实现。

后续里程碑：

| 里程碑 | 内容 |
| --- | --- |
| M2 | 刚性距离约束（PBD / XPBD 投影，质量加权）：不会拉长的绳子、不会塌的布 |
| M3 | 碰撞（球-球 / 球-平面，位置修正 + 摩擦），粒子睡眠与休眠唤醒 |
| M4 | 布料的结构 / 剪切 / 弯曲三类弹簧 + 自碰撞剔除，网格蒙皮输出 |
| M5 | Jobs + Burst 并行求解器（首次引入 `Unity.Collections` / `Unity.Burst` 依赖） |

## 运行测试

`Window > General > Test Runner > EditMode > Physics Simulation.Editor.Tests`，或在包目录执行 Unity 批处理测试。

## 许可

MIT，见 [LICENSE.md](LICENSE.md)。版本变更记录见 [CHANGELOG.md](CHANGELOG.md)。
