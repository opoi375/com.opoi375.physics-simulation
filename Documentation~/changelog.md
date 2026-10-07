# 更新日志

## [1.5.0] - 2026-10-06

### Added
- **流体水面渲染（等值面）** `Runtime/Fluid/FluidSurface.cs`：把粒子 splat 成规则格点上的标量场 `α`（用**同一个核**、按生成间距点阵的自和归一化，所以静止水体内部 ≈ 1），再用 **marching tetrahedra**（每个立方体按 Kuhn 剖成 6 个四面体）取 `α = 0.5` 的等值面。没有用 256 case 的 marching cubes 表：那张表要手抄四千多个整数，抄错表现为"偶尔破面"，最难被测试发现；Kuhn 剖分对平移不变 ⇒ 相邻格子共用面对角线 ⇒ **等值面天生闭合**，用例直接数边界边（`CountBoundaryEdges` 必须为 0）。法线取场梯度的中心差分，不用 `Mesh.RecalculateNormals`
- **`FluidRenderMode`（`Particles` / `Surface` / `Both`，默认 `Particles`）** 与 `FluidBehaviour` 上的水面字段（`surfaceCellSize` / `surfaceIsoLevel` / `surfaceRefreshEveryNFrames` / `surfaceMaxCells` / `surfaceColor`）+ 只读观察口（`SurfaceMesh` / `SurfaceRevision` / `SurfaceTriangleCount` / `ParticleBatchCount`）；`FluidSurfaceMaterial` 是管线无关的半透明水面材质
- **`FluidParameters.maxSpeed`**（默认 0 = 不限制，演示 8 m/s）：CFL 式的平流速度上限，补上"位移护栏只管约束修正、平流位移没有上限"这个洞
- **内侧盒子容器代理 `BoxContainerProxy`**（`Runtime/Collision/CollisionProxies.cs`）：语义与实体代理**相反** —— 体内的点逐位不变，体外的点**逐轴**钉回最近内壁（往凸集合做最小位移投影），所以角上越界是回到内角而不是沿单轴逃出去。它是 `ICollisionProxy` 里唯一**故意不遵守"体外原样返回"这条契约**的实现，碰撞页有专门的 warning 讲为什么
- **`FluidBehaviour` 上的一等容器字段** `enableBoxContainer` / `containerCenter` / `containerHalfSize`：容器**必须**跟着组件走 —— 手动 `Collisions.Add` 的代理在 `Rebuild()`（进 Play 模式就会触发）之后会被丢掉，实测那箱水直落到 `y = −92`、动能 16904
- **演示水箱退回纯视觉 + 挡镜头的墙不画**：六块板不再挂任何 Collider，`CreateTank` 不给顶盖与朝向相机的两面墙建 Renderer，并由 `SegmentIntersectsPlate` 硬断言"相机到水面四个角点的连线不许穿过还画着的板"
- **`FluidSurfaceTests` 19 条 + `FluidTankSealTests` 2 条**，布料规模比值基准 1 条；收尾时再加 `BoxContainerProxy` 单元 6 条、流体容器 Unity 层 2 条（重建后仍在 / 跨步兜水）与相机取景几何断言；全量 **270 通过**（静态按文件逐条数过并与 Runner 对账，工具页那张表的旧数字 33 碰撞 / 7 软体演示与文件实际不符，已改为 38 / 8）
- **流体求解器（PBF）** `Runtime/Fluid/`：`FluidKernel`（poly6 / spiky 梯度 / 粘性拉普拉斯，归一化系数由**解析积分**测试钉住）、`FluidNeighborSearch`（均匀网格哈希 + CSR 邻居表，重建逐位确定）、`FluidVolume`（盒 / 球 / 柱 / 溃坝四种采样 + 预算截断上报）、`FluidSimulation`（密度约束投影 + 双密度修正 + 拉力钳制 + XSPH 黏度 + 涡度约束 + 子步与 dt 钳制，碰撞直接复用 v1.3.0 的 `CollisionSet`）
- **`FluidBehaviour`**：世界↔局部换算（质点存**世界空间**，投影后没有速度回算，用局部空间会每帧叠一个 g）、Collider 自动桥接、**`Graphics.DrawMeshInstanced` 渲染**（不生成十万面网格，共享一个 `Mesh` 与一个 `Material`）
- **编辑器工具 Fluid（130~132）**：`Create Fluid Demo Scene` / `Build Fluid In Current Scene` / `Dump State`，演示场景是**密封水箱 + 左侧水柱的溃坝**
- **`DemoSceneSave`**：所有演示工具的存盘口子，未命名场景直接中止报错，绝不弹框；并有一条**全仓源码扫描**测试守着
- **文档四页（中英）**：流体模块页（PBF 流水线、核函数归一化、三条保险丝、渲染与材质、水箱接缝教训、实测成本表）与流体参数参考页
- 本版本的补丁把上面几页都改了：保险丝三条→**四条**（`maxSpeed`）、渲染一节改成**粒子与水面两小节**、§7 的三条漏水 warning 合并成"同一个根因：把实体板当碰撞体"并新增"挡镜头的墙不画""生成位置要按实测粒子算"两条、§9 补水面的边界与容器的边界；碰撞页代理表加 `BoxContainerProxy` 一行 + 一条"故意违反契约规矩 1"的 warning；参数参考加 `maxSpeed`、**水面参数**与**内侧盒子容器**小节；工具页测试表加 `FluidSurfaceTests` / `FluidTankSealTests` 两行并把合计改成 270（中英同步）
- **68 个新 EditMode 测试**（9 核函数 + 8 邻居搜索 + 6 体积形状 + 24 求解器 + 10 Unity 层 + 9 演示工具 + 2 存盘口子），另给布料补 1 条 `maxDeltaTime`；含水面、密封、容器与取景在内，全量 **270 通过**

