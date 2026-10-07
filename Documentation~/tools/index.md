# 编辑器工具

菜单都在 **Tools → Physics Simulation** 下（质点弹簧 100~103，布料 110~112），与卡通渲染包同一套菜单风格。

| 菜单 | 优先级 | 做什么 | 会写盘吗 |
| --- | --- | --- | --- |
| Create Demo Scene | 100 | 新建场景 → 搭演示链条 + 相机 + 方向光 → 存成 `Assets/Scenes/PhysicsDemo.unity` | ✅ 会（静默存盘） |
| Build In Current Scene | 101 | 只在**当前**场景重建演示链条（先删掉已有的） | ❌ 不写盘，只标脏 |
| Dump State | 102 | 打印系统状态到 Console（诊断） | ❌ |
| Build Chain Only | 103 | 只搭链条，不动相机/灯光，也不删已有的 | ❌ |

## Create Demo Scene

生成一条"1 个固定吊点 + 5 节弹簧链"，`k` 逐节递减（`1600 → 1100 → 750 → 500 → 320`），整条链按 `38°` 侧偏释放，因此一播放就能看到**上硬下软**的层次：上面几节几乎不形变，下面几节被甩得明显拉长。

参数细节见 [参数参考 §演示链条的默认参数](/reference/mass-spring-parameters#演示链条的默认参数)。

::: warning 为什么所有存盘都走 `DemoSceneSave`
两个坑，都是真踩出来的：

1. `SaveCurrentModifiedScenesIfUserWantsTo()` 会弹模态确认框。从脚本 / 自动化（UnitySkills 的 `ExecuteMenuItem`、CI）里调用时**没人点那个框**，编辑器主线程就永久堵死。
2. 换成 `EditorSceneManager.SaveOpenScenes()` 也**不算安全**——v1.5.0 就栽在这里：手上有未命名场景（没有文件路径）时，`SaveOpenScenes()` 会替它**弹出系统"保存场景"对话框**，从菜单 API 调进来同样把主线程堵死，表现为"菜单点了 300 秒超时"，比第 1 个坑更隐蔽，因为它看起来是个"静默"API。

所以四个模块的演示工具一律走 `Editor/DemoSceneSave.cs`：

1. `AllOpenScenesHavePaths()` 先检查有没有未保存过的场景，有就**中止并明确报错**（让用户先存或先关掉那个场景），绝不弹框；
2. 只 `MarkSceneDirty` + `SaveScene(scene)` 那些**有路径**的脏场景；
3. 最后用 `SaveScene(active, "Assets/Scenes/XXXDemo.unity")` 存目标场景。

`DemoSceneSaveTests` 里除了单测 `CanSaveSilently`（把"全是空白字符的路径"也算未命名，这条是测试逼出来的边界），还有一条**全仓源码扫描**：只要 `Editor/**/*.cs` 里再出现 `EditorSceneManager.SaveOpenScenes(` 或 `SaveCurrentModifiedScenesIfUserWantsTo(`（注释行除外），测试直接红。这条规则本身也比任何一句文档可靠。
:::

## Build In Current Scene

在当前场景里重建演示链条：先找到场景里已有的 `MassSpringBehaviour` 并 `Undo.DestroyObjectImmediate` 掉，再建一条新的，最后 `MarkSceneDirty`。**不写场景文件**——想留就自己 Ctrl+S。所有创建都走 `Undo`，可以 Ctrl+Z 撤销干净。

## Dump State

把"能不能跑起来"的前提一次打全：

```text
[PhysicsSimulation] 系统状态
  粒子数        = 6
  弹簧数        = 5
  固定点数      = 1
  substeps      = 8（生效 8）
  maxDeltaTime  = 0.0667 s
  gravity       = (0.000, -9.810, 0.000)
  globalDamping = 0.600 1/s
  最大速度      = 1.2735 m/s
  出现 NaN/Inf  = 否
  质点位置：
    [0] (0.0000, 3.0000, 0.0000)  v=(0.0000, 0.0000, 0.0000)  m=1.000  (fixed)
    [1] (-0.0544, 2.5188, 0.0000)  v=(-0.2931, 0.0428, 0.0000)  m=0.800
    ...
    spring[0] 0→1 rest=0.4500 k=1600.0 c=1.50 strain=0.0612
```

播放中连续点几次，看**最大速度**与**应变**的变化趋势，比盯着屏幕猜"它到底有没有在动"可靠得多。三种典型症状：

| 症状 | 说明 | 处理 |
| --- | --- | --- |
| `出现 NaN/Inf = 是` | 已经发散 | 加 `substeps`、降 `stiffness`，或检查质量是否接近 0 |
| `最大速度` 持续单调上涨 | 能量在往里灌（积分顺序错 / 阻尼为负） | 检查是否自己写了积分；本包的积分顺序见 [质点弹簧 §1](/mass-spring/) |
| `strain` 一直很大且不收敛 | 太软，或 `restLength` 与实际初始距离不符 | 用 `Capture Current As Rest` 定格，或把 `restLength` 填 0 让它自动取 |


## Cloth / 布料工具（v1.1.0）

菜单在 **Tools → Physics Simulation → Cloth** 下，优先级 110~112。

| 菜单 | 优先级 | 做什么 | 会写盘吗 |
| --- | --- | --- | --- |
| Create Cloth Demo Scene | 110 | 新建场景 → 20×14 布料 + 顶边钉住 + 风 + 障碍物球 + 相机灯光 → 存成 `Assets/Scenes/ClothDemo.unity` | ✅ 会（静默存盘） |
| Build In Current Scene | 111 | 只在当前场景加一块演示布料 | ❌ 不写盘，只标脏 |
| Dump State | 112 | 打印质点数 / 三类约束数 / 钉住数 / 最大拉伸比 / 是否出现非有限值 | ❌ |

演示场景里两个刻意的设计：

- **相机站在 `+Z` 一侧**：布料躺在局部 XY 平面、正面法线朝 `+Z`，相机在 `-Z` 就只能看到侧刃。
- **风只朝 `+Z` 吹**（`y` 分量为 0）：带向上分量会把整面旗掀到障碍物球上面去，就看不到"裹住球"了。
- **材质来自当前管线的 `defaultMaterial`**（反射获取，包本身不依赖 URP/HDRP），并且**不能**标 `HideFlags.DontSave` ——
  那样存盘后进 Play 模式引用会丢，整块布画成品红。

## Soft Body / 软体工具（v1.2.0）

菜单在 **Tools → Physics Simulation → Soft Body** 下，优先级 120~122。

| 菜单 | 优先级 | 做什么 | 会写盘吗 |
| --- | --- | --- | --- |
| Create Soft Body Demo Scene | 120 | 新建场景 → 蓝色果冻（**不钉任何质点**，自由落体砸地面）+ 橙色袋子（顶面钉住 + 纵向初速 2.2 m/s）+ **地面（Cube + BoxCollider）** + 相机灯光 → 存成 `Assets/Scenes/SoftBodyDemo.unity` | ✅ 会（静默存盘） |
| Build In Current Scene | 121 | 只在当前场景加这两块软体 | ❌ 不写盘，只标脏 |
| Dump State | 122 | 打印场景里**每一个** `SoftBodyBehaviour` 的 `enabled` / `autoSimulate` / `activeInHierarchy` / `IsBuilt`、质点与三类约束数量、体积保持率、最大拉伸比、最大速度、**碰撞代理个数**、**质点世界 y 最低/最高值**与"最低质点高出盒子上表面多少" | ❌ |
| Audit Selected Meshes | 123 | 审计**选中**的模型/网格资源：用真实求解器跑 90 步（地面 = 世界空间 `PlaneCollisionProxy`），逐行给焊接比 / 闭合性 / 体积保持 / 每步耗时 / 判定 | ❌ |
| Audit Mesh Assets In Folder | 124 | 扫整个目录（默认 `Assets`），输出「默认参数」+「按包围盒对角线放大的推荐参数」**两张表 + 好转/变差对照**到 `Logs/SoftBodyMeshAudit.md` | ❌ |

演示里两个刻意的设计，都是踩出来的：

- **扰动走 `initialVelocity`，不是直接推质点**。编辑期用 `SetPositions` 把顶部推偏，Play 时 `Awake → Rebuild`
  会拿源网格重建模拟，扰动一瞬间丢光，画面就成了静态。所以初速度必须是可序列化的字段。
- **刚度调到 130 而不是默认 1200**。默认值很硬（0.8 kg 只沉 6 mm），演示要一眼看出"软"。
- 材质与布料演示共用同一套管线默认材质逻辑（见上一节），同样**不能**标 `HideFlags.DontSave`。
- **地面必须是 BoxCollider，不能是 `CreatePrimitive(Plane)`**。Plane 自带 MeshCollider，而 v1.3.0 只做 primitive 解析碰撞、
  明确不支持 MeshCollider（也不会拿包围盒冒充）。用 Plane 的结果就是"场景里明明有地面，果冻还是穿过去"——最难查的一类现象。
  果冻的 `collideWithSceneColliders` 打开、`sceneColliders` 填地面，于是它能整块落地。

`Dump State` 是"画面不动"的第一现场工具：它会直接告诉你模拟到底在不在跑（最大速度是不是 0）。

## Fluid / 流体工具（v1.5.0）

菜单在 **Tools → Physics Simulation → Fluid** 下，优先级 130~132。

| 菜单项 | 优先级 | 做什么 | 写盘 |
| --- | --- | --- | --- |
| Create Fluid Demo Scene | 130 | 新建场景 → **六面密封水箱**（底板 + 四面墙 + 顶盖，带 `BoxCollider`）+ 左侧水柱（溃坝）+ 相机灯光，水体组件默认配成 **`Surface` 水面模式** → 存成 `Assets/Scenes/FluidDemo.unity` | ✅ 经 `DemoSceneSave`，未命名场景先中止 |
| Build Fluid In Current Scene | 131 | 只在当前场景加水箱与水柱，不动相机灯光 | ❌ 不写盘，只标脏 |
| Dump State | 132 | 打印每个 `FluidBehaviour` 的质点数 / 平滑长度 / 平均邻居数 / 质量 / 密度比 / 最大速度 / 包围盒 / 是否被预算截断 / 碰撞代理个数 | ❌ |

水箱几何不是随便摆的，`FluidDemoToolsTests` 钉住三件事，每一件都对应一次真实翻车：

- **墙底必须扎进底板、四面墙在平面上必须互相重叠**（`BuildTank_WallsSealTheFloorSeamAndEachOther`）。墙与底板之间只要留一条缝，盒子代理的"最浅穿透轴"就会把水沿墙底往外拱，而墙体外侧没有地板——水会漏下去，然后以自由落体加速度砸穿相机。现场看起来是"水箱爆炸"，实际是一条**单向活门**。修好之前 850 个质点在 23 秒内被甩到 1651 米外。
- **水柱必须与所有壁留出间隙**（`DemoConfig_ColumnClearsEveryWallSoTheProxyHasNoTies`，单侧 0.08 m）。质点贴在两个面正中间时，代理取"最浅轴"，`distance == 0` 的多面并列会抛向随机一侧，第一帧就有质点被弹飞。
- **水池要有深度、相机要俯看**（`DemoConfig_PoolIsDeepEnoughAndCameraLooksIntoTheTank`）。齐眼高度的水平相机会被前墙完全挡住——截图里那片"空的盒子"就是这件事；而水池浅于四行质点时水摊成一层，从侧面看等于没有。

这些约束连同数值都写在 `FluidDemoTools` 的常量里，不在场景里手调，所以每次跑菜单都会重建到同一个几何。

## 运行测试

**Window → General → Test Runner → EditMode**。包是内嵌包（`Packages/` 下），测试会自动出现在列表里，不需要往 `Packages/manifest.json` 的 `testables` 里加东西。

| 测试文件 | 数量 | 覆盖 |
| --- | --- | --- |
| `MassSpringIntegratorTests.cs` | 5 | 惯性、自由落体离散闭式解、全局阻尼单调衰减、粒子阻尼钳制、一阶收敛 |
| `SpringForceTests.cs` | 4 | 原长零力、等大反向轴向力、质心守恒、加速度按 1/m 分配且总动量为零 |
| `MassSpringSystemTests.cs` | 6 | 固定点、确定性、参数校验、高刚度 + 子步稳定性、复位、dt 钳制 |
| `MassSpringUnityLayerTests.cs` | 10 | 配置 → 系统的翻译层、`MassSpringBehaviour` 的重建与错误上报、`Capture Current As Rest` |
| `MassSpringDemoToolsTests.cs` | 5 | 演示链条结构、k 递减、原长自动、子步够稳、可视化绑定完整 |
| `FluidKernelTests.cs` | 9 | poly6 / spiky 梯度 / 粘性拉普拉斯的**解析积分与边界**（∫poly6=W(r=0)、∫spiky=W、∇spiky=0 在边界、负半径、归一化系数） |
| `FluidNeighborSearchTests.cs` | 8 | 均匀哈希 + CSR 表：自匹配排除、半径语义、逐位确定性、**重建后计数表必须清零**（这条测出了粘性的隐藏 bug）、跨格邻居不漏 |
| `FluidVolumeTests.cs` | 6 | 长方体 / 球 / 柱的采样间距、数量对账、预算截断上报、角点不重复 |
| `FluidSimulationTests.cs` | 24 | 静止方块密度≈ρ0、受压变密、溃坝向前推、XSPH 不增加动能、涡度不无中生有、拉力钳制、**护栏只兜底不背锅**、`maxDeltaTime` 钳制、**首帧秒级 dt 不把水甩出箱外**、确定性、参数校验 |
| `FluidSurfaceTests.cs` | 19 | 标量场 splat（峰值/单调衰减/空输入/逐位确定）、等值面几何（**闭合：边界边必须为 0**、顶点不出采样范围、法线朝外、两团水不许搭桥、预算自动放大体素、低阈值必须包住高阈值）、`FluidBehaviour` 三种渲染模式与节流、演示水面常量与六面水箱 |
| `FluidTankSealTests.cs` | 2 | 300 步内没有任何粒子穿出六面水箱（行为）；单步最大位移（平流 + 约束轨道）必须小于最薄一块板到中线的余量（结构） |
| `FluidUnityLayer.cs` → `FluidUnityLayerTests.cs` | 10 | `FluidBehaviour` 世界↔局部、代理重同步、预算截断、`DrawMeshInstanced` 不生成网格、`DumpState` 不抛异常 |
| `FluidDemoToolsTests.cs` | 9 | 预算与实际粒数、六面密封水箱几何（含顶盖贴桶口）、间隙、池深与相机取景、超预算要吭声 |
| `DemoSceneSaveTests.cs` | 2 | `CanSaveSilently` 边界 + **全仓扫描：绝不允许再出现会弹框的存盘 API** |
| `ClothSimulationTests.cs` | 18 | 拓扑与索引、三类约束、拉伸限幅、球体障碍永不穿入、风与阻尼、确定性、参数校验 |
| `ClothUnityLayerTests.cs` | 14 | `ClothBehaviour` 局部空间约定、实例网格写回、钉边、构建失败不抛异常 |
| `ClothDemoToolsTests.cs` | 6 | 演示布料结构、障碍物世界半径、长时间步进不炸、材质来源、空场景 Dump |
| `SoftBodySimulationTests.cs` | 17 | 顶点焊接（含格坐标/容差/`float` 精度墙三条边界）、结构/弯曲弹簧拓扑、闭合判定与有向体积、体积保持、`SetVelocity`、参数校验与状态不变性 |
| `SoftBodyUnityLayerTests.cs` | 11 | `SoftBodyBehaviour` 局部空间、实例网格拓扑照抄、四种钉法、`initialVelocity`、构建失败静默 |
| `SoftBodyDemoToolsTests.cs` | 8 | 演示果冻结构与初速、180 步不炸且体积不塌、材质来源、程序化长方体绕序与体积 |

| `SoftBodyMeshAuditTests.cs` | 22 | 审计契约：判定分级（含 `InvertedWinding` / `DegenerateVolume` 两条漏报回归）、note 列、表格列数不被异常消息撕坏 |
| `SoftBodyModelAuditToolsTests.cs` | 15 | 网格抽取、预算线筛选、Markdown 报告与分档合计对账、推荐参数缩放、两轮对照 |

合计 **270** 个 EditMode 测试：质点弹簧 30、布料 38、软体 36、**碰撞 38**、模型审计 22 + 扫描工具 15、**流体 89**（9 核函数 + 8 邻居搜索 + 6 体积形状 + 24 求解器 + 19 水面 + 2 密封 + **12 Unity 层** + 9 演示工具）、存盘口子 2。

::: warning 这张表的数字是**逐文件数出来的**
上一版写的是 240，并把碰撞记成 33、软体演示记成 7 —— 那两条与测试文件里的实际条数不符（32 与 8）。本版把每一行按 `[Test] + [TestCase]` 静态数过一遍，再与 Test Runner 的运行数对账；两边不一致时以**跑出来的数**为准。（v1.5.0 收尾时又给碰撞代理加了 6 条 `BoxContainerProxy` 用例、给流体加了 2 条容器用例：碰撞 32 → **38**、流体 87 → **89**、合计 262 → **270**，静态逐条相加与 Runner 完全一致。）:::
