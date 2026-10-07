# 流体模拟（Fluid / PBF）

`v1.5.0` 起提供。把一团水（盒子、球、溃坝水柱）变成会摊开、会绕障碍、会被墙挡住、但**不会自己变成子弹**的粒子流体。

实现的是 **PBF（Position Based Fluids，Macklin et al. 2016）**：和布料同源，都是**位置约束模型** ——
每个子步先预测位置，再把"密度等于静止密度"这条约束投影回去，最后用位置差反推速度。
所以它没有显式压力项、没有刚度系数可调，`restDensity` 是物理量而不是旋钮；稳定性靠**子步数 + 投影迭代 + 两道保险丝**。

> 核心求解器 `FluidSimulation` 是纯 C# + `UnityEngine.Vector3`，不依赖 Unity 物理、不依赖 URP/HDRP、不依赖 Job/Burst，
> 可以脱离 GameObject 在 EditMode 里直接跑，**同一份参数在任意机器上逐位一致**。

## 1. 五分钟上手

```csharp
using UnityEngine;
using PhysicsSimulation;

public class WaterBucket : MonoBehaviour
{
    public FluidParameters parameters = new FluidParameters
    {
        particleSpacing = 0.05f,     // 决定粒子数与粒子质量 m = ρ0·d³
        kernelRadius    = 0.10f,     // 必须 > d；h = 2d 时三维点阵约 21~25（实测） 个邻居
        substeps        = 2,
        solverIterations = 2,
        clampTensileLambda = true,   // 拉力钳制：只许推开，不许吸住
    };

    FluidSimulation _fluid;

    void Awake()
    {
        // 一坨 0.5×1.0×0.5 的溃坝水（从 min 点向 +x/+y/+z 长）
        _fluid = new FluidSimulation(parameters, FluidVolume.DamBreak(0.5f, 1.0f, 0.5f, 0.05f));

        // 场景里的碰撞体：解析成代理，不经过 Unity 物理
        _fluid.Collisions.Add(new BoxCollisionProxy(Vector3.zero, new Vector3(2f, 0.1f, 2f), Quaternion.identity));
    }

    void Update() => _fluid.Step(Time.deltaTime);
}
```

## 2. 用 `FluidBehaviour` 组件（推荐）

组件跑在**自身局部空间**：粒子坐标是局部的，`BoxCollisionProxy` 等世界代理会"变到世界 → 推出 → 变回局部"。

| 字段 | 作用 |
| --- | --- |
| `parameters` | 见[参数参考](/reference/fluid-parameters) |
| `volumeShape` | `Box / DamBreak / Sphere` 三种初始点阵 |
| `volumeSize` / `volumeRadius` | 点阵尺寸（Box/DamBreak 用 size，Sphere 用 radius） |
| `collideWithSceneColliders` + `sceneColliders` | 把场景 `Collider` 桥接成碰撞代理 |
| `updateCollidersEveryStep` | 代理每帧重采样（移动的平台要开，静态水箱不必） |
| `autoSimulate` | 关掉后由外部调 `Step(dt)`，做定步长/回放 |
| `renderParticles` | 用 `Graphics.DrawMeshInstanced` 画粒子（不生成 Mesh，不吃 SkinnedMesh 预算） |
| `autoParticleSize` / `particleRenderScale` | 粒子方块边长：自动取 1.6×间距，或手填 |
| `particleColor` / `emissionStrength` | 颜色与自发光（材质走管线默认材质，见 §6） |

常用方法/属性：`Rebuild()`、`Step(dt)`、`ResetToInitialLayout()`、`Simulation`、`IsBuilt`、`LastBuildError`、
`ParticlePositions`、`EffectiveParticleRadius`、`RenderBatchCount(n, max)`（`DrawMeshInstanced` 单次上限 1023，超了自动分批）。

## 3. 一个子步里发生了什么