### Fixed

- **`FluidSurface.PlanGrid` 的格子数用 `int` 存，1e12 会溢出成负数**：于是"超预算"被判成"没超"，一个百万格的网格被静悄悄分配。现在 `CellCount` / `NodeCount` 是 `long`，节点数超过上限明确抛异常
- **`FluidBehaviour` 在 EditMode 里调 `Object.Destroy` 回收水面网格**：Unity 打一条 `Destroy may not be called from edit mode!` 的 Error，而测试框架把任何未预期 Error 算成测试失败 —— 两个水面用例就这样莫名变红。现在走 `DisposeAsset`：运行时 `Destroy`、编辑器 `DestroyImmediate`
- **核函数归一化写错**：poly6 用了 h⁷（应为 h⁹），spiky 梯度与粘性拉普拉斯用了 45/(πh⁷)（应为 45/(πh⁶)）。后果是静止水块密度比 7.1（该是 1.0），方块一帧摊成煎饼 —— 现在三条积分都有解析测试
- **邻居表重建没清 `_stamp`**：上一轮的邻居被当成本轮邻居读，黏度算子拿到陈旧邻居集
- **PBF 投影的尺度与弛豫形式错**：位移少乘 m、λ 分母少 Σ、投影后没有按 `v = (x − x_prev)/dt` 重算 —— 单轮位移是正确值的约 42 万倍，一帧甩飞
- **`FluidParameters.Validate()` 的分支顺序**导致非正常输入被静默丢弃
- **流体没有 `maxDeltaTime`**（布料从一开始就有）：编辑器进 Play 的第一帧秒级 dt 把整箱水甩到 1651 米外，画面上就是"爆炸"。现在四个求解器语义一致
- **演示水箱接缝是一条单向活门**：墙底与底板之间的缝让盒子代理把水沿墙底往外拱，外侧没有地板，水直接自由落体砸穿相机（`v ≈ √(2gh)` 是漏不是炸）。修成墙底扎进底板 + 四面墙平面重叠，并加测试钉住
- **水柱贴壁**：与三面墙和地板同时相切 → 最浅轴并列 → 第一帧抛向随机一侧。现在单侧留 0.08 m
- **六面封死的水箱仍然漏（角部传送带）**：把六块板各挂一个 `BoxCollisionProxy` 之后，300 步实测仍有 120 粒从地板 `-z` 边被横向挤出去、越过地板与墙的外表面后自由落体（样例 `(-0.635, -0.529, -0.964)`，速度 3.3 → 4.1 m/s 递增）。先加过一轮"背板"（6 块只碰不画的兜底板，12 个代理，跑出外沿 0 粒），但兜住不等于对：实测仍有 **150 粒被按进墙板**、最深 **0.486 m**（停在墙外表面内侧 1.4 cm，与 dt 无关：100/400 步、1/60、0.02、1/30 数字一样），画面上就是水贴着墙皮。根因是"每个代理各自沿最浅面投影"，不是步长。**最终解法**：板子全部退回纯视觉，水的边界交给一个 `BoxContainerProxy`（逐轴钉回内壁 = 往凸集合做最小位移投影）；实测 300 步 0 粒出界、包围盒回到内空以内，背板那 6 个代理随之删除（12 → **1**）
- **水柱生成在墙里**：`FluidVolume.DamBreak` 的水体在深度方向是**居中**的，而演示按"三个轴都贴最小角"把水柱摆到 `DemoColumnOffset.z = −0.37`，于是 1540 粒里 **560 粒生在 −z 墙体内**（一帧密度冲到 1392）。现在 offset 改成 0，摆放断言改成量**真实生成出来的粒子包围盒**，不再信形状参数的语义
- **手工挂的容器代理在 Play 里消失**：代理存在 `CollisionSet` 里，而 `OnEnable → Rebuild()` 会重建它，于是编辑器里看着好好的，一进 Play 就"碰撞代理 0"、一箱水直落到 `y = −92`（动能 16904）。容器改成 `FluidBehaviour` 的序列化字段，在 `Rebuild()` 内部重新挂上，并有 `BoxContainer_SurvivesEveryRebuild` 钉住
- **相机到水面的连线被墙顶挡住**：水在箱里但截图里是一个空盒子。改成"挡镜头的墙不画"，并把取景判据写成硬几何断言（相机到水面四个角点不许穿过任何还画着的板）
- **相机齐眼高度 + 水池太浅**：截图里那片"空盒子"就是前墙挡住了相机；水摊成一层，从侧面看等于没有。现在俯视取景且池深 ≥ 4 行质点，两个常量都被测试钉住
- **`EditorSceneManager.SaveOpenScenes()` 在未命名场景下会弹系统对话框**，从菜单 API 调进来直接把主线程堵死（表现为菜单 300 秒超时）。全部改走 `DemoSceneSave`

