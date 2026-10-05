# 软体模拟（Soft Body）

质点弹簧给的是"链子"，布料给的是"一片"，软体要的是"一块"：一个任意网格，掉在地上会瘪、被推开会晃、
松手又鼓回原来的样子。v1.2.0 做的就是这件事——**把任何一份 Mesh 变成会形变又保体积的果冻**。

核心只有一句：从网格里自己长出拓扑。顶点焊接成质点，三角形边长成结构弹簧，共边三角形的对顶点长成
弯曲弹簧，闭合三角形的有向体积长成体积约束。你给它一个网格，它自己知道该连哪。

## 1. 五分钟上手

```csharp
using PhysicsSimulation;
using UnityEngine;

// 从任意网格造一个软体（MeshAsset 可以是导入的 FBX、Unity 图元、或程序化网格）
var data = SoftBodyMeshData.FromMesh(MeshAsset);
var soft = new SoftBodySimulation(new SoftBodyParameters
{
    mass = 0.9f,
    springStiffness = 260f,
    volumeStiffness = 1400f,
    substeps = 4
});
soft.Build(data);

// 想让它挂住而不是整块掉下去：钉住最高的一层
for (int i = 0; i < soft.ParticleCount; i++)
    if (soft.GetPosition(i).y > 0.45f) soft.SetPinned(i, true);

// 每帧推进（单位是米/秒，重力已经写在参数里）
soft.Step(Time.fixedDeltaTime);

// 把质点位置展开回原始网格顶点顺序，写回你自己那份实例网格
var vertices = soft.CaptureMeshVertices();
instanceMesh.vertices = vertices;
instanceMesh.RecalculateNormals();
```

`Build` 失败会抛异常并给出中文原因（空网格、索引越界、三角形数不是 3 的倍数都会明确报出来）。
用组件的话这些都不用管，见下一节。

## 2. 用 `SoftBodyBehaviour` 组件

```csharp
var go = new GameObject("Jelly");
go.AddComponent<MeshFilter>();
go.AddComponent<MeshRenderer>();
var soft = go.AddComponent<SoftBodyBehaviour>();
soft.sourceMesh = MeshAsset;
soft.pinMode = SoftBodyPinMode.None;             // 整块自由落体
soft.collideWithSceneColliders = true;           // 落在场景碰撞体上（v1.3.0）
soft.sceneColliders = new List<Collider> { ground };
soft.initialVelocity = new Vector3(2.4f, 0f, 0f); // 推一把
soft.Rebuild();
```

组件的约定和 `ClothBehaviour` 完全一致：

- **模拟在组件自身的局部空间**。把 GameObject 搬走、转掉、缩放，都不会给软体施加任何力——
  这样才能一边用动画摆位、一边让它自己晃。
- **绝不改你的源网格资源**。写回的是组件自己那份实例网格，顶点数与三角形索引照抄源网格。
- **构建失败不抛异常打断游戏**，原因写进 `LastBuildError`，`IsBuilt` 保持 false，`Step` 静默返回。

`pinMode` 有四种：`None`（整块自由）、`TopVertices` / `BottomVertices`（按高度取一层，厚度容差
`pinLayerThickness`）、`ExplicitIndices`（按源网格顶点下标精确钉）。

::: warning 扰动要用 `initialVelocity`，别直接推质点
只改运行时质点位置的扰动（比如编辑期 `SetPositions` 推偏一下）会在 Play 时 `Awake → Rebuild`
拿源网格重建模拟的**一瞬间全部丢失**，画面就成了静态。演示场景就踩过这个坑，所以才加了
可序列化的 `initialVelocity`：每次 `Rebuild` 时给所有未钉住的质点施加初速度。
:::

## 3. 从网格长出来的三种约束

| 约束 | 来源 | 作用 | 关掉会怎样 |
| --- | --- | --- | --- |
| 结构弹簧 | 三角形的边（去重后每条只有一根） | 保持面内形状，抗拉抗压 | 网格直接散架 |
| 弯曲弹簧 | 共边三角形的一对"对顶点" | 抵抗面片之间的折角 | 软体变得像湿纸，一碰就折 |
| 体积约束 | 全部闭合三角形的有向体积 | 抗"瘪"，被压下去会鼓回来 | 一捏就扁，且回不来 |

细分后的长方体（每面 2×2）焊接后是 26 个质点：8 个角 + 12 条棱的中点 + 6 个面心。
注意**结构弹簧数不等于棱数**——一个三角化的长方体有 18 条结构弹簧，因为每个面上的对角线也是
真实存在的三角形边。这不是 bug，是网格自己决定的。

弯曲弹簧的数量取决于三角化方式，所以文档不给公式；参考页里给的是它的不变量（与结构弹簧不重叠、
无重复、闭合网格上必然大于 0）。

## 4. 体积约束怎么算

用散度定理把体积写成三角形上的求和：

```
V = Σ_t  (1/6) · x0 · (x1 × x2)
```

符号跟着绕序走，所以**绕序必须朝外**；否则 V 是负的，恢复力会把软体往"翻面"的方向推。
对每个质点求梯度（就是它周围三角形面积向量加权和的 1/6），得到

```
F_i = -( k_v · (V - V₀) + c_v · dV/dt ) · ∇_i V
```

`dV/dt = Σ_j ∇_j V · v_j`。这是一根**梯度恢复力**，不是位置投影，所以它和结构弹簧共用同一套
显式积分与子步，不需要额外的迭代。

开放网格（存在只被一个三角形用到的边）体积没有意义：`IsClosed` 为 false，`Volume()` 直接返回 0，
体积力跳过。这时它退化成一个"会晃的壳"，仍然能用。

