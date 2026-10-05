# 布料模拟（Cloth）

`v1.1.0` 起提供。把一张 `columns × rows` 的网格变成会下垂、会飘、会被球顶起来的布料。

与质点弹簧模块不同，布料**不是力模型**，而是**位置约束模型（PBD / XPBD 风格的距离约束）**：
每个子步先预测位置，再把所有约束"投影"回满足的状态，最后用位置差反推速度。
所以 `stiffness` 再大也不会像显式弹簧那样炸开，参数非常好调。

> 核心求解器 `ClothSimulation` 是纯 C# + `UnityEngine.Vector3`，不依赖 Unity 物理系统、不依赖 URP/HDRP、不依赖 Job/Burst，
> 可以脱离 GameObject 在 EditMode 里直接跑，也保证**同一份参数在任意机器上跑出逐位一致的结果**。

## 1. 五分钟上手

```csharp
using UnityEngine;
using PhysicsSimulation;

public class Flag : MonoBehaviour
{
    public ClothParameters parameters = new ClothParameters { columns = 24, rows = 16, spacing = 0.06f };

    ClothSimulation _cloth;

    void Awake()
    {
        _cloth = new ClothSimulation(parameters);

        // 把整条顶边钉住 = 一面旗；钉左上角 = 披在肩上的斗篷
        for (int col = 0; col < parameters.columns; col++)
            _cloth.SetPinned(_cloth.IndexOf(col, 0), true);
    }

    void Update()
    {
        _cloth.AddWindImpulse(new Vector3(0.35f, 0f, 1.1f), Time.deltaTime);
        _cloth.Step(Time.deltaTime);

        // 想要自己建网格：_cloth.GetPosition(i) 逐点取；想直接用组件见下一节
    }
}
```

## 2. 用 `ClothBehaviour` 组件（推荐）

`ClothBehaviour` 把"求解器 + 网格 + 碰撞体 + 风"打包成一个挂载即用的组件，运行在**组件自身的局部空间**：
网格躺在局部 XY 平面，列沿 `+X`、行沿 `-Y`、正面法线朝 `+Z`。所以相机要站在 `+Z` 一侧才看得到正面。

| 字段 | 作用 |
| --- | --- |
| `parameters` | 上面那张参数表 |
| `pinEdges` | `None / Top / Bottom / Left / Right` 可组合，决定哪些边被钉住 |
| `autoSimulate` | 关掉后由外部调 `Step(dt)`，方便做定步长/回放 |
| `windAcceleration` | 每帧给所有自由质点加的加速度（近似风，不是压力面积模型） |
| `obtainObstaclesFromTransforms` | 自动把 `obstacles` 里的 `Transform` 当成球体障碍 |
| `obstacles` | 障碍列表；有 `SphereCollider` 就用它的半径（乘世界缩放），否则用 `obstacleRadiusFallback` |
| `generateMesh` / `recalculateNormals` | 是否生成/更新网格与法线 |
| `drawGizmoWireframe` | Scene 视图里画结构边线框与质点小圆 |

常用方法：`Rebuild()`（改完参数重建）、`Step(dt)`、`ResetToInitialLayout()`、`CaptureCurrentAsInitial()`（把当前姿态设为初始姿态）、
`CollectStructuralEdges(...)`（拿结构边做调试绘制）、`System` / `IsBuilt` / `LastBuildError` / `Mesh`。

::: tip 障碍物：v1.1.0 的球在局部空间，v1.3.0 的场景 Collider 在世界空间
`ClothSimulation.AddSphereObstacle(center, radius)` 现在登记一条 **`Simulation` 空间**的 `SphereCollisionProxy`：
坐标与质点同样在布料局部空间，`ObstacleCount` 则是**代理总数**（含桥接进来的世界 Collider）。
另一条路是 `collideWithSceneColliders` + `sceneColliders` —— `ClothBehaviour` 把场景 `Collider` 采样成 **`World` 空间**代理，
每条世界代理让质点"变到世界 → 推出 → 变回局部"。于是 Box / Capsule 是精确的（v1.2.0 之前只能拿球，
还得用 `lossyScale / √3` 近似被非均匀缩放弄歪的椭球）。两条路可以共存，`Step` 内按 `Collisions` 插入顺序遍历，确定性不变。
:::

## 3. 拓扑：三种约束

