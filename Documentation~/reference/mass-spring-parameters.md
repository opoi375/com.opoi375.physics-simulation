# 参数参考

本页是**字段速查表**。原理与调参思路在 [质点弹簧](/mass-spring/)。

## `MassSpringBehaviour`（Inspector）

挂在 GameObject 上的驱动组件（`Add Component → Physics Simulation / Mass Spring Behaviour`）。

### 积分参数

| 字段 | 类型 | 默认 | 单位 | 说明 |
| --- | --- | --- | --- | --- |
| `gravity` | Vector3 | `(0, -9.81, 0)` | m/s² | 模拟重力。设零可单独看弹簧行为 |
| `globalDamping` | float | `0.5` | 1/s | 全局线性阻尼，写成除数 `(1 + c·dt)`，不会反号 |
| `substeps` | int | `8` | 个 | 一个 `FixedUpdate` 均分成几份积分。稳定性主要靠它，成本线性上升 |
| `maxDeltaTime` | float | `1/15` | s | 单步 dt 上限（钳制） |

### 质点列表 `particles`

| 字段 | 类型 | 默认 | 说明 |
| --- | --- | --- | --- |
| `position` | Vector3 | — | 初始世界位置，也是 `Reset To Initial Layout` 回到的位置 |
| `mass` | float | `1` | 千克，必须有限正数 |
| `pinned` | bool | `false` | 固定点（`inverseMass` 归零） |
| `initialVelocity` | Vector3 | `zero` | 初速度 |
| `damping` | float | `0` | 粒子级阻尼 `c_particle`（1/s），乘数钳到 `[0,1]` |

### 弹簧列表 `springs`

| 字段 | 类型 | 默认 | 说明 |
| --- | --- | --- | --- |
| `a` | int | `0` | 端点 A 的质点索引（链式建模里它是"上面那一节"） |
| `b` | int | `0` | 端点 B 的质点索引，必须 `!= a` 且在范围内 |
| `stiffness` | float | `200` | 刚度 k（N/m） |
| `damping` | float | `1` | 轴向阻尼 c（N·s/m） |
| `restLength` | float | `0` | **填 0 或负数 = 自动取两端初始距离** |

### Gizmos

| 字段 | 类型 | 默认 | 说明 |
| --- | --- | --- | --- |
| `groundReferenceSize` | float | `0` | 地面参考线半边长（米），0 = 不画。画在世界 Y=0 处，沿 X / Z 各一条 |
| `particleRadiusBase` | float | `0.06` | 质点线框球基准半径；实际半径 `= base * (1 + ln(1 + 4m))`，钳到 `[0.01, 2]` |

 gizmo 配色：

| 元素 | 颜色 |
| --- | --- |
| 活动质点 | 青色线框球 |
| 固定质点 | 琥珀色线框球 |
| 弹簧（原长附近） | 淡绿 |
| 弹簧被拉伸 | 向红过渡（`|应变| * 2` 到顶） |
| 弹簧被压缩 | 向蓝过渡 |
| 地面参考线 | 半透明灰 |

### 运行时只读属性

| 成员 | 类型 | 说明 |
| --- | --- | --- |
| `System` | `MassSpringSystem` | 当前构建出的系统；配置非法时为 `null` |
| `IsBuilt` | bool | `System != null` |
| `LastBuildError` | string | 最近一次构建失败的原因（`Rebuild()` 返回 false 时写入） |

### 方法 / 右键菜单

| 成员 | 说明 |
| --- | --- |
| `Rebuild()` | 按当前配置重建系统。返回 `false` 表示配置非法（原因见 `LastBuildError`），**不抛异常** |
| `Capture Current As Rest` | 右键菜单：把**当前模拟出来的位置/速度/弹簧长度**写回配置，作为新的初始布局 |
| `Reset To Initial Layout` | 右键菜单：调用 `System.ResetToInitial()` 回到初始布局 |

