// Copyright (c) 2026 PhysicsSimulation. MIT License.
using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using PhysicsSimulation;
using PhysicsSimulation.EditorTools;
using UnityEngine;

namespace PhysicsSimulation.Editor.Tests
{
    /// <summary>
    /// 接触的能量表现 + 互碰球半径的可调性（v1.6.0）。
    ///
    /// 这个文件是**实测标定**出来的，不是设想出来的。本轮为了搞清"两个软体为什么会互相弹开 3 m"，
    /// 先立了两个假设，又被自己的数据各自否掉，最后量到真正的原因。过程留在注释里，
    /// 因为它比结论更容易被后人重新犯一遍：
    ///
    ///   假设①「布料把位置修正量吐成速度 ⇒ 弹射」。
    ///        实测（三块材料自由落体到同一面地板，150 步）：
    ///          质点弹簧 离壁峰值 0.000 / 末速 0.000
    ///          软体      离壁峰值 0.760 / 末速 0.004
    ///          布料      离壁峰值 1.204 / 末速 0.000
    ///        ⇒ 落定之后三者法向速度都归零，**没有持续注入**。峰值是材料自己的弹性回弹，是物理。
    ///          质点弹簧注入量为 0 却照样出现 3 m 漂移，直接把这条否掉。
    ///
    ///   假设②「云半径太大」。
    ///        系数 0.75 → 0.5：need 从 0.300 降到 0.228，漂移从 3.26 降到 2.94。
    ///        方向对，但量级不够 ⇒ 它不是"改错了值"，而是球云接触的**固有几何代价**。
    ///
    ///   真因（实测，240 步零载荷，两个 0.6 m 软体，只改网格密度从而只改半径）：
    ///        间距   质点数  r      need    240步后最近球心距   体心漂移
    ///        sub=3   56    0.114  0.228   2.13                2.94
    ///        sub=4   98    0.085  0.171   1.13                1.96
    ///        sub=5  152    0.068  0.137   0.68                1.55
    ///        sub=6  218    0.057  0.114   0.59                1.36
    ///     球云规定"两球心相距 < 2r 就算穿透"，所以**两个体的表面天生至少要隔开约一个质点间距**。
    ///     硬摆得比这更近，求解器就必须把壳掰凹进去那么多；凹量存成弹簧能（½·k·δ² ≈ 24 J），
    ///     摊到 56 个质点上 ≈ 0.9 m/s，与实测的弹射速度吻合，漂移随 need 线性下降。
    ///     结论：这是**颗粒化接触的代价**，不是算法缺陷。本版按用户决定作为已知限制交付，
    ///     并把半径做成可调（默认 0.5），同时把"想安静堆叠就加密网格"写进文档。
    /// </summary>
    public class InelasticContactTests
    {
        const float Skin = 0.02f;

        static readonly List<UnityEngine.Object> Trash = new List<UnityEngine.Object>();

        static T Track<T>(T o) where T : UnityEngine.Object { Trash.Add(o); return o; }

        GameObject Spawn(string name)
        {
            var go = new GameObject(name);
            Trash.Add(go);
            return go;
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = Trash.Count - 1; i >= 0; i--)
            {
                if (Trash[i] != null) UnityEngine.Object.DestroyImmediate(Trash[i]);
            }
            Trash.Clear();
        }

        // ================================================================ ① 接触落定之后不许留法向速度

        [Test]
        public void Cloth_FallingOntoPlane_RestsWithoutSeparatingSpeed()
        {
            // Given 一张不钉住的布，自由落体到一块地板上
            var cloth = new ClothSimulation(new ClothParameters
            {
                columns = 6, rows = 4, spacing = 0.1f, substeps = 2, iterations = 2,
                gravity = new Vector3(0f, -9.81f, 0f), collisionThickness = Skin,
            });
            const float floorY = -0.6f;
            cloth.Collisions.Add(new PlaneCollisionProxy(new Vector3(0f, floorY, 0f), Vector3.up),
                CollisionProxySpace.World);

            // When 落定之后再走一段
            for (int i = 0; i < 150; i++) cloth.Step(1f / 60f);

            // Then 落定之后**不许有朝外的速度**（实测 0.000）。
            //      门槛只钉"末速"，不钉"落地瞬间的峰值"（实测 1.204）：峰值是布的弹性回弹，
            //      是材料属性不是缺陷；把它当缺陷锁死会逼实现把正常的回弹也抹掉。
            float worstOutward = 0f;
            for (int i = 0; i < cloth.ParticleCount; i++)
            {
                if (cloth.IsPinned(i)) continue;
                float outward = Vector3.Dot(cloth.GetVelocity(i), Vector3.up);
                if (outward > worstOutward) worstOutward = outward;
            }
            Assert.That(cloth.HasNonFiniteState(), Is.False, "接触之后不许出现 NaN");
            Assert.That(worstOutward, Is.LessThanOrEqualTo(0.3f),
                "布料落定 150 步之后仍有朝外速度 " + worstOutward.ToString("F3") + " m/s（实测基线 0.000）");
        }

