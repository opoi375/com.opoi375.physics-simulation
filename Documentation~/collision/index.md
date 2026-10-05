# 碰撞代理（v1.3.0）

v1.0.0 ~ v1.2.0 三个阶段里，"碰撞"这件事一直是缺席的：布料的球障碍只能填坐标、软体直接穿过地面、质点弹簧连检测都没有。
v1.3.0 把它补上，但**补法有讲究**——一旦求解器内部去读场景，"同参数同步数跑两次逐位一致"这条从 v1.0.0 就立下的承诺就没了。

## 一句话结论

::: info
求解器**不认识** `Collider`，只认识注入进来的**碰撞代理**。
Unity 层负责把场景里的 `Collider` 采样成代理喂进去；纯逻辑层依然只对 `(位置, 几何)` 做算术，
所以每一条断言都能闭式写出来，不需要建场景、不需要等物理步、也不会随 Unity 版本漂。
:::

```csharp
using PhysicsSimulation;
using UnityEngine;

// 逻辑层：手动喂几何，完全可单测
soft.Collisions.Add(new PlaneCollisionProxy(Vector3.zero, Vector3.up), CollisionProxySpace.World);
soft.Step(Time.fixedDeltaTime);

// Unity 层：把场景里的 Collider 交给它
behaviour.collideWithSceneColliders = true;
behaviour.sceneColliders = new List<Collider> { groundCollider };
behaviour.Rebuild();
```

## 契约：`PushOut` 的四条规矩

代理只有一个方法：

```csharp
public interface ICollisionProxy
{
    Vector3 PushOut(Vector3 point, float skin);   // 把点推到体外至少 skin
}
```

四条规矩全都有测试锁着，改语义会先红：

1. **点在体外 → 原样返回，逐位不变。** 不允许"算了又没改"引入浮点误差——否则静止放置的软体会自己抖起来。
2. **点在体内 → 顶到体外。** 方向唯一确定，不看速度、不看历史，所以同一输入永远同一输出。
3. **方向未定义（球心重合 / 落在轴线上 / 正好贴在表面上）→ 用固定的备用轴（`+Y`），绝不产生 NaN。**
   这是 v1.1.0 布料球障碍就有的约定，v1.3.0 把它抬成了接口契约。
4. **`skin` 为负按 0 处理。** 非法值不许把点往几何体里面塞。

## 支持哪些几何

| 代理 | 语义 | 体内脱出方向 | 备注 |
| --- | --- | --- | --- |
| `SphereCollisionProxy` | 球 | 沿径向 | 算术与 v1.1.0 布料球障碍**逐字相同**，有逐位对照测试 |
| `BoxCollisionProxy` | 方向包围盒（OBB） | 沿**穿透最浅**的那个面；体外则沿最近表面点法线 | 支持任意旋转；薄板落地从最近面脱出，不会被甩到侧面 |
| `CapsuleCollisionProxy` | 线段 + 半径 | 柱身沿径向、端帽按球 | 轴段退化（长度 0）时整体退化成球，不崩 |
| `PlaneCollisionProxy` | **半空间** | 一律顶回法线一侧 | 见下——软体能落地的关键 |

::: warning 平面是半空间，不是"无限薄的墙"
`PlaneCollisionProxy` 没有"体内多深"的概念：质点穿到地面以下 3 米，也一律顶回**面上 + skin**。
如果按"减掉穿透深度"实现，高速下坠时一帧就能穿过去、然后被留在地下——软体被地面吞掉就是这么来的。
法线在构造时归一化，非单位法线不许放大推出距离（有测试）。
:::

::: danger 不支持 MeshCollider / Terrain，而且不会拿包围盒冒充
本版按决定**只做 primitive 解析**。`ColliderProxies.TryFrom` 遇到 `MeshCollider`、`Terrain`、被禁用的碰撞体
返回 `null`，然后被跳过。

这不是偷懒：拿包围盒冒充一面有洞的墙，会让画面里明明有个门、软体却撞在空气上——
"某个碰撞体没生效"比"凭空多出一面看不见的墙"好查得多。Dump State 会直接报告收到了几个代理。

:::

## 空间：`Simulation` 还是 `World`

求解器永远在自己的**模拟空间**里跑（质点弹簧是与世界无关的裸坐标，布料/软体是组件**局部**坐标）。
每条代理登记时都要说明自己在哪个空间：

```csharp
set.Add(sphere, CollisionProxySpace.Simulation);  // 直接和局部坐标比算
set.Add(box,    CollisionProxySpace.World);       // 质点变到世界 → 推出 → 变回局部
```

于是同一份求解器里两种来源可以共存：布料旧的 `obstacles`（局部球，v1.1.0 语义原封不动）
和新桥接进来的世界 Collider 可以同时挂在一个物体上。

::: tip 为什么世界空间的代理要绕一圈变换
因为另一条路更糟：把世界 Collider **折算进**局部空间，球会变成椭球（非均匀缩放）、盒会变成斜 parallelepiped
（父级既旋转又非均匀缩放）。v1.2.0 之前布料就是这么干的——拿 `lossyScale.magnitude / √3` 近似一个椭球，
缩放一歪布料就"陷进"石头里半米。改成质点往返世界空间之后，盒/胶囊是**精确**的。
:::