```
dt ──钳到 maxDeltaTime──> 切成 substeps 份，每份：
  1. 预测    x̂ = x + v·h + g·h²            （v 先按 vorticity/xsph 之前的值积分）
  2. 邻居    均匀网格哈希 → CSR（starts/indices），cell = h
  3. 碰撞    解析代理把 x̂ 推出体外（记录接触法线）
  4. 密度    ρᵢ = Σⱼ m·W(xᵢ−xⱼ)            （poly6，与梯度同一遍扫完）
  5. λ       λᵢ = −Cᵢ / (Σ|∇C|² + ε)，Cᵢ = ρᵢ/ρ0 − 1
  6. 投影    Δxᵢ = (m/ρ0)·Σⱼ(λᵢ+λⱼ)∇Wᵢⱼ   ×solverIterations 轮，每轮位移过"护栏"
  7. 碰撞    再推一次（保证投影不把水塞进墙里）
  8. 速度    v = (x̂ − x)/h，再沿接触法线去掉"弹出去"的分量
  9. 涡度    （可选）vorticity confinement；XSPH 速度平滑
```

调参速查：

- 水摊得太开 / 密度对不上 ⇒ 加 `substeps`（比加 `solverIterations` 划算）；
- 水"黏"、表面结膜 ⇒ 确认 `clampTensileLambda = true`（关掉它会互相吸引，见 §5）；
- 想软一点（泡沫/泥浆感） ⇒ 提 `complianceAlpha`，它是**松弛因子**：λ 乘 `1/(1+α)`；
- 粒子数爆炸 ⇒ `particleSpacing` 是三次方关系，`0.05 → 0.06` 少掉 42% 的粒子。

## 4. 核函数与归一化（数值细节都在这儿）

| 核 | 表达式 | 系数 |
| --- | --- | --- |
| Poly6（密度） | `W(r) = 315/(64πh⁹)·(h²−r²)³` | `Poly6Coefficient(h)` |
| Spiky 梯度（压力） | `∇W = −45/(πh⁶)·(h−r)²·(δ/r)` | `SpikyGradientCoefficient(h)` |
| 粘性拉普拉斯 | `∇²W = 45/(πh⁶)·(h−r)` | `ViscosityLaplacianCoefficient(h)` |

两条踩过的坑，写死在测试里：

::: warning h⁹ 与 h⁶，不是 h⁷
`Poly6Coefficient` 早先按 `h²·h²·h²·h = h⁷` 拼错了分母（应当是 `h⁹`）；`SpikyGradientCoefficient`
和 `ViscosityLaplacianCoefficient` 写成 `45/(πh⁷)`（应当是 `h⁶`）。前者让密度整体差一个 `h²` 量级，
后者让压力梯度**大 10 倍**。核系数的精确值有 `FluidKernelTests` 逐条对着解析式钉住。
:::

::: tip 梯度模长的化简
`SpikyGradient` 里那个 `/r` 会被 `|δ| = r` 约掉，所以 `|∇W| = 45/(πh⁶)·(h−r)²`。
写 λ 的分母时直接用这个式子，别再乘一次 `1/r`。
:::

方向约定：`SpikyGradient(delta, r)` 返回**从 i 指向 j** 的向量（`delta = xᵢ − x`，所以带一个负号）。

## 5. 四道保险丝，以及它们各自救过什么火

PBF 的投影在**病态构型**下会给出荒谬的位移（邻居太少时密度约束根本不可满足，投影会一路推到"对岸"）。
本包用四道互相独立的保险：

1. **位移护栏** `MaxCorrectionPerIterationFactor = 0.25`
   单轮迭代每个粒子最多移动 `0.25·h`。它是"别把水甩上天"的保险丝，不是流体主参数：
   实测 `0.02 ~ 0.25` 的稳定段很宽（4 秒后 `v_max` 0.83 ~ 1.02 m/s）。置 0 或负数 = 关掉。
2. **拉力钳制** `clampTensileLambda`
   `λ > 0` 意味着"这坨水太稀了，要把邻居吸过来" —— 真实液体没有这个力（表面张力另算），
   留着它会让稀疏粒子互相吸成团、自由水面结膜。默认开：只允许推开（λ ≤ 0）。
   代价：关掉它，一个点阵块会在没有重力的情况下**自己收缩**（`TensileClamp_OffShrinksALatticeBlob` 钉住这条）。
3. **dt 上限** `maxDeltaTime`（默认 1/30 秒）
   见下面那条 warning，它是演示场景"水自己炸开"的真凶。