生命周期：`Awake` / `OnEnable` 建系统；编辑期 `OnValidate` 改参数即重建（播放模式下**绝不**重建）；`FixedUpdate` 调 `Step(Time.fixedDeltaTime)`。

## `MassSpringParticleLink`

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `target` | `MassSpringBehaviour` | 留空则自动取父对象上的 `MassSpringBehaviour` |
| `particleIndex` | int | 绑定的质点索引，越界时静默跳过（不抛，避免可视化脚本打断模拟） |
| `offset` | Vector3 | 相对质点位置的偏移（米） |

`LateUpdate` 里同步 `transform.position`。纯可视化，删掉它模拟结果完全不变。

## `MassSpringSystem`（纯逻辑）

| 成员 | 签名 | 说明 |
| --- | --- | --- |
| `Parameters` | `MassSpringParameters` | 系统参数（可直接改字段） |
| `Particles` | `IReadOnlyList<Particle>` | 质点只读视图（元素本身可变） |
| `Springs` | `IReadOnlyList<Spring>` | 弹簧只读视图（元素不可变） |
| `AddParticle` | `int AddParticle(Vector3 position, float mass, bool pinned = false, float damping = 0f)` | 返回索引；质量非有限正数 / 位置含 NaN·Inf / 阻尼为负 → `ArgumentOutOfRangeException` |
| `AddSpring` | `int AddSpring(int a, int b, float restLength, float stiffness, float damping)` | 返回索引；`restLength <= 0` 自动取当前距离；索引越界 / `a == b` / 参数非法 → `ArgumentOutOfRangeException` |
| `Pin` / `Unpin` | `void Pin(int index)` | 钉死 / 解开（`inverseMass` 在 `0` 与 `1/mass` 间切换）；越界抛异常 |
| `ApplyForces` | `void ApplyForces()` | 力累积器清零 → 重力 → 弹簧内力 |
| `Step` | `void Step(float deltaTime)` | 钳制 dt → 分子步 → 每子步 `ApplyForces()` + 积分；`dt <= 0` 或 `NaN`/`Inf` → `ArgumentOutOfRangeException` |
| `ResetToInitial` | `void ResetToInitial()` | 位置/速度/固定状态/粒子阻尼复位，力累积器清零 |
| `MaxSpeed` | `float MaxSpeed()` | 当前最大速度大小（诊断用） |
| `HasNonFiniteState` | `bool HasNonFiniteState()` | 是否出现 NaN / Infinity（诊断用） |

## `Particle` / `Spring`

见 [质点弹簧 §4](/mass-spring/#_4-参数表)。要点：`force` 是**每步重算的累积器**，`inverseMass` 由 `Pin`/`Unpin` 维护，`Spring` 的字段全是 `readonly`（要改参数请重建）。

## 演示链条的默认参数

`Tools → Physics Simulation → Create Demo Scene` 建出来的东西：

| 项 | 值 |
| --- | --- |
| 质点 | 1 个固定吊点（`y = 3`，`m = 1`）+ 5 个链节（间距 `0.45 m`，`m = 0.8`，末端 `m = 1.2`） |
| 弹簧 | 5 根，`k = 1600 → 1100 → 750 → 500 → 320`（上硬下软），`c = 1.5`，`restLength` 自动 |
| 积分 | `gravity = (0,-9.81,0)`、`globalDamping = 0.6`、`substeps = 8`、`maxDeltaTime = 1/15` |
| 初始姿态 | 整条链侧偏 `38°` 释放（垂直挂着静止 = 什么都看不见） |
| 场景 | `Main Camera` 对准链条中心（FOV 42）、一盏 42°/-28° 的方向光（软阴影）、`groundReferenceSize = 5` 的地面参考线 |
| 可视化 | 每个活动质点一个 `MassSpringParticleLink` + 球体（末端球更大），吊点用一个方块表示 |

最硬一节的稳定性核算：`k·h²/m = 1600 × (1/480)² / 0.8 ≈ 0.0087 ≪ 4`。