球仍然有近似：非均匀缩放的 `SphereCollider` 在数学上是个椭球，桥接层取**最大外接轴**，
宁可稍微厚一点，也不让质点钻进看得见的外面。文档级别的老实话，写在这里。

## 半隐式欧拉与 PBD 对碰撞的处理不一样

这是 v1.3.0 里唯一一个"不看代码绝对猜不到"的点：

- **布料（PBD）** 每个子步最后用 `v = (pos - prev) / h` 回算速度。位置被投影修正了，速度**自动**就跟着修正了。
- **质点弹簧 / 软体（半隐式欧拉）** 没有这种回算。如果只把质点顶到体外而不动速度，
  法向速度会**一路累积**：位置被地面卡住、速度每秒加 9.81，跑久了不是飞走就是 NaN。

所以碰撞实现里对质点版本多做了一件事——**把"还在往几何体里扎"的那一份速度减掉**：

```csharp
Vector3 normal = correction.normalized;            // 修正量方向就是外法线
float intoSurface = Vector3.Dot(velocity, normal);
if (intoSurface < 0f) velocity -= normal * intoSurface;
```

只削法向、切向原样保留 ⇒ 物体会沿着斜面**滑**，而不是粘在上面。没有摩擦系数可调（v1 不做）。

实测：一个质点落在平面上跑 1.5 秒，速度上界 6 m/s 以内、位置不低于 `平面 + skin`——
这条就是 `MassSpring_ParticleFallingOnGroundPlaneRestsInsteadOfSinking`，
断言的正是"速度有界"而不是"位置正确"，因为只顶位置漏了削速度时位置看起来也是对的。

## 与旧行为逐位一致（默认关，且关了就等于没这个版本）

`collideWithSceneColliders` 默认 **false**。关掉时：

- 不注入变换矩阵（单位矩阵），于是质点坐标压根不进 `Matrix4x4` 乘法；
- 不生成任何代理，于是碰撞整段跳过。

布料的球障碍路径另有逐位对照测试：把 v1.2.0 那段算术原样抄进测试，对 13 个刻意造的样本
（体内 / 体外 / 贴面 / 球心重合 / 极接近阈值）比 `BitConverter.SingleToInt32Bits`。

::: info 演示场景里地面从 Plane 换成了带 BoxCollider 的扁盒子
`GameObject.CreatePrimitive(PrimitiveType.Plane)` 自带的是 **MeshCollider** —— 正好是本版不支持的那种。
如果演示还用 Plane，就会出现"场景里明明有地面，软体还是穿过去"这种最难查的现象。
所以 `SoftBodyDemoTools` 现在造地面用 Cube + `BoxCollider`。自己项目里想让软体落地，同理：**给地面一个盒/球/胶囊碰撞体**。
:::

实测（`Tools ▸ Physics Simulation ▸ Soft Body ▸ Dump State`，Play 中）：

```
SoftBodyJelly：钉住 0 | 碰撞代理 1 个
质点世界 y 最低 -0.0100 | 盒子地面上表面 -0.0200 ⇒ 最低质点高出 0.0100
体积保持率 0.981 | 最大拉伸比 1.0893 | 非有限状态 False
```

最低质点高出地面正好等于 `collisionThickness`（0.01）；整块软体**一个质点都没钉**，
自由落体砸在地面上压扁 1.9% 又被体积约束撑回来。

## 参数与开销

| 字段 | 位置 | 默认 | 说明 |
| --- | --- | --- | --- |
| `collisionThickness` | `ClothParameters` / `MassSpringParameters` / `SoftBodyParameters` | 0.01 m | 皮肤厚度；世界空间代理会按缩放折算 |
| `collideWithSceneColliders` | 三个 Behaviour | `false` | 打开才注入矩阵、才读 Collider |
| `sceneColliders` | 三个 Behaviour | 空 | 参与碰撞的 Collider 列表 |
| `updateCollidersEveryStep` | 三个 Behaviour | `false` | 碰撞体会动/会开关时再开，每步重建代理列表（会分配内存） |

开销是 `O(质点数 × 代理数)` 每个子步，纯算术、无分配（`updateCollidersEveryStep` 关掉时）。
v1.3.0 **没有做任何性能优化**：并行求解器仍然排在 v1.4.0。

## 本版仍然不做

- `MeshCollider` / `Terrain` / 任意凸多面体；
- 摩擦、弹性系数、恢复系数（只有"法向削掉、切向保留"这一种响应）；
- 自碰撞（布料穿过自己、软体穿过自己）；
- 三角形/体元级相交——全部是**质点**级；
- 与 `Rigidbody` 互驱：既不会让刚体带动锚点，也不会把力还给刚体。软体能压住箱子，箱子不会动。
- 碰撞事件回调（`OnCollisionEnter` 风格）。

::: warning 求解器不查场景，所以关掉的碰撞体不会自动生效
代理是在 `Rebuild()`（或 `updateCollidersEveryStep` 打开时的每步）时读一次几何快照。
运行时把 `Collider.enabled` 翻成 false，得重开 `updateCollidersEveryStep` 或手动 `Rebuild()` 才反映出来。
:::
