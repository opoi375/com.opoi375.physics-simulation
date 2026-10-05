# 质点弹簧（Mass-Spring）

一句话：**把物体抽象成一堆有质量的点，用弹簧把它们连起来，每一步算弹簧推了多少力、把力换成速度和位移。**

绳子、链条、摆动、吊着的招牌、布料、软体，全都是这一套的不同拓扑 + 不同约束而已。

## 1. 每一步在算什么

一个 `Step(dt)` 的内部流程：

```text
span = min(dt, maxDeltaTime)          # dt 钳制：掉帧 / 暂停回来不会灌进一个巨大的时间步
h    = span / substeps                # 子步长
重复 substeps 次：
    ApplyForces()                     # 力累积器清零 → 加重力 → 加所有弹簧内力
    对每个质点积分(h)                  # 半隐式欧拉 + 隐式阻尼
```

### 受力

```text
# 重力（固定点也累加，但因为 inverseMass = 0，换成加速度就没了）
force_i = gravity * mass_i

# 每根弹簧：dir 是 a→b 的单位向量
d   = x_b - x_a
l   = |d|
dir = l > 1e-8 ? d / l : 0
F_b = -( k * (l - restLength) + c_spring * dot(v_b - v_a, dir) ) * dir
F_a = -F_b                            # 严格等大反向 ⇒ 内力不改系统总动量
```

- `k * (l - restLength)`：**胡克定律**，拉伸（`l > restLength`）时把两端往一起拉。
- `c_spring * dot(v_b - v_a, dir)`：**沿轴向的相对速度阻尼**。没有它，弹簧会永远振下去；有它，相对靠近/远离的运动会被吃掉。
- `F_a = -F_b` 是**写死的**，不是巧合：只要两端力不严格反向等值，质心就会自己漂走（测试 `Step_StretchedSpring_PullsEndsTogetherAndKeepsCenterOfMass` 就是钉这个的）。

### 积分（半隐式欧拉 + 隐式阻尼）

```text
a     = force * inverseMass
v_new = (v + a * dt) / (1 + c_global * dt)        # 隐式除法
v_new = v_new * clamp(1 - c_particle * dt, 0, 1)  # 粒子级阻尼，钳到 [0,1]
x_new = x + v_new * dt                            # 用"新"速度推进位置
```

顺序很重要：**先更新速度、再用新速度更新位置**才叫半隐式（辛）欧拉。反过来写成 `x += v*dt; v += a*dt` 会退化成显式欧拉，等效于给系统**注入**能量，弹簧会自己越摆越大。

::: tip 为什么阻尼写成除法
显式写法 `v -= c * v * dt` 在 `c * dt > 1` 时把速度**反向**、`c * dt > 2` 时直接发散。除数形式 `v / (1 + c*dt)` 无论 `c*dt` 多大都只会把速度趋近 0，符号永远不变——大阻尼、大子步、手滑填了 1e5 的阻尼系数都炸不掉。测试 `Step_GlobalDamping_DecaysSpeedMonotonicallyWithoutSignFlip` 逐帧检查了"单调变小 + 不反号"。
:::

固定点不需要特判：`inverseMass = 0` 时加速度恒为 0，速度保持原值（通常就是 0），位置一步都不动。

## 2. 自由落体的"正确答案"不是 ½gt²

半隐式欧拉在 `n` 步后的位移是**离散闭式解**：

```text
x_n = g * dt² * n * (n + 1) / 2
```

而连续解是 `½ g t²`（`t = n·dt`）。两者差 `½ g t · dt` —— 一阶误差，**步长越小差得越少，但永远不为零**。

拿 `½gt²` 去断言数值积分的结果，会把它当成 bug 去"修"，然后修出一个真的 bug。本包的测试直接断言离散闭式解（`Step_FreeFallMatchesDiscreteClosedForm`），并额外验证一阶收敛：`dt` 缩到 `1/4`，误差缩到约 `1/4`（`Step_FreeFallErrorQuartersWhenDtQuarters`）。

## 3. 稳定性：只看一个数

单个弹簧-质点系统，半隐式欧拉稳定的条件是

```text
ω * h < 2      其中 ω = sqrt(k / m_reduced)
等价于          k * h² / m < 4
```

`h` 是**子步长**（`span / substeps`），不是 `dt`。所以：

| 想变硬（k↑） | 正确做法 | 错误做法 |
| --- | --- | --- |
| ✅ | 加 `substeps`，让 `h` 变小 | ❌ 加 `globalDamping` 压住爆炸（会把运动一起吃掉，链子变成面条） |
| ✅ | 降低质点质量比（`k/m` 才是关键） | ❌ 把 `maxDeltaTime` 调大"反正会钳制" |

演示链条的最硬一节是 `k = 1600`、`m = 0.8`、`substeps = 8`、`dt = 1/60`：

```text
h = (1/60) / 8 = 0.002083 s
k * h² / m = 1600 * 4.34e-6 / 0.8 = 0.0087   ≪ 4   ✅
```

测试 `BuildChain_SubstepsKeepHardestLinkStable` 会把这个数算一遍并要求它 `< 1`——以后谁改演示参数改到边缘，测试会直接红。

