# 软体参数参考

命名空间 `PhysicsSimulation`（扁平，没有 `PhysicsSimulation.SoftBody` 这种子命名空间）。
`SoftBodyParameters.Validate()` 会在构造 `SoftBodySimulation` 时自动调用，非法值**立刻抛异常**，
不会悄悄给你一个数值已经溢出的模拟。

## 输入网格：`SoftBodyMeshData`

| 成员 | 说明 |
| --- | --- |
| `SoftBodyMeshData(Vector3[] vertices, int[] triangles)` | 直接给顶点与三角形索引。构造时即校验：数组非空、三角形数是 3 的倍数、索引在范围内 |
| `static FromMesh(Mesh mesh)` | 从 Unity 网格取 `vertices` / `triangles`。空网格会抛中文异常 |
| `Vertices` / `Triangles` | 只读 |
| `VertexCount` / `TriangleCount` | 顶点数与三角形数 |

## `SoftBodyParameters`

| 字段 | 默认 | 约束 | 说明 |
| --- | --- | --- | --- |
| `mass` | 1 | 有限且 `> 0` | 每个质点的质量（千克）。弹簧力与体积力都按 `1/mass` 折算加速度 |
| `gravity` | `(0, -9.81, 0)` | 有限 | 常重力加速度，局部空间 |
| `damping` | 0.5 | `>= 0` | 全局速度衰减，每小步 `v /= 1 + damping·h`。想多晃几秒就调小它 |
| `maxSpeed` | 40 | `>= 0`（0 表示不限） | 速度封顶。**最后一道防线**，正常参数下永远碰不到 |
| `springStiffness` | 1200 | 有限且 `>= 0` | 结构弹簧刚度（牛顿每米，真实弹簧系数，不是布料那套 PBD 比值）。要"软"就往下调，演示果冻用 130 |
| `springDamping` | 6 | `>= 0` | 结构弹簧的轴向阻尼，抑制来回抖 |
| `bendStiffness` | 150 | `>= 0` | 弯曲弹簧刚度（对顶点距离保持） |
| `bendDamping` | 2 | `>= 0` | 弯曲弹簧阻尼 |
| `volumeStiffness` | 4000 | `>= 0` | 体积约束刚度：`F = -k_v·(V-V₀)·∇V`。0 表示关掉体积（会瘪） |
| `volumeDamping` | 20 | `>= 0` | 体积变化率阻尼 `c_v·dV/dt`，防止充气过度来回鼓 |
| `substeps` | 4 | `>= 1` | 一个 `Step(dt)` 内切几个小步。**提高它比提高刚度划算** |
| `maxDeltaTime` | 1/15 | 有限且 `> 0` | `ClampDeltaTime(dt)` 上限，卡顿一帧也不会把软体炸掉 |
| `weldTolerance` | 1e-4 | 有限且 `> 0` | 顶点焊接容差（米）。小于它的重复顶点合并成一个质点 |
| `enableStretchLimit` | true | — | 是否做最大拉伸限幅 |
| `maxStretchRatio` | 2 | `> 1` | 结构弹簧允许的最大长度比，超限后 8 趟 Gauss-Seidel 位置投影 |

方法：`Validate()`（非法即抛）、`ClampDeltaTime(dt)`、`EffectiveSubsteps`（`substeps < 1` 时按 1 算）。

## 组件字段：`SoftBodyBehaviour`

| 字段 | 默认 | 说明 |
| --- | --- | --- |
| `sourceMesh` | null | 源网格。**永远不会被改写**，写回的是组件自己那份实例网格 |
| `parameters` | 默认值 | 上面那张表 |
| `pinMode` | `None` | `None` / `TopVertices` / `BottomVertices` / `ExplicitIndices` |
| `pinVertexIndices` | 空 | `ExplicitIndices` 时使用，按**源网格顶点下标**（内部自动过焊接映射） |
| `pinLayerThickness` | 1e-3 | Top/Bottom 判定"这一层"的高度容差（米） |
| `initialVelocity` | `zero` | 每次 `Rebuild` 施加给所有未钉住质点的初速度。**扰动必须走这里**，直接改质点位置会在 Play 重建时丢光 |
| `autoSimulate` | true | 关掉后由外部调 `Step(dt)`（定步长、回放、网络同步） |
| `generateMesh` | true | 关掉则只跑求解器，不生成实例网格 |
| `recalculateNormals` | true | 每帧重算法线。关掉省一点时间，但光照跟着旧法线走 |
| `drawGizmoWireframe` | true | Scene 视图画结构弹簧与质点（钉住的画大点） |

## 拓扑规则

- **焊接**：空间哈希（格边长 = `weldTolerance`，探测邻域 27 格，平方距离比较）把重合顶点合成一个质点。
  一个每面 2×2 细分的长方体：54 个网格顶点 ⇒ 26 个质点（8 角 + 12 棱中点 + 6 面心）。
- **结构弹簧** = 去重后的三角形边。所以三角化的长方体是 **18 根**，不是 12 条棱——面上的对角线是真实边。
- **弯曲弹簧** = 被两个三角形共用的边，其两个"对顶点"连一根；与结构弹簧去重。
  数量随三角化方式变化，因此只保证不变量：与结构弹簧不重叠、无重复、闭合网格上必然大于 0。
- **闭合判定** `IsClosed`：每条边恰好被 2 个三角形使用。开放网格体积无意义 ⇒ `Volume()` 返回 0、体积力跳过。

## 只读与诊断 API（`SoftBodySimulation`）

| 成员 | 说明 |
| --- | --- |
| `Build(SoftBodyMeshData)` | 建拓扑与系统；非法输入抛异常且不改动已有状态 |
| `IsBuilt`、`Parameters`、`System` | `System` 是底层 `MassSpringSystem`（`MaxSpeed()` 等诊断从它读） |
| `ParticleCount` / `MeshVertexCount` / `StructuralSpringCount` / `BendSpringCount` / `TriangleCount` / `IsClosed` | 规模 |
| `IndexOfVertex(int)` / `SetVertexPinned(int, bool)` | 网格顶点下标 ⇄ 质点下标 |
| `GetPosition` / `GetVelocity` / `SetVelocity` | 单质点读写。`SetVelocity` 只改速度，绝不碰位置 |
| `SetPinned` / `IsPinned` | 按质点下标钉住 |
| `CapturePositions` / `SetPositions` | 整体取/摆位。**`SetPositions` 会把速度清零** |
| `WritePositionsTo(Vector3[])` / `CaptureMeshVertices()` | 按源网格顶点顺序展开写回 |
| `GetStructuralSpring(i)` / `GetBendSpring(i)` | 返回 `SoftBodyEdge { a, b, restLength }` |
| `Volume()` / `RestVolume()` | 当前/静止有向体积（绕序朝外才为正） |
| `Step(float dt)` | 推进（内部做 `ClampDeltaTime` 与子步） |
| `ResetToInitial()` / `CaptureInitialLayout()` | 回到初始布局 / 把当前姿态设为初始布局 |
| `MaxStretchRatio()` | 所有结构弹簧的最大 `length/restLength` |
| `HasNonFiniteState()` | 任一质点出现 NaN / Infinity 即为 true |
| `CollectEdges(List<ValueTuple<Vector3,Vector3>>, bool includeBend)` | Gizmos / 调试用的线段集合（局部空间） |

## 相关页面

- [软体模拟](/soft-body/) · [布料参数参考](/reference/cloth-parameters) · [质点弹簧参数参考](/reference/mass-spring-parameters)