| 类型 | 连接谁 | 静止长度 | 数量（`c` 列 `r` 行） | 管什么 |
| --- | --- | --- | --- | --- |
| `Structural` 结构 | 上下左右相邻 | `spacing` | `r·(c-1) + c·(r-1)` | 布不会拉长，决定面积与轮廓 |
| `Shear` 剪切 | 每个格子的两条对角线 | `spacing·√2` | `2·(c-1)·(r-1)` | 抗"错切"，布不容易斜着塌 |
| `Bend` 弯曲 | 隔一个的同向邻居 | `2·spacing` | `r·(c-2) + c·(r-2)` | 抗折，决定布料"硬不硬"（牛仔 vs 丝绸） |

`enableShear` / `enableBend` 可以整体关掉某类约束来省性能；关掉剪切与弯曲后只剩结构边，
适合做"绳子网"或追求最大性能的小物件。

## 4. 求解流程与参数手感

一个 `Step(dt)` 内部：

1. `dt` 先被 `maxDeltaTime` 截断，再按 `substeps` 切成小步；
2. 每个小步：**预测**（重力 + 隐式阻尼 `1/(1+damping·h)`）→ **投影**约束 `iterations` 轮（结构 → 剪切 → 弯曲）→ **拉伸限幅** → **碰撞**推出 → **回写速度** `(newPos - prevPos)/h`；
3. 钉住的质点 `inverseMass = 0`，任何阶段都不会被移动。

调参速查：

- 布太"橡皮筋" ⇒ 提高 `structuralStiffness`（1 就是每轮完全投影）；
- 布太"铁皮" ⇒ 降低 `bendStiffness`（0.05~0.1 是软布，0.3+ 是帆布/皮革）；
- 抖动/穿模 ⇒ 提高 `substeps`（比提高 `iterations` 更划算）；
- 想省性能 ⇒ 降 `iterations` 到 1、关 `enableBend`、减小网格分辨率；
- 极限参数下想保证不烂 ⇒ `maxStretchRatio` 是硬保险，多轮限幅扫描把过拉伸压回上限附近。

::: warning 拉伸限幅是 Gauss-Seidel 近似
`ClampStretch()` 最多做 32 轮扫描，一轮的修正可能把另一条约束顶出去，所以它是**收敛式**保证而不是同时精确解。
碰撞推出发生在限幅之后，因此"质点一定在球外"是硬保证，而那一瞬间的拉伸可能短暂超过 `maxStretchRatio`。
:::

## 5. 性能实测（托管求解器，无 Burst）

测试环境：Unity Editor 内 EditMode 基准用例，Mono 托管执行，`substeps = 4`、`iterations = 2`，多轮取最佳/均值。

| 网格 | 质点 | 约束 | 最佳 | 均值 | 说明 |
| --- | --- | --- | --- | --- | --- |
| 32 × 32 | 1 024 | 5 826 | **4.65 ms/步** | 4.95 ms/步 | 披风/旗帜档，单帧预算内 |
| 64 × 64 | 4 096 | 23 938 | **20.2 ms/步** | 20.5 ms/步 | 压力档，约两帧一步（60 fps 下需要降频或插值） |

基准用例本身就是回归门槛：`Benchmark_32x32_ManagedSolverFitsInsideOneFrame`（< 8 ms）与
`Benchmark_64x64_ManagedSolverStaysWithinTwoFrames`（< 33 ms）。
`v1.4.0` 的 Jobs + Burst 可选程序集就是冲着把 64×64 压进一帧去的（v1.3.0 先做了碰撞，本版没有动性能）。

## 6. 编辑器工具

- `Tools/Physics Simulation/Cloth/Create Cloth Demo Scene` —— 生成 `Assets/Scenes/ClothDemo.unity`：20×14 网格、顶边钉住、朝相机吹的风、一颗会被裹住的球，静默存盘不打扰。
- `Tools/Physics Simulation/Cloth/Build In Current Scene` —— 只往当前场景里加一块布。
- `Tools/Physics Simulation/Cloth/Dump State` —— 把质点数/约束数/钉住数/最大拉伸/是否出现非有限值打到 Console。

## 7. 下一步

- [参数参考](/reference/cloth-parameters)
- [质点弹簧模块](/mass-spring/)
- [更新日志](/changelog)
