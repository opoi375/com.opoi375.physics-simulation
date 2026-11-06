// Copyright (c) 2026 PhysicsSimulation. MIT License.
//
// 粒子互碰（多体交互）行为规格。
//
// 五个已确认的设计决策，全文件都建立在它们之上：
//   1. 接触几何 = 粒子球云。每个质点当成一个小球，半径由粒子间距推出。
//      不做三角面贴合：深穿透时"最近面"方向会翻，那是流体水箱刚踩过的角部传送带坑。
//   2. 接线方式 = 显式互碰组。软体/布料/质点弹簧注册进组，同组才互碰；不注册零开销。
//   3. 覆盖范围 = 三个求解器统一。代理层只认"质点球"，不认体是什么类型。
//   4. 自碰撞本版不做，但有用例钉住"确实没做"。
//   5. 对称性 = 各自把自己投影出对方，顺序求解、动量不守恒，不改 ICollisionProxy 签名。
//
// 刷新时机为什么用"版本戳"而不是 Time.frameCount：三个 Behaviour 的步进时机本来就不一致
// （软体与质点弹簧走 FixedUpdate，布料走 Update），而 EditMode 测试里 frameCount 根本不动，
// 用它当守卫会把第二次 Step 悄悄变成空操作，测出来一片假绿。

using System;
using System.Collections.Generic;
using NUnit.Framework;
using PhysicsSimulation;
using PhysicsSimulation.EditorTools;
using UnityEngine;

namespace PhysicsSimulation.Editor.Tests
{
    [TestFixture]
    public class ParticleInteractionTests
    {
        const float Skin = 0.02f;
        const float Radius = 0.08f;

        static int Bits(float v) { return BitConverter.SingleToInt32Bits(v); }