### Changed

- **演示水箱从"六块实体板"改成"六块纯视觉板 + 1 个内侧盒子容器"**：碰撞代理数 12 → **1**，挡镜头的两面侧墙与顶盖不渲染，水完全留在内空以内（300 步实测）
- **演示场景 `Assets/Scenes/FluidDemo.unity` 现在是水面模式 + 六面封闭水箱**（1540 粒 / 预算 1500 / `spacing 0.07`）。原来的开顶设计本意是"看得见飞溅"，但 Play 里浪头把水直接抛过 1.1 m 的墙头，300 帧后包围盒 10.3 × 9.1 × 6.7 m，一箱水泼在箱外 —— 而主体是健康的（密度均值比 0.917、`v_rms` 2.6 m/s），是弹道不是发散。水箱只有 Collider 没有 Mesh，封顶不影响取景
- **`FluidTankSealTests` 的跑长从 10 步抬到 300 步**：穿板漏水 10 步就看得见，弹道溅出要跑到后期才暴露，**窗口太短本身就是一种漏测**
- **布料基准的门槛换了口径**：`32×32` 由 8 ms 改为 20 ms 硬上限、`64×64` 由 33 ms 改为 66 ms，另加 `Benchmark_ClothCostScalesNearLinearlyWithParticleCount`（比值门，实测线性应为 4.00，门槛 12）。这与软体、流体同一套理由：绝对毫秒测的是机器空闲度