        [Test]
        public void Cloth_AtRestOnPlane_DoesNotCreepUpward()
        {
            // Given 同上，先让它落到贴住
            var cloth = new ClothSimulation(new ClothParameters
            {
                columns = 6, rows = 4, spacing = 0.1f, substeps = 2, iterations = 2,
                gravity = new Vector3(0f, -9.81f, 0f), collisionThickness = Skin,
            });
            const float floorY = -0.6f;
            cloth.Collisions.Add(new PlaneCollisionProxy(new Vector3(0f, floorY, 0f), Vector3.up),
                CollisionProxySpace.World);

            for (int i = 0; i < 90; i++) cloth.Step(1f / 60f);
            float settled = MeanHeight(cloth);

            // When 再走同样长的一段
            for (int i = 0; i < 90; i++) cloth.Step(1f / 60f);
            float later = MeanHeight(cloth);

            // Then 平均高度不许持续上涨。若位置修正量被积成速度，整张布会被自己抬走。
            Assert.That(later - settled, Is.LessThanOrEqualTo(0.05f),
                "布料贴地之后平均高度还在涨（" + settled.ToString("F3") + " → " + later.ToString("F3") + " m）");
            Assert.That(settled, Is.GreaterThan(floorY), "布应当停在地板之上，而不是陷进去");
        }

        [Test]
        public void SoftBody_FallingOnPlane_StillRestsOnTheSkinLayer()
        {
            // Given 软体落在地板上。这条路径走 ApplyCorrection（只削穿入分量），
            //        本轮**没有**改它：这条测试的作用是把"逐位不动"钉住，防止以后有人顺手改坏。
            var mesh = Track(SoftBodyDemoTools.BuildBoxMesh(0.4f, 0.4f, 0.4f, 2));
            var soft = new SoftBodySimulation(new SoftBodyParameters
            {
                gravity = new Vector3(0f, -9.81f, 0f), collisionThickness = Skin, maxDeltaTime = 1f / 30f,
            });
            soft.Build(SoftBodyMeshData.FromMesh(mesh));

            const float floorY = -0.25f;
            soft.Collisions.Add(new PlaneCollisionProxy(new Vector3(0f, floorY, 0f), Vector3.up),
                CollisionProxySpace.World);
            for (int i = 0; i < 180; i++) soft.Step(1f / 60f);

            float lowest = float.PositiveInfinity, worstOutward = 0f;
            for (int i = 0; i < soft.ParticleCount; i++)
            {
                Vector3 p = soft.GetPosition(i);
                if (p.y < lowest) lowest = p.y;
                float outward = Vector3.Dot(soft.GetVelocity(i), Vector3.up);
                if (outward > worstOutward) worstOutward = outward;
            }

            Assert.That(soft.HasNonFiniteState(), Is.False, "软体接触之后不许出现 NaN");
            Assert.That(lowest, Is.GreaterThanOrEqualTo(floorY + Skin - 1e-3f),
                "软体最低质点陷进地板皮肤了，实测 y=" + lowest.ToString("F4"));
            Assert.That(worstOutward, Is.LessThanOrEqualTo(0.3f),
                "软体贴地后的离壁速度必须保持原来的良态（实测基线 0.004），实测 " + worstOutward.ToString("F3"));
        }