4. **速度上限（CFL 式）** `maxSpeed`（默认 0 = 不限制，演示取 8 m/s）
   位移护栏限制的是**约束修正**，而平流位移 = 速度 × 子步长，没有上限。PBF 在稀疏/深穿透构型下能给出
   十几米每秒的速度，一步就跨过整块薄板 —— 这是演示水箱"活板门漏水"的另一半原因
   （前一半见 §7 的顶盖 warning）。`Validate()` 拒绝负数；置 0 表示不限制。
   用例：`DemoTank_SingleStepMaxExcursionFitsInsideEveryProxySkin` 把"单步最大位移 < 最薄板到中线的余量"钉死。

::: warning 秒级 `Time.deltaTime` 会把整池水甩到一千米外
进 Play 的第一帧、卡帧、切后台再回来，Unity 可能给出**秒级**的 `deltaTime`。PBF 的子步长直接进重力积分和投影，
不钳住的话一帧就能把水甩飞 —— 实测演示场景刚进 Play 头几帧，质心就掉到 `y = −1394 m`，包围盒 162×1492×115 m。
布料/质弹簧/软体从一开始就有 `maxDeltaTime`，流体是这次补上的（同一套语义：`<= 0` 表示不钳制）。
回归用例：`FirstFrameHugeDeltaTime_DoesNotLetTheFluidEscapeTheTank`。
:::

## 6. 渲染：粒子（`DrawMeshInstanced`）与水面（等值面）

### 6.1 粒子模式（`FluidRenderMode.Particles`，默认）

- `FluidParticleMesh` 缓存一个朝向相机的四边形（billboard）网格，`FluidBehaviour` 每帧按粒子位置填 `Matrix4x4[]`，
  每 1023 个一批调 `Graphics.DrawMeshInstanced`（Unity 的单批上限）。粒子数上千也不会生成十万面 Mesh。
- `FluidParticleMaterial.Get()` 用反射取**当前管线的 `defaultMaterial`**（含 `BindingFlags.Instance`），
  所以包不依赖 URP/HDRP，Built-in 下也能拿到不品红的材质；参数走 `_BaseColor` / `_EmissionColor` 与 `_Color` 双写。
- 材质**不能**标 `HideFlags.DontSave`，否则存盘后进 Play 引用丢失，画成品红（布料演示同一条坑）。

### 6.2 水面模式（`FluidRenderMode.Surface` / `Both`）

一堆小球看着像"粒子特效"，不像水。水面模式把同一批粒子变成一个连续、半透明、会跟着流动的网格：

1. **splat 成标量场**。把粒子按同一个核 `W` 打到规则格点上：`α(x) = Σⱼ W(|x − xⱼ|) / Σ_lattice W`。
   分母是**同一个核在生成间距 `d` 的点阵上的自和**，所以静止水体内部 `α ≈ 1`、体外迅速归零，
   阈值 `isoLevel = 0.5` 就是水面。归一化用同一个核是刻意的：换一个核，`α` 的内部值就不是 1，阈值也跟着漂。
2. **取等值面**。用 **marching tetrahedra**：每个立方体按 Kuhn 方式剖成 6 个四面体，逐个四面体出 1~2 个三角形。
   没用 256 case 的 marching cubes 表 —— 那张表要手抄四千多个整数，抄错的表现是"偶尔破面"，
   而这种错最难被测试发现。Kuhn 剖分对平移不变，所以相邻格子共用同一条面对角线，**等值面天生闭合**，
   二义性（ambiguous face）由四面体自己消化。闭合性不是口头承诺：用例直接数边界边，
   `CountBoundaryEdges` 必须为 0（`BuildMesh_CompactBlob_ProducesWatertightSurface`）。
3. **法线用场的梯度**（中心差分）而不是 `Mesh.RecalculateNormals`：体素场的梯度指向 `α` 减小最快的方向，
   就是外法线；用例断言至少 98% 的顶点法线与"离开水团中心"的方向同向。

工程上的四条边界：

