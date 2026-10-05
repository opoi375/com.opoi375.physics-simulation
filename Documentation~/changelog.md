# 更新日志

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
