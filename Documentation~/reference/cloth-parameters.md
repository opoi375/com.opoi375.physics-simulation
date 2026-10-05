# 布料参数参考

`ClothParameters`（命名空间 `PhysicsSimulation`）。所有字段都可以在 Inspector 里改，改完记得 `ClothBehaviour.Rebuild()`。
`Validate()` 会在构造 `ClothSimulation` 时自动调用，非法值**立刻抛异常**，不会悄悄给出一个错的模拟。

## 网格

| 字段 | 默认 | 约束 | 说明 |
| --- | --- | --- | --- |
| `columns` | 16 | `>= 2` | 列数（局部 `+X` 方向） |
| `rows` | 16 | `>= 2` | 行数（局部 `-Y` 方向） |
| `spacing` | 0.1 | 有限且 `> 0` | 相邻质点静止距离，单位米。整块布尺寸 = `(columns-1)·spacing × (rows-1)·spacing` |
| `mass` | 1 | 有限且 `> 0` | 单个质点质量。距离约束用 `inverseMass` 分配修正量，所以均匀质量下 `mass` 只影响与外力求解器混用时的表现 |

## 力与积分

| 字段 | 默认 | 约束 | 说明 |
| --- | --- | --- | --- |
| `gravity` | `(0, -9.81, 0)` | 有限 | 常重力加速度 |
| `damping` | 0.05 | `>= 0` | 隐式阻尼，每小步速度除以 `1 + damping·h`。`0` 完全无损，`0.05~0.4` 是常见布料区间 |
| `substeps` | 4 | `>= 1` | 一个 `Step(dt)` 内切几个小步。**提高它比提高 `iterations` 更稳也更划算** |
| `iterations` | 2 | `>= 1` | 每个小步内约束投影几轮 |
| `maxDeltaTime` | 1/15 | 有限且 `> 0` | `ClampDeltaTime(dt)` 的上限，防止卡顿一帧把布炸掉 |

## 刚度（PBD 语义，不是弹簧系数）

投影比例按 `alpha = 1 - (1 - stiffness)^(1/iterations)` 折算，
所以 `stiffness = 1` 表示"每轮直接投到位"，`stiffness = 0` 表示"完全不修正"。

| 字段 | 默认 | 范围 | 说明 |
| --- | --- | --- | --- |
| `structuralStiffness` | 1 | `[0, 1]` | 结构边（横竖相邻）。低于 1 会看到明显拉长 |
| `shearStiffness` | 0.6 | `[0, 1]` | 剪切边（对角线）。管抗斜向错切 |
| `bendStiffness` | 0.2 | `[0, 1]` | 弯曲边（隔一个）。丝绸 0.02~0.08，棉布 0.1~0.25，帆布/皮革 0.3+ |
| `enableShear` | true | — | 关掉后不生成剪切约束（省约 1/3 约束量） |
| `enableBend` | true | — | 关掉后不生成弯曲约束 |

## 稳定性保险

| 字段 | 默认 | 约束 | 说明 |
| --- | --- | --- | --- |
| `maxStretchRatio` | 2 | 有限且 `>= 1` | 单条约束最大 `长度/静止长度`。每小步投影后做多轮限幅扫描（最多 32 轮） |
| `collisionThickness` | 0.01 | 有限且 `>= 0` | 质点与碰撞代理的最小间距（相当于布的"厚度"）。v1.3.0 起对桥接进来的场景 Collider 同样生效 |

::: warning 两点诚实的说明
1. **拉伸限幅是收敛近似**：Gauss-Seidel 式逐条修正，改一条会顶歪另一条，所以极端参数下允许约 2% 的残留超限；测试里断言的是 `MaxStretchRatio() <= maxStretchRatio × 1.02`。
2. **碰撞在限幅之后**：`Step` 的顺序是 预测 → 投影 → 限幅 → 碰撞推出 → 回写速度。"质点一定在球外"是硬保证，代价是推出瞬间可能短暂超过拉伸上限。
:::

## 只读诊断 API

| 成员 | 说明 |
| --- | --- |
| `ClothSimulation.ParticleCount` / `Columns` / `Rows` | 网格规模 |
| `ClothSimulation.ConstraintCount` / `Constraints` | 约束数量与列表（结构 → 剪切 → 弯曲 连续排布） |
| `IndexOf(col, row)` | 行列 → 质点下标（行优先 `row * columns + col`），越界抛异常 |
| `GetPosition(i)` / `GetVelocity(i)` / `CapturePositions()` | 读状态 |
| `SetPinned(i, bool)` / `IsPinned(i)` | 钉住 |
| `AddWindImpulse(acc, dt)` | 给所有自由质点加一帧风加速度 |
| `AddSphereObstacle(center, radius)` / `ClearObstacles()` / `ObstacleCount` | 球体障碍（局部空间） |
| `MaxStretchRatio()` | 当前最大 `长度/静止长度`，`1.0` = 完全没拉伸 |
| `HasNonFiniteState()` | 是否出现 NaN / Infinity，用来做冒烟断言 |
| `ResetToInitial()` / `CaptureInitialLayout()` | 回到初始布局 / 把当前布局设为初始 |

## 约束数量公式

设列数 `c`、行数 `r`：

```text
structural = r·(c-1) + c·(r-1)
shear      = 2·(c-1)·(r-1)
bend       = r·(c-2) + c·(r-2)
```

例：`64 × 64` ⇒ 结构 8064 + 剪切 7938 + 弯曲 7936 = **23938** 条约束。

## 相关页面

- [布料模拟](/cloth/)
- [质点弹簧参数参考](/reference/mass-spring-parameters)
