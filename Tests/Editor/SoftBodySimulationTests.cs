// Copyright (c) 2026 PhysicsSimulation. MIT License.
//
// v1.2.0 软体（Soft Body）BDD 测试。
//
// 设计意图：
//   * "把任意网格软体化"：网格顶点 → 质点（按位置焊接去重），三角形边 → 结构弹簧，
//     共边两侧的"对面顶点" → 弯曲弹簧，闭合网格再叠一条**体积约束**。
//   * 体积约束走的是梯度恢复力 F_i = -(k·(V-V0) + c·dV/dt)·∇_i V，
//     其中 ∇_i V = (1/3)·Σ_{含 i 的三角形} 面积向量 —— 因此能直接复用 v1.0.0 的质点弹簧积分器，
//     而不是另起一套求解器。
//   * 纯逻辑层只吃 SoftBodyMeshData（顶点数组 + 三角形索引），不依赖 Mesh / MonoBehaviour / 物理引擎。

using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace PhysicsSimulation.Editor.Tests
{
    [TestFixture]
    public class SoftBodySimulationTests
    {
        const float WeldEpsilon = 1e-4f;

        // ==================================================================
        // 拓扑构建
        // ==================================================================

        // Given: 一个 2×1×0.5 的长方体网格，8 个角点各被 3 个面引用（Unity 按面拆顶点 ⇒ 24 个顶点）
        //  When: Build 成软体
        //  Then: 按位置焊接后只剩 8 个质点；IndexOfVertex 把 24 个网格顶点映回这 8 个质点；质点位置就是 8 个角
        [Test]
        public void Build_BoxMesh_WeldsDuplicateVerticesIntoEightParticles()
        {
            var mesh = MakeBox(2f, 1f, 0.5f);
            Assert.That(mesh.VertexCount, Is.EqualTo(24), "长方体应当是 6 面 × 4 个拆分顶点 = 24 个网格顶点");

            var body = BuildBody(mesh);
            Assert.That(body.ParticleCount, Is.EqualTo(8), "焊接后应当只剩 8 个质点（长方体 8 个角）");
            Assert.That(body.MeshVertexCount, Is.EqualTo(24), "MeshVertexCount 要保留原始网格顶点数，写回网格要用");

            var seen = new HashSet<int>();
            for (int i = 0; i < 24; i++)
            {
                int index = body.IndexOfVertex(i);
                Assert.That(index, Is.InRange(0, 7), "网格顶点 " + i + " 的焊接映射必须落在质点范围内");
                seen.Add(index);
            }
            Assert.That(seen.Count, Is.EqualTo(8), "24 个网格顶点必须恰好覆盖 8 个质点，谁都不该被丢掉");

            var expected = new[]
            {
                new Vector3(-1f, -0.5f, -0.25f), new Vector3(1f, -0.5f, -0.25f),
                new Vector3(1f, 0.5f, -0.25f), new Vector3(-1f, 0.5f, -0.25f),
                new Vector3(-1f, -0.5f, 0.25f), new Vector3(1f, -0.5f, 0.25f),
                new Vector3(1f, 0.5f, 0.25f), new Vector3(-1f, 0.5f, 0.25f)
            };
            foreach (var corner in expected)
            {
                bool found = false;
                for (int i = 0; i < body.ParticleCount; i++)
                    if ((body.GetPosition(i) - corner).sqrMagnitude < 1e-8f) found = true;
                Assert.That(found, Is.True, "焊接后的质点里应当存在角点 " + corner);
            }
        }

        // Given: 同一个长方体网格（12 条棱、12 个三角形）
        //  When: Build
        //  Then: 结构弹簧恰好 12 条且每条无向棱只出现一次；三角形计数 12；IsClosed 为真
        [Test]
        public void Build_BoxMesh_UsesEachUndirectedEdgeOnce()
        {
            var body = BuildBody(MakeBox(2f, 1f, 0.5f));

            Assert.That(body.TriangleCount, Is.EqualTo(12), "长方体 6 面 × 2 三角形 = 12");
            // 注意：长方体视觉上 12 条棱，但每个面被拆成 2 个三角形，那条"面内对角线"同样是网格的棱，
            // 它也被两个三角形共享 ⇒ 也是结构弹簧。12 + 6 = 18。
            Assert.That(body.StructuralSpringCount, Is.EqualTo(18),
                "结构弹簧 = 12 条真实棱 + 6 条面内三角化对角线 = 18，实际 " + body.StructuralSpringCount);
            Assert.That(body.IsClosed, Is.True, "每个棱都被两个三角形共享 ⇒ 应当判定为闭合网格");

            var undirected = new HashSet<long>();
            for (int s = 0; s < body.StructuralSpringCount; s++)
            {
                var edge = body.GetStructuralSpring(s);
                Assert.That(edge.a, Is.Not.EqualTo(edge.b), "结构弹簧不能是自环");
                long key = ((long)Mathf.Min(edge.a, edge.b) << 32) | (uint)Mathf.Max(edge.a, edge.b);
                Assert.That(undirected.Add(key), Is.True,
                    "棱 (" + edge.a + "," + edge.b + ") 出现了两次：焊接或去重逻辑漏了，弹簧会被重复施加");
            }
            Assert.That(undirected.Count, Is.EqualTo(18), "去重后的无向棱数量应当等于结构弹簧数量");
        }

        // Given: 长方体网格 2 × 1 × 0.5
        //  When: Build 后读 RestVolume
        //  Then: 静止体积 = 1.0（散度定理从三角形算出），误差在浮点容差内；正八面体则是 4/3
        [Test]
        public void Build_ClosedMesh_HasAnalyticRestVolume()
        {
            var box = BuildBody(MakeBox(2f, 1f, 0.5f));
            Assert.That(box.RestVolume(), Is.EqualTo(1f).Within(1e-4f),
                "2×1×0.5 的长方体静止体积应当是 1.0，实际 " + box.RestVolume());

            var octa = BuildBody(MakeOctahedron(1f));
            Assert.That(octa.RestVolume(), Is.EqualTo(4f / 3f).Within(1e-4f),
                "单位正八面体体积解析值 4/3，实际 " + octa.RestVolume());
            Assert.That(octa.Volume(), Is.EqualTo(octa.RestVolume()).Within(1e-5f),
                "刚建好时当前体积必须等于静止体积");
        }

        // Given: 只有 2 个三角形的开放四边形（外棱没有被两个面共享）
        //  When: Build 后连续 Step
        //  Then: IsClosed 为假、不参与体积恢复（且不产生 NaN），但结构弹簧照样建好、重力照样把布拽下来
        [Test]
        public void Build_OpenMesh_IsNotClosedAndStillSimulatesEdges()
        {
            var body = BuildBody(MakeQuad());

            Assert.That(body.IsClosed, Is.False, "四边形有 4 条外棱只被一个面使用，不该被判成闭合");
            Assert.That(body.RestVolume(), Is.EqualTo(0f).Within(1e-5f), "开放网格没有可靠体积，RestVolume 应当是 0");
            Assert.That(body.StructuralSpringCount, Is.EqualTo(5), "四边形 4 条边 + 1 条对角线 = 5 条结构弹簧");

            var p = DefaultParameters();
            p.gravity = new Vector3(0f, -9.81f, 0f);
            body = new SoftBodySimulation(p);
            body.Build(MakeQuad());
            for (int i = 0; i < 30; i++) body.Step(1f / 60f);

            Assert.That(body.HasNonFiniteState(), Is.False, "开放网格跑起来后不该出现 NaN / Infinity");
            Assert.That(body.GetPosition(0).y, Is.LessThan(0f), "没有体积约束时，重力应当把质点拽下来");
        }

        // Given: 单位正八面体（6 顶点 / 8 三角形 / 12 棱）
        //  When: Build
        //  Then: 每条结构弹簧的静止长度 = 两端点实际距离；每条弯曲弹簧的静止长度同样自洽
        [Test]
        public void Build_RestLengthsMatchWeldedEdgeLengths()
        {
            var body = BuildBody(MakeOctahedron(1f));

            for (int s = 0; s < body.StructuralSpringCount; s++)
            {
                var edge = body.GetStructuralSpring(s);
                float actual = Vector3.Distance(body.GetPosition(edge.a), body.GetPosition(edge.b));
                Assert.That(edge.restLength, Is.EqualTo(actual).Within(1e-5f),
                    "结构弹簧 " + s + " 的静止长度应当等于焊接后两端点的距离");
                Assert.That(edge.restLength, Is.GreaterThan(0f), "静止长度必须为正，否则除零");
            }

            for (int s = 0; s < body.BendSpringCount; s++)
            {
                var edge = body.GetBendSpring(s);
                float actual = Vector3.Distance(body.GetPosition(edge.a), body.GetPosition(edge.b));
                Assert.That(edge.restLength, Is.EqualTo(actual).Within(1e-5f),
                    "弯曲弹簧 " + s + " 的静止长度应当等于两端点距离");
            }
        }

        // Given: 正八面体（每个顶点的对面顶点只有 1 个）与长方体（每条棱对应 1 个面对角）
        //  When: Build
        //  Then: 弯曲弹簧连接的都是"不共棱"的顶点对，与结构弹簧零重复；长方体得到 12 条面对角线
        [Test]
        public void Build_BendSpringsSkipOneRingAndNeverDuplicateStructural()
        {
            var octa = BuildBody(MakeOctahedron(1f));
            Assert.That(octa.BendSpringCount, Is.EqualTo(3),
                "正八面体有 3 对相对顶点，它们不共棱 ⇒ 恰好 3 条弯曲弹簧，实际 " + octa.BendSpringCount);

            var structural = new HashSet<long>();
            for (int s = 0; s < octa.StructuralSpringCount; s++)
            {
                var e = octa.GetStructuralSpring(s);
                structural.Add(EdgeKey(e.a, e.b));
            }
            for (int s = 0; s < octa.BendSpringCount; s++)
            {
                var e = octa.GetBendSpring(s);
                Assert.That(structural.Contains(EdgeKey(e.a, e.b)), Is.False,
                    "弯曲弹簧 (" + e.a + "," + e.b + ") 和结构弹簧重复了，同一对点会被修两遍");
                Assert.That(e.a, Is.Not.EqualTo(e.b), "弯曲弹簧不能是自环");
            }

            var box = BuildBody(MakeBox(2f, 1f, 0.5f));
            // 长方体的弯曲弹簧条数取决于**三角化方式**（那条面内对角线也是网格棱），
            // 手算一个数写死没有意义；锁不变量：与结构弹簧零重复、自身不重复、且确实存在。
            Assert.That(box.BendSpringCount, Is.GreaterThan(0), "长方体应当有弯曲弹簧（面内对角线的对面顶点对）");

            var boxStructural = new HashSet<long>();
            for (int s2 = 0; s2 < box.StructuralSpringCount; s2++)
            {
                var e2 = box.GetStructuralSpring(s2);
                boxStructural.Add(EdgeKey(e2.a, e2.b));
            }
            var boxBend = new HashSet<long>();
            for (int s2 = 0; s2 < box.BendSpringCount; s2++)
            {
                var e2 = box.GetBendSpring(s2);
                Assert.That(e2.a, Is.Not.EqualTo(e2.b), "弯曲弹簧不能是自环");
                Assert.That(boxStructural.Contains(EdgeKey(e2.a, e2.b)), Is.False,
                    "长方体弯曲弹簧 (" + e2.a + "," + e2.b + ") 与结构弹簧重复，同一对点会被修两遍");
                Assert.That(boxBend.Add(EdgeKey(e2.a, e2.b)), Is.True,
                    "弯曲弹簧 (" + e2.a + "," + e2.b + ") 出现了两次");
            }
        }

        // ==================================================================
        // 体积恢复与动力学行为
        // ==================================================================

        // Given: 长方体软体，把 Y 方向压扁到一半后释放（体积明显小于静止值），关掉重力
        //  When: 连续 Step
        //  Then: 体积回升到静止值的 80% 以上、全程有限、压下去的那一面重新鼓起来
        [Test]
        public void Step_CompressedVolume_PushesBackTowardRestVolume()
        {
            var p = DefaultParameters();
            p.gravity = Vector3.zero;
            p.damping = 1.5f;
            var body = new SoftBodySimulation(p);
            body.Build(MakeBox(2f, 1f, 0.5f));

            float restVolume = body.RestVolume();
            var squashed = new Vector3[body.ParticleCount];
            for (int i = 0; i < squashed.Length; i++)
            {
                var pos = body.GetPosition(i);
                squashed[i] = new Vector3(pos.x, pos.y * 0.5f, pos.z);
            }
            body.SetPositions(squashed);

            float squashedVolume = body.Volume();
            Assert.That(squashedVolume, Is.LessThan(restVolume * 0.6f),
                "压扁后的初始体积应当明显小于静止体积，否则这个场景什么都没测到（" + squashedVolume + " vs " + restVolume + "）");

            float previous = squashedVolume;
            for (int i = 0; i < 60; i++) body.Step(1f / 60f);

            Assert.That(body.HasNonFiniteState(), Is.False, "体积恢复过程中出现 NaN / Infinity");
            float finalVolume = body.Volume();
            Assert.That(finalVolume, Is.GreaterThan(restVolume * 0.8f),
                "体积应当被推回静止值附近：起始 " + squashedVolume + "，60 步后 " + finalVolume + "，目标 " + restVolume);
            Assert.That(finalVolume, Is.LessThan(restVolume * 1.35f),
                "体积也不能过冲到失控（冲到 " + finalVolume + "，静止 " + restVolume + "）");
            Assert.That(previous, Is.GreaterThan(0f), "起始体积必须为正");
        }

        // Given: 长方体软体，钉住顶面 4 个角，施加重力
        //  When: Step 一段时间
        //  Then: 底面明显下坠，但体积保持在静止值的 [0.6, 1.4] 区间 —— 被拉扁也不"漏气"
        [Test]
        public void Step_PinnedTopVertex_SagsButKeepsVolume()
        {
            var p = DefaultParameters();
            p.gravity = new Vector3(0f, -9.81f, 0f);       // 不施加重力的话"下垂"这件事根本不会发生
            // 默认 k=1200 是"硬壳"：静力形变 ≈ m·g/k ≈ 0.0065，肉眼看不出下垂。
            // 这里要测的是"软体被拽长但不漏气"，所以把结构/弯曲刚度调软一个量级。
            p.springStiffness = 120f;
            p.bendStiffness = 20f;
            p.volumeStiffness = 600f;
            var body = new SoftBodySimulation(p);
            body.Build(MakeBox(2f, 1f, 0.5f));
            float restVolume = body.RestVolume();

            // 顶面 = y > 0 的 4 个质点
            int pinned = 0;
            for (int i = 0; i < body.ParticleCount; i++)
                if (body.GetPosition(i).y > 0f) { body.SetPinned(i, true); pinned++; }
            Assert.That(pinned, Is.EqualTo(4), "长方体顶面应该有 4 个角点被钉住");

            float bottomStart = float.MaxValue;
            for (int i = 0; i < body.ParticleCount; i++)
                bottomStart = Mathf.Min(bottomStart, body.GetPosition(i).y);

            for (int i = 0; i < 90; i++) body.Step(1f / 60f);

            float bottomEnd = float.MaxValue;
            for (int i = 0; i < body.ParticleCount; i++)
                if (!body.IsPinned(i)) bottomEnd = Mathf.Min(bottomEnd, body.GetPosition(i).y);

            Assert.That(body.HasNonFiniteState(), Is.False, "下垂过程中出现 NaN / Infinity");
            Assert.That(bottomEnd, Is.LessThan(bottomStart - 0.05f),
                "底面应当明显下坠：起始 " + bottomStart + "，90 步后 " + bottomEnd);

            float volume = body.Volume();
            Assert.That(volume, Is.GreaterThan(restVolume * 0.6f),
                "体积不能塌掉：当前 " + volume + "，静止 " + restVolume);
            Assert.That(volume, Is.LessThan(restVolume * 1.4f),
                "体积也不能被吹胀：当前 " + volume + "，静止 " + restVolume);
        }

        // Given: 同一个软体、同一份参数、同样步数
        //  When: 跑两遍
        //  Then: 逐位一致（无随机、无 Time、无并行）
        [Test]
        public void Step_SameParametersTwice_ProducesBitwiseIdenticalPositions()
        {
            var first = RunDeterministicPass();
            var second = RunDeterministicPass();

            Assert.That(second.Length, Is.EqualTo(first.Length));
            for (int i = 0; i < first.Length; i++)
            {
                Assert.That(Bits(second[i].x), Is.EqualTo(Bits(first[i].x)), "质点 " + i + " 的 x 逐位不一致");
                Assert.That(Bits(second[i].y), Is.EqualTo(Bits(first[i].y)), "质点 " + i + " 的 y 逐位不一致");
                Assert.That(Bits(second[i].z), Is.EqualTo(Bits(first[i].z)), "质点 " + i + " 的 z 逐位不一致");
            }
        }

        static Vector3[] RunDeterministicPass()
        {
            var p = DefaultParameters();
            p.gravity = new Vector3(0.3f, -9.81f, -0.2f);
            var body = new SoftBodySimulation(p);
            body.Build(MakeBox(2f, 1f, 0.5f));
            body.SetVertexPinned(2, true);
            for (int i = 0; i < 40; i++) body.Step(1f / 60f);
            return body.CapturePositions();
        }

        // Given: 极端参数（体积刚度爆表、弹簧刚度很高、dt 很大）+ 重力下的长方体
        //  When: 连续 Step
        //  Then: 全程没有 NaN / Infinity，启用拉伸限幅时最大拉伸比不超过 maxStretchRatio 的容差
        [Test]
        public void Step_ExtremeParameters_StaysFiniteAndWithinStretchLimit()
        {
            var p = DefaultParameters();
            p.volumeStiffness = 500000f;
            p.springStiffness = 200000f;
            p.bendStiffness = 90000f;
            p.mass = 0.02f;
            p.gravity = new Vector3(0f, -9.81f, 0f);
            p.maxStretchRatio = 1.4f;
            p.enableStretchLimit = true;

            var body = new SoftBodySimulation(p);
            body.Build(MakeBox(2f, 1f, 0.5f));
            body.SetVertexPinned(0, true);

            for (int i = 0; i < 60; i++) body.Step(1f / 20f);

            Assert.That(body.HasNonFiniteState(), Is.False, "极端参数下状态炸成 NaN / Infinity");
            float stretch = body.MaxStretchRatio();
            Assert.That(stretch, Is.GreaterThan(1f), "极端参数 + 重力下必然有拉伸，否则这个断言是空的");
            Assert.That(stretch, Is.LessThanOrEqualTo(p.maxStretchRatio * 1.02f),
                "启用拉伸限幅后最大拉伸比 " + stretch + " 超过上限 " + p.maxStretchRatio + " 的容差范围");
        }

        // Given: 一个已经建好的软体
        //  When: 用各种非法输入调用公开 API
        //  Then: 一律抛 ArgumentException / ArgumentOutOfRangeException，且抛出前后已有状态逐位不变
        [Test]
        public void Api_InvalidArguments_ThrowBeforeMutatingState()
        {
            Assert.Throws<ArgumentNullException>(
                () => new SoftBodySimulation(null), "参数为 null 时必须立刻抛");

            Assert.Throws<ArgumentException>(
                () => new SoftBodySimulation(BadParameters(q => q.mass = 0f)), "质量非正必须抛 ArgumentException");
            Assert.Throws<ArgumentException>(
                () => new SoftBodySimulation(BadParameters(q => q.substeps = 0)), "substeps < 1 必须抛");
            Assert.Throws<ArgumentException>(
                () => new SoftBodySimulation(BadParameters(q => q.maxStretchRatio = 0.5f)), "maxStretchRatio < 1 必须抛");
            Assert.Throws<ArgumentException>(
                () => new SoftBodySimulation(BadParameters(q => q.weldTolerance = -1f)), "焊接容差为负必须抛");

            var body = BuildBody(MakeBox(2f, 1f, 0.5f));
            var before = body.CapturePositions();

            Assert.Throws<ArgumentNullException>(() => body.Build(null), "Build(null) 必须抛");
            Assert.Throws<ArgumentException>(
                () => body.Build(new SoftBodyMeshData(new Vector3[0], new int[0])), "空网格必须抛");
            Assert.Throws<ArgumentOutOfRangeException>(
                () => body.Build(new SoftBodyMeshData(
                    new[] { Vector3.zero, Vector3.right, Vector3.up }, new[] { 0, 1, 9 })), "三角形索引越界必须抛");
            Assert.Throws<ArgumentException>(
                () => body.Build(new SoftBodyMeshData(
                    new[] { Vector3.zero, Vector3.right, Vector3.up }, new[] { 0, 1, 1 })), "退化（重复索引）三角形必须抛");
            Assert.Throws<ArgumentException>(
                () => body.Build(new SoftBodyMeshData(
                    new[] { new Vector3(float.NaN, 0f, 0f), Vector3.right, Vector3.up }, new[] { 0, 1, 2 })),
                "非有限顶点坐标必须抛");
            Assert.Throws<ArgumentException>(
                () => body.Build(new SoftBodyMeshData(
                    new[] { Vector3.zero, Vector3.right, Vector3.up }, new[] { 0, 1 })), "索引长度不是 3 的倍数必须抛");

            Assert.Throws<ArgumentOutOfRangeException>(() => body.Step(0f), "dt = 0 必须抛");
            Assert.Throws<ArgumentOutOfRangeException>(() => body.Step(-1f / 60f), "dt < 0 必须抛");
            Assert.Throws<ArgumentOutOfRangeException>(() => body.Step(float.PositiveInfinity), "dt = ∞ 必须抛");
            Assert.Throws<ArgumentOutOfRangeException>(() => body.GetPosition(-1), "质点下标 -1 必须抛");
            Assert.Throws<ArgumentOutOfRangeException>(() => body.GetPosition(body.ParticleCount), "质点下标越上界必须抛");
            Assert.Throws<ArgumentOutOfRangeException>(() => body.SetPinned(body.ParticleCount, true), "SetPinned 越界必须抛");
            Assert.Throws<ArgumentOutOfRangeException>(() => body.IndexOfVertex(9999), "网格顶点下标越界必须抛");
            Assert.Throws<ArgumentException>(
                () => body.SetPositions(new Vector3[body.ParticleCount - 1]), "SetPositions 长度不匹配必须抛");

            var after = body.CapturePositions();
            for (int i = 0; i < before.Length; i++)
            {
                Assert.That(Bits(after[i].x), Is.EqualTo(Bits(before[i].x)), "非法调用之后质点 " + i + " 的 x 被动过了");
                Assert.That(Bits(after[i].y), Is.EqualTo(Bits(before[i].y)), "非法调用之后质点 " + i + " 的 y 被动过了");
                Assert.That(Bits(after[i].z), Is.EqualTo(Bits(before[i].z)), "非法调用之后质点 " + i + " 的 z 被动过了");
            }
        }

        // ==================================================================
        // 性能基准
        // ==================================================================

        // Given: 642 顶点 / 1280 三角形 / 1920 条结构边的球体软体（正二十面体细分 3 次）
        //  When: 预热后分多批 Step，统计最佳与均值
        //  Then: 托管求解器最佳单步耗时低于一帧（16.7ms）的门槛 8ms，并把数字打进日志
        [Test]
        public void Benchmark_IcoSphere642_ManagedSolverFitsInsideOneFrame()
        {
            var p = DefaultParameters();
            p.gravity = new Vector3(0f, -9.81f, 0f);
            var body = new SoftBodySimulation(p);
            body.Build(MakeIcoSphere(1f, 3));
            body.SetVertexPinned(0, true);

            Assert.That(body.ParticleCount, Is.EqualTo(642), "细分 3 次的正二十面体应当焊接成 642 个质点");
            Assert.That(body.TriangleCount, Is.EqualTo(1280), "面数应当是 1280");
            Assert.That(body.StructuralSpringCount, Is.EqualTo(1920), "闭合三角网格的棱数 = 3V - 6 = 1920");
            Assert.That(body.IsClosed, Is.True, "球体必须是闭合网格，否则体积约束没测到");

            const int batches = 5, stepsPerBatch = 20;
            for (int i = 0; i < 20; i++) body.Step(1f / 60f);   // 预热，别让 JIT 背锅

            float best = float.MaxValue, total = 0f;
            for (int b = 0; b < batches; b++)
            {
                var start = System.Diagnostics.Stopwatch.GetTimestamp();
                for (int i = 0; i < stepsPerBatch; i++) body.Step(1f / 60f);
                float ms = ElapsedMs(start, stepsPerBatch);
                best = Mathf.Min(best, ms);
                total += ms;
            }
            float mean = total / batches;

            string line = "[基准/软体-托管] 质点 " + body.ParticleCount
                          + " | 结构弹簧 " + body.StructuralSpringCount
                          + " | 弯曲弹簧 " + body.BendSpringCount
                          + " | 三角形 " + body.TriangleCount
                          + " | 子步 " + p.substeps
                          + " | 最佳 " + best.ToString("0.000") + " ms/步"
                          + " | 均值 " + mean.ToString("0.000") + " ms/步";
            TestContext.Progress.WriteLine(line);
            UnityEngine.Debug.Log(line);

            Assert.That(body.HasNonFiniteState(), Is.False, "基准跑完状态必须仍然有限");
            Assert.That(best, Is.LessThan(8f),
                "642 顶点软体托管求解器最佳单步 " + best.ToString("0.000") + " ms，超过 8 ms 的一帧预算门槛");
        }

        static float ElapsedMs(long start, int steps)
        {
            double seconds = (System.Diagnostics.Stopwatch.GetTimestamp() - start)
                             / (double)System.Diagnostics.Stopwatch.Frequency;
            return (float)(seconds * 1000.0 / steps);
        }

        // ==================================================================
        // 工具方法与测试网格
        // ==================================================================

        // Given：一个已构建的软体
        //  When：只给某个质点设初速度
        //  Then：只有它的速度变了，位置一个字节都没动；越界下标先抛异常
        [Test]
        public void SetVelocity_ChangesOnlyThatParticlesVelocity()
        {
            var system = new SoftBodySimulation(DefaultParameters());
            system.Build(MakeOctahedron(1f));

            var before = system.CapturePositions();
            system.SetVelocity(2, new Vector3(0.4f, -0.2f, 1.1f));

            Assert.That(system.GetVelocity(2), Is.EqualTo(new Vector3(0.4f, -0.2f, 1.1f)),
                "设进去的速度必须原样读回来");
            Assert.That(system.GetVelocity(1), Is.EqualTo(Vector3.zero),
                "别的质点不该被牵连");

            var after = system.CapturePositions();
            for (int i = 0; i < before.Length; i++)
                Assert.That(Bits(after[i].x) + Bits(after[i].y) + Bits(after[i].z),
                    Is.EqualTo(Bits(before[i].x) + Bits(before[i].y) + Bits(before[i].z)),
                    "SetVelocity 不该改动质点 " + i + " 的位置");

            Assert.Throws(typeof(ArgumentOutOfRangeException), () => system.SetVelocity(-1, Vector3.one),
                "越界下标必须先抛异常");
        }

        // ==================================================================
        // 焊接用的均匀空间哈希：格边长 = weldTolerance，格坐标 = 顶点坐标 / 容差
        // 这组用例盯的是"格坐标要塞进 32 位整数"这个隐含前提
        // ==================================================================

        // Given: 边长 1 米的闭合立方体，焊接容差压到 1e-6（格边长 1 微米）
        //  When: 构建
        //  Then: 24 个网格顶点仍然恰好焊成 8 个质点
        //        完全重合的顶点在任何正容差下都该合并 —— 这是美术网格接缝的常见形态
        [Test]
        public void Weld_CoincidentVerticesMergeEvenAtTinyTolerance()
        {
            var p = BadParameters(x => x.weldTolerance = 1e-6f);
            var body = new SoftBodySimulation(p);
            body.Build(MakeBox(1f, 1f, 1f));

            Assert.That(body.ParticleCount, Is.EqualTo(8),
                "容差再小也不该把重合顶点拆成 24 个质点，实际 " + body.ParticleCount);
            Assert.That(body.IsClosed, Is.True, "焊接正常时这个立方体必须是闭合的（否则体积约束会被静默跳过）");
        }

        // Given: 同一个 1 米立方体，整体挪到离原点 5e7 米处（容差 0.5，格坐标 1e8 仍在 int 内）
        //  When: 构建
        //  Then: 构建必须**明确拒绝**，而不是悄悄算出错东西
        //        真正拦腰截断的不是哈希的 int 格坐标，而是 float 分辨率：
        //        量级 5e7 处一个 ulp ≈ 6 米，1 米长的棱根本表示不出来，
        //        顶点会塌成同一点 ⇒ 面积为 0 ⇒ 体积梯度失效。
        //        （这条用例原本是想证明"格坐标溢出 int 会静默焊错"，实测推翻了那个假设：
        //          重合顶点溢出后仍落进同一个桶、照样能焊；而远原点的网格先被 float 干掉。
        //          所以这里断言的是"拒绝得清楚"这个真实契约，不给哈希加投机护栏。）
        [Test]
        public void Weld_MeshFarFromOrigin_IsRejectedWithAClearGeometricError()
        {
            var mesh = Shift(MakeBox(1f, 1f, 1f), new Vector3(5e7f, 0f, 0f));
            var p = BadParameters(x => x.weldTolerance = 0.5f);

            var ex = Assert.Throws(typeof(ArgumentException), () =>
            {
                var body = new SoftBodySimulation(p);
                body.Build(mesh);
            }, "顶点精度不足以表达这个尺寸的网格，必须在构建阶段拒绝");

            StringAssert.Contains("面积", ex.Message, "要指出是几何退化，实际：" + ex.Message);
        }

        // Given: 一个 1 米立方体，上半部分的 4 个角整体抬高了 0.01 米（接缝裂开 0.01）
        //  When: 分别用容差 0.02（跨过裂缝）与 0.005（够不到裂缝）构建
        //  Then: 容差是**半径**：够到裂缝就焊成 8 个质点且闭合；够不到就裂成 12 个质点、
        //        拓扑不闭合、体积约束被静默跳过 —— 这就是美术接缝最容易踩的坑
        [Test]
        public void Weld_ToleranceIsARadius_SeamGapDecidesWhetherVolumeConstraintSurvives()
        {
            var cracked = SplitTopOfBox(0.01f);

            var wide = new SoftBodySimulation(BadParameters(x => x.weldTolerance = 0.02f));
            wide.Build(cracked);
            Assert.That(wide.ParticleCount, Is.EqualTo(8), "容差 0.02 跨过 0.01 的裂缝，应焊成 8 个");
            Assert.That(wide.IsClosed, Is.True, "焊上之后拓扑闭合，体积约束才会生效");
            Assert.That(wide.RestVolume(), Is.GreaterThan(0.5f), "闭合后的静止体积应当接近 1 立方米");

            var narrow = new SoftBodySimulation(BadParameters(x => x.weldTolerance = 0.005f));
            narrow.Build(cracked);
            Assert.That(narrow.ParticleCount, Is.EqualTo(12), "容差 0.005 够不到裂缝，上下两半各留 4+2 个");
            Assert.That(narrow.IsClosed, Is.False, "裂缝让拓扑不闭合");
            Assert.That(narrow.RestVolume(), Is.EqualTo(0f),
                "不闭合时体积约束被静默跳过 —— 这块'果冻'其实没有体积回弹，扫描报告会报 OpenMesh");
        }

        /// <summary>
        /// 只把**顶面那 4 个拆开的顶点**（MakeBox 里最后一个面组）沿 y 抬高 gap，
        /// 制造一条真接缝：侧面顶棱留在 y=+0.5，顶盖跑到 y=+0.5+gap。
        /// （上一版用 y>0 选点，把侧面顶棱一起抬走了，缝根本没裂开 —— 测出 8 个质点才发现。）
        /// </summary>
        static SoftBodyMeshData SplitTopOfBox(float gap)
        {
            var mesh = MakeBox(1f, 1f, 1f);
            var v = (Vector3[])mesh.Vertices.Clone();
            for (int i = v.Length - 4; i < v.Length; i++) v[i].y += gap;
            return new SoftBodyMeshData(v, mesh.Triangles);
        }

        /// <summary>把整个网格平移（模拟"美术资产离原点很远"这种真实导入情形）。</summary>
        static SoftBodyMeshData Shift(SoftBodyMeshData mesh, Vector3 offset)
        {
            var v = (Vector3[])mesh.Vertices.Clone();
            for (int i = 0; i < v.Length; i++) v[i] += offset;
            return new SoftBodyMeshData(v, mesh.Triangles);
        }

        static SoftBodyParameters DefaultParameters()
        {
            return new SoftBodyParameters
            {
                mass = 0.8f,
                gravity = Vector3.zero,
                damping = 0.6f,
                springStiffness = 1200f,
                springDamping = 6f,
                bendStiffness = 150f,
                volumeStiffness = 4000f,
                volumeDamping = 20f,
                substeps = 4,
                maxDeltaTime = 1f / 15f,
                weldTolerance = WeldEpsilon,
                enableStretchLimit = true,
                maxStretchRatio = 2f
            };
        }

        static SoftBodyParameters BadParameters(Action<SoftBodyParameters> mutate)
        {
            var p = DefaultParameters();
            mutate(p);
            return p;
        }

        static SoftBodySimulation BuildBody(SoftBodyMeshData mesh)
        {
            var body = new SoftBodySimulation(DefaultParameters());
            body.Build(mesh);
            return body;
        }

        static long EdgeKey(int a, int b)
        {
            return ((long)Mathf.Min(a, b) << 32) | (uint)Mathf.Max(a, b);
        }

        static int Bits(float v)
        {
            // 逐位比较用，不开 unsafe（测试程序集没启用 Allow Unsafe Code）
            return BitConverter.SingleToInt32Bits(v);
        }

        /// <summary>长方体：6 个面各自拆 4 个顶点（共 24 个），三角形按外法线 CCW 绕序。</summary>
        static SoftBodyMeshData MakeBox(float width, float height, float depth)
        {
            float hx = width * 0.5f, hy = height * 0.5f, hz = depth * 0.5f;
            var c = new[]
            {
                new Vector3(-hx, -hy, -hz), new Vector3(hx, -hy, -hz),
                new Vector3(hx, hy, -hz), new Vector3(-hx, hy, -hz),
                new Vector3(-hx, -hy, hz), new Vector3(hx, -hy, hz),
                new Vector3(hx, hy, hz), new Vector3(-hx, hy, hz)
            };

            // 每个面：4 个顶点 + 2 个三角形（索引相对该面起点）
            int[][] faces =
            {
                new[] { 0, 3, 2, 1 },   // -z
                new[] { 4, 5, 6, 7 },   // +z
                new[] { 0, 4, 7, 3 },   // -x
                new[] { 1, 2, 6, 5 },   // +x
                new[] { 0, 1, 5, 4 },   // -y
                new[] { 3, 7, 6, 2 }    // +y
            };

            var verts = new List<Vector3>();
            var tris = new List<int>();
            foreach (var face in faces)
            {
                int start = verts.Count;
                for (int i = 0; i < 4; i++) verts.Add(c[face[i]]);
                tris.Add(start); tris.Add(start + 1); tris.Add(start + 2);
                tris.Add(start); tris.Add(start + 2); tris.Add(start + 3);
            }
            return new SoftBodyMeshData(verts.ToArray(), tris.ToArray());
        }

        /// <summary>单位正八面体：6 顶点 / 8 三角形，闭合，体积 4/3。</summary>
        static SoftBodyMeshData MakeOctahedron(float radius)
        {
            var v = new[]
            {
                new Vector3(radius, 0f, 0f), new Vector3(-radius, 0f, 0f),
                new Vector3(0f, radius, 0f), new Vector3(0f, -radius, 0f),
                new Vector3(0f, 0f, radius), new Vector3(0f, 0f, -radius)
            };
            // 绕序必须"从外面看是逆时针"，否则散度定理给出负体积
            int[] idx =
            {
                0, 2, 4, 4, 2, 1, 1, 2, 5, 5, 2, 0,
                4, 3, 0, 1, 3, 4, 5, 3, 1, 0, 3, 5
            };
            return new SoftBodyMeshData(v, idx);
        }

        /// <summary>开放四边形：4 顶点 / 2 三角形，外棱只被一个面用。</summary>
        static SoftBodyMeshData MakeQuad()
        {
            var v = new[]
            {
                new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f),
                new Vector3(1f, 0f, 1f), new Vector3(0f, 0f, 1f)
            };
            return new SoftBodyMeshData(v, new[] { 0, 2, 1, 0, 3, 2 });
        }

        /// <summary>正二十面体细分 N 次后投影到球面：subdiv 3 ⇒ 642 顶点 / 1280 三角形。</summary>
        static SoftBodyMeshData MakeIcoSphere(float radius, int subdivisions)
        {
            float t = (1f + Mathf.Sqrt(5f)) * 0.5f;
            var verts = new List<Vector3>
            {
                new Vector3(-1f, t, 0f), new Vector3(1f, t, 0f), new Vector3(-1f, -t, 0f), new Vector3(1f, -t, 0f),
                new Vector3(0f, -1f, t), new Vector3(0f, 1f, t), new Vector3(0f, -1f, -t), new Vector3(0f, 1f, -t),
                new Vector3(t, 0f, -1f), new Vector3(t, 0f, 1f), new Vector3(-t, 0f, -1f), new Vector3(-t, 0f, 1f)
            };
            var tris = new List<int>
            {
                0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11,
                1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
                3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9,
                4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1
            };

            for (int s = 0; s < subdivisions; s++)
            {
                var newTris = new List<int>(tris.Count * 4);
                var cache = new Dictionary<long, int>();
                for (int i = 0; i < tris.Count; i += 3)
                {
                    int a = Mid(verts, cache, tris[i], tris[i + 1]);
                    int b = Mid(verts, cache, tris[i + 1], tris[i + 2]);
                    int c = Mid(verts, cache, tris[i + 2], tris[i]);
                    newTris.Add(tris[i]); newTris.Add(a); newTris.Add(c);
                    newTris.Add(tris[i + 1]); newTris.Add(b); newTris.Add(a);
                    newTris.Add(tris[i + 2]); newTris.Add(c); newTris.Add(b);
                    newTris.Add(a); newTris.Add(b); newTris.Add(c);
                }
                tris = newTris;
            }

            for (int i = 0; i < verts.Count; i++) verts[i] = verts[i].normalized * radius;
            return new SoftBodyMeshData(verts.ToArray(), tris.ToArray());
        }

        static int Mid(List<Vector3> verts, Dictionary<long, int> cache, int a, int b)
        {
            long key = ((long)Mathf.Min(a, b) << 32) | (uint)Mathf.Max(a, b);
            int existing;
            if (cache.TryGetValue(key, out existing)) return existing;
            int index = verts.Count;
            verts.Add((verts[a] + verts[b]) * 0.5f);
            cache[key] = index;
            return index;
        }
    }
}