| 事项 | 取值 / 行为 | 为什么 |
| --- | --- | --- |
| 体素边长 | `surfaceCellSize`，演示取 `0.75 × particleSpacing` | 比间距小才不会被体素切成积木；再小就是白花帧时 |
| 格子预算 | `surfaceMaxCells`（默认 `262144`） | 超预算时**自动放大体素**，`grid.Min` 与覆盖范围不动，只放大不缩小 |
| 单张网格顶点上限 | `MaxMeshVertices`，超了抛 `InvalidOperationException` | Unity 的 `Mesh.triangles` 默认 ushort 索引（65535），不报错就是破面 |
| 重建节奏 | `surfaceRefreshEveryNFrames`（演示 2 帧） | 水面重建一帧的代价高于若干次粒子约束投影，节流比优化先做 |

同一份输入必须逐位重现，水面也不例外：`PlanGrid` 的遍历顺序、四面体的枚举顺序都是固定的，
用例 `BuildMesh_SameInput_ProducesBitIdenticalVertices` 断言两次生成的顶点与索引逐位相同
（本包的确定性底线：不许有随机数、哈希遍历、并行归约）。

::: warning `Destroy` 在 EditMode 里会打一条 Error，而测试框架把任何未预期 Error 算成失败
水面每重建一次就要回收上一张 `Mesh`。编辑器里调 `Object.Destroy` 会打
`Destroy may not be called from edit mode!`，测试不会因为你的断言通过就放过这条日志 ——
两个水面用例就是这样莫名变红的。运行时用 `Destroy`、编辑器里用 `DestroyImmediate`，
`FluidBehaviour.DisposeAsset` 就是为这件事存在的。
:::

::: tip 水面模式没有额外依赖，也没有额外精度
它只吃粒子位置，不参与求解，所以**开不开水面不影响模拟结果**；`Both` 模式也只是两次提交，
数值上与 `Particles` 逐位一致（这一点由 `RenderMode_*` 三条用例从行为侧钉住）。

## 7. 编辑器工具与演示场景

- `Tools/Physics Simulation/Fluid/Create Fluid Demo Scene`（130）—— 生成 `Assets/Scenes/FluidDemo.unity`：
  一个六面封死的水箱（地板 + 四面墙 + 顶盖，**只画不碰**）+ 一坨溃坝水（1540 粒，预算 1500）。
  兜住水的是 **1 个内侧盒子容器代理**（`BoxContainerProxy`，挂在 `FluidBehaviour` 的序列化字段上），不是六块板
  —— 演示场景里 `碰撞代理 1` 就是这个数
  + **3/4 俯视相机** + **水面模式**（`FluidRenderMode.Surface`）。
  **朝向相机的两面墙和顶盖不生成 `Renderer`**（diorama 式处理）：水浅的时候，不裁掉挡镜头那面墙，
  截图里就只是一只空盒子——实测发生过，而水其实确实在箱里（`Dump State` 报 `水面 三角 14044`）。
  裁掉不影响物理，因为兜水的是容器代理而不是板子。
- `Tools/Physics Simulation/Fluid/Build Fluid In Current Scene`（131）—— 只往当前场景加一坨水和一个水箱。
- `Tools/Physics Simulation/Fluid/Dump State`（132）—— 打印每坨水的粒子数 / 平均与最大邻居度 / 密度均值最小最大 /
  包围盒 / 质心 / 动能 / 质量 / 碰撞代理数 / 子步·迭代·XSPH·涡度·拉力钳制。**画面不对时的第一现场工具。**

演示配置里有三条是踩出来的，全部有用例钉住：