### Notes
- **Burst 顺延到 v1.6.0**：本版做的是流体而不是并行化，流体目前跑托管求解器 —— 基准实测：固定 `d = 0.05 / h = 0.1` 时 1000 粒 13.3~21.2 ms/步、4096 粒 64.7~97.7 ms/步（同用例两次运行差 1.6 倍，所以门限用规模比值 4.59~4.88）；演示那档 1456 粒（平均邻居 25.1）落在两行之间，量级 20~60 ms/步，60 fps 一帧走不完 2 子步 × 2 迭代
- **基准门槛的口径换了**：软体那条 `8 ms` 绝对门槛在同一段代码、机器有后台负载时被打穿过（实测 9.4 / 9.5 ms 直接红），它测的是机器空闲度而不是求解器退化。本版统一改掉：流体两条基准只断言**规模比值**（实测 4.59~5.75，门槛 12），软体保留 20 ms 硬上限并新增 `Benchmark_SoftBodyCostScalesNearLinearlyWithParticleCount`（162 质点对 642 质点，实测比值 5.99~6.33）。布料 64×64 的 33 ms 门槛带负载实测 32.36 ms，只剩 0.6 ms 余量 —— 这是已知脆点，改比值排在 v1.6.0
- 渲染有**粒子**与**等值面水面**两档（marching tetrahedra，不是 marching cubes，也不做 UF 核加权模糊）；没有表面张力，黏性只有 XSPH 这一档
- 容器只做**一个长方体**（可带旋转）：内表面是硬边界，做不出"水从洞里漏出去"；要漏水就别用容器。多个容器叠加会重新引入"每个代理各自裁决"的问题（本版 §7 的教训）
- 自碰撞依然没有（布料、软体、流体都没有）；规模上限受托管 O(n·k) 成本约束，演示预算定在 1500 粒
- `MaxCorrectionPerIterationFactor` 是**可写的静态护栏**（不是可调参数）：调它只能用来做诊断实验，正常配置不要动## [1.4.0] - 2026-10-05

### Added
- **模型审计** `Runtime/SoftBody/SoftBodyMeshAudit.cs`：`SoftBodyMeshAudit.Audit(data, parameters, steps, addGround)` 用**真实** `SoftBodySimulation` 跑固定 1/60 步长、落地在世界空间 `PlaneCollisionProxy` 地面上，产出 `SoftBodyAuditResult`（焊接比、质点/弹簧/三角形数、闭合性、有向静止体积、体积保持率、最大拉伸、最低质点世界 y、每步耗时、判定）。**对脏输入永不抛异常** —— 构建失败记成 `BuildFailed` 并把原因留在 `BuildError`
- **七档判定** `SoftBodyAuditVerdict`：`Healthy` / `OpenMesh` / `InvertedWinding` / `DegenerateVolume` / `DegenerateWeld` / `Unstable` / `BuildFailed`。后两档是**扫描真实资产后补的**：绕序整体朝内的网格旧分类报 `Healthy`（两个负体积一除保持率就是正的），零厚度壳与"真的塌成 0"都印成 `0.000` 分不开
- **编辑器扫描工具** `Tools/Physics Simulation/Soft Body/Audit Selected Meshes (123)` 与 `Audit Mesh Assets In Folder (124)`：一次扫全项目，输出「默认参数」+「按包围盒对角线放大的推荐参数」两张 Markdown 表和一份好转/变差对照，落盘 `Logs/SoftBodyMeshAudit.md`；预算线（单次 120 个、顶点 4000）+ `StringComparer.Ordinal` 排序保证两次扫描逐字一致
- **`RecommendedParameters(diagonal)` / `Diagonal(data)` / `BuildComparison()`**：把"这类模型该怎么调"变成可执行的对照，而不是文档里一句空话
- **文档两页（中英）**：[从任意网格到质点](/soft-body/mesh-to-particles) 讲清焊接**真的是均匀空间哈希不是八叉树**（格边长 = 容差、27 邻居探查、精确平方距离裁决、为什么不用八叉树、容差是半径、构建拒绝清单）；[真实模型实测](/soft-body/model-audit) 给出本项目 **104 个网格**的逐行判定与成本
- **40 个新 EditMode 测试**（22 审计 + 15 扫描工具 + 3 焊接边界），全量 **170 通过**；整个工程 578 项 576 通过 2 跳过