        [Test]
        public void Contact_TangentialVelocityIsNeverTouched()
        {
            // Given 一个质点沿地板滑，同时带着穿入速度
            var system = new MassSpringSystem();
            system.Parameters.gravity = Vector3.zero;
            system.Parameters.globalDamping = 0f;
            system.Parameters.substeps = 1;
            system.Parameters.collisionThickness = Skin;
            int index = system.AddParticle(new Vector3(0f, 0.005f, 0f), 1f);
            system.Particles[index].velocity = new Vector3(1.25f, -0.75f, -0.5f);
            system.Collisions.Add(new PlaneCollisionProxy(Vector3.zero, Vector3.up), CollisionProxySpace.World);

            // When 走一步（质点被顶出地板）
            system.Step(1f / 60f);

            // Then 只削法向，切向**逐位不动** ⇒ 滑动而不粘住。这条同时划住"非弹性"的边界：
            //      不许有人为了压弹射而在这里偷偷加摩擦。
            Vector3 velocity = system.Particles[index].velocity;
            Assert.That(velocity.x, Is.EqualTo(1.25f).Within(1e-5f), "切向 x 被动了");
            Assert.That(velocity.z, Is.EqualTo(-0.5f).Within(1e-5f), "切向 z 被动了");
            Assert.That(Vector3.Dot(velocity, Vector3.up), Is.LessThanOrEqualTo(1e-5f),
                "穿入地板的法向速度必须被抹掉，实测 " + Vector3.Dot(velocity, Vector3.up).ToString("F4"));
        }

        [Test]
        public void Contact_ThreeSolversAllComeToRestOnTheSameFloor()
        {
            // Given 三个求解器各拿一块材料，从同样高度落到同一块地板（靠自由落体获得入射速度：
            //        布料没有公开的速度写入口，硬造入射速度会把测试绑在内部实现上）
            var gravity = new Vector3(0f, -9.81f, 0f);
            const float floorY = -0.5f;

            var massSpring = new MassSpringSystem();
            massSpring.Parameters.gravity = gravity;
            massSpring.Parameters.substeps = 2;
            massSpring.Parameters.collisionThickness = Skin;
            int mi = massSpring.AddParticle(Vector3.zero, 1f);
            massSpring.Collisions.Add(new PlaneCollisionProxy(new Vector3(0f, floorY, 0f), Vector3.up),
                CollisionProxySpace.World);
            for (int i = 0; i < 150; i++) massSpring.Step(1f / 60f);
            float msOut = Vector3.Dot(massSpring.Particles[mi].velocity, Vector3.up);
            Assert.That(massSpring.Particles[mi].position.y, Is.GreaterThanOrEqualTo(floorY + Skin - 1e-3f),
                "质点弹簧落到了地板下面");

            var cloth = new ClothSimulation(new ClothParameters
            {
                columns = 6, rows = 3, spacing = 0.08f, substeps = 2, iterations = 2,
                gravity = gravity, collisionThickness = Skin,
            });
            cloth.Collisions.Add(new PlaneCollisionProxy(new Vector3(0f, floorY, 0f), Vector3.up),
                CollisionProxySpace.World);
            for (int i = 0; i < 150; i++) cloth.Step(1f / 60f);
            float clOut = 0f;
            for (int i = 0; i < cloth.ParticleCount; i++)
            {
                if (cloth.IsPinned(i)) continue;
                float o = Vector3.Dot(cloth.GetVelocity(i), Vector3.up);
                if (o > clOut) clOut = o;
            }

            var mesh = Track(SoftBodyDemoTools.BuildBoxMesh(0.12f, 0.12f, 0.12f, 1));
            var soft = new SoftBodySimulation(new SoftBodyParameters
            {
                gravity = gravity, collisionThickness = Skin, maxDeltaTime = 1f / 30f,
            });
            soft.Build(SoftBodyMeshData.FromMesh(mesh));
            soft.Collisions.Add(new PlaneCollisionProxy(new Vector3(0f, floorY, 0f), Vector3.up),
                CollisionProxySpace.World);
            for (int i = 0; i < 150; i++) soft.Step(1f / 60f);
            float sbOut = 0f;
            for (int i = 0; i < soft.ParticleCount; i++)
            {
                float o = Vector3.Dot(soft.GetVelocity(i), Vector3.up);
                if (o > sbOut) sbOut = o;
            }

            // Then 三个求解器在"落定"这件事上口径一致（实测基线 0.000 / 0.000 / 0.004）
            Assert.That(msOut, Is.LessThanOrEqualTo(0.3f), "质点弹簧落定后的离壁速度 " + msOut.ToString("F3"));
            Assert.That(sbOut, Is.LessThanOrEqualTo(0.3f), "软体落定后的离壁速度 " + sbOut.ToString("F3"));
            Assert.That(clOut, Is.LessThanOrEqualTo(0.3f), "布料落定后的离壁速度 " + clOut.ToString("F3"));
        }

