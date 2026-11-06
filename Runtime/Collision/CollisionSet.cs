// Copyright (c) 2026 PhysicsSimulation. MIT License.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace PhysicsSimulation
{
    /// <summary>一条已登记的碰撞代理：几何 + 它所在的空间 + 谁登记的。</summary>
    public struct CollisionEntry
    {
        public ICollisionProxy Proxy;
        public CollisionProxySpace Space;

        /// <summary>
        /// 登记者。null = 无主（脚本手工塞的）；否则是拥有它的对象（比如 Collider 桥、互碰组）。
        /// 有了它，“刷新自己那批”才不会顺手把别人的代理一起清掉 —— 这是互碰能跟
        /// <c>collideWithSceneColliders</c> 共存的前提（以前桥接每步一次 <c>ClearWorldSpace()</c>，
        /// 会把同空间的互碰代理抹干，表现为“编辑器里接上了、跑起来一点不碰”）。
        /// </summary>
        public object Owner;

        public CollisionEntry(ICollisionProxy proxy, CollisionProxySpace space)
        {
            Proxy = proxy;
            Space = space;
            Owner = null;
        }

        public CollisionEntry(ICollisionProxy proxy, CollisionProxySpace space, object owner)
        {
            Proxy = proxy;
            Space = space;
            Owner = owner;
        }
    }

    /// <summary>
    /// 碰撞代理列表。三个求解器共用同一份语义。
    ///
    /// 迭代顺序 = 插入顺序，且**不排序、不去重、不并行乱序**：
    /// 多个代理同时命中同一个质点时，"谁最后起作用"会影响结果，所以顺序本身就是确定性契约的一部分。
    /// </summary>
    public sealed class CollisionSet
    {
        readonly List<CollisionEntry> _entries = new List<CollisionEntry>();

        public int Count { get { return _entries.Count; } }

        public CollisionEntry this[int index] { get { return _entries[index]; } }

        public void Add(ICollisionProxy proxy, CollisionProxySpace space)
        {
            if (proxy == null)
            {
                throw new ArgumentNullException("proxy", "碰撞代理不允许为 null：不支持的 Collider 应当在外面先判掉");
            }
            _entries.Add(new CollisionEntry(proxy, space));
        }

        /// <summary>带登记者地登记，给“每步重建自己那批”的组件用（Collider 桥、互碰组）。</summary>
        public void Add(ICollisionProxy proxy, CollisionProxySpace space, object owner)
        {
            if (proxy == null)
            {
                throw new ArgumentNullException("proxy", "碰撞代理不允许为 null：不支持的 Collider 应当在外面先判掉");
            }
            _entries.Add(new CollisionEntry(proxy, space, owner));
        }

        public void Add(ICollisionProxy proxy)
        {
            Add(proxy, CollisionProxySpace.Simulation);
        }

        /// <summary>
        /// 按**引用**摘掉一个代理（不是按相等），返回摘掉几个。
        /// 互碰组需要它：体重建、退出组、组被停用时都必须把线拆干净，
        /// 否则留下的就是一个“不再更新的旧云” —— 比完全不碰更难查。
        /// 不排序、不去重、不动其余条目的顺序，所以剩下的代理裁决顺序与从前一致。
        /// </summary>
        public int Remove(ICollisionProxy proxy)
        {
            if (proxy == null)
            {
                throw new ArgumentNullException("proxy", "要摘掉的代理不允许为 null：要清空请用 Clear()");
            }
            int removed = 0;
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(_entries[i].Proxy, proxy))
                {
                    _entries.RemoveAt(i);
                    removed++;
                }
            }
            return removed;
        }

        /// <summary>
        /// 只清掉世界空间的条目。
        /// Unity 层每次重读 Collider 时用它：桥接进来的碰撞体一律登记成 World，
        /// 而脚本/旧 API 手工塞进模拟空间的代理（比如布料的 <c>AddSphereObstacle</c>）不会被误删。
        /// </summary>
        public void ClearWorldSpace()
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (_entries[i].Space == CollisionProxySpace.World) _entries.RemoveAt(i);
            }
        }

        /// <summary>
        /// 只清掉**指定登记者**的世界空间条目（按引用比登记者）。
        /// 这是与 <see cref="ClearWorldSpace()"/> 的关键区别：后者会把别人登记的也一锅抹，
        /// 前者只回收自己放的那一批。
        /// </summary>
        public int ClearWorldSpaceOwnedBy(object owner)
        {
            if (owner == null)
            {
                throw new ArgumentNullException("owner", "要清全部请用 ClearWorldSpace()，传 null 到这里是写错了");
            }
            int removed = 0;
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (_entries[i].Space == CollisionProxySpace.World && ReferenceEquals(_entries[i].Owner, owner))
                {
                    _entries.RemoveAt(i);
                    removed++;
                }
            }
            return removed;
        }

        /// <summary>
        /// 只清掉**模拟空间**的条目。旧的布料障碍物刷新路径用它：
        /// 每次重读自己那批球之前，先把上一批同空间的清掉，但不能连桥接进来的世界空间碰撞体一起槓掉。
        /// </summary>
        public void ClearSimulationSpace()
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (_entries[i].Space == CollisionProxySpace.Simulation) _entries.RemoveAt(i);
            }
        }

        public void Clear()
        {
            _entries.Clear();
        }

        internal List<CollisionEntry> Entries { get { return _entries; } }
    }

    /// <summary>
    /// 把碰撞代理应用到质点上。三种求解器的唯一实现，避免"三套推出公式各自漂移"。
    ///
    /// 位置数组版（布料）：只改位置——PBD 后面会用 (pos - prev) / h 回算速度，投影本身就把速度带正了。
    /// 质点版（质点弹簧 / 软体）：半隐式欧拉**没有**这种回算，只顶位置的话法向速度会一路累积，
    /// 于是这里额外把"穿入方向"的速度分量抹掉。这是两条路径必须分开的原因，不是重复代码。
    /// </summary>
    static class CollisionPass
    {
        public static void ResolvePositions(CollisionSet set, Vector3[] positions, float skin,
                                            Matrix4x4 localToWorld, Matrix4x4 worldToLocal, bool worldIsIdentity)
        {
            ResolvePositions(set, positions, skin, localToWorld, worldToLocal, worldIsIdentity, null);
        }

        /// <summary>
        /// 同上，另外把**每个粒子被推出去的方向**累加进 <paramref name="contacts"/>（模拟空间，
        /// 未归一化，调用方按需要归一）。给需要"接触法向"的求解器用：位置投影在物理上是
        /// 约束而不是冲量，把它读进速度里会让流体自己把自己弹射出去（见流体 <c>RemoveBounce</c>）。
        /// 传 null 就是原来的行为，逐位不变。
        /// </summary>
        public static void ResolvePositions(CollisionSet set, Vector3[] positions, float skin,
                                            Matrix4x4 localToWorld, Matrix4x4 worldToLocal, bool worldIsIdentity,
                                            Vector3[] contacts)
        {
            ResolvePositions(set, positions, skin, localToWorld, worldToLocal, worldIsIdentity, contacts, null);
        }

        /// <summary>
        /// 同上，另外可传每个质点的**逆质量**：<c>&lt;= 0</c> 的质点一律不动。
        /// 传 null 就是原来的行为（逐位不变），给根本没有“钉住”概念的求解器（流体）用。
        /// 这不是锦上添花，而是补漏洞：<see cref="ResolveParticles"/> 一开始就有这条设卡
        /// （“固定点不参与碰撞：它由脚本钉着，顶它没意义”），而基于 <c>Vector3[]</c> 的这条路径漏了，
        /// 于是同一个包里，软体/质点弹簧的钉住点顶不动、布料的钉住点会被 Collider 或互碰顶跑。
        /// </summary>
        public static void ResolvePositions(CollisionSet set, Vector3[] positions, float skin,
                                            Matrix4x4 localToWorld, Matrix4x4 worldToLocal, bool worldIsIdentity,
                                            Vector3[] contacts, float[] inverseMass)
        {
            List<CollisionEntry> entries = set.Entries;
            for (int e = 0; e < entries.Count; e++)
            {
                CollisionEntry entry = entries[e];

                if (entry.Space == CollisionProxySpace.Simulation || worldIsIdentity)
                {
                    // 模拟空间（或变换本来就是单位矩阵）：直接对局部位置算，逐位等价于 v1.2.0 的旧路径
                    for (int i = 0; i < positions.Length; i++)
                    {
                        if (inverseMass != null && inverseMass[i] <= 0f) continue;   // 钉住的：由脚本拿着，顶它没意义
                        Vector3 before = positions[i];
                        positions[i] = entry.Proxy.PushOut(before, skin);
                        if (contacts != null)
                        {
                            Vector3 push = positions[i] - before;
                            if (push.sqrMagnitude > 1e-12f) contacts[i] += push.normalized;
                        }
                    }
                    continue;
                }

                float worldSkin = skin * AverageScale(localToWorld);
                for (int i = 0; i < positions.Length; i++)
                {
                    if (inverseMass != null && inverseMass[i] <= 0f) continue;      // 同上：钉住的不参与
                    Vector3 world = localToWorld.MultiplyPoint(positions[i]);
                    Vector3 resolved = entry.Proxy.PushOut(world, worldSkin);
                    if (resolved != world)
                    {
                        if (contacts != null)
                        {
                            // 法向用 worldToLocal 的旋转部分搬回模拟空间；变换带缩放时这是近似
                            Vector3 push = worldToLocal.MultiplyVector(resolved - world);
                            if (push.sqrMagnitude > 1e-12f) contacts[i] += push.normalized;
                        }
                        positions[i] = worldToLocal.MultiplyPoint(resolved);
                    }
                }
            }
        }

        public static void ResolveParticles(CollisionSet set, IReadOnlyList<Particle> particles, float skin,
                                            Matrix4x4 localToWorld, Matrix4x4 worldToLocal, bool worldIsIdentity)
        {
            List<CollisionEntry> entries = set.Entries;
            for (int e = 0; e < entries.Count; e++)
            {
                CollisionEntry entry = entries[e];

                for (int i = 0; i < particles.Count; i++)
                {
                    Particle particle = particles[i];
                    if (particle.inverseMass <= 0f) continue;          // 固定点不参与碰撞：它由脚本钉着，顶它没意义

                    if (entry.Space == CollisionProxySpace.Simulation || worldIsIdentity)
                    {
                        Vector3 resolved = entry.Proxy.PushOut(particle.position, skin);
                        ApplyCorrection(particle, particle.position, resolved);
                        continue;
                    }

                    float worldSkin = skin * AverageScale(localToWorld);
                    Vector3 world = localToWorld.MultiplyPoint(particle.position);
                    Vector3 worldResolved = entry.Proxy.PushOut(world, worldSkin);
                    if (worldResolved == world) continue;
                    Vector3 localResolved = worldToLocal.MultiplyPoint(worldResolved);
                    ApplyCorrection(particle, particle.position, localResolved);
                }
            }
        }

        /// <summary>写回位置，并把"还在往几何体里扎"的那一份速度减掉（只削法向，切向保留 ⇒ 滑动而不粘住）。</summary>
        static void ApplyCorrection(Particle particle, Vector3 before, Vector3 after)
        {
            if (after == before) return;

            Vector3 correction = after - before;
            particle.position = after;

            float correctionSqr = correction.sqrMagnitude;
            if (correctionSqr <= 0f) return;                            // 没有位移量 ⇒ 没有法线可言

            float length = (float)Math.Sqrt(correctionSqr);
            Vector3 normal = correction * (1f / length);
            float intoSurface = Vector3.Dot(particle.velocity, normal);
            if (intoSurface < 0f)
            {
                particle.velocity = particle.velocity - normal * intoSurface;
            }
        }

        /// <summary>变换矩阵三个轴列向量的平均长度，用来把"模拟空间的皮肤"折算成"世界空间的皮肤"。</summary>
        static float AverageScale(Matrix4x4 localToWorld)
        {
            float x = new Vector3(localToWorld.m00, localToWorld.m10, localToWorld.m20).magnitude;
            float y = new Vector3(localToWorld.m01, localToWorld.m11, localToWorld.m21).magnitude;
            float z = new Vector3(localToWorld.m02, localToWorld.m12, localToWorld.m22).magnitude;
            float average = (x + y + z) / 3f;
            return average > 1e-6f ? average : 1f;
        }
    }
}