### Fixed
- **扫描报告的合计行把新判定塞进 `BuildFailed`**：旧 `switch` 用 `default: failed++` 兜底，导致 104 行报告里 9 行（`InvertedWinding` 4 + `DegenerateVolume` 3 + 真 `BuildFailed` 2）被一起报成"构建失败 9"。现在七档各自计数，并加了**各档之和必须等于总数**的对账异常 —— 以后再加枚举值会走 `default` 计入"未知判定"而不是污染某一档
- **`note` 列对每个健康行都印 `(unnamed)`**：`Sanitize` 的空串兜底本来是给模型名字用的，串到了 note 上。现在健康行是明确的 `-`，`Note()` 无话可说时返回空串
- **`BuildFailed` 行没有原因**：报告只有表格，而表格里不带 `BuildError` 就等于让读的人去猜。现在 note 列带上原因原文（并做单行化，异常消息里的换行与竖线不会撕坏表格）

### Notes
- **本版不做性能工作**（并行求解器改排 v1.5.0）。审计的 `ms/step` 只是"这个量级能不能用"的判据
- `RecommendedParameters` 是**诊断建议，不是新默认值**：实测救回 6 个（树、草、蘑菇、单胞盒子），同时打坏 1 个（`Tunnel_Mesh` 1.323 → 0.208 被过冲压扁）。默认值保持保守
- 审计只跑 90 步、只测单体、地面是世界空间半空间；`Healthy` 只代表数值健康，不代表观感好

## [1.3.0] - 2026-10-05

### Added
- **碰撞代理**：`ICollisionProxy.PushOut(point, skin)` 一个方法就是全部契约。四种纯解析几何 —— 球 / OBB 盒（任意旋转）/ 胶囊（退化轴自动降为球）/ **半空间**平面。详见 [碰撞代理](/collision/)
- **三条契约**：体外原样返回逐位不变；体内沿穿透最浅方向顶出；方向未定义用固定备用轴 `+Y` 且绝不产生 NaN；`skin < 0` 按 0 处理
- **两种登记空间**：`Simulation` 与 `World`（质点往返世界空间），可以在同一物体上共存 ⇒ 布料 v1.1.0 的球障碍语义原封不动
- **`ColliderProxies` 桥接**：从场景 `Collider` 采样；`MeshCollider` / `Terrain` / 禁用的碰撞体返回 `null` 并跳过，**不拿包围盒冒充**
- **三个求解器统一接入**：`MassSpringSystem` / `ClothSimulation` / `SoftBodySimulation` 都有 `Collisions`、`HasColliders`、`SetSimulationToWorld`；`collisionThickness` 现在是三处都有的参数（默认 0.01 m）
- **Unity 层开关**：`collideWithSceneColliders`（默认关）、`sceneColliders`、`updateCollidersEveryStep`（默认关）
- **软体演示改造**：地面换成 Cube + `BoxCollider`；果冻**不钉任何质点**，自由落体砸地上（实测最低质点停在地面上表面之上正好 0.01 m，体积保持率 0.981）
- **33 个新测试**（21 几何契约 + 12 集成），含一条与 v1.2.0 布料球障碍算术的逐位对照；全量 **130 通过**