        // ================================================================ ② 互碰球半径可调

        [Test]
        public void CloudRadiusFactor_DefaultsToHalfTheSpacing()
        {
            // Given 三个 Behaviour 刚挂上，一个值都没改
            var soft = MakeSoftBody("Soft", Vector3.zero, 3);
            var cloth = MakeCloth("Cloth", 0.1f);

            // Then 默认 0.5：相邻两球刚好相切 ⇒ 两个体的表面**真的碰上**才算接触。
            //      0.75（本轮之前的硬编码值）会让两团在还差半个间距时就互推，并且弹得更狠。
            Assert.That(soft.cloudRadiusFactor, Is.EqualTo(0.5f), "软体默认系数");
            Assert.That(cloth.cloudRadiusFactor, Is.EqualTo(0.5f), "布料默认系数（必须与软体同口径）");
            Assert.That(cloth.CloudRadius, Is.EqualTo(0.1f * 0.5f).Within(1e-6f),
                "布料半径必须等于间距 × 系数");
        }

        [Test]
        public void CloudRadiusFactor_RoundTripsWhenEdited()
        {
            // Given 一块布，改系数
            var cloth = MakeCloth("Cloth", 0.2f);

            // When/Then 半径跟着系数线性走，并且改回去逐位还原（Inspector 上能真的调）
            cloth.cloudRadiusFactor = 0.3f;
            Assert.That(cloth.CloudRadius, Is.EqualTo(0.2f * 0.3f).Within(1e-6f), "调到 0.3 之后");

            cloth.cloudRadiusFactor = 0.7f;
            Assert.That(cloth.CloudRadius, Is.EqualTo(0.2f * 0.7f).Within(1e-6f), "调到 0.7 之后");

            cloth.cloudRadiusFactor = 0.5f;
            Assert.That(cloth.CloudRadius, Is.EqualTo(0.2f * 0.5f).Within(1e-6f), "改回默认之后");
        }

        [Test]
        public void CloudRadiusFactor_ZeroStaysWellDefinedInsteadOfNaN()
        {
            // Given 系数被填成 0（半径 0 ⇒ 空间哈希的格子尺寸也是 0）
            var cloth = MakeCloth("Cloth", 0.2f);
            cloth.cloudRadiusFactor = 0f;
            Assert.That(cloth.CloudRadius, Is.EqualTo(0f), "系数 0 时半径就该是 0");

            var body = Track(new GameObject("ZeroCloud"));
            var a = MakeSoftBodyAt(body, "A", new Vector3(-0.2f, 1f, 0f), 3);
            var b = MakeSoftBodyAt(body, "B", new Vector3(0.2f, 1f, 0f), 3);
            a.cloudRadiusFactor = 0f;
            b.cloudRadiusFactor = 0f;
            var group = body.AddComponent<ParticleInteractionGroup>();
            group.Add(a);
            group.Add(b);

            // When 步进
            for (int i = 0; i < 30; i++) { a.Step(1f / 60f); b.Step(1f / 60f); }

            // Then 绝不 NaN：半径为 0 时 need 退化成 skin，投影仍然良定义。
            //      这条逼出来一个真缺陷——格子尺寸 = 2×半径，半径 0 会让 reach = ceil(need/0) 炸掉。
            Assert.That(AllFinite(a), Is.True, "半径 0 时软体 A 出现了 NaN");
            Assert.That(AllFinite(b), Is.True, "半径 0 时软体 B 出现了 NaN");
        }