::: warning 三条漏水 warning 其实是同一个根因：把实体板当碰撞体
第一版水箱是六块**实体板**（地板 + 四面墙 + 顶盖，每块一个 `BoxCollisionProxy`），于是连着踩了三个坑：
① 墙底与底板之间的水平缝成了一条**单向活门**，水被沿最浅轴从缝里拱出去，实测漏到地板下方 65 m
（`v = 35.7 m/s = √(2g·65)`，是自由落体不是爆炸）；② 缝封死之后**竖直方向**还漏，0.2 m 厚的地板中线就在
上表面下方 0.1 m，粒子一旦被压过中线，"最浅轴"翻到板的另一面，水从地板**下表面**被挤出去（三维活板门），
加厚到 `2.5t` 才压住；③ 六面封死之后仍然漏，300 步后 120 粒从地板 `-z` 边被横向挤出去，越过地板与墙的
**外表面**之后开始自由落体（实测样例 `(-0.635, -0.529, -0.964)`，速度 3.3 → 4.1 m/s 递增），
这叫**角部传送带**：两块互相搭接的"密封"板，在它们的交线附近把水往角外递。
三个坑的共同点：`BoxCollisionProxy` 的语义是"沿**穿透最浅的那一面**推到体外"，而水箱要的恰恰相反，
每块板各自裁决自己的最浅轴，接缝与中线就永远是漏点。
**现在的做法**：板子退回成纯视觉，水的边界交给一个 `BoxContainerProxy`（内侧盒子，越界的点**逐轴**钉回
最近内壁，等价于往凸集合做最小位移投影，所以角上越界的点是被推回内角，不是沿单轴逃到外面）。
实测：300 步 0 粒出界，包围盒回到内空以内，`FluidTankSealTests` 两条钉住。
用例：`BuildTank_WallsSealTheFloorSeamAndEachOther`、`Tank_HasSixWallsFloorSidesAndCeiling`、
`DemoTank_NoParticleEndsUpOutsideTheTankAfterTenSteps`（名字里的"十步"是历史，**已抬到 300 步**：
穿板漏水 10 步就看得见，弹道溅出与角部传送带要跑到后期才暴露 —— **时间不够长本身就是一种漏测**）。
:::

::: warning 挡镜头的墙不画：水在箱里但截图里看不见
容器方案顺带买到的自由度：既然板子不参与物理，就可以只画不挡镜头的那几面，内空 1.5 × 1.1 × 0.9 的桶，
溃坝摊平后水深只有 0.26 m，池底沉在 1.1 m 深的箱底，实测机位 `(1.33, 2.36, −2.21)` 到池心的连线在
`z = −0.713`（正好是墙顶 `y = 1.1`）穿过 −z 墙，于是截图里就是一个空盒子，水明明在箱里，看不见。
把相机抬到 5 m 以上确实能看见，但演示就变成俯视图，浪头与水面形状全压扁。现在 `PlateBlocksCamera` 判掉
朝外法线指向相机那一侧的板，`CreateTank` 把顶盖与这两面墙连 Renderer 都不建，
并且有一条**硬几何断言**：相机到水面四个角点的连线，不许穿过任何还画着的板（`SegmentIntersectsPlate`），
:::

::: warning 水柱不能贴着墙摆放
初始点阵如果**同时**贴住墙面和地板（到几个面的距离都是 0），"最浅轴"就退化成 tie-break，推出去的方向是任意的，
整批粒子会被塞到地板底下。演示里四周各留一个粒子间距（`DemoConfig_ColumnClearsEveryWallSoTheProxyHasNoTies`）。
:::

::: warning 生成位置要按实测粒子算，别按形状参数猜
`FluidVolume.DamBreak` 的水体在深度方向是**居中**的，而演示当初把水柱按"从 −z 起算"摆到
`DemoColumnOffset.z = −0.37`，结果 1540 粒里有 **560 粒直接生成在墙里**（越界 → 被容器钉回 → 一帧内
密度爆到 1392）。现在演示的摆放断言改成量**真实生成出来的粒子包围盒**，不再信形状参数的语义，
用例：`FluidDemoToolsTests` 里那条按实测包围盒摆位的断言。
:::

::: warning 水柱不能贴着墙摆放
初始点阵如果**同时**贴住墙面和地板（到几个面的距离都是 0），"最浅轴"就退化成 tie-break，推出去的方向是任意的，
整批粒子会被塞到地板底下。演示里四周各留一个粒子间距（`DemoConfig_ColumnClearsEveryWallSoTheProxyHasNoTies`）。
:::

::: tip 相机必须俯视，水量必须够深
平视时前墙比水高，截图里就是一堵墙（真的发生过）。所以取景参数是常量 + 用例锁住：
`DemoPoolDepth / spacing >= 4`（否则水摊成一张膜，`size.y` 直接是 0）、视线朝下、水落在视锥里。
:::

## 8. 性能实测（托管求解器，无 Burst）

