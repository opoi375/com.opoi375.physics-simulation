# 更新日志

## [1.0.0] - 2026-10-05

### Added
- **质点弹簧系统**：`Particle`（位置 / 速度 / 质量 / `inverseMass` / 力累积器 / 粒子阻尼 / 固定标志）、`Spring`（端点索引、原长、刚度 k、轴向阻尼 c）、`MassSpringSystem`（`AddParticle` / `AddSpring` / `Pin` / `Unpin` / `ApplyForces` / `Step` / `ResetToInitial` / `MaxSpeed` / `HasNonFiniteState`）
- **半隐式（辛）欧拉积分器**：阻尼写成除数 `(1 + c_global·dt)`，任意 `c_global·dt` 都只衰减不反号；粒子级阻尼乘数 `1 - c·dt` 钳到 `[0,1]`
- **子步与 dt 钳制**：`Step(dt)` 先把 dt 钳到 `maxDeltaTime`（默认 1/15 秒），再均分成 `substeps` 份逐步积分
- **确定性**：无 `Random` / 无 `Time` / 无并行，同参数同步数跑两次逐位一致
- **参数校验**：索引越界、质量非有限正数、`dt <= 0`、`a == b` 等一律抛 `ArgumentOutOfRangeException`，且抛出前不改动系统状态
- **Unity 层**：`MassSpringBehaviour`（Inspector 配置质点/弹簧、`FixedUpdate` 驱动、Gizmos 按应变着色、右键菜单 `Capture Current As Rest` / `Reset To Initial Layout`）、`MassSpringParticleLink`（Transform 跟随质点）、`MassSpringBuilder`（配置 → 系统的纯翻译层）
- **编辑器工具**：`Tools > Physics Simulation > Create Demo Scene / Build In Current Scene / Dump State / Build Chain Only`，静默存盘（不用会弹模态框的 `SaveCurrentModifiedScenesIfUserWantsTo`）
- **演示场景**：固定吊点 + 5 节链（k 逐节递减 `1600 → 320`）、侧偏 38° 释放、相机与方向光、地面参考线
- **30 个 EditMode 测试**：积分器闭式解、弹簧力对称性与守恒、系统确定性与稳定性、配置翻译层、演示构建器结构
- **中英双语文档站**（VitePress，`Documentation~`）+ GitHub Pages 部署工作流

### Notes
- 包依赖只有 `com.unity.test-framework`，**不依赖 URP** 或任何渲染管线
- v1 明确不做：刚体、碰撞、刚性距离约束、布料、软体、XPBD、Jobs/Burst 并行、与 `Rigidbody` 互操作。扩展点以 `TODO` 注释标在 `MassSpringSystem` 顶部
- 后续版本：1.1.0 布料、1.2.0 软体、1.3.0 Jobs + Burst 并行