### Changed
- 布料球障碍迁移进 `CollisionSet`（算术逐位不变，`ObstacleCount` 现在是代理总数）；不再用 `lossyScale / √3` 近似椭球
- 软体演示果冻从"钉底面 + 横向初速"改为"不钉 + 落体"，`PinMode` 现在能表达"整块自由落地"这个最有说服力的用例

### Performance
- 本版不做性能工作（并行仍在 v1.4.0）。碰撞 `O(质点 × 代理)`/子步，无代理时整段跳过 ⇒ 默认关 = 零开销

### Fixed
- 质点弹簧/软体碰撞只顶位置会让法向速度无限累积 ⇒ 现在削掉穿入分量、保留切向
- 平面按"穿透深度"实现会被高速穿地 ⇒ 改半空间语义
- `CreatePrimitive(Plane)` 自带 MeshCollider 导致"有地面却穿过去" ⇒ 明确跳过并在 Dump State 报出代理数

---

## [1.2.0] - 2026-10-05

### Added
- **软体模拟（Soft Body）**：`SoftBodyMeshData` / `SoftBodyParameters` / `SoftBodySimulation` / `SoftBodyEdge` —— 把**任意网格**变成会形变又保体积的果冻，不需要你手搭拓扑
- **拓扑自己长出来**：空间哈希焊接重合顶点（格边长 = `weldTolerance`，27 格邻域 + 平方距离）→ 三角形边去重成结构弹簧 → 共边三角形的对顶点成弯曲弹簧 → 每条边恰好 2 个三角形即 `IsClosed`
- **体积约束**：散度定理有向体积 `V = Σ (1/6)·x0·(x1×x2)`，配梯度恢复力 `F_i = -(k_v·(V-V₀) + c_v·dV/dt)·∇_iV`。它是力而不是位置投影，所以和弹簧共用同一套显式积分与子步；开放网格体积无意义 ⇒ 自动跳过
- **复用 v1.0.0 内核**：软体不写第二套求解器，直接驱动 `MassSpringSystem.ApplyForces()` + 半隐式欧拉，体积力作为额外外力注入
- **稳定性保险**：`maxStretchRatio` 超限后 8 趟 Gauss-Seidel 位置投影、`maxSpeed` 速度封顶（默认 40 m/s，硬弹簧炸穿时的最后一道防线）、`maxDeltaTime` 钳制 + `substeps`
- **Unity 层**：`SoftBodyBehaviour`（局部空间模拟、实例网格拓扑照抄源网格、`SoftBodyPinMode` 四种钉法、可序列化的 `initialVelocity`、`generateMesh` / `recalculateNormals` / `drawGizmoWireframe`、失败写 `LastBuildError` 不抛异常）
- **编辑器工具**：`Tools > Physics Simulation > Soft Body > Create Soft Body Demo Scene / Build In Current Scene / Dump State`（优先级 120~122，静默存盘）；`Dump State` 会遍历场景内每个软体并打印 `enabled` / `autoSimulate` / `IsBuilt` / 体积保持率 / 最大拉伸比 / 最大速度
- **演示场景** `Assets/Scenes/SoftBodyDemo.unity`：底面钉住被推一把的蓝色果冻 + 顶面钉住荡摆的橙色袋子；Play 模式实测形变可见（帧间变化 5.2%）、体积保持率 0.998~1.000、无非有限值
- **共享演示材质工具** `DemoMaterialHelper`：反射取当前管线 `defaultMaterial` 作模板（布料/软体共用一套逻辑，避免 `HideFlags.DontSave` 那个坑被重复踩）
- **31 个软体 EditMode 测试**（13 核心求解器 + 11 Unity 层 + 7 编辑器工具），全量 97 个测试通过
- **性能基准**：642 质点（1920 结构 + 1920 弯曲弹簧、1280 三角形、子步 4）最佳 **2.745 ms/步**、均值 2.814 ms/步；同机复测布料 32×32 最佳 3.299 ms/步、64×64 最佳 19.447 ms/步
- **中英双语文档**：`/soft-body/` 模块指南与 `/reference/soft-body-parameters` 参数参考