        [Test]
        public void CloudRadius_StillCappedByRestSizeWhenFactorIsHuge()
        {
            // Given 稀网格 + 夸张的系数（最容易吹成气球的组合）
            var soft = MakeSoftBody("Huge", Vector3.zero, 1);

            // When
            soft.cloudRadiusFactor = 40f;

            // Then 半径仍被静息尺度钳住：球不许比身体还大（那会让两团互相卡死，穿透一帧都退不掉）
            float restScale = Mathf.Pow(0.6f, 1f);   // 0.6 m 立方体的静息尺度 cbrt(0.216) = 0.6
            Assert.That(soft.CloudRadius, Is.LessThanOrEqualTo(restScale * 0.25f + 1e-6f),
                "系数调到 40 之后半径失控了：" + soft.CloudRadius.ToString("F4")
                + " m，而身体才 " + restScale.ToString("F2") + " m");
            Assert.That(soft.CloudRadius, Is.GreaterThan(0f), "钳完也不许塌成 0");
        }

        [Test]
        public void Interaction_DeeplyOverlappingSoftBodies_SeparateAndReboundDecays()
        {
            // Given 两个零重力软体，**故意**互插到对方体内 0.08 m（球云第一次判定接触时已经这么深了：
            //        质点间距 0.228，接触判定距离 r+skin = 0.124，所以表面要先互相吃进去一截才碰上）
            var group = Spawn("Group").AddComponent<ParticleInteractionGroup>();
            var a = MakeSoftBody("A", new Vector3(-0.16f, 1f, 0f), 3);
            var b = MakeSoftBody("B", new Vector3(0.16f, 1f, 0f), 3);
            a.parameters.gravity = Vector3.zero;
            b.parameters.gravity = Vector3.zero;
            group.Add(a);
            group.Add(b);

            float contact = a.CloudRadius + 0.01f;          // 球云真实的接触判定距离 = 对方半径 + skin
            Assert.That(MinCenterDistance(a, b), Is.LessThan(contact), "开局必须重叠，否则没东西可验");

            // When/Then ①确实分开，并且回弹**随时间衰减**（实测 1.03 → 0.18 m/60步）
            float first = BlockDisplacement(a, b, group, 60);
            BlockDisplacement(a, b, group, 60);
            float last = BlockDisplacement(a, b, group, 120);
            Assert.That(last, Is.LessThan(first),
                "末段位移必须小于初段：接触只在重叠期间做功，不该持续灌能量。初段 "
                + first.ToString("F3") + " m，末段 " + last.ToString("F3") + " m");
            Assert.That(MinCenterDistance(a, b), Is.GreaterThan(contact),
                "走了 240 步还没分开到接触距离之外，投影没起作用");

            // Then ②体必须还是**完整的一个体**，不能被互碰撕开或压扁。
            //      实测 0.52（静息半宽约 0.52）——这条才是"能量有没有乱灌"的真判据：
            //      上一轮我削单次查询量（25% need），总平移 4.61→4.15 m 几乎没动，
            //      而散布一直是 0.52，说明弹性能存弹簧与体积里，不在"某一步搬多远"里。
            Assert.That(Spread(a), Is.EqualTo(Spread(b)).Within(0.08f), "两体形变量差得太多");
            Assert.That(Spread(a), Is.InRange(0.45f, 0.62f),
                "互碰不该把体撕开或压扁，实测散布 " + Spread(a).ToString("F3") + " m（静息约 0.52）");

            // Then ③有界：不许发散。实测 360 步后 gap 3.53 m（回弹初速约 1.4 m/s 被阻尼吃掉），
            //      上限放到 6 m 只为了抓"持续加速"这种真正的失控，不干涉合法回弹。
            Assert.That(MinCenterDistance(a, b), Is.LessThan(6f),
                "分开距离必须收敛，不许持续加速：实测 " + MinCenterDistance(a, b).ToString("F3") + " m");
            Assert.That(AllFinite(a) && AllFinite(b), Is.True, "互碰之后不许出现 NaN");
        }


        // ================================================================ helpers

        static bool IsUnitySerializable(Type t)
        {
            if (t.IsPrimitive && t != typeof(IntPtr) && t != typeof(UIntPtr)) return true;
            if (t == typeof(string)) return true;
            if (t == typeof(Vector3) || t == typeof(Vector2) || t == typeof(Vector4)
                || t == typeof(Quaternion) || t == typeof(Color) || t == typeof(Matrix4x4)) return true;
            if (t.IsEnum) return true;
            return t.IsSerializable;
        }

