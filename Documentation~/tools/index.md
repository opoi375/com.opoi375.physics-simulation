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

::: warning 为什么不用 `SaveCurrentModifiedScenesIfUserWantsTo()`
它会弹模态对话框。从脚本 / 自动化（UnitySkills 的 `ExecuteMenuItem`、CI）里调用时**没人点那个框**，编辑器主线程就永久堵死了。本工具改用静默路径：

1. 先 `EditorSceneManager.SaveOpenScenes()` 保存已打开的场景；存不了（未命名场景没有文件路径）就**中止并报错**，不弹框；
2. 再 `EditorSceneManager.SaveScene(active, "Assets/Scenes/PhysicsDemo.unity")` 存目标场景。

这是卡通渲染包踩过的坑（见其 `CHANGELOG` 1.4.0），这里直接沿用结论。
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
| Create Soft Body Demo Scene | 120 | 新建场景 → 蓝色果冻（底面钉住 + 横向初速 2.4 m/s）+ 橙色袋子（顶面钉住 + 纵向初速 2.2 m/s）+ 地面相机灯光 → 存成 `Assets/Scenes/SoftBodyDemo.unity` | ✅ 会（静默存盘） |
| Build In Current Scene | 121 | 只在当前场景加这两块软体 | ❌ 不写盘，只标脏 |
| Dump State | 122 | 打印场景里**每一个** `SoftBodyBehaviour` 的 `enabled` / `autoSimulate` / `activeInHierarchy` / `IsBuilt`、质点与三类约束数量、体积保持率、最大拉伸比、最大速度 | ❌ |

演示里两个刻意的设计，都是踩出来的：

- **扰动走 `initialVelocity`，不是直接推质点**。编辑期用 `SetPositions` 把顶部推偏，Play 时 `Awake → Rebuild`
  会拿源网格重建模拟，扰动一瞬间丢光，画面就成了静态。所以初速度必须是可序列化的字段。
- **刚度调到 130 而不是默认 1200**。默认值很硬（0.8 kg 只沉 6 mm），演示要一眼看出"软"。
- 材质与布料演示共用同一套管线默认材质逻辑（见上一节），同样**不能**标 `HideFlags.DontSave`。

`Dump State` 是"画面不动"的第一现场工具：它会直接告诉你模拟到底在不在跑（最大速度是不是 0）。

## 运行测试

**Window → General → Test Runner → EditMode**。包是内嵌包（`Packages/` 下），测试会自动出现在列表里，不需要往 `Packages/manifest.json` 的 `testables` 里加东西。

| 测试文件 | 数量 | 覆盖 |
| --- | --- | --- |
| `MassSpringIntegratorTests.cs` | 5 | 惯性、自由落体离散闭式解、全局阻尼单调衰减、粒子阻尼钳制、一阶收敛 |
| `SpringForceTests.cs` | 4 | 原长零力、等大反向轴向力、质心守恒、加速度按 1/m 分配且总动量为零 |
| `MassSpringSystemTests.cs` | 6 | 固定点、确定性、参数校验、高刚度 + 子步稳定性、复位、dt 钳制 |
| `MassSpringUnityLayerTests.cs` | 10 | 配置 → 系统的翻译层、`MassSpringBehaviour` 的重建与错误上报、`Capture Current As Rest` |
| `MassSpringDemoToolsTests.cs` | 5 | 演示链条结构、k 递减、原长自动、子步够稳、可视化绑定完整 |
| `ClothSimulationTests.cs` | 16 | 拓扑与索引、三类约束、拉伸限幅、球体障碍永不穿入、风与阻尼、确定性、参数校验 |
| `ClothUnityLayerTests.cs` | 14 | `ClothBehaviour` 局部空间约定、实例网格写回、钉边、构建失败不抛异常 |
| `ClothDemoToolsTests.cs` | 6 | 演示布料结构、障碍物世界半径、长时间步进不炸、材质来源、空场景 Dump |
| `SoftBodySimulationTests.cs` | 13 | 顶点焊接、结构/弯曲弹簧拓扑、闭合判定与有向体积、体积保持、`SetVelocity`、参数校验与状态不变性 |
| `SoftBodyUnityLayerTests.cs` | 11 | `SoftBodyBehaviour` 局部空间、实例网格拓扑照抄、四种钉法、`initialVelocity`、构建失败静默 |
| `SoftBodyDemoToolsTests.cs` | 7 | 演示果冻结构与初速、180 步不炸且体积不塌、材质来源、程序化长方体绕序与体积 |

合计 97 个 EditMode 测试。
