# 快速上手

## 10 秒版

1. **Tools → Physics Simulation → Create Demo Scene**
2. 按 **Play**

你会看到一条从天花板吊点垂下的 5 节链子，从侧偏 38° 的姿态释放后摆动：上面几节几乎不形变，下面几节被甩得明显拉长——这就是"逐节变软"的效果。Scene 视图里质点画成线框球（黄色 = 固定点，青色 = 活动点），弹簧按应变着色（**拉伸偏红、压缩偏蓝**）。

## 10 行版：在场景里挂一个

1. 空物体上 **Add Component → Physics Simulation / Mass Spring Behaviour**
2. 在 Inspector 的 **Particles** 里加两行：第一行勾上 `pinned`（吊点），第二行填质量与位置
3. 在 **Springs** 里加一行：`a = 0`、`b = 1`、`stiffness = 200`、`restLength` **留 0**（自动取两端初始距离）
4. 按 **Play**

![演示链条的结构](/diagram-chain.svg)

## 纯代码版：不碰 MonoBehaviour

```csharp
using PhysicsSimulation;
using UnityEngine;

var system = new MassSpringSystem();
system.Parameters.gravity   = new Vector3(0f, -9.81f, 0f);
system.Parameters.substeps  = 8;                       // 稳定性的主要手段
system.Parameters.globalDamping = 0.6f;                // 1/秒

int anchor = system.AddParticle(new Vector3(0f, 3f, 0f), mass: 1f, pinned: true);
int bob    = system.AddParticle(new Vector3(0.6f, 2.6f, 0f), mass: 0.8f);
system.AddSpring(anchor, bob, restLength: 0f, stiffness: 900f, damping: 1.5f);  // restLength<=0 → 自动取初始距离

// 谁爱调用谁：FixedUpdate 里喂 Time.fixedDeltaTime
system.Step(Time.fixedDeltaTime);
Debug.Log($"摆锤位置 {system.Particles[bob].position}，最大速度 {system.MaxSpeed():F3} m/s");
```

::: tip 为什么 `restLength` 填 0
`AddSpring` 里 `restLength <= 0` 表示"按两端**当前**距离自动算"。手动量一遍再填数是最常见的错（差 1 厘米，链子一开场就自己晃）。要显式控制就直接填正数。
:::

## 让它落在地上（v1.3.0）

三个组件共用同一组开关：

```csharp
massSpring.collideWithSceneColliders = true;
cloth.collideWithSceneColliders = true;
soft.collideWithSceneColliders = true;
// 谁的 sceneColliders 就填谁的 List<Collider>，例如地面
soft.sceneColliders = new List<Collider> { ground.GetComponent<BoxCollider>() };
```

::: warning 地面不能是 `CreatePrimitive(Plane)`
Plane 自带 **MeshCollider**，而本版只做 primitive 解析碰撞（球 / OBB 盒 / 胶囊 / 半空间），
`MeshCollider` 会被 `ColliderProxies.TryFrom` 返回 `null` 跳过 —— 结果就是"场景里明明有地面，还是穿过去"。
用 Cube + `BoxCollider` 当本地面，或直接给逻辑层喂一个 `PlaneCollisionProxy`。
:::

默认 `false`：不注入变换矩阵、不生成代理，于是模拟与 v1.2.0 **逐位一致**。想确认到底有没有生效，
用 `Tools ▸ Physics Simulation ▸ Soft Body ▸ Dump State`，它会报出代理个数与"最低质点高出地面上表面多少"。

## 让它动起来：三种驱动方式

| 方式 | 怎么做 | 适用 |
| --- | --- | --- |
| `MassSpringBehaviour` | 挂组件、填列表、播放 | 关卡里的一段绳、吊着的招牌 |
| 自己 new 系统 | 在任意 `MonoBehaviour.FixedUpdate` 里 `Step(Time.fixedDeltaTime)` | 需要自己掌控生命周期（比如按 `substeps` 做定长步回放） |
| `MassSpringParticleLink` | 给一个 Transform 挂上，填 `particleIndex`，`LateUpdate` 自动跟随 | 想让看得见的球跟着质点走 |

::: warning 只用 `Time.fixedDeltaTime`，不要用 `Time.deltaTime`
把可变帧间隔的 `deltaTime` 直接喂给物理，会得到"帧率越高摆得越快"的经典 bug。本包在 `Step()` 里还额外做了 `maxDeltaTime` 钳制（默认 1/15 秒），但**钳制是兜底，不是让你随便喂**：请始终在 `FixedUpdate` 里推进。
:::

## 另外两个模块：一行起步

上面是质点弹簧。另外两个模块各自最短的可用路径：

```csharp
using PhysicsSimulation;
using UnityEngine;

// 布料：一面 20×14、顶边钉住、朝 +Z 吹风的旗
var cloth = gameObject.AddComponent<ClothBehaviour>();
cloth.parameters = new ClothParameters { columns = 20, rows = 14, spacing = 0.1f };
cloth.pinEdges = ClothPinEdges.Top;     // 只钉顶边
cloth.Rebuild();

// 软体：把任意网格变成会瘪又会鼓回来的果冻
var soft = gameObject.AddComponent<SoftBodyBehaviour>();
soft.sourceMesh = someMesh;                       // 顶点会被焊接，三角形边自动成弹簧
soft.pinMode = SoftBodyPinMode.BottomVertices;    // 钉住最低一层
soft.initialVelocity = new Vector3(2.4f, 0f, 0f); // 推一把（必须走这个字段，见软体文档）
soft.Rebuild();
```

两者都在 `FixedUpdate` 里自己推进（`autoSimulate` 为真时），也都能 `Step(dt)` 手动驱动。
不想写代码就用菜单：**Tools → Physics Simulation → Cloth / Soft Body → Create … Demo Scene**。

## 下一步

- 公式、参数含义、稳定性推导：[质点弹簧](/mass-spring/) · [布料](/cloth/) · [软体](/soft-body/)
- 字段速查：[质点弹簧参数](/reference/mass-spring-parameters) · [布料参数](/reference/cloth-parameters) · [软体参数](/reference/soft-body-parameters)
- 出问题时怎么查：[编辑器工具](/tools/)