        static float MeanHeight(ClothSimulation cloth)
        {
            float sum = 0f;
            for (int i = 0; i < cloth.ParticleCount; i++) sum += cloth.GetPosition(i).y;
            return sum / cloth.ParticleCount;
        }

        ClothBehaviour MakeCloth(string name, float spacing)
        {
            var go = Spawn(name);
            var cloth = go.AddComponent<ClothBehaviour>();
            cloth.parameters.columns = 5;
            cloth.parameters.rows = 5;
            cloth.parameters.spacing = spacing;
            cloth.pinEdges = ClothPinEdges.Top;
            cloth.autoSimulate = false;
            cloth.Rebuild();
            Assert.That(cloth.IsBuilt, Is.True, "布料没建起来：" + cloth.LastBuildError);
            return cloth;
        }

        SoftBodyBehaviour MakeSoftBody(string name, Vector3 at, int subdivisions)
        {
            var go = Spawn(name);
            return BuildOn(go, name, at, subdivisions);
        }

        SoftBodyBehaviour MakeSoftBodyAt(GameObject go, string name, Vector3 at, int subdivisions)
        {
            go.name = name;
            return BuildOn(go, name, at, subdivisions);
        }

        SoftBodyBehaviour BuildOn(GameObject go, string name, Vector3 at, int subdivisions)
        {
            go.transform.position = at;
            var behaviour = go.AddComponent<SoftBodyBehaviour>();
            behaviour.sourceMesh = Track(SoftBodyDemoTools.BuildBoxMesh(0.6f, 0.6f, 0.6f, subdivisions));
            behaviour.parameters.maxDeltaTime = 1f / 30f;
            behaviour.autoSimulate = false;
            behaviour.Rebuild();
            Assert.That(behaviour.IsBuilt, Is.True, "软体没建起来：" + behaviour.LastBuildError);
            return behaviour;
        }

        static float Spread(SoftBodyBehaviour b)
        {
            var ps = b.CloudWorldPositions();
            Vector3 m = MeanCloud(b);
            float far = 0f;
            for (int i = 0; i < ps.Length; i++) far = Mathf.Max(far, Vector3.Distance(ps[i], m));
            return far;
        }

        static float MaxV(SoftBodyBehaviour b)
        {
            var sim = b.Simulation;
            float best = 0f;
            for (int i = 0; i < sim.ParticleCount; i++) best = Mathf.Max(best, sim.GetVelocity(i).magnitude);
            return best;
        }

        /// <summary>连续 steps 步里两个体质心的合计位移（互碰把多少能量变成了整体运动）。</summary>
        static float BlockDisplacement(SoftBodyBehaviour a, SoftBodyBehaviour b,
                                       ParticleInteractionGroup group, int steps)
        {
            Vector3 beforeA = MeanCloud(a), beforeB = MeanCloud(b);
            for (int i = 0; i < steps; i++) { a.Step(1f / 60f); b.Step(1f / 60f); }
            return Vector3.Distance(beforeA, MeanCloud(a)) + Vector3.Distance(beforeB, MeanCloud(b));
        }

        static Vector3 MeanCloud(SoftBodyBehaviour b)
        {
            var ps = b.CloudWorldPositions();
            Vector3 m = Vector3.zero;
            for (int i = 0; i < ps.Length; i++) m += ps[i];
            return m / (ps.Length > 0 ? ps.Length : 1);
        }

        static float MinCenterDistance(IParticleBody a, IParticleBody b)
        {
            var pa = a.CloudWorldPositions();
            var pb = b.CloudWorldPositions();
            float best = float.PositiveInfinity;
            for (int i = 0; i < pa.Length; i++)
                for (int k = 0; k < pb.Length; k++)
                {
                    float d = Vector3.Distance(pa[i], pb[k]);
                    if (d < best) best = d;
                }
            return best;
        }

        static bool AllFinite(SoftBodyBehaviour b)
        {
            var ps = b.CloudWorldPositions();
            for (int i = 0; i < ps.Length; i++)
            {
                if (float.IsNaN(ps[i].x) || float.IsNaN(ps[i].y) || float.IsNaN(ps[i].z)) return false;
                if (float.IsInfinity(ps[i].x) || float.IsInfinity(ps[i].y) || float.IsInfinity(ps[i].z)) return false;
            }
            return true;
        }
    }
}