### Fixed
- 程序化长方体网格的三角形绕序 4 面朝外、2 面朝内 ⇒ 散度定理体积只剩解析值的 **1/3**（新增的 `BuildBoxMesh_WithSingleSubdivision_IsAWatertightBox` 抓到）
- 反射读管线 `defaultMaterial` 时漏传 `BindingFlags.Instance` ⇒ 永远取不到模板材质，演示材质悄悄退化成"猜着色器名字"
- 实例网格先设 `triangles` 再设 `vertices` ⇒ Unity 直接拒绝这批索引（`Failed setting triangles... VertexCount: 0`），网格没有面、法线全零
- 演示扰动只写在运行时质点位置上 ⇒ Play 时 `Awake → Rebuild` 用源网格重建模拟，扰动一瞬间丢光、画面静止 ⇒ 改为可序列化的 `initialVelocity`
- `SoftBodySimulation.BendSpringCount` 被当方法调用（CS1955）、`Object` 在 `using System;` 下二义（CS0104）等编译期问题

### Notes
- 包依赖仍然只有 `com.unity.test-framework`，核心不依赖 URP / Unity.Mathematics / Burst
- 已知限制：软体**不与场景求交**（没有碰撞体，会直接穿过地面，所以演示用"钉住"而不是"落地"）；无自碰撞；体积是梯度恢复力不是硬约束（剧烈形变下允许约 1% 偏差）；弯曲弹簧数量随三角化方式变化，因此只对它断言不变量而非具体数值
- 计划：1.3.0 Jobs + Burst 并行求解（可选程序集，托管实现保留为回退，本版本基准即对照基线）

## [1.1.0] - 2026-10-05

### Added
- **布料模拟（Cloth）**：`ClothParameters` / `ClothSimulation` / `DistanceConstraint` / `ClothConstraintType`，把 `columns × rows` 网格变成会下垂、会飘、会被球顶起的布
- **位置约束求解（PBD / XPBD 风格距离约束）**：每小步 预测 → 投影（结构 → 剪切 → 弯曲）→ 拉伸限幅 → 碰撞推出 → 由位置差回写速度；刚度用 `alpha = 1 - (1-k)^(1/iterations)` 折算，`stiffness = 1` 也不会炸
- **三类约束**：结构边（横竖相邻）、剪切边（每格两条对角线）、弯曲边（隔一个的同向邻居），可分别关断与调刚度
- **稳定性保险**：`maxStretchRatio` 多轮（≤32）限幅扫描、`maxDeltaTime` 钳制、`collisionThickness` 碰撞厚度
- **风与障碍**：`AddWindImpulse(acc, dt)`、`AddSphereObstacle` / `ClearObstacles` / `ObstacleCount`（局部空间球体）
- **确定性**：固定遍历顺序、无随机、无 `Time`、无并行 ⇒ 同参数同步数逐位一致（有测试锁死）
- **Unity 层**：`ClothBehaviour`（局部空间模拟、`ClothPinEdges` 四边钉住、自动从 `Transform`/`SphereCollider` 取障碍、`Rebuild` / `Step` / `ResetToInitialLayout` / `CaptureCurrentAsInitial` / `CollectStructuralEdges`、Gizmos 线框）+ `ClothMeshBuilder`（顶点=质点、每格两三角、UV 覆盖 `[0,1]²`、>65k 顶点自动 32 位索引）
- **编辑器工具**：`Tools > Physics Simulation > Cloth > Create Cloth Demo Scene / Build In Current Scene / Dump State`（优先级 110~112，静默存盘）
- **演示场景** `Assets/Scenes/ClothDemo.unity`：20×14 网格、顶边钉住、朝相机吹的风、会被裹住的障碍物球；Play 模式实测布料下缘在球两侧翻起
- **36 个布料 EditMode 测试**（16 核心求解器 + 14 Unity 层 + 6 编辑器工具），全量 66 个测试通过
- **性能基准用例（含回归门槛）**：托管求解器 32×32（1024 质点 / 5826 约束）最佳 **4.65 ms/步**、均值 4.95 ms/步；64×64（4096 质点 / 23938 约束）最佳 **20.2 ms/步**、均值 20.5 ms/步
- **中英双语文档**：`/cloth/` 模块指南与 `/reference/cloth-parameters` 参数参考