## 5. 参数手感

- `springStiffness` 是牛顿每米量级的真实弹簧系数（不是布料那套 PBD 比值）。默认 1200 很硬，
  静态挂 0.8 kg 的物体只沉 6 mm——**想看出"软"要往下调**，演示用的果冻是 130。
- 硬弹簧 + 显式积分会炸。三道保险：`substeps`（默认 4）、`maxDeltaTime`（默认 1/15 秒）、
  `maxStretchRatio`（超限后做 8 趟 Gauss-Seidel 位置投影）与 `maxSpeed`（默认 40 米/秒的速度封顶）。
  `maxSpeed` 是最后一道防线，正常参数下永远碰不到；碰上了说明刚度给得太离谱。
- `volumeStiffness` 提高会让它更像充气的球，但也更容易在剧烈碰撞式形变里抖。默认 4000 配
  `volumeDamping = 20` 是个稳的组合。
- `damping` 是全局速度衰减（0.5 意味着每秒保留约 60%）。想让它多晃几下去掉 `damping`，
  而不是去掉弯曲弹簧。

## 6. 性能实测（托管求解器，无 Burst）

Unity 6000.5.6f1，托管单线程，EditMode 基准测试，5 批 × 60 步取最佳/均值：

| 规模 | 配置 | 最佳 | 均值 |
| --- | --- | --- | --- |
| 软体 642 质点 | 1920 结构 + 1920 弯曲弹簧、1280 三角形、子步 4 | **2.745 ms/步** | 2.814 ms/步 |
| 布料 32×32 | 1024 质点、5826 约束、子步 4 | 3.299 ms/步 | 3.351 ms/步 |
| 布料 64×64 | 4096 质点、23938 约束、子步 4 | 19.447 ms/步 | 20.477 ms/步 |

一个细分两级的二十面体球（642 质点）单帧 2.7 ms，够和 32×32 的布料同屏跑。基准测试本身是
`Benchmark_IcoSphere642_ManagedSolverFitsInsideOneFrame`，门槛 8 ms，超了会红。

## 6.5 落地：碰撞代理（v1.3.0）

软体在 v1.2.0 是"钉住一层再推一把"，因为它**根本不参与碰撞**，会直接穿过地面。v1.3.0 补上了这块：

```csharp
var soft = go.AddComponent<SoftBodyBehaviour>();
soft.pinMode = SoftBodyPinMode.None;             // 一个质点都不钉
soft.collideWithSceneColliders = true;
soft.sceneColliders = new List<Collider> { groundCollider };
soft.Rebuild();
```

- 求解器只认注入的 `ICollisionProxy`（球 / OBB 盒 / 胶囊 / 半空间），**不查 `PhysicsScene`**，所以确定性与逐位复现不受影响；
- 模拟在局部空间，所以桥接进来的世界 Collider 会让每个质点"变到世界 → 推出 → 变回局部"，盒与胶囊是精确的；
- 软体是半隐式欧拉，没有 PBD 那种 `v = (pos - prev)/h` 回算 —— 只顶位置会让法向速度无限累积，
  所以碰撞同时**削掉穿入方向的速度分量**、保留切向（会沿斜面滑，不粘）；
- `MeshCollider` / `Terrain` 明确不支持，也**不拿包围盒冒充** ⇒ 地面得用 Cube + `BoxCollider`；
- 开关默认 `false`，关掉时与 v1.2.0 **逐位一致**（不注入矩阵、不生成代理）。

实测（Play 中 Dump State）：

```
SoftBodyJelly：钉住 0 | 碰撞代理 1 个
质点世界 y 最低 -0.0100 | 盒子地面上表面 -0.0200 ⇒ 最低质点高出 0.0100
体积保持率 0.981 | 最大拉伸比 1.0893 | 非有限状态 False
```

最低质点高出地面正好等于 `collisionThickness`。完整契约、四种几何的脱出方向与开销见 [碰撞代理](/collision/)。

## 7. 编辑器工具

`Tools ▸ Physics Simulation ▸ Soft Body`：

- **Create Soft Body Demo Scene**（120）：生成 `Assets/Scenes/SoftBodyDemo.unity`，
  一块**不钉任何质点**、自由落体砸在地面（Cube + `BoxCollider`）上的蓝色果冻 + 一块顶面钉住荡摆的橙色袋子。
- **Build In Current Scene**（121）：只在当前场景里加这两块，不写盘。
- **Dump State**（122）：把场景里**每一个** `SoftBodyBehaviour` 的质点/弹簧/三角形数量、闭合性、
  体积保持率、最大拉伸比、最大速度、`IsBuilt` 与失败原因打到 Console。

调试"为什么画面不动"就靠 Dump State：它会直接告诉你 `autoSimulate`、`enabled`、
`activeInHierarchy` 和 `IsBuilt`，以及最大速度是不是零。

详见 [编辑器工具一览](/tools/)。

## 8. 深入阅读

- [从任意网格到质点](/soft-body/mesh-to-particles)：焊接的**真实做法**（均匀空间哈希，不是八叉树）、容差为什么是半径、  float 精度墙、构建阶段的拒绝清单。
- [真实模型实测](/soft-body/model-audit)：本项目 104 个网格逐个跑 90 步的判定表、失败主因归类、调参对照与成本分布。
- [软体参数参考](/reference/soft-body-parameters)：每个字段的默认值、语义、越界行为。

## 9. 下一步

- v1.5.0 会把求解器搬进 Jobs + Burst（可选程序集），这份基准就是对照基线；v1.3.0 做的是碰撞、v1.4.0 是模型审计，都没有动性能。