::: warning 显式积分在 `k·dt²/m > 4` 时必炸
测试 `Step_HighStiffnessWithSubsteps_StaysFiniteAfterThousandSteps` 故意用 `k = 50000`、`dt = 1/15`（`k·dt²/m = 222`，远超阈值），只靠 16 个子步把它救回来，然后跑 1000 步断言"有限且不发散"。注意它断言的是**数值有界**，不是"看起来像物理"——后者不可测。
:::

## 4. 参数表

### `MassSpringParameters`

| 字段 | 单位 | 默认 | 说明 / 调参建议 |
| --- | --- | --- | --- |
| `gravity` | m/s² | `(0, -9.81, 0)` | 常规模拟重力。设零可以单独验证弹簧内力（测试里就这么干） |
| `globalDamping` | 1/s | `0` | 全局线性阻尼。想"慢下来"优先调它，一般 `0.2 ~ 2`；`0` = 无阻尼（守恒，适合回放） |
| `substeps` | 个 | `1` | 子步数。**稳定性的主要旋钮**。硬弹簧 / 短绳建议 `4 ~ 16`，成本线性增长 |
| `maxDeltaTime` | s | `1/15` | 单步 dt 上限。默认值意味着"低于 15 FPS 时模拟会慢放而不是爆炸" |
| `EffectiveSubsteps` | 个 | — | 只读：`substeps < 1` 时按 1 处理 |
| `ClampDeltaTime(dt)` | s | — | 只读：返回钳制后的时间跨度（`maxDeltaTime <= 0` 视为不钳制） |

### `Particle`

| 字段 | 单位 | 说明 |
| --- | --- | --- |
| `position` | m | 世界位置 |
| `velocity` | m/s | 速度 |
| `force` | N | **本步**的合力累积器，每次 `ApplyForces()` 清零重算，别把它当持久状态 |
| `mass` | kg | 必须为有限正数，否则 `ArgumentOutOfRangeException` |
| `inverseMass` | 1/kg | `1/mass`；`0` = 固定点。由 `Pin` / `Unpin` 维护，别手改 |
| `damping` | 1/s | 粒子级阻尼 `c_particle`，乘数 `1 - c·dt` 被钳到 `[0,1]` |
| `pinned` | — | 是否固定（与 `inverseMass == 0` 等价） |
| `InitialPosition` / `InitialVelocity` | m / m/s | 只读快照，`ResetToInitial()` 回到这里 |

### `Spring`

| 字段 | 单位 | 说明 |
| --- | --- | --- |
| `a` / `b` | 索引 | 端点质点索引，`a→b` 定义正方向；越界或 `a == b` 直接抛异常 |
| `restLength` | m | 原长。`AddSpring` 传 `<= 0` 表示自动取两端当前距离 |
| `stiffness` | N/m | 刚度 `k`。越大越硬，也越容易数值发散 |
| `damping` | N·s/m | 轴向阻尼 `c_spring` |
| `Strain(posA, posB)` | — | 只读：`(l - restLength) / restLength`，Gizmos 按它着色 |

## 5. 常见坑（都是踩过的）

| 现象 | 根因 | 处理 |
| --- | --- | --- |
| 链子一开场自己就晃 | `restLength` 手填的值 ≠ 初始实际距离 | 填 `0` 让系统自动取；或运行后用 `Capture Current As Rest` 定格 |
| 弹簧越摆越大、最后 NaN | 用了显式欧拉（先位置后速度），或 `k·h²/m > 4` | 加 `substeps`；确认积分顺序 |
| 大阻尼下速度反向、抖动 | 阻尼写成 `v -= c*v*dt` | 用除数形式 `(v + a·dt) / (1 + c·dt)`（本包已这么写） |
| 帧率越高摆得越快 | 喂了 `Time.deltaTime` | 只喂 `Time.fixedDeltaTime`，或自己按定长步推进 |
| 固定点"抖" | 每步手动把位置写回去，但速度已经攒起来了 | 用 `Pin()`（`inverseMass = 0`），让速度根本长不出来 |
| 暂停很久后回来系统炸开 | 一个巨大的 `dt` 灌进来 | `maxDeltaTime` 钳制（默认 1/15） |
| 状态对比测试"永远绿" | 用 `Vector3.ToString()` 做快照，默认只有 2 位小数，微小变化看不见 | 用 `"R"` 往返格式（本包的 `CaptureState` 就这么干） |
| 播放模式下系统被莫名复位 | 在 `OnValidate` 里重建系统 | 守卫 `if (Application.isPlaying) return;`（`MassSpringBehaviour` 已加） |

## 6. 确定性的边界

`MassSpringSystem` 保证：**同一份配置、同样的 `dt` 序列 ⇒ 逐位一致**。它不读 `Time`、不用 `Random`、不并行、不按哈希表顺序遍历（质点与弹簧都是 `List`，按插入顺序）。

不保证的：跨平台浮位级一致（`sin/cos/sqrt` 的实现差异）、以及你自己在 `Step` 外面改质点状态后的行为。要跨平台"看起来一致"，请固定 `dt` 序列（定长步）而不是依赖帧率。

## 7. 下一步

- 挂到场景里：[快速上手](/guide/quickstart)
- 可视化与诊断：[编辑器工具](/tools/)
