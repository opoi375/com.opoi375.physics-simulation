# 编辑器工具

菜单都在 **Tools → Physics Simulation** 下，优先级 100~103（与卡通渲染包同一套菜单风格）。

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

## 运行测试

**Window → General → Test Runner → EditMode**。包是内嵌包（`Packages/` 下），测试会自动出现在列表里，不需要往 `Packages/manifest.json` 的 `testables` 里加东西。

| 测试文件 | 数量 | 覆盖 |
| --- | --- | --- |
| `MassSpringIntegratorTests.cs` | 5 | 惯性、自由落体离散闭式解、全局阻尼单调衰减、粒子阻尼钳制、一阶收敛 |
| `SpringForceTests.cs` | 4 | 原长零力、等大反向轴向力、质心守恒、加速度按 1/m 分配且总动量为零 |
| `MassSpringSystemTests.cs` | 6 | 固定点、确定性、参数校验、高刚度 + 子步稳定性、复位、dt 钳制 |
| `MassSpringUnityLayerTests.cs` | 10 | 配置 → 系统的翻译层、`MassSpringBehaviour` 的重建与错误上报、`Capture Current As Rest` |
| `MassSpringDemoToolsTests.cs` | 5 | 演示链条结构、k 递减、原长自动、子步够稳、可视化绑定完整 |