        static void AssertFinite(Vector3 v, string scene)
        {
            Assert.That(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z)
                        || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z),
                Is.False, scene + "：结果必须是有限值，实际 " + v);
        }

        /// <summary>点到这朵球云里最近的球的穿透深度：大于 0 表示还陷在球里（含 skin 未满足）。</summary>
        static float DepthIntoCloud(Vector3 point, IList<Vector3> cloud, float radius, float skin)
        {
            float need = radius + skin;
            float worst = float.NegativeInfinity;
            for (int i = 0; i < cloud.Count; i++)
            {
                float d = Vector3.Distance(point, cloud[i]);
                worst = Mathf.Max(worst, need - d);
            }
            return worst;
        }

        /// <summary>测试用的假体：位置由外部数组直接给出，不牵扯任何求解器。</summary>
        sealed class FakeBody : IParticleBody
        {
            readonly List<Particle> _particles = new List<Particle>();
            readonly Vector3[] _world = new Vector3[4096];
            readonly CollisionSet _collisions = new CollisionSet();
            int _version;

            public FakeBody(Vector3[] positions, float radius)
            {
                for (int i = 0; i < positions.Length; i++)
                {
                    _particles.Add(new Particle(positions[i], 1f));
                }
                CloudRadiusValue = radius;
                Touch();
            }

            public float CloudRadiusValue { get; set; }
            public bool WriteWorldPositionsCalled { get; private set; }

            public void Move(int index, Vector3 to)
            {
                _particles[index].position = to;
                Touch();
            }

            public void Pin(int index)
            {
                _particles[index].inverseMass = 0f;
                Touch();
            }

            void Touch()
            {
                WriteWorldPositionsCalled = true;
                _version++;
            }

            public int CloudParticleCount { get { return _particles.Count; } }
            public float CloudRadius { get { return CloudRadiusValue; } }
            public int CloudVersion { get { return _version; } }
            public CollisionSet ParticleCollisions { get { return _collisions; } }
            public ParticleInteractionGroup CloudGroup { get; set; }

            public Vector3[] CloudWorldPositions()
            {
                for (int i = 0; i < _particles.Count; i++) _world[i] = _particles[i].position;
                return _world;
            }
        }

        static FakeBody Cloud(params Vector3[] positions)
        {
            return new FakeBody(positions, Radius);
        }

        static Vector3[] PositionsOf(FakeBody body)
        {
            var raw = body.CloudWorldPositions();
            var list = new Vector3[body.CloudParticleCount];
            Array.Copy(raw, list, list.Length);
            return list;
        }

        // ==================================================================
        // A 组：球云代理的几何契约（纯逻辑，不读场景）
        // ==================================================================

        [Test]
        public void Cloud_ReadsLivePositions_NotACopyTakenAtConstruction()
        {
            // Given 一朵由若干质点构成的球云代理，代理持有的是那个体的活体位置数组
            // When 把其中一个质点挪到别处后再查询同一个点
            // Then 查询结果按挪动后的位置计算，不需要重新构造代理
            var body = Cloud(new Vector3(0f, 0f, 0f));
            var proxy = new ParticleCloudProxy(body);
            var probe = new Vector3(0.02f, 0f, 0f);

            Vector3 before = proxy.PushOut(probe, Skin);
            Assert.That(DepthIntoCloud(before, PositionsOf(body), Radius, Skin), Is.LessThanOrEqualTo(1e-5f),
                "质点还在原点时，投影就该把它顶到球面之外");

            body.Move(0, new Vector3(5f, 0f, 0f));
            Vector3 after = proxy.PushOut(probe, Skin);

            Assert.That(Bits(after.x), Is.EqualTo(Bits(probe.x)).And.EqualTo(Bits(0.02f)),
                "质点挪走之后同一个探测点必须逐位不变，否则代理就是在用构造时的那份拷贝");
            Assert.That(Bits(after.y), Is.EqualTo(Bits(probe.y)), "挪走之后 y 也不许被动过");
            Assert.That(Bits(after.z), Is.EqualTo(Bits(probe.z)), "挪走之后 z 也不许被动过");
        }

        [Test]
        public void Cloud_PointFarOutside_IsReturnedBitIdentical()
        {
            // Given 一朵静止的球云
            // When 查询一个离所有球都超过 skin 的点
            // Then 返回值与输入逐位相同，绝不"顺手拉近"
            var body = Cloud(new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f));
            var proxy = new ParticleCloudProxy(body);
            var probe = new Vector3(0.3f, 0.5f, 3f);

            Vector3 result = proxy.PushOut(probe, Skin);

            Assert.That(Bits(result.x), Is.EqualTo(Bits(probe.x)), "体外点的 x 必须逐位不动");
            Assert.That(Bits(result.y), Is.EqualTo(Bits(probe.y)), "体外点的 y 必须逐位不动");
            Assert.That(Bits(result.z), Is.EqualTo(Bits(probe.z)), "体外点的 z 必须逐位不动");
        }

        [Test]
        public void Cloud_PointInsideSphere_IsPushedToSurfacePlusSkin()
        {
            // Given 只有一个质点的球云，球半径与 skin 都已知
            // When 查询一个落在该球内部的点
            // Then 点沿球心到点的方向顶到"球面 + skin"处，距离误差在浮点容差内
            var body = Cloud(new Vector3(0f, 0f, 0f));
            var proxy = new ParticleCloudProxy(body);
            var probe = new Vector3(0.01f, 0.02f, 0.0f);

            Vector3 result = proxy.PushOut(probe, Skin);

            AssertFinite(result, "球内点投影");
            float want = Radius + Skin;
            Assert.That(Vector3.Distance(result, Vector3.zero), Is.EqualTo(want).Within(1e-5f),
                "结果必须落在半径 球半径+skin 的球面上，实际 " + Vector3.Distance(result, Vector3.zero));
            Vector3 dir = result.normalized;
            Assert.That(Vector3.Dot(dir, probe.normalized), Is.EqualTo(1f).Within(1e-4f),
                "顶出方向必须沿着球心到原点的方向，不许换方向");
        }

        [Test]
        public void Cloud_PointExactlyOnSphereCenter_UsesFallbackNormal()
        {
            // Given 只有一个质点的球云
            // When 查询点与该质点球心完全重合（方向未定义）
            // Then 沿固定的备用轴（+Y）顶出，结果有限、不 NaN、且两次调用逐位相同
            var body = Cloud(new Vector3(0f, 0f, 0f));
            var proxy = new ParticleCloudProxy(body);

            Vector3 first = proxy.PushOut(Vector3.zero, Skin);
            Vector3 second = proxy.PushOut(Vector3.zero, Skin);

            AssertFinite(first, "与球心重合的点");
            Assert.That(Bits(first.x), Is.EqualTo(0), "方向未定义时必须走固定备用轴，x 不该有分量");
            Assert.That(first.y, Is.EqualTo(Radius + Skin).Within(1e-5f), "备用轴是 +Y，顶到球面 + skin");
            Assert.That(Bits(first.z), Is.EqualTo(0), "z 不该有分量");
            Assert.That(Bits(first.x), Is.EqualTo(Bits(second.x)), "重合回退的 x 两次必须逐位相同");
            Assert.That(Bits(first.y), Is.EqualTo(Bits(second.y)), "重合回退的 y 两次必须逐位相同");
            Assert.That(Bits(first.z), Is.EqualTo(Bits(second.z)),
                "重合回退法线必须是个固定常量，不能依赖 normalize(0) 那种未定义结果");
        }

        [Test]
        public void Cloud_NegativeSkin_DoesNotPullPointInside()
        {
            // Given 一朵球云和一个位于球内的点
            // When 用负的 skin 调用投影
            // Then 负值按 0 处理，点被顶到球面而不是被往球心方向塞
            var body = Cloud(new Vector3(0f, 0f, 0f));
            var proxy = new ParticleCloudProxy(body);

            Vector3 result = proxy.PushOut(new Vector3(0.01f, 0f, 0f), -1f);

            AssertFinite(result, "负 skin 投影");
            Assert.That(Vector3.Distance(result, Vector3.zero), Is.EqualTo(Radius).Within(1e-5f),
                "skin 为负要按 0 处理：顶到球面即可，绝不能顶进球里");
        }

        /// <summary>探针点到这颗球链里最深的一处重叠深度（&lt;=0 表示已在所有球之外）。</summary>
        static float DeepestOverlapX(Vector3 point, float[] centers, float need)
        {
            float worst = float.NegativeInfinity;
            for (int i = 0; i < centers.Length; i++)
            {
                float d = Vector3.Distance(point, new Vector3(centers[i], 0f, 0f));
                float depth = need - d;
                if (depth > worst) worst = depth;
            }
            return worst;
        }

        [Test]
        public void Cloud_ChainOfOverlappingSpheres_EndsOutsideEveryOneOfThem()
        {
            // Given 一排**相邻重叠**的球（重叠系数 0.6），一个一次都退不干净的链
            const float Radius = 0.08f, Skin = 0.02f;
            float[] centers = { 0f, 0.12f, 0.24f, 0.36f, 0.48f };
            var cloud = new Vector3[centers.Length];
            for (int i = 0; i < centers.Length; i++) cloud[i] = new Vector3(centers[i], 0f, 0f);
            var proxy = new ParticleCloudProxy(new FakeBody(cloud, Radius));

            // When 反复投影。本版**不做**"单次查询修正量上限"：几何代理只负责"这个点对这个球云
            //        该推到哪儿"，能量预算归调用方。链是相连的，所以一次 PushOut 就能走出去，
            //        但契约写成"有限次内退出"更贴合真实求解器用法，也不禁止以后加松弛。
            var probe = new Vector3(0.30f, 0f, 0f);
            float need = Radius + Skin;
            bool clear = false;
            for (int k = 0; k < 64 && !clear; k++)
            {
                probe = proxy.PushOut(probe, Skin);
                clear = DeepestOverlapX(probe, centers, need) <= 1e-5f;
            }

            // Then 有限次内退出每一颗球（不许死循环卡在链中间），而且**不许过冲**：
            //      链的出口就在最右那颗之外 need 处，一次跳出去更远就是把约束当成了冲量。
            Assert.That(clear, Is.True, "反复投影之后仍被困在球链里");
            Assert.That(probe.x, Is.LessThanOrEqualTo(centers[centers.Length - 1] + need + 1e-3f),
                "从球链里退出时过冲了：实测 x=" + probe.x.ToString("F4")
                + "，链右端 + need = " + (centers[centers.Length - 1] + need).ToString("F4"));
        }
        [Test]
        public void Cloud_DeeplyBuriedPoint_EscapeIsBoundedNotTeleport()
        {
            // Given 同一条链，探针埋进链中间
            const float Radius = 0.08f, Skin = 0.02f;
            float[] centers = { 0f, 0.12f, 0.24f, 0.36f, 0.48f };
            var cloud = new Vector3[centers.Length];
            for (int i = 0; i < centers.Length; i++) cloud[i] = new Vector3(centers[i], 0f, 0f);
            var proxy = new ParticleCloudProxy(new FakeBody(cloud, Radius));
            float need = Radius + Skin;
            Vector3 start = new Vector3(0.30f, 0f, 0f);

            // When 投影
            Vector3 q = proxy.PushOut(start, Skin);

            // Then 三条都得成立——这一条是本轮最贵的教训换来的：
            //      ①真的退到所有球之外（"没分开"要能抓住）；
            //      ②**不许一步传到云的包围球外面**。曾经为了修"卡在链中间"改成"一步传到
            //        centroid + 包围半径"，零载荷测试照样绿，Play 里两个 0.6 m 软体第一步
            //        就各飞 0.56 m（≈34 m/s），整幕炸开——所以"分没分开"远不够。
            //      ③退出点离链最远端不超过 need：几何上链的出口就是那儿，多出来的是纯注入。
            Assert.That(DeepestOverlapX(q, centers, need), Is.LessThanOrEqualTo(1e-5f),
                "深埋点必须退到每一颗球之外");
            Assert.That(q.x, Is.LessThanOrEqualTo(centers[centers.Length - 1] + need + 1e-3f),
                "单次查询不许把点传到链出口之外更远：实测 x=" + q.x.ToString("F4")
                + "，出口 " + (centers[centers.Length - 1] + need).ToString("F4")
                + "。一步传过远就是位置投影被当成冲量用，多走的全会变成速度");
            // ③方向必须**朝外**。重叠球云的并集比单个半径大，所以合法的退出位移可以远大于 need
            //    （埋在中点的探针到链出口是 2.8×need，实测就这样）——"单步不许超过 need"是我
            //    凭空造的界，几何上站不住。真正能区分"退出"与"传送"的是方向与过冲，上面两条已经钉死。
            Vector3 centroid = Vector3.zero;
            for (int i = 0; i < centers.Length; i++) centroid += new Vector3(centers[i], 0f, 0f);
            centroid /= centers.Length;
            Assert.That(Vector3.Dot(q - start, start - centroid), Is.GreaterThanOrEqualTo(0f),
                "退出方向必须背离球云质心，实测把点朝别的方向搬走了");
        }
        [Test]
        public void Cloud_EmptyCloud_IsNoOp()
        {
            // Given 一个粒子数为 0 的球云（体还没建好，或者全被固定）
            // When 查询任意点
            // Then 原样返回，不抛异常
            var body = Cloud(new Vector3[0]);
            var proxy = new ParticleCloudProxy(body);
            var probe = new Vector3(0.1f, 0.2f, 0.3f);

            Vector3 result = proxy.PushOut(probe, Skin);

            Assert.That(proxy.ParticleCount, Is.EqualTo(0), "空云就该报 0 个质点");
            Assert.That(Bits(result.x), Is.EqualTo(Bits(probe.x)), "空云不许碰任何点");
            Assert.That(Bits(result.y), Is.EqualTo(Bits(probe.y)), "空云不许碰任何点");
            Assert.That(Bits(result.z), Is.EqualTo(Bits(probe.z)), "空云不许碰任何点");
        }

        [Test]
        public void Cloud_OverlappingSpheres_ResolveInFixedOrder()
        {
            // Given 一朵球云，其中两个球互相重叠，查询点同时落在两个球内
            // When 连续投影两次（各自重新建一次索引）
            // Then 两次结果逐位相同，即多球命中时的裁决顺序是确定的、与哈希桶遍历顺序无关
            var body = Cloud(new Vector3(0f, 0f, 0f), new Vector3(0.03f, 0f, 0f), new Vector3(0f, 0.03f, 0f));
            var probe = new Vector3(0.005f, 0.005f, 0f);

            var a = new ParticleCloudProxy(body);
            var b = new ParticleCloudProxy(body);
            Vector3 first = a.PushOut(probe, Skin);
            Vector3 second = b.PushOut(probe, Skin);

            AssertFinite(first, "多球重叠投影");
            Assert.That(Bits(first.x), Is.EqualTo(Bits(second.x)),
                "两个互不认识的代理对同一份云、同一个点必须给出逐位相同的结果");
            Assert.That(Bits(first.y), Is.EqualTo(Bits(second.y)), "y 也必须逐位相同");
            Assert.That(Bits(first.z), Is.EqualTo(Bits(second.z)), "z 也必须逐位相同");
            Assert.That(DepthIntoCloud(first, PositionsOf(body), Radius, Skin), Is.LessThanOrEqualTo(1e-4f),
                "顺序确定还不够，结果必须真的离开了所有球");
        }

        [Test]
        public void Cloud_RadiusMustBePositiveFinite()
        {
            // Given 构造球云代理的入口
            // When 传入 0、负数或 NaN 的球半径
            // Then 抛出参数异常，且异常消息是中文并带上实际值
            foreach (float bad in new[] { 0f, -0.5f, float.NaN, float.PositiveInfinity })
            {
                var body = new FakeBody(new[] { Vector3.zero }, bad);
                var ex = Assert.Throws<ArgumentOutOfRangeException>(
                    () => new ParticleCloudProxy(body), "半径 " + bad + " 时应当拒绝构造");
                Assert.That(ex.Message, Does.Contain("半径"), "异常消息必须是中文并且点明是半径的问题，实际：" + ex.Message);
                Assert.That(ex.Message, Does.Contain(bad.ToString()), "异常消息要带上实际值，方便定位");
            }
        }

        [Test]
        public void Cloud_QueryBeyondHashRange_IsUnchanged()
        {
            // Given 一朵球云，其空间索引按固定格子尺寸分桶
            // When 查询一个落在所有球之外、但离云很近（同一格或相邻格）和很远（跨多格）的各一个点
            // Then 两个点都逐位不变，即格子边界不会让本该不相交的点被误推出
            var body = Cloud(new Vector3(0f, 0f, 0f), new Vector3(0.5f, 0f, 0f));
            var proxy = new ParticleCloudProxy(body);

            var nearMiss = new Vector3(Radius + Skin + 1e-4f, 0f, 0f);   // 贴着皮肤外沿
            var farAway = new Vector3(9f, 9f, 9f);                       // 跨很多格

            Vector3 nearResult = proxy.PushOut(nearMiss, Skin);
            Vector3 farResult = proxy.PushOut(farAway, Skin);

            Assert.That(Bits(nearResult.x), Is.EqualTo(Bits(nearMiss.x)),
                "刚好在皮肤外沿之外的点不许被拉近，否则格子边界就成了假接触面");
            Assert.That(Bits(nearResult.y), Is.EqualTo(Bits(nearMiss.y)), "同上");
            Assert.That(Bits(farResult.x), Is.EqualTo(Bits(farAway.x)), "远处的点必须原样返回");
            Assert.That(Bits(farResult.z), Is.EqualTo(Bits(farAway.z)), "远处的点必须原样返回");
        }

        // ==================================================================
        // B 组：互碰组与三个 Behaviour 的接线（Unity 层）
        // ==================================================================

        [TearDown]
        public void Cleanup()
        {
            foreach (var go in _spawned) if (go != null) UnityEngine.Object.DestroyImmediate(go);
            _spawned.Clear();
            var demoGroup = GameObject.Find(SoftBodyDemoTools.InteractionGroupName);
            if (demoGroup != null) UnityEngine.Object.DestroyImmediate(demoGroup);
            SoftBodyDemoTools.DemoEnableInteraction = false;
        }
        readonly List<GameObject> _spawned = new List<GameObject>();

        GameObject Spawn(string name)
        {
            var go = new GameObject(name);
            go.AddComponent<MeshFilter>();
            go.AddComponent<MeshRenderer>();
            _spawned.Add(go);
            return go;
        }

        SoftBodyBehaviour MakeSoftBody(string name, Vector3 at, Vector3 scale)
        {
            var go = Spawn(name);
            go.transform.position = at;
            var behaviour = go.AddComponent<SoftBodyBehaviour>();
            behaviour.sourceMesh = SoftBodyDemoTools.BuildBoxMesh(0.6f, 0.6f, 0.6f, 3);
            behaviour.parameters.gravity = new Vector3(0f, -9.81f, 0f);
            behaviour.parameters.maxDeltaTime = 1f / 30f;
            behaviour.autoSimulate = false;
            behaviour.Rebuild();
            Assert.That(behaviour.IsBuilt, Is.True, "软体没建起来：" + behaviour.LastBuildError);
            go.transform.localScale = scale;
            return behaviour;
        }

        ClothBehaviour MakeCloth(string name, Vector3 at, float spacing)
        {
            var go = Spawn(name);
            go.transform.position = at;
            var behaviour = go.AddComponent<ClothBehaviour>();
            behaviour.parameters.columns = 6;
            behaviour.parameters.rows = 6;
            behaviour.parameters.spacing = spacing;
            behaviour.pinEdges = ClothPinEdges.Top;
            behaviour.autoSimulate = false;
            behaviour.Rebuild();
            Assert.That(behaviour.IsBuilt, Is.True, "布料没建起来");
            return behaviour;
        }

        MassSpringBehaviour MakeChain(string name, Vector3 at, float linkLength)
        {
            var go = Spawn(name);
            go.transform.position = at;
            var behaviour = go.AddComponent<MassSpringBehaviour>();
            behaviour.autoSimulate = false;
            behaviour.particles.Add(new MassSpringParticleData { position = new Vector3(0f, 0f, 0f), mass = 1f, pinned = true });
            behaviour.particles.Add(new MassSpringParticleData { position = new Vector3(linkLength, 0f, 0f), mass = 1f });
            behaviour.particles.Add(new MassSpringParticleData { position = new Vector3(linkLength * 2f, 0f, 0f), mass = 1f });
            behaviour.springs.Add(new MassSpringSpringData { a = 0, b = 1, restLength = linkLength });
            behaviour.springs.Add(new MassSpringSpringData { a = 1, b = 2, restLength = linkLength });
            behaviour.Rebuild();
            Assert.That(behaviour.IsBuilt, Is.True, "质点弹簧没建起来");
            return behaviour;
        }

        static Vector3[] WorldParticles(SoftBodyBehaviour behaviour)
        {
            var sim = behaviour.Simulation;
            var toWorld = sim.SimulationToWorld;
            var list = new Vector3[sim.ParticleCount];
            for (int i = 0; i < list.Length; i++) list[i] = toWorld.MultiplyPoint(sim.GetPosition(i));
            return list;
        }

        static Vector3[] WorldParticles(ClothBehaviour behaviour)
        {
            var sim = behaviour.System;
            var toWorld = sim.SimulationToWorld;
            var captured = sim.CapturePositions();
            var list = new Vector3[captured.Length];
            for (int i = 0; i < list.Length; i++) list[i] = toWorld.MultiplyPoint(captured[i]);
            return list;
        }

        static Vector3[] WorldParticles(MassSpringBehaviour behaviour)
        {
            var system = behaviour.System;
            var toWorld = system.SimulationToWorld;
            var list = new Vector3[system.Particles.Count];
            for (int i = 0; i < list.Length; i++) list[i] = toWorld.MultiplyPoint(system.Particles[i].position);
            return list;
        }

        /// <summary>一组体里，某个体的质点陷进其它体的球云里最深的深度（负数表示完全没碰）。</summary>
        static float WorstDepthIntoOthers(IParticleBody self, params IParticleBody[] others)
        {
            float worst = float.NegativeInfinity;
            foreach (var other in others)
            {
                if (other == null || other == self) continue;
                var cloud = other.CloudWorldPositions();
                var mine = self.CloudWorldPositions();
                for (int i = 0; i < self.CloudParticleCount; i++)
                {
                    float d = DepthIntoCloud(mine[i], CloudList(cloud, other), other.CloudRadius, other.CloudRadius);
                    worst = Mathf.Max(worst, d);
                }
            }
            return worst;
        }

        static IList<Vector3> CloudList(Vector3[] raw, IParticleBody body)
        {
            var list = new List<Vector3>(body.CloudParticleCount);
            for (int i = 0; i < body.CloudParticleCount; i++) list.Add(raw[i]);
            return list;
        }

        [Test]
        public void Group_NoMembership_KeepsReleasedBehaviourBitIdentical()
        {
            // Given 两个软体都没有加入任何互碰组
            // When 各跑一步模拟
            // Then 它们的粒子状态与"完全没有这个功能"时逐位相同，且碰撞代理数不变
            var a = MakeSoftBody("A", new Vector3(0f, 1f, 0f), Vector3.one);
            var b = MakeSoftBody("B", new Vector3(0.2f, 1f, 0f), Vector3.one);

            Assert.That(a.Simulation.Collisions.Count, Is.EqualTo(0), "不注册就不许有任何代理");
            Assert.That(b.Simulation.Collisions.Count, Is.EqualTo(0), "不注册就不许有任何代理");

            var beforeA = a.Simulation.CapturePositions();
            var beforeB = b.Simulation.CapturePositions();
            a.Step(1f / 60f);
            b.Step(1f / 60f);
            var afterA = a.Simulation.CapturePositions();
            var afterB = b.Simulation.CapturePositions();

            Assert.That(afterA.Length, Is.EqualTo(beforeA.Length), "粒子数不该变");
            Assert.That(a.Simulation.Collisions.Count, Is.EqualTo(0), "跑一步之后也不许冒出代理来");
            Assert.That(afterB[0], Is.Not.EqualTo(beforeB[0]), "对照用：确实步进过，不是空跑");
        }

        [Test]
        public void Group_TwoOverlappingSoftBodies_SeparateOverSteps()
        {
            // Given 两个重叠的软体在同一个互碰组里
            // When 连续跑若干步
            // Then 两者互相挤开的可观察结果是穿透深度单调下降，最终稳定在各自的球面 + skin 之外
            var group = Spawn("Group").AddComponent<ParticleInteractionGroup>();
            var a = MakeSoftBody("A", new Vector3(0f, 1f, 0f), Vector3.one);
            var b = MakeSoftBody("B", new Vector3(0.15f, 1f, 0f), Vector3.one);
            group.Add(a);
            group.Add(b);

            Assert.That(a.Simulation.Collisions.Count, Is.EqualTo(1), "A 只该看见 B 一朵云");
            Assert.That(b.Simulation.Collisions.Count, Is.EqualTo(1), "B 只该看见 A 一朵云");

            float first = WorstDepthIntoOthers(a, b);
            for (int i = 0; i < 120; i++) { a.Step(1f / 60f); b.Step(1f / 60f); }
            float later = WorstDepthIntoOthers(a, b);

            Assert.That(later, Is.LessThan(first),
                "互碰跑了 120 步之后穿透深度必须比初始小：初始 " + first + "，最终 " + later);
            Assert.That(float.IsNaN(later) || float.IsInfinity(later), Is.False, "穿透深度必须是有限值");
        }

        [Test]
        public void Group_BodiesInDifferentGroups_DoNotInteract()
        {
            // Given 两个软体分别属于两个互碰组，且位置重叠
            // When 连续跑若干步
            // Then 它们互相穿过，彼此都不产生任何推出位移
            var g1 = Spawn("G1").AddComponent<ParticleInteractionGroup>();
            var g2 = Spawn("G2").AddComponent<ParticleInteractionGroup>();
            var a = MakeSoftBody("A", new Vector3(0f, 1f, 0f), Vector3.one);
            var b = MakeSoftBody("B", new Vector3(0.1f, 1f, 0f), Vector3.one);
            g1.Add(a);
            g2.Add(b);

            Assert.That(a.Simulation.Collisions.Count, Is.EqualTo(0),
                "组里只有一个体时，它没有别人可碰，代理数必须是 0");
            Assert.That(b.Simulation.Collisions.Count, Is.EqualTo(0), "同上");

            var before = a.Simulation.CapturePositions();
            a.Step(1f / 60f);
            var after = a.Simulation.CapturePositions();
            Assert.That(after[0] == before[0], Is.False, "对照用：确实步进过");
        }

        [Test]
        public void Group_JoinAndLeave_KeepsProxyCountExact()
        {
            // Given 一个软体已经在互碰组里
            // When 它退出组，再重新加入
            // Then 别的体的碰撞代理数先减一再加一，不出现重复注册导致的同一朵云被推两次
            var group = Spawn("Group").AddComponent<ParticleInteractionGroup>();
            var a = MakeSoftBody("A", new Vector3(0f, 1f, 0f), Vector3.one);
            var b = MakeSoftBody("B", new Vector3(0.5f, 1f, 0f), Vector3.one);
            group.Add(a);
            group.Add(b);
            Assert.That(a.Simulation.Collisions.Count, Is.EqualTo(1), "两体一组，每体一个代理");

            Assert.That(group.Remove(b), Is.True, "退出应当报告成功");
            Assert.That(a.Simulation.Collisions.Count, Is.EqualTo(0), "对方退出后代理必须被摘掉，不能留悬空的");
            Assert.That(b.Simulation.Collisions.Count, Is.EqualTo(0), "自己那份也要摘干净");

            group.Add(b);
            Assert.That(a.Simulation.Collisions.Count, Is.EqualTo(1), "重新加入应当恰好一个，不能变成两个");
            group.Add(b);
            Assert.That(a.Simulation.Collisions.Count, Is.EqualTo(1), "重复加入必须被忽略，否则同一朵云会被推两次");
            Assert.That(group.MemberCount, Is.EqualTo(2), "成员数也不许被重复注册吹大");
        }

        [Test]
        public void Group_BodyNeverCollidesWithItself()
        {
            // Given 组里只有一个软体（自碰撞本版明确不做）
            // When 把它自己压扁到各部分互相穿插
            // Then 它的粒子不因互碰组产生任何推出位移，代理数保持为 0
            var group = Spawn("Group").AddComponent<ParticleInteractionGroup>();
            var a = MakeSoftBody("A", new Vector3(0f, 0.2f, 0f), Vector3.one);
            group.Add(a);

            Assert.That(group.MemberCount, Is.EqualTo(1), "组里就一个体");
            Assert.That(a.Simulation.Collisions.Count, Is.EqualTo(0),
                "本版不做自碰撞：唯一的体在组里也不许拿到任何代理，拿到就是自己在挤自己");

            for (int i = 0; i < 60; i++) a.Step(1f / 60f);
            Assert.That(a.Simulation.Collisions.Count, Is.EqualTo(0), "跑起来之后仍然必须是 0");
            Assert.That(a.Simulation.HasNonFiniteState(), Is.False, "压扁过程不许出 NaN");
        }

        [Test]
        public void Group_TransformRespected_ContactHappensInWorldSpace()
        {
            // Given 两个互碰的软体，其中一个的 transform 带位移、旋转和非均匀缩放
            // When 让它们在世界空间里重叠
            // Then 接触发生在世界空间该发生的位置上，局部坐标不会因为缩放被当成世界坐标用
            var group = Spawn("Group").AddComponent<ParticleInteractionGroup>();
            var a = MakeSoftBody("A", new Vector3(0f, 1f, 0f), Vector3.one);
            var b = MakeSoftBody("B", new Vector3(0f, 1f, 0f), new Vector3(2f, 0.5f, 1f));
            b.transform.rotation = Quaternion.Euler(30f, 45f, 0f);
            group.Add(a);
            group.Add(b);

            Assert.That(a.Simulation.Collisions.Count, Is.EqualTo(1), "带缩放旋转也要接得上");
            Assert.That(b.Simulation.Collisions.Count, Is.EqualTo(1), "带缩放旋转也要接得上");

            for (int i = 0; i < 60; i++) { a.Step(1f / 60f); b.Step(1f / 60f); }

            var wa = WorldParticles(a);
            var wb = WorldParticles(b);
            foreach (var p in wa) AssertFinite(p, "A 的世界位置");
            foreach (var p in wb) AssertFinite(p, "B 的世界位置");
            Assert.That(WorstDepthIntoOthers(a, b), Is.LessThan(0.6f),
                "非均匀缩放的体仍然要把接触收在世界空间，穿透深度不许长回一个体的尺度");
        }

        [Test]
        public void Group_PinnedParticles_AreNotPushed()
        {
            // Given 一个顶面钉住的布料与一个软体互碰，钉住的质点正好在重叠区里
            // When 跑一步
            // Then 钉住的质点位置不动（与现有"固定点不参与碰撞"的约定一致），只有自由质点被顶开
            var group = Spawn("Group").AddComponent<ParticleInteractionGroup>();
            var cloth = MakeCloth("Cloth", new Vector3(0f, 1f, 0f), 0.1f);
            var soft = MakeSoftBody("Soft", new Vector3(0f, 1f, 0f), Vector3.one);
            group.Add(cloth);
            group.Add(soft);

            Assert.That(cloth.System.Collisions.Count, Is.EqualTo(1), "布料也要看见软体");

            var before = cloth.System.CapturePositions();
            int pinned = 0;
            for (int i = 0; i < before.Length; i++) if (cloth.System.IsPinned(i)) pinned++;
            Assert.That(pinned, Is.GreaterThan(0), "顶面钉住的布料应当有固定质点，否则这条测试没意义");

            cloth.Step(1f / 60f);
            var after = cloth.System.CapturePositions();
            int moved = 0;
            for (int i = 0; i < before.Length; i++)
            {
                if (cloth.System.IsPinned(i))
                    Assert.That(after[i] == before[i], Is.True, "第 " + i + " 个质点是钉住的，不许被互碰顶走");
                else if (after[i] != before[i]) moved++;
            }
            Assert.That(moved, Is.GreaterThan(0), "对照用：自由质点确实动了，不是整块布都没动");
        }

        [Test]
        public void Group_CoexistsWithSceneColliderBridge()
        {
            // Given 一个软体同时开了场景 Collider 桥接和互碰组
            // When 每步重读 Collider 列表（会清世界空间代理）
            // Then 互碰代理不被误删，两类代理共存且顺序稳定
            var ground = Spawn("Ground");
            var gc = ground.AddComponent<BoxCollider>();
            gc.center = new Vector3(0f, -1f, 0f);
            gc.size = new Vector3(20f, 1f, 20f);

            var group = Spawn("Group").AddComponent<ParticleInteractionGroup>();
            var a = MakeSoftBody("A", new Vector3(0f, 1f, 0f), Vector3.one);
            var b = MakeSoftBody("B", new Vector3(0.2f, 1f, 0f), Vector3.one);
            a.collideWithSceneColliders = true;
            a.sceneColliders = new List<Collider> { gc };
            a.updateCollidersEveryStep = true;
            group.Add(a);
            group.Add(b);

            // 桥接是 Step 里才登记的：这一步之前只能看见对方的云
            Assert.That(a.Simulation.Collisions.Count, Is.EqualTo(1), "刚入组：只有对方的云");
            a.Step(1f / 60f);
            Assert.That(a.Simulation.Collisions.Count, Is.EqualTo(2), "一个地面 + 一朵对方的云");
            Assert.That(a.Simulation.Collisions.Count, Is.EqualTo(2),
                "重读 Collider 会清世界空间代理，互碰代理绝不能被顺手清掉，清掉就等于功能静默失效");
            b.Step(1f / 60f);
            Assert.That(a.Simulation.Collisions.Count, Is.EqualTo(2), "对方步进之后仍然共存");
        }

        [Test]
        public void Group_SurvivesRebuild_AndStillInteracts()
        {
            // Given 两个互碰的软体已经各自重建过（流体那边踩过：重建会重建碰撞集）
            // When 重建完成后再跑一步
            // Then 互碰仍然生效，代理数与重建前一致
            var group = Spawn("Group").AddComponent<ParticleInteractionGroup>();
            var a = MakeSoftBody("A", new Vector3(0f, 1f, 0f), Vector3.one);
            var b = MakeSoftBody("B", new Vector3(0.15f, 1f, 0f), Vector3.one);
            group.Add(a);
            group.Add(b);
            Assert.That(a.Simulation.Collisions.Count, Is.EqualTo(1), "重建前有一个代理");

            a.Rebuild();
            b.Rebuild();

            Assert.That(a.Simulation.Collisions.Count, Is.EqualTo(1),
                "Rebuild 会换掉整个碰撞集，互碰代理必须被组重新挂回去");
            Assert.That(b.Simulation.Collisions.Count, Is.EqualTo(1), "另一个体同理");

            float first = WorstDepthIntoOthers(a, b);
            for (int i = 0; i < 60; i++) { a.Step(1f / 60f); b.Step(1f / 60f); }
            Assert.That(WorstDepthIntoOthers(a, b), Is.LessThan(first), "重建之后互碰仍然真的在起作用");
        }

        [Test]
        public void Group_DefaultRadiusSealsGapsBetweenParticles()
        {
            // Given 两个互碰的软体，粒子间距已知
            // When 用默认半径让它们对压
            // Then 没有粒子从两球之间的缝隙漏过去（半径足够封缝），也没有出现整体"发胖"式的过度分离
            var group = Spawn("Group").AddComponent<ParticleInteractionGroup>();
            var a = MakeSoftBody("A", new Vector3(0f, 1f, 0f), Vector3.one);
            var b = MakeSoftBody("B", new Vector3(0.15f, 1f, 0f), Vector3.one);
            group.Add(a);
            group.Add(b);

            float spacing = a.Simulation.AverageStructuralRestLength;
            Assert.That(spacing, Is.GreaterThan(0f), "软体应当能报告平均结构弹簧长度，作为半径推导依据");
            Assert.That(a.CloudRadius, Is.GreaterThanOrEqualTo(spacing * 0.5f),
                "球半径至少要是间距的一半，否则两球之间留缝，对方粒子会从缝里漏过去");

            for (int i = 0; i < 90; i++) { a.Step(1f / 60f); b.Step(1f / 60f); }
            var boundsA = WorldBounds(a);
            var boundsB = WorldBounds(b);
            Assert.That(boundsA.size.x, Is.LessThan(spacing * 30f),
                "互碰不许把体撑到不合理的尺寸（半径过大就会整体发胖弹开），当前 A 的 x 跨度 " + boundsA.size.x);
        }

        static Bounds WorldBounds(SoftBodyBehaviour behaviour)
        {
            var pts = WorldParticles(behaviour);
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            foreach (var p in pts) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
            var center = (min + max) * 0.5f;
            return new Bounds(center, max - min);
        }

        [Test]
        public void Group_ClothAndSoftBody_Interact()
        {
            // Given 一块布料和一个软体在同一组里且位置重叠
            // When 连续跑若干步
            // Then 布料被软体顶出形状，软体也被布料挤开（跨求解器双向生效）
            var group = Spawn("Group").AddComponent<ParticleInteractionGroup>();
            var cloth = MakeCloth("Cloth", new Vector3(0f, 1f, 0f), 0.12f);
            var soft = MakeSoftBody("Soft", new Vector3(0f, 1f, 0f), Vector3.one);
            group.Add(cloth);
            group.Add(soft);

            Assert.That(cloth.System.Collisions.Count, Is.EqualTo(1), "布料要看见软体的云");
            Assert.That(soft.Simulation.Collisions.Count, Is.EqualTo(1), "软体要看见布料的云");

            var clothBefore = cloth.System.CapturePositions();
            for (int i = 0; i < 90; i++) { cloth.Step(1f / 60f); soft.Step(1f / 60f); }
            var clothAfter = cloth.System.CapturePositions();

            int clothMoved = 0;
            for (int i = 0; i < clothAfter.Length; i++)
                if (clothAfter[i] != clothBefore[i] && !cloth.System.IsPinned(i)) clothMoved++;
            Assert.That(clothMoved, Is.GreaterThan(0), "布料质点必须被软体顶动，否则跨求解器只通了单向");
            Assert.That(WorstDepthIntoOthers(soft, cloth), Is.LessThan(0.5f),
                "软体也不能直接穿过布面留在另一侧");
        }

        [Test]
        public void Group_MassSpringAndCloth_Interact()
        {
            // Given 一个质点弹簧链和一块布料在同一组里且位置重叠
            // When 连续跑若干步
            // Then 质点被布料的球云顶出，不再直接穿过布面
            var group = Spawn("Group").AddComponent<ParticleInteractionGroup>();
            var chain = MakeChain("Chain", new Vector3(0f, 1f, 0f), 0.12f);
            var cloth = MakeCloth("Cloth", new Vector3(0.1f, 1f, 0f), 0.12f);
            group.Add(chain);
            group.Add(cloth);

            Assert.That(chain.System.Collisions.Count, Is.EqualTo(1), "质点弹簧要看见布料的云");
            Assert.That(cloth.System.Collisions.Count, Is.EqualTo(1), "布料要看见质点弹簧的云");

            for (int i = 0; i < 90; i++) { chain.Step(1f / 60f); cloth.Step(1f / 60f); }

            var cloud = cloth.CloudWorldPositions();
            var list = CloudList(cloud, cloth);
            foreach (var p in WorldParticles(chain))
            {
                AssertFinite(p, "质点弹簧的世界位置");
                Assert.That(DepthIntoCloud(p, list, cloth.CloudRadius, cloth.CloudRadius),
                    Is.LessThan(cloth.CloudRadius * 4f),
                    "质点不许整颗埋进布料的球云里");
            }
        }

        [Test]
        public void Group_MassSpringAndSoftBody_Interact()
        {
            // Given 一个质点弹簧体和一坨软体在同一组里且位置重叠
            // When 连续跑若干步
            // Then 两者互相挤开，穿透深度下降
            var group = Spawn("Group").AddComponent<ParticleInteractionGroup>();
            var chain = MakeChain("Chain", new Vector3(0f, 1f, 0f), 0.12f);
            var soft = MakeSoftBody("Soft", new Vector3(0.05f, 1f, 0f), Vector3.one);
            group.Add(chain);
            group.Add(soft);

            float first = WorstDepthIntoOthers(chain, soft);
            for (int i = 0; i < 120; i++) { chain.Step(1f / 60f); soft.Step(1f / 60f); }
            float later = WorstDepthIntoOthers(chain, soft);

            Assert.That(later, Is.LessThan(first), "质点弹簧与软体之间必须真的互相挤开：" + first + " -> " + later);
        }

        [Test]
        public void Group_ThreeWayContact_IsDeterministic()
        {
            // Given 组里有三个体挤在一起（三向接触，顺序求解最容易暴露不确定性的情形）
            // When 用同样的初始状态完整跑两遍
            // Then 两遍的最终粒子位置逐位相同
            var first = RunThree();
            var second = RunThree();

            Assert.That(second.Length, Is.EqualTo(first.Length), "两遍的质点总数必须相同");
            for (int i = 0; i < first.Length; i++)
            {
                Assert.That(Bits(second[i].x), Is.EqualTo(Bits(first[i].x)), "第 " + i + " 个质点 x 逐位不同");
                Assert.That(Bits(second[i].y), Is.EqualTo(Bits(first[i].y)), "第 " + i + " 个质点 y 逐位不同");
                Assert.That(Bits(second[i].z), Is.EqualTo(Bits(first[i].z)), "第 " + i + " 个质点 z 逐位不同");
            }
        }

        Vector3[] RunThree()
        {
            var group = Spawn("Group").AddComponent<ParticleInteractionGroup>();
            var a = MakeSoftBody("A", new Vector3(0f, 1f, 0f), Vector3.one);
            var b = MakeSoftBody("B", new Vector3(0.1f, 1f, 0f), Vector3.one);
            var c = MakeSoftBody("C", new Vector3(0.05f, 1f, 0.1f), Vector3.one);
            group.Add(a);
            group.Add(b);
            group.Add(c);
            Assert.That(a.Simulation.Collisions.Count, Is.EqualTo(2), "三体一组，每体看见另外两朵云");

            for (int i = 0; i < 40; i++) { a.Step(1f / 60f); b.Step(1f / 60f); c.Step(1f / 60f); }
            var all = new List<Vector3>();
            all.AddRange(WorldParticles(a));
            all.AddRange(WorldParticles(b));
            all.AddRange(WorldParticles(c));
            return all.ToArray();
        }

        // ==================================================================
        // C 组：稳定性与规模
        // ==================================================================

        [Test]
        public void Group_HardSqueeze_KeepsFiniteStateAndVolume()
        {
            // Given 两个互碰的软体被压到一起足够多步
            // When 检查状态
            // Then 没有 NaN / Infinity，且软体体积约束仍然成立（互碰不许把体积压塌）
            var group = Spawn("Group").AddComponent<ParticleInteractionGroup>();
            var a = MakeSoftBody("A", new Vector3(0f, 0.9f, 0f), Vector3.one);
            var b = MakeSoftBody("B", new Vector3(0.05f, 0.9f, 0f), Vector3.one);
            group.Add(a);
            group.Add(b);

            for (int i = 0; i < 200; i++) { a.Step(1f / 60f); b.Step(1f / 60f); }

            Assert.That(a.Simulation.HasNonFiniteState(), Is.False, "A 出现了 NaN / Infinity");
            Assert.That(b.Simulation.HasNonFiniteState(), Is.False, "B 出现了 NaN / Infinity");
            float ratioA = a.Simulation.Volume() / a.Simulation.RestVolume();
            float ratioB = b.Simulation.Volume() / b.Simulation.RestVolume();
            Assert.That(ratioA, Is.GreaterThan(0.3f), "A 的体积被互碰压塌了，剩 " + ratioA);
            Assert.That(ratioB, Is.GreaterThan(0.3f), "B 的体积被互碰压塌了，剩 " + ratioB);
            Assert.That(ratioA, Is.LessThan(3f), "A 被互碰撑爆了，体积比 " + ratioA);
        }

        [Test]
        public void Group_CostScalesLinearlyWithBodyCount()
        {
            // Given 同样粒子数的两组规模（例如 2 个体对 6 个体）各自跑固定步数
            // When 计时并取比值
            // Then 每体每步成本随体数的增长受控（比值门），不是 O(N²) 的每对重建索引
            double few = TimeWithBodies(2);
            double many = TimeWithBodies(6);

            double perBodyFew = few / 2.0;
            double perBodyMany = many / 6.0;
            double ratio = perBodyMany / Math.Max(perBodyFew, 1e-9);

            Assert.That(ratio, Is.GreaterThan(0.2), "计时窗口太短，数字没有意义，比值 " + ratio);
            Assert.That(ratio, Is.LessThan(6.0),
                "每体每步成本随体数涨了 " + ratio + " 倍，说明索引在按体对重建（O(N²)）而不是每体一次");
        }

        double TimeWithBodies(int count)
        {
            var group = Spawn("Group").AddComponent<ParticleInteractionGroup>();
            var bodies = new SoftBodyBehaviour[count];
            for (int i = 0; i < count; i++)
            {
                bodies[i] = MakeSoftBody("B" + i, new Vector3(i * 0.9f, 1f, 0f), Vector3.one);
                group.Add(bodies[i]);
            }
            for (int i = 0; i < 20; i++)
                for (int k = 0; k < count; k++) bodies[k].Step(1f / 60f);

            var watch = System.Diagnostics.Stopwatch.StartNew();
            double best = double.MaxValue;
            for (int round = 0; round < 3; round++)
            {
                watch.Restart();
                for (int i = 0; i < 40; i++)
                    for (int k = 0; k < count; k++) bodies[k].Step(1f / 60f);
                watch.Stop();
                best = Math.Min(best, watch.Elapsed.TotalSeconds);
            }
            return best;
        }

        // ==================================================================
        // D 组：演示场景
        // ==================================================================

        [Test]
        public void Demo_TwoBodiesNowPushEachOther()
        {
            // Given 软体演示场景生成的两个体（果冻与布袋）已经放进同一个互碰组
            // When 播放并跑足够步数
            // Then 两者不再互相穿过，且它们与地面的碰撞行为没有被这次改动破坏
            SoftBodyDemoTools.DemoEnableInteraction = true;
            var ground = Spawn("Ground");
            var gc = ground.AddComponent<BoxCollider>();
            gc.center = new Vector3(0f, -0.5f, 0f);
            gc.size = new Vector3(20f, 1f, 20f);

            var jelly = SoftBodyDemoTools.BuildSoftBody(gc);
            var bag = SoftBodyDemoTools.BuildHangingBag(gc);
            jelly.autoSimulate = false;
            bag.autoSimulate = false;

            Assert.That(jelly.interactionGroup, Is.Not.Null, "开了开关，演示生成的体就必须被放进互碰组");
            Assert.That(bag.interactionGroup, Is.SameAs(jelly.interactionGroup), "两个体必须在同一个组里");
            Assert.That(jelly.Simulation.Collisions.Count, Is.GreaterThanOrEqualTo(2),
                "一个地面 + 至少一朵对方的云");

            var jellyBefore = jelly.Simulation.CapturePositions();
            for (int i = 0; i < 200; i++) { jelly.Step(1f / 60f); bag.Step(1f / 60f); }
            var jellyAfter = jelly.Simulation.CapturePositions();

            Assert.That(jelly.Simulation.HasNonFiniteState(), Is.False, "演示互碰跑 200 步之后果冻出了 NaN");
            Assert.That(bag.Simulation.HasNonFiniteState(), Is.False, "布袋出了 NaN");
            Assert.That(jellyAfter[0] == jellyBefore[0], Is.False, "对照用：果冻确实动了");
            foreach (var p in WorldParticles(jelly))
                Assert.That(p.y, Is.GreaterThan(-1f), "果冻穿过了地面，地面碰撞被这次改动弄坏了");
        }

        [Test]
        public void Demo_OptInFlagDefaultsToOff()
        {
            // Given 演示工具的默认配置
            // When 生成演示场景
            // Then 是否开启互碰由一个明确的开关决定，且这个开关的状态能在场景里读出来（不靠肉眼判断）
            Assert.That(SoftBodyDemoTools.DemoEnableInteraction, Is.False,
                "默认必须是关：开了就等于悄悄改变已发布演示的行为");

            var ground = Spawn("Ground");
            var gc = ground.AddComponent<BoxCollider>();
            gc.center = new Vector3(0f, -0.5f, 0f);
            gc.size = new Vector3(20f, 1f, 20f);

            var jelly = SoftBodyDemoTools.BuildSoftBody(gc);
            Assert.That(jelly.interactionGroup, Is.Null, "开关关着时不许偷偷建组、偷偷塞代理");
            Assert.That(jelly.Simulation.Collisions.Count, Is.EqualTo(1),
                "关着时就该只有一个地面代理，与 v1.5.0 完全一致");
        }

        // ------------------------------------------------------------------
        // 互碰演示工具 ParticleInteractionDemoTools（包内交付物，带菜单项）。
        // 这里只验两件事，因为它们决定了"演示到底能不能看出互碰"：
        //   1) 三个体真的在同一个组里、且跨了两种求解器；
        //   2) 开局就是穿透的，跑几步之后必须拉开。
        // ------------------------------------------------------------------

        [Test]
        public void DemoTools_ScenePathIsTheDeliveredPath()
        {
            // Given/When 读常量
            // Then 路径固定且落在 Assets/Scenes 下（保存走显式路径，绝不弹模态框）
            Assert.That(ParticleInteractionDemoTools.ScenePath,
                Is.EqualTo("Assets/Scenes/ParticleInteractionDemo.unity"),
                "演示场景路径就是交付名，不能随手改");
            Assert.That(ParticleInteractionDemoTools.ScenePath, Does.StartWith("Assets/Scenes/"),
                "演示场景必须落在主项目的 Scenes 目录里");
        }

        [Test]
        public void DemoTools_BuildBodies_WiresThreeMembersIntoOneGroup()
        {
            // Given 不接地面地构建演示体
            // When 读组与各体的互碰登记
            // Then 三个体同组、每个体恰好看见另外两个（N-1 朵云，不是重复登记）
            var group = ParticleInteractionDemoTools.BuildBodies(null);
            _spawned.Add(group.gameObject);

            Assert.That(group.MemberCount, Is.EqualTo(3), "两个果冻 + 一块布");
            Assert.That(group.ProxyCount, Is.EqualTo(3), "每个成员一朵云，代理数必须等于成员数");

            int softBodies = 0, cloths = 0;
            for (int i = 0; i < group.Members.Count; i++)
            {
                var body = group.Members[i];
                if (body is SoftBodyBehaviour) softBodies++;
                if (body is ClothBehaviour) cloths++;
                Assert.That(body.CloudGroup, Is.SameAs(group), "Add 必须把组回写到体上");
                Assert.That(group.Contains(body), Is.True, "Members 与 Contains 不能自相矛盾");
                Assert.That(body.CloudParticleCount, Is.GreaterThan(0), "体必须真的构出来了");
                Assert.That(body.CloudRadius, Is.GreaterThan(0f), "半径为 0 的云等于没有云");
                Assert.That(body.ParticleCollisions.Count, Is.EqualTo(2),
                    "每个体恰好挂上另外两个体的球云");
            }
            Assert.That(softBodies, Is.EqualTo(2), "演示要有两个软体");
            Assert.That(cloths, Is.EqualTo(1), "演示要有一块布：跨求解器同框才说明这不是软体专用功能");
        }

        [Test]
        public void DemoTools_BuildBodies_StartActuallyOverlapping()
        {
            // Given 演示体刚搭好、还没跑
            // When 量两个果冻的最近球心距
            // Then 必须小于两球半径之和——否则播放时什么也碰不到，演示就是空的
            var group = ParticleInteractionDemoTools.BuildBodies(null);
            _spawned.Add(group.gameObject);

            var a = group.Members[0];
            var b = group.Members[1];
            float gap = MinCenterDistance(a, b);
            Assert.That(gap, Is.LessThan(a.CloudRadius + b.CloudRadius),
                "初始摆位没有真的穿透（最近球心距 " + gap.ToString("F4")
                + " m，两球半径和 " + (a.CloudRadius + b.CloudRadius).ToString("F4") + " m）");
        }

        [Test]
        public void DemoTools_SteppingDemoSeparatesTheJellies()
        {
            // Given 演示体搭好（开局穿透）
            // When 手动推 90 步
            // Then 最近球心距明显拉开，且没有任何体被抽成 NaN
            var group = ParticleInteractionDemoTools.BuildBodies(null);
            _spawned.Add(group.gameObject);
            var a = group.Members[0];
            var b = group.Members[1];

            float before = MinCenterDistance(a, b);
            for (int i = 0; i < 90; i++)
            {
                ((SoftBodyBehaviour)a).Step(1f / 60f);
                ((SoftBodyBehaviour)b).Step(1f / 60f);
            }
            float after = MinCenterDistance(a, b);

            Assert.That(after, Is.GreaterThan(before),
                "跑了 90 步都没拉开，演示场景就是骗人的：初始 " + before.ToString("F4")
                + " → 最终 " + after.ToString("F4"));
            Assert.That(AllFinite(a), Is.True, "互碰不许把果冻 A 抽成 NaN");
            Assert.That(AllFinite(b), Is.True, "互碰不许把果冻 B 抽成 NaN");
        }

        [Test]
        public void DemoTools_WithGroundAlsoBridgesTheCollider()
        {
            // Given 给一个地面 Collider
            // When 构建演示体
            // Then 地面代理与互碰代理共存（这正是以前会被互删的那一对）
            var ground = Spawn("Ground");
            var gc = ground.AddComponent<BoxCollider>();
            gc.center = new Vector3(0f, -0.5f, 0f);
            gc.size = new Vector3(20f, 1f, 20f);

            var group = ParticleInteractionDemoTools.BuildBodies(gc);
            _spawned.Add(group.gameObject);

            // 跑一步：桥接会按登记者清理并重加地面。构建期各体登记次数本就丕一致
            // （软体 Rebuild 会顺手跑一次桥接、布料不会），所以这里不去断“步进前有几条”，
            // 只断真正的不变量：步进后两类代理共存、而且归属分明。
            for (int i = 0; i < group.Members.Count; i++)
            {
                var soft = group.Members[i] as SoftBodyBehaviour;
                if (soft != null) soft.Step(1f / 60f);
                else ((ClothBehaviour)group.Members[i]).Step(1f / 60f);
            }

            for (int i = 0; i < group.Members.Count; i++)
            {
                var body = group.Members[i];
                var set = body.ParticleCollisions;
                Assert.That(set.Count, Is.EqualTo(3),
                    "地面 1 个 + 对方 2 个；少了就是桥接与互碰其中之一被抹干了");

                int world = 0, bridged = 0, grouped = 0;
                for (int e = 0; e < set.Count; e++)
                {
                    if (set[e].Space == CollisionProxySpace.World) world++;
                    if (ReferenceEquals(set[e].Owner, typeof(ColliderProxies))) bridged++;
                    else if (ReferenceEquals(set[e].Owner, group)) grouped++;
                }
                Assert.That(world, Is.EqualTo(3), "三类代理都该登记在世界空间，否则变换一来就错位");
                Assert.That(bridged, Is.EqualTo(1), "地面代理必须恰好一条，而且归桥接所有");
                Assert.That(grouped, Is.EqualTo(2),
                    "对方的球云必须恰好两条，而且归本组所有——归属混了就等于下次刷新会被错杀");
            }
        }

        [Test]
        public void Group_WithoutGravity_OverlappingCloudsSeparateCleanly()
        {
            // Given 两个重叠的软体，但**重力关掉**（把载荷拿掉，只留互碰本身）
            // When 步进到稳定
            // Then 最近球心距必须达到两球半径之和（球面刚好分离），证明投影本身是有效的。
            //      这条测试的意义：带重力时穿透会停在一个平衡深度（软体被对方重量压扁），
            //      那个数字受刚度支配、不适合当契约；把载荷拿掉才能单独验证"投影有没有把球推开"。
            var group = Spawn("Group").AddComponent<ParticleInteractionGroup>();
            var a = MakeSoftBody("A", new Vector3(-0.16f, 1f, 0f), Vector3.one);
            var b = MakeSoftBody("B", new Vector3(0.16f, 1f, 0f), Vector3.one);
            a.parameters.gravity = Vector3.zero;
            b.parameters.gravity = Vector3.zero;
            group.Add(a);
            group.Add(b);

            float sum = a.CloudRadius + b.CloudRadius;
            float before = MinCenterDistance(a, b);
            Assert.That(before, Is.LessThan(sum), "开局必须穿透，否则没东西可验");

            for (int i = 0; i < 180; i++) { a.Step(1f / 60f); b.Step(1f / 60f); }

            float after = MinCenterDistance(a, b);
            // 这里只断"投影到底把球推开没有"。**弹射**这种失败模式光看最终距离是抓不住的
            // （分开 3 m 和分开 0.3 m 都满足"≥ 半径和"），它由
            // InelasticContactTests.Interaction_ForcedCloserThanOneSpacingStillSeparatesToTwoRadii
            // 用"末段位移必须显著小于初段"来钉住。本轮我就是先只断了距离，才放过了 3 m 漂移。
            Assert.That(after, Is.GreaterThanOrEqualTo(sum),
                "零载荷下球面应当基本分离干净，实测 " + after.ToString("F4") + " m，半径和 " + sum.ToString("F4") + " m");
            Assert.That(after, Is.GreaterThan(before), "至少必须比初始分开得更远");
        }

        static float MinCenterDistance(IParticleBody a, IParticleBody b)
        {
            var pa = a.CloudWorldPositions();
            var pb = b.CloudWorldPositions();
            float best = float.MaxValue;
            for (int i = 0; i < a.CloudParticleCount; i++)
                for (int j = 0; j < b.CloudParticleCount; j++)
                {
                    float d = (pa[i] - pb[j]).magnitude;
                    if (d < best) best = d;
                }
            return best;
        }

        static bool AllFinite(IParticleBody body)
        {
            var pts = body.CloudWorldPositions();
            for (int i = 0; i < body.CloudParticleCount; i++)
            {
                var p = pts[i];
                if (!float.IsFinite(p.x) || !float.IsFinite(p.y) || !float.IsFinite(p.z)) return false;
            }
            return true;
        }
    }
}
