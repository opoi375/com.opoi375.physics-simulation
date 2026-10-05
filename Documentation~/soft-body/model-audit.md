# 真实模型实测：软体放到项目的美术资产上会怎样

演示场景里的 jelly icosphere 是自己生成的规整球体，它健康不代表随便一个 `.fbx` 都健康。
这一页是**把包所在 Unity 工程里的真实模型全扫一遍**得到的结果，数字全部来自
`Logs/SoftBodyMeshAudit.md`（由菜单自动生成），不是估算。

- 运行时 API：[`Runtime/SoftBody/SoftBodyMeshAudit.cs`](https://github.com/opoi375/com.opoi375.physics-simulation/blob/main/Runtime/SoftBody/SoftBodyMeshAudit.cs)
- 编辑器工具：[`Editor/SoftBody/SoftBodyModelAuditTools.cs`](https://github.com/opoi375/com.opoi375.physics-simulation/blob/main/Editor/SoftBody/SoftBodyModelAuditTools.cs)
- 焊接与粒子的生成原理见 [从任意网格到质点](/soft-body/mesh-to-particles)

---

## 1. 怎么跑

| 菜单 | 作用 |
| --- | --- |
| `Tools/Physics Simulation/Soft Body/Audit Selected Meshes (123)` | 只审 Project 窗口里选中的东西（`GameObject` / `Mesh` / `Model` 都行，多子网格逐个成行） |
| `Tools/Physics Simulation/Soft Body/Audit Mesh Assets In Folder (124)` | 扫整个目录（默认 `Assets`），一次出**两张表 + 一份对照汇总** |

审计做的事很直白：拿真实 `SoftBodySimulation` 跑 **90 步 × 1/60 秒**，底下垫一张 `y = 0` 的世界空间
`PlaneCollisionProxy` 当地面，然后把"有没有落地、体积保没保住、有没有翻面、多少钱一步"写成 Markdown 表格。
报告落到 `Logs/SoftBodyMeshAudit.md`（没有 `Logs/` 目录时打 Console）。

**预算线**（扫描工具不能把编辑器挂住）：

- 单次最多审 **120** 个网格；顶点数 > **4000** 的直接跳过并单独计数。
- 资产路径按 `StringComparer.Ordinal` 排序 ⇒ 同一批资产两次扫描得到逐字相同的表。

---

## 2. 总览：107 个网格里，61 个直接能用

扫 `Assets`（93 个 `.fbx`，其中不少带多个子网格，另有独立网格资产）：**发现 107 个网格**，3 个超顶点预算被跳过，实审 **104** 个。

| 判定 | 默认参数 | 推荐参数（按尺寸放大刚度与子步） | 含义 |
| --- | --- | --- | --- |
| `Healthy` | **61**（58.7%） | **66** | 闭合、绕序正确、90 步后体积在 0.60–1.40 带内 |
| `OpenMesh` | 11 | 11 | 拓扑不闭合 ⇒ **体积约束被静默跳过** |
| `Unstable` | 23 | 18 | 塌了，或者跑着跑着翻了面 |
| `InvertedWinding` | 4 | 4 | 闭合但有向体积为负 —— 整个模型里朝外 |
| `DegenerateVolume` | 3 | 3 | 闭合却围不住体积（零厚度壳） |
| `DegenerateWeld` | 0 | 0 | 焊接把网格压成不到 4 个点 |
| `BuildFailed` | 2 | 2 | 构建阶段明确拒绝（见 §5） |

拓扑闭合率：**91 / 104 = 87.5%**。

---

## 3. 失败的主因不是"塌下去"，是"翻了面"

把 104 行按 note 归类：

| 真实死法 | 个数 | 说明 |
| --- | --- | --- |
| 跑起来之后**体积符号反转**（整块翻面） | 9 | 体积约束一步跨过 0，里朝外了 —— 这是最大的一类 |
| 塌到体积保持率出带（但没有翻面） | 14 | 自身重量压过弹簧/体积回弹 |
| 导入时绕序就朝内 | 4 | 静止体积为负，求解器对称地照样跑，但每面都是里朝外 |
| 零厚度壳（围住体积 < 1 cm³） | 3 | 体积约束没有可回弹的余量 |
| 几何本身有缺陷（构建拒绝） | 2 | 见 §5 |
| 设计如此不闭合 | 11 | 敞口箱子、地面、坡道、楼梯 |

`体积保持率` 为负就是"翻面"的数值指纹。旧版扫描报告把两种情况都印成 `0.000`，
现在 `InvertedWinding` / `DegenerateVolume` 各自成档，note 列直接写原因。

---

## 4. 代表性数据（默认参数，节选）

| 模型 | 顶点 | 质点 | 焊接比 | 闭合 | 体积保持 | 最大拉伸 | ms/步 | 判定 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| prop_barrel | 180 | 60 | 3.00 | yes | 0.982 | 1.043 | 0.142 | Healthy |
| prop_mine_track_curve | 648 | 216 | 3.00 | yes | 0.819 | 1.107 | 0.426 | Healthy |
| prop_flower_pot | 662 | 180 | 3.68 | yes | 0.784 | 1.204 | 0.449 | Healthy |
| prop_chest | 240 | 68 | 3.53 | no | 0.000 | 1.202 | 0.122 | OpenMesh |
| prop_crate_open | 120 | 32 | 3.75 | no | 0.000 | 1.025 | 0.052 | OpenMesh |
| prop_tree_sakura | 482 | 123 | 3.92 | yes | 0.514 | 2.000 | 0.478 | Unstable |
| prop_clock_tower | 1684 | 473 | 3.56 | yes | **-1.308** | 2.000 | 2.093 | Unstable |
| Stairs_650_400_300_Mesh[1] | 240 | 80 | 3.00 | yes | -0.049 | 2.420 | 0.366 | Unstable |
| Box_350x250x200_Mesh | 24 | 8 | 3.00 | yes | **5.518** | 2.000 | 0.024 | Unstable |
| FactoryRoom[7] | 1656 | 552 | 3.00 | yes | 0.000 | 2.000 | 2.198 | Unstable |
| FactoryRoom[10] | 1560 | 520 | 3.00 | yes | 0.000 | 1.193 | 1.077 | DegenerateVolume |
| prop_cave_entrance | 806 | 0 | 0.00 | no | — | — | — | BuildFailed |

看两个极端：

- `Box_350x250x200_Mesh` 只有 **8 个质点**（24 顶点焊成 8 个角）。一个单胞立方体可以整体翻面、
  也可以鼓到 **5.5 倍**体积 —— 质点太少时"体积约束"没有任何空间分辨率去抵抗变形。
  **结论：8 个角的盒子不适合当软体**，要么细分网格，要么换布料/质点弹簧。
- `prop_clock_tower` 3.9 × 7.6 × 3.9 米、473 个质点，默认参数下保持率 **-1.308**（翻了面）。
  它不是"网格太粗"，是**尺寸**问题：质点质量固定 1 kg，7 米高的果冻被自身重量压穿体积约束。

---

## 5. 构建拒绝的原文（note 列）

这两个模型不是"效果不好"，是**几何不能成立**：

```text
prop_cave_entrance  三角形 83 在焊接容差 0.0001 下退化成一条线，请缩小 weldTolerance 或简化网格
Structure_Mesh      三角形 58 面积为 0（三点共线），体积梯度会失效
```

`prop_cave_entrance` 那条值得注意：**0.0001 米**的容差下第 83 号三角形就退化了，说明它存在近乎重合的顶点对（焊接后三点落到同一条线上）。通常是资产侧的重复顶点，但也可能是容差对这张模型的尺度偏大 —— 两种情况都把 `weldTolerance` 调小重跑一次就能分开。

---

## 6. 按尺寸放大参数：救 6 个，打坏 1 个

`RecommendedParameters(diagonal)` 把刚度按包围盒对角线线性放大（1–8 倍），子步 `4 + (diagonal-1)`。
同一批 104 个网格再扫一遍：

| 模型 | 默认保持率 | 推荐保持率 | 判定变化 |
| --- | --- | --- | --- |
| prop_tree_pine_tall | 0.447 | 1.044 | Unstable → **Healthy** |
| prop_tree_round | 0.353 | 1.185 | Unstable → **Healthy** |
| prop_tree_sakura | 0.514 | 1.153 | Unstable → **Healthy** |
| prop_mushroom_cluster | 0.352 | 0.734 | Unstable → **Healthy** |
| prop_reed | 0.245 | 0.693 | Unstable → **Healthy** |
| Box_350x250x200_Mesh | 5.518 | 0.709 | Unstable → **Healthy** |
| **Tunnel_Mesh** | **1.323** | **0.208** | **Healthy → Unstable** ⚠️ |

汇总行：**好转 6 | 不变 97 | 变差 1 | 换了推荐参数仍然不健康 20**。

这是一次**权衡而不是免费午餐**，必须这样写进文档：

- 树的共性死法（高瘦 + 自身重量）确实被"更硬 + 更多子步"救回来了；
- 但 `Tunnel_Mesh`（2.5 × 6.0 × 2.5 米的筒）在放大后被**过冲压扁**：更硬的体积约束配更硬的弹簧，
  在同一步长下反而更容易冲过 0。
- **所以它是"诊断建议"，不是包的新默认值。** 默认值保持保守，扫描报告负责告诉你该改哪一个。

20 个换了参数仍不健康的，几乎全是 `FactoryRoom[*]` 的零厚度壳 / 反向绕序子网格和敞口箱子 ——
**瓶颈在美术资产，不在求解器**。

---

## 7. 成本：预算线为什么设在 4000 顶点

| 样本 | 质点 | ms/步 |
| --- | --- | --- |
| 预算线内 104 个网格 | 中位 67 | 中位 **0.140**，p90 **0.394**，最大 **2.198**（`FactoryRoom[7]`，552 质点） |
| `terrain_island` | 2497 | **5.374** |
| `IslandMesh_Showcase` | 2497 | **5.394** |
| `WaterPlane_Showcase` | 5329（焊接比 1.00，无重复顶点） | **12.359** |

后三个正是被预算线跳过的（顶点数 > 4000），数字来自加预算线之前那次全量扫描。
**5329 个质点一步 12.4 毫秒** = 单帧 60 fps 预算的 74%，而它还是个不闭合的地形面（没有体积回弹）。
预算线不是洁癖，是把"注定不能用的量级"在扫描阶段就挡下来。

---

## 8. API 用法

```csharp
using PhysicsSimulation;
using UnityEngine;

var data = SoftBodyMeshData.FromMesh(mesh);          // 任意 Mesh → 顶点 + 三角形
var result = SoftBodyMeshAudit.Audit(data, steps: 90, addGround: true);

Debug.Log(result);                                   // 一行说清判定与全部数字
if (result.Verdict != SoftBodyAuditVerdict.Healthy)
    Debug.LogWarning(SoftBodyMeshAudit.Note(result)); // 原因原文
```

`SoftBodyMeshAudit` 永不对脏输入抛异常 —— 构建失败会被记成 `BuildFailed` 并把原因写进 `BuildError`，
这正是扫描能一次跑完 104 个模型的前提。

---

## 9. 这份实测的边界（别把它读成"包的质量保证"）

- **只跑 90 步（0.6 秒模拟时间）**。慢速失稳（几十秒后才开始漏体积）测不出来。
- **地面是一张无限大的半空间平面**，不是演示场景里的 `BoxCollider`；换场景碰撞时结论要重跑。
- **单体审计**，不含体与体之间的碰撞（包本身也不提供自碰撞）。
- 参数是**运行时默认值**或 §6 那条固定启发式，不是逐个模型调优后的结果。
- 判定只看"闭合 / 体积 / 拉伸 / 数值健康"，**不看观感**。`Healthy` 不等于"看着像好吃的果冻"。
- 样本是**这一个工程**的美术资产（低多边形道具 + 楼梯生成器的产物），不是通用模型基准。
- 104 行里有**近重复几何**：`terrain_island` 与 `IslandMesh_Showcase` 的顶点/质点/包围盒完全一致（140 × 14.35 × 140，2497 质点），几乎肯定是同一张地形被导入了两份。比例数字按"行"统计，不是按"唯一几何"统计。
- 另一类**不是**重复：7 个岩石变体都是 60 顶点 / 12 质点，`prop_bench` 与 `prop_fence` 都是 96 / 32 —— 它们是不同的资产，只是同一家族的拓扑规模相同。