测试环境：Unity Editor 内 EditMode 基准用例，Mono 托管执行，`substeps = 2`、`iterations = 2`，
**`d = 0.05 m`、`h = 0.1 m` 固定，只改箱体尺寸**（0.5 / 0.65 / 0.8 m 立方）。这台机器当时有后台负载，
同一个用例两次运行的绝对值能差到 1.6 倍，所以下表给**区间**（两次实测原值），基准门限用**比值**而不是绝对毫秒。

| 粒子数 | 每步耗时（两次运行） | 平均邻居 | 说明 |
| --- | --- | --- | --- |
| 1 000（0.5 m 立方） | **13.3 ~ 21.2 ms** | 21.0 | 60 fps 的一帧预算已经吃满 |
| 2 197（0.65 m 立方） | **41.7 ~ 69.4 ms** | —（该用例不打印邻居度） | 邻居表占比就是在这档量的 |
| 4 096（0.8 m 立方） | **64.7 ~ 105.5 ms** | 22.9 | 上限档，托管下谈不上一帧走一步 |

- 邻居表重建：2197 粒那档整步 41.7 ~ 69.4 ms，其中**重建 10.8 ~ 20.4 ms，占 26 ~ 29%**，是最大单项开销；
  `Benchmark_NeighbourTableBuildIsTheDominantCost` 会把它打出来。
- 近邻搜索是均匀网格哈希 + CSR，规模律接近线性：`Benchmark_NeighbourCostScalesNearLinearlyWithParticleCount`
  要求 `t(4N)/t(N) < 12`，实测 **4.59 ~ 5.75**（理想线性是 4，超线性来自邻居度随规模从 21.0 升到 22.9）。
- **演示那一档没有单独测过毫秒**：1456 粒、`d = 0.07`、`h = 0.14`、平均邻居 25.1，按粒子数与邻居度落在上表 1000 与 2197 之间，
  量级就是**每步 20 ~ 70 ms**。`DemoParticleBudget = 1500` 是照这张表取的：再大就得跨帧走步或降子步，
  别指望托管求解器一帧 16.7 ms 走完。
- 上表只量 `Step()`，不含 `DrawMeshInstanced` 的绘制成本。

`v1.6.0` 的 Jobs + Burst 可选程序集要把这些数字压一个数量级（布料/软体的 Burst 版同批处理）。

## 9. 已知边界

- **水面是观感层，不是物理**：`FluidSurface` 只读粒子位置，不参与密度约束。薄于一个体素的水膜、
  或粒子间距大于体素时，会出现"水面把缝隙糊上"的假连接；两团真分开的水不会自己搭桥（有用例断言）。
- **等值面分辨率受预算限制**：`surfaceMaxCells` 超了就自动放大体素，此时水面会退化成方块状。
  大水箱 + 小体素的组合要么降密度要么分块，本包不做分块。
- **单相流体**：没有表面张力、没有多材质、没有气液耦合。涡度约束（`vorticityEpsilon`）只能补一点"打旋"的观感，默认关。
- **自由水面偏稀**：实测稳定后平均密度比约 **0.89 ~ 0.99**（表面粒子邻居少），不是 bug，是 SPH 自由表面的固有偏差。
- **深度方向要够采样**：静水深至少 4 层粒子才像水，否则摊成一张膜（见 §7 的 tip）。
- **不支持 MeshCollider**：与 v1.3.0 的碰撞层一致，只做解析代理（球/盒/胶囊/平面 + 内侧盒子容器）。
- **容器只有轴对齐长方体**：`BoxContainerProxy` 支持旋转，但它是**一个盒子**，不是任意形状的水池；
  要做异形容器得自己按轴切几个容器代理，而多个容器叠加时同样有"各自裁决"的问题（§7 的教训）。
- **不做粒子与 Unity 物理的双向耦合**：碰撞体是单向的（刚体影响水，水不推动刚体）。

## 10. 下一步

- [流体参数参考](/reference/fluid-parameters)
- [场景碰撞](/collision/)
- [布料模拟](/cloth/)（同一套 PBD 思路，约束不同）
- [编辑器工具](/tools/)
- [更新日志](/changelog)