### Notes
- 包依赖仍然只有 `com.unity.test-framework`，核心不依赖 URP / Unity.Mathematics / Burst
- 已知限制：无自碰撞、无三角形-三角形相交；风是"给质点加加速度"的近似，不是面积压力模型；拉伸限幅是 Gauss-Seidel 收敛近似（极端参数允许约 2% 残留超限）；碰撞只有球体
- 计划：1.2.0 软体（把任意网格软体化的组件）· 1.3.0 Jobs + Burst 并行求解（目标把 64×64 压进单帧）

## [1.0.0] - 2026-10-05

### Added
- **质点弹簧系统**：`Particle`（位置 / 速度 / 质量 / `inverseMass` / 力累积器 / 粒子阻尼 / 固定标志）、`Spring`（端点索引、原长、刚度 k、轴向阻尼 c）、`MassSpringSystem`（`AddParticle` / `AddSpring` / `Pin` / `Unpin` / `ApplyForces` / `Step` / `ResetToInitial` / `MaxSpeed` / `HasNonFiniteState`）
- **半隐式（辛）欧拉积分器**：阻尼写成除数 `(1 + c_global·dt)`，任意 `c_global·dt` 都只衰减不反号；粒子级阻尼乘数 `1 - c·dt` 钳到 `[0,1]`
- **子步与 dt 钳制**：`Step(dt)` 先把 dt 钳到 `maxDeltaTime`（默认 1/15 秒），再均分成 `substeps` 份逐步积分
- **确定性**：无 `Random` / 无 `Time` / 无并行，同参数同步数跑两次逐位一致
- **参数校验**：索引越界、质量非有限正数、`dt <= 0`、`a == b` 等一律抛 `ArgumentOutOfRangeException`，且抛出前不改动系统状态
- **Unity 层**：`MassSpringBehaviour`（Inspector 配置质点/弹簧、`FixedUpdate` 驱动、Gizmos 按应变着色、右键菜单 `Capture Current As Rest` / `Reset To Initial Layout`）、`MassSpringParticleLink`（Transform 跟随质点）、`MassSpringBuilder`（配置 → 系统的纯翻译层）
- **编辑器工具**：`Tools > Physics Simulation > Create Demo Scene / Build In Current Scene / Dump State / Build Chain Only`，静默存盘（不用会弹模态框的 `SaveCurrentModifiedScenesIfUserWantsTo`）
- **演示场景**：固定吊点 + 5 节链（k 逐节递减 `1600 → 320`）、侧偏 38° 释放、相机与方向光、地面参考线
- **30 个 EditMode 测试**：积分器闭式解、弹簧力对称性与守恒、系统确定性与稳定性、配置翻译层、演示构建器结构
- **中英双语文档站**（VitePress，`Documentation~`）+ GitHub Pages 部署工作流

### Notes
- 包依赖只有 `com.unity.test-framework`，**不依赖 URP** 或任何渲染管线
- v1 明确不做：刚体、碰撞、刚性距离约束、布料、软体、XPBD、Jobs/Burst 并行、与 `Rigidbody` 互操作。扩展点以 `TODO` 注释标在 `MassSpringSystem` 顶部
- 后续版本：1.1.0 布料、1.2.0 软体、1.3.0 Jobs + Burst 并行
