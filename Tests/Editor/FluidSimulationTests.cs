// Copyright (c) 2026 PhysicsSimulation. MIT License.
//
// v1.5.0 流体求解器（PBF）测试。
//
// 这一组刻意**不看"像不像水"**，只看四条能被闭式验证的东西：
//   1. 密度 = Σ m·W（与 O(n²) 暴力枚举逐位对齐）；
//   2. 压缩必须把密度降下来、把包围盒撑开（约束真的在起作用，而不是只改了 λ）；
//   3. 质量守恒 + 零 dt 是空操作 + 两次同样的运行逐位相同；
//   4. 碰撞代理（v1.3.0 那套）在预测位置上生效 ⇒ 没有粒子能埋进地面以下。
// 拉力钳制（tensile clamp）单独测：它是 PBF 不"自聚成一坨"的唯一原因，开关两侧行为必须相反。

using System;
using NUnit.Framework;
using UnityEngine;

namespace PhysicsSimulation.Editor.Tests
{
    [TestFixture]
    public class FluidSimulationTests
    {
        const float RestDensity = 1000f;
        const float Spacing = 0.05f;
        const float H = 0.1f;

        static FluidParameters Defaults()
        {
            var p = new FluidParameters();
            p.restDensity = RestDensity;
            p.particleSpacing = Spacing;
            p.kernelRadius = H;
            p.gravity = Vector3.zero;          // 绝大多数用例要隔离掉重力，单独有用例覆盖重力
            p.substeps = 1;
            p.solverIterations = 2;
            return p;
        }

        static FluidSimulation Make(FluidParameters p, FluidParticleSet set)
        {
            return new FluidSimulation(p, set);
        }

        // ---------------------------------------------------------------- 参数校验

        [Test]
        public void Constructor_RejectsKernelRadiusNotLargerThanSpacing()
        {
            // Given:  h ≤ 间距 ⇒ 核支撑范围内可能一个邻居都没有，密度恒等于自身项
            var p = Defaults();
            p.kernelRadius = Spacing * 0.9f;

            // Then:  明确拒绝，并说清后果
            var e = Assert.Throws<ArgumentOutOfRangeException>(() =>
                Make(p, FluidVolume.Box(new Vector3(0.2f, 0.2f, 0.2f), Spacing)));
            StringAssert.Contains("kernelRadius", e.Message, "要指名参数：" + e.Message);
            StringAssert.Contains("间距", e.Message, "要说清和间距的关系：" + e.Message);
        }

        [Test]
        public void Constructor_RejectsOutOfRangeControlParameters()
        {
            var p = Defaults();
            p.xsphViscosity = 1.4f;                       // XSPH 是速度插值系数，>1 会反向过冲
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                Make(p, FluidVolume.Box(new Vector3(0.2f, 0.2f, 0.2f), Spacing)),
                "xsphViscosity 必须在 [0,1]");

            var q = Defaults();
            q.solverIterations = 0;                        // 0 次迭代 = 不解约束
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                Make(q, FluidVolume.Box(new Vector3(0.2f, 0.2f, 0.2f), Spacing)), "迭代次数至少 1");

            var r = Defaults();
            r.restDensity = 0f;
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                Make(r, FluidVolume.Box(new Vector3(0.2f, 0.2f, 0.2f), Spacing)), "静止密度必须为正");
        }

        // ---------------------------------------------------------------- 密度

        [Test]
        public void ComputeDensity_MatchesBruteForceSumIncludingSelf()
        {
            // Given:  一坨不规则粒子
            var rng = new System.Random(4242);
            int n = 120;
            var positions = new Vector3[n];
            for (int i = 0; i < n; i++)
                positions[i] = new Vector3((float)rng.NextDouble() * 0.4f,
                                           (float)rng.NextDouble() * 0.4f,
                                           (float)rng.NextDouble() * 0.4f);
            var set = new FluidParticleSet { Positions = positions, Velocities = new Vector3[n], Count = n };
            var sim = Make(Defaults(), set);

            // When:  即时算第 60 个粒子的密度
            float actual = sim.ComputeDensity(60);

            // Then:  与暴力 Σ m·W_poly6（含自身项）一致
            float m = sim.ParticleMass;
            float expected = 0f;
            for (int j = 0; j < n; j++)
            {
                var d = positions[60] - positions[j];
                expected += m * FluidKernel.Poly6(d.sqrMagnitude, H);
            }
            Assert.AreEqual(expected, actual, 1e-3f * Mathf.Max(1f, expected),
                "密度与暴力枚举不等 ⇒ 邻居表或核函数用错了其中一个");
        }

        [Test]
        public void DesignSpacingLattice_HasDensityNearRestDensity()
        {
            // Given:  按"设计间距"排开的点阵（质量 = ρ0·d³）
            var sim = Make(Defaults(), FluidVolume.Box(new Vector3(0.4f, 0.4f, 0.4f), Spacing));

            // When:  看内部某点的密度
            int probe = FindInterior(sim);
            float density = sim.ComputeDensity(probe);

            // Then:  与静止密度差在 25% 以内 —— 差得多说明"间距/核半径/质量"三者关系不一致，
            //        表现就是水一开场就自己炸开或者塌成一坨
            Assert.AreEqual(RestDensity, density, 0.25f * RestDensity,
                "设计间距下的密度 = " + density + "，离静止密度 " + RestDensity + " 太远");
        }

        // ---------------------------------------------------------------- 约束真的在做事

        [Test]
        public void Step_ReducesDensityAndExpandsBounds_WhenOverCompressed()
        {
            // Given:  把点阵整体压到 0.8 倍（质量不变 ⇒ 密度约升到 1/0.8³ ≈ 1.95 倍）
            var p = Defaults();
            var set = FluidVolume.Box(new Vector3(0.4f, 0.4f, 0.4f), Spacing);
            var centroid = Vector3.zero;
            for (int i = 0; i < set.Count; i++) centroid += set.Positions[i];
            centroid /= set.Count;
            for (int i = 0; i < set.Count; i++)              // ← 这一步原来漏了，密度当然不降
                set.Positions[i] = centroid + (set.Positions[i] - centroid) * 0.8f;
            var sim = Make(p, set);
            int probe = FindInterior(sim);
            float densityBefore = sim.ComputeDensity(probe);
            Bounds boundsBefore = sim.Bounds();

            // When
            sim.Step(1f / 120f);

            // Then:  密度下降、包围盒撑开
            float densityAfter = sim.ComputeDensity(probe);
            Assert.Less(densityAfter, densityBefore,
                "过压缩之后密度没降（" + densityBefore + " → " + densityAfter + "）⇒ 密度约束没作用到位置上");
            Assert.Greater(sim.Bounds().size.x, boundsBefore.size.x,
                "过压缩之后包围盒没撑开 ⇒ Δλ 算了但没写回位置");
        }

        [Test]
        public void TensileClamp_KeepsLonelyParticlesFromAttracting_WhenOn()
        {
            // Given:  两个孤零零的粒子，密度远低于静止密度（Ci < 0 ⇒ Δλ > 0 ⇒ 会互相吸引）
            //         符号约定：压缩时 Ci>0 ⇒ Δλ<0 ⇒ 推开。所以**该钳掉的是正 λ**。
            //         字段若叫 clampNegativeLambda 会把人带反，故命名 clampTensileLambda。
            var p = Defaults();
            p.clampTensileLambda = true;      // 钳的是**正** λ（吸引项），不是负 λ
            var set = TwoParticles(0.05f);
            var sim = Make(p, set);

            // When
            sim.Step(1f / 60f);

            // Then:  位置逐位不变 —— 负 λ 会凭空把水吸成一团点，默认必须钳掉
            Assert.AreEqual(0f, Vector3.Distance(sim.GetPosition(0), new Vector3(0f, 0f, 0f)), 1e-6f,
                "钳制开着时孤立粒子对不该动，实际移动了 " + Vector3.Distance(sim.GetPosition(0), Vector3.zero));
            Assert.AreEqual(0.05f, Vector3.Distance(sim.GetPosition(0), sim.GetPosition(1)), 1e-5f,
                "钳制开着时距离变了：" + Vector3.Distance(sim.GetPosition(0), sim.GetPosition(1)));
        }

        [Test]
        public void TensileClamp_OffShrinksALatticeBlob_SurfacePullsInward()
        {
            // Given:  一坨按设计间距摆好的水体（0.2 m 立方：内部 ρ≈ρ0、表面 ρ<ρ0）。
            //         关掉钳制后，表面那些"欠密度"粒子的正 λ 不再被抹掉，整坨水被自己吸小 ——
            //         这才是"关掉钳制水会缩成一团点"的机制。
            //
            //         为什么不能拿两个粒子测这件事（此前的写法，改成这样是因为它一直在骗人）：
            //         两点的密度上限只有 2m·W(0) ≈ 0.39ρ0，约束**永远无法满足**，投影会把修正
            //         推到极限 —— 实测单轮 1.6 m，两个粒子直接互相穿过。那是退化构型，
            //         不是拉力钳制的语义；它现在归到下面那条护栏用例里去。
            var p = Defaults();
            p.clampTensileLambda = false;
            var q = Defaults();
            q.clampTensileLambda = true;

            var off = Make(p, FluidVolume.Box(new Vector3(0.2f, 0.2f, 0.2f), Spacing));
            var on = Make(q, FluidVolume.Box(new Vector3(0.2f, 0.2f, 0.2f), Spacing));
            Assert.AreEqual(off.ParticleCount, on.ParticleCount, "对照组必须同一坨水");

            // When:  无重力走 30 步，只看"自己吸自己"
            for (int i = 0; i < 30; i++) { off.Step(1f / 60f); on.Step(1f / 60f); }

            // Then:  关掉钳制的明显缩得更小，且没有炸
            float vOff = VolumeOf(off.Bounds()), vOn = VolumeOf(on.Bounds());
            Assert.Less(vOff, vOn * 0.9f,
                "关掉钳制 30 步后体积 " + vOff + "，开着钳制 " + vOn + " ⇒ 表面没把水体吸小");
            Assert.That(off.HasNonFiniteState(), Is.False, "关掉钳制之后状态炸了（限幅护栏失效？）");
            Assert.That(on.HasNonFiniteState(), Is.False, "开着钳制反而炸了");
        }

        [Test]
        public void UnsatisfiableSparsePair_IsCappedByTheCorrectionRail()
        {
            // Given:  两点相距 5 cm，密度约束在这里不可满足，正 λ 没有上限。
            //         护栏必须挡住它：单轮迭代每个粒子最多被推走 h·MaxCorrectionPerIterationFactor。
            //         （加护栏之前实测：一步把粒子甩到 1.1 m 外，两点互相穿过。）
            var p = Defaults();
            p.clampTensileLambda = false;
            p.solverIterations = 1;
            var sim = Make(p, TwoParticles(0.05f));
            var a0 = sim.GetPosition(0);
            var b0 = sim.GetPosition(1);

            // When
            sim.Step(1f / 60f);

            // Then
            float rail = H * FluidSimulation.MaxCorrectionPerIterationFactor + 1e-4f;
            Assert.Less(Vector3.Distance(sim.GetPosition(0), a0), rail,
                "单轮迭代把粒子推了 " + Vector3.Distance(sim.GetPosition(0), a0)
                + " m（护栏 " + rail + "）⇒ 稀疏区没有上限，一帧就能甩掉整坨水");
            Assert.Less(Vector3.Distance(sim.GetPosition(1), b0), rail,
                "第二个粒子没被护栏管住：" + Vector3.Distance(sim.GetPosition(1), b0));
        }

        static float VolumeOf(Bounds b) { return b.size.x * b.size.y * b.size.z; }

        // ---------------------------------------------------------------- 守恒与可复现

        [Test]
        public void TotalMass_IsConservedAcrossSteps()
        {
            // Given
            var sim = Make(Defaults(), FluidVolume.DamBreak(0.3f, 0.6f, 0.3f, Spacing));
            float before = sim.TotalMass();

            // When
            for (int i = 0; i < 60; i++) sim.Step(1f / 60f);

            // Then:  PBF 只改位置与速度，粒子数与质量都不该变
            Assert.AreEqual(before, sim.TotalMass(), 1e-4f * Mathf.Max(1f, before),
                "总质量漂移了 ⇒ 粒子被丢掉或质量被改");
            Assert.AreEqual(FluidVolume.DamBreak(0.3f, 0.6f, 0.3f, Spacing).Count, sim.ParticleCount,
                "粒子数变了");
        }

        [Test]
        public void Step_WithZeroDt_IsANoOp()
        {
            var sim = Make(Defaults(), FluidVolume.Box(new Vector3(0.2f, 0.2f, 0.2f), Spacing));
            var snapshot = new Vector3[sim.ParticleCount];
            for (int i = 0; i < snapshot.Length; i++) snapshot[i] = sim.GetPosition(i);

            sim.Step(0f);

            for (int i = 0; i < snapshot.Length; i++)
                Assert.AreEqual(BitConverter.SingleToInt32Bits(snapshot[i].y),
                                BitConverter.SingleToInt32Bits(sim.GetPosition(i).y),
                    "dt=0 也动了位置，第 " + i + " 个 ⇒ 除零/未短路，回放会飘");
        }

        [Test]
        public void TwoIdenticalRuns_AreBitIdentical()
        {
            // Given:  同样的参数与初始点阵，两个独立实例
            var a = Make(Defaults(), FluidVolume.DamBreak(0.3f, 0.5f, 0.3f, Spacing));
            var b = Make(Defaults(), FluidVolume.DamBreak(0.3f, 0.5f, 0.3f, Spacing));

            // When:  各跑 40 步
            for (int i = 0; i < 40; i++) { a.Step(1f / 60f); b.Step(1f / 60f); }

            // Then:  逐位相同（邻居表遍历顺序、浮点运算顺序都必须是确定的）
            for (int i = 0; i < a.ParticleCount; i++)
            {
                Assert.AreEqual(BitConverter.SingleToInt32Bits(a.GetPosition(i).x),
                                BitConverter.SingleToInt32Bits(b.GetPosition(i).x),
                    "第 " + i + " 个粒子两次运行结果不同 ⇒ 有不确定的遍历顺序");
                Assert.AreEqual(BitConverter.SingleToInt32Bits(a.GetVelocity(i).z),
                                BitConverter.SingleToInt32Bits(b.GetVelocity(i).z));
            }
        }

        // ---------------------------------------------------------------- 压力真的顶得住
        //
        // 这一组的来历：演示场景里 1053 个粒子落到水箱底之后，Dump 出来是
        //   包围盒 y 尺寸 0.000、平均邻居 109、密度均值 7114（静止的 7.11 倍）。
        // 也就是说整坨水被压成了一张单粒子厚的饼，而密度约束一点没顶住 ——
        // 之前那些"密度有下降""包围盒有撑开"的单调性断言全都过了，因为它们只要求
        // **动了**，不要求**动得够**。密度约束的价值在于"够"，所以这里全部改成量级断言。

        [Test]
        public void OverCompressedLattice_RemovesMostOfTheDensityError()
        {
            // Given:  设计间距的点阵整体缩到 0.7 倍 ⇒ 密度理论上升到 (1/0.7)³ ≈ 2.9 倍。
            //         这里刻意**不**要求它"回到 1.0 并稳住"：一坨没有容器、又钳掉拉力的水，
            //         物理上就会越扩越散（表面粒子没有任何东西把它拉回去）。能证伪
            //         "压力是个摆设"的是：一步之内误差被吃掉的比例、以及不许炸开。
            var p = Defaults();
            p.gravity = Vector3.zero;                   // 隔离掉重力，只看压力
            var set = FluidVolume.Box(new Vector3(0.25f, 0.25f, 0.25f), Spacing);
            var centroid = Vector3.zero;
            for (int i = 0; i < set.Count; i++) centroid += set.Positions[i];
            centroid /= set.Count;
            for (int i = 0; i < set.Count; i++)
                set.Positions[i] = centroid + (set.Positions[i] - centroid) * 0.7f;

            var sim = Make(p, set);
            float before = MeanDensityRatio(sim);
            float widthBefore = sim.Bounds().size.x;
            Debug.Log("[诊断] 粒子 " + set.Count + " 构造压缩比 " + before
                      + " 初始宽度 " + widthBefore);
            Assert.Greater(before, 1.3f,
                "这个构造根本没造出像样的过压缩（比值 " + before + "），后面的断言是空的");

            // When:  一个子步、两轮投影
            sim.Step(1f / 60f);

            // Then:  ① 密度误差至少吃掉一半
            float one = MeanDensityRatio(sim);
            Debug.Log("[诊断] 一步之后 " + one + " 宽度 " + sim.Bounds().size.x);
            Assert.Less(one - 1f, (before - 1f) * 0.5f,
                "过压缩 " + before + " 倍走一步后还剩 " + one + " 倍 ⇒ 投影没吃掉误差，压力形同不存在");

            // ② 体积确实撑开了
            Assert.Greater(sim.Bounds().size.x, widthBefore * 1.02f,
                "密度松开了但包围盒没撑开（" + widthBefore + " → " + sim.Bounds().size.x
                + "）⇒ 修正没落到位置上");

            // ③ 不许一步就炸：撑大幅度有限
            Assert.Less(sim.Bounds().size.x, widthBefore * 3f,
                "一步之内宽度变成 " + sim.Bounds().size.x + "（原 " + widthBefore
                + "）⇒ 投影发散了");

            // When:  再走 8 步
            for (int i = 0; i < 8; i++) sim.Step(1f / 60f);

            // Then:  过压缩基本消失（允许表面稀疏把均值拉到 1 以下），且状态有限
            float many = MeanDensityRatio(sim);
            Debug.Log("[诊断] 九步之后 " + many + " 宽度 " + sim.Bounds().size.x);
            Assert.Less(many, 1.25f,
                "走了 9 步还剩 " + many + " 倍静止密度 ⇒ 收敛太慢，画面里水会一直抖");
            Assert.That(sim.HasNonFiniteState(), Is.False, "松弛之后状态炸了");
        }

        static float MeanDensityRatio(FluidSimulation sim)
        {
            float sum = 0f;
            for (int i = 0; i < sim.ParticleCount; i++) sum += sim.ComputeDensity(i);
            return sum / Mathf.Max(1, sim.ParticleCount) / sim.Parameters.restDensity;
        }

        static FluidParameters BuildParams()
        {
            var q = Defaults();
            q.substeps = 2;
            q.solverIterations = 2;
            q.clampTensileLambda = true;
            return q;
        }

        [Test]
        public void ColumnOnFloor_SpreadsSideways_InsteadOfPanecaking()
        {
            // Given:  一柱水（0.4 宽 × 0.8 高 × 0.4 深）落在无限大地板上，正是演示场景的缩小版。
            //         这条用例是"压力量级"的直接证伪器：缩放过半的旧实现里，1053 个粒子
            //         全被地板压到同一个 y（密度 7.11 倍、包围盒 y 尺寸 0.000）。
            var p = BuildParams();
            p.gravity = new Vector3(0f, -9.81f, 0f);
            var set = FluidVolume.DamBreak(0.4f, 0.8f, 0.4f, Spacing);
            var sim = Make(p, set);
            sim.Collisions.Add(new PlaneCollisionProxy(Vector3.zero, Vector3.up));   // 地板 y=0
            float widthBefore = sim.Bounds().size.x;
            Assert.Greater(sim.Bounds().center.y, 0.3f, "初始水柱就该是'高'的，不然测不出塌开");

            // When
            for (int i = 0; i < 240; i++) sim.Step(1f / 60f);

            // Then:  ① 不许压成饼：平均密度最多约 1.6 倍静止值
            float ratio = MeanDensityRatio(sim);
            Assert.Less(ratio, 1.6f,
                "落地后密度比 " + ratio + "（平均邻居 " + sim.AverageNeighborDegree
                + "）⇒ 水被压成了一张单层的饼，压力没顶住");

            // ② 必须横向摊开（溃坝的样子），而不是原地变矮
            float widthAfter = sim.Bounds().size.x;
            Assert.Greater(widthAfter, widthBefore * 1.25f,
                "水平方向只从 " + widthBefore + " 摊到 " + widthAfter + " ⇒ 水没有往两边跑");

            // ③ 不许穿地板，也不许整体悬空
            var b = sim.Bounds();
            Assert.GreaterOrEqual(b.min.y, -1e-3f, "有粒子穿进地板：" + b.min.y);
            Assert.Less(b.center.y, 0.5f, "一柱水落完还在半空（质心 " + b.center.y + "）⇒ 没落地");
            Assert.That(sim.HasNonFiniteState(), Is.False, "长跑之后状态炸了");
        }

        // ---------------------------------------------------------------- 水箱（演示构型）
        //
        // 这条用例照 Tools/Physics Simulation/Fluid/Create Demo Scene 的桶写：底板 + 四面墙
        // （无顶）、一柱水靠边。它补上了"落地摊开"那条没覆盖的东西 —— 墙。
        // 触发过的真实故障：密度修好之后，水一碰到墙就被弹成几十 m/s 的子弹，
        // 整坨从桶里飞出去（实测包围盒 59×184×67 m、动能 18894 J）。

        [Test]
        public void TankOfBoxes_ColumnStaysInsideAndNeverBecomesBullets()
        {
            var p = BuildParams();
            p.gravity = new Vector3(0f, -9.81f, 0f);
            var set = FluidVolume.DamBreak(0.5f, 1.0f, 0.5f, Spacing);
            var shift = new Vector3(-0.9f, 0f, -0.25f);
            for (int i = 0; i < set.Count; i++) set.Positions[i] += shift;

            var sim = Make(p, set);
            AddTank(sim, 1.2f, 0.2f, 2.0f);

            // When: 走 4 秒
            for (int i = 0; i < 240; i++) sim.Step(1f / 60f);

            // Then: 状态有限
            Debug.LogWarning("[诊断] 水箱 4 秒后 maxv=" + MaxSpeedOf(sim)
                + " size=(" + sim.Bounds().size.x.ToString("F2") + ","
                + sim.Bounds().size.y.ToString("F2") + "," + sim.Bounds().size.z.ToString("F2")
                + ") min.y=" + sim.Bounds().min.y.ToString("F3")
                + " 密度比=" + MeanDensityRatio(sim).ToString("F3")
                + " 邻居=" + sim.AverageNeighborDegree.ToString("F1"));
            Assert.That(sim.HasNonFiniteState(), Is.False, "长跑之后状态炸了");

            // 不许自己变成子弹：真实溃坝的水流速在 1~3 m/s 量级
            float maxSpeed = 0f;
            for (int i = 0; i < sim.ParticleCount; i++)
                maxSpeed = Mathf.Max(maxSpeed, sim.GetVelocity(i).magnitude);
            Assert.Less(maxSpeed, 5f,
                "最快粒子 " + maxSpeed + " m/s ⇒ 水被墙弹成了子弹");

            // 必须还在桶里（内腔 ±1.2 m，允许一个粒子半径的余量）
            var b = sim.Bounds();
            Assert.GreaterOrEqual(b.min.x, -1.25f, "有粒子冲出左墙：" + b.min.x);
            Assert.LessOrEqual(b.max.x, 1.25f, "有粒子冲出右墙：" + b.max.x);
            Assert.GreaterOrEqual(b.min.z, -1.25f, "有粒子冲出后墙：" + b.min.z);
            Assert.LessOrEqual(b.max.z, 1.25f, "有粒子冲出前墙：" + b.max.z);
            Assert.GreaterOrEqual(b.min.y, -0.05f, "有粒子沉进地板：" + b.min.y);

            // 不许压成饼
            Assert.Less(MeanDensityRatio(sim), 1.6f,
                "桶里稳定后的密度比 " + MeanDensityRatio(sim) + "（平均邻居 "
                + sim.AverageNeighborDegree + "）");
        }

        [Test]
        public void ClampDeltaTime_CapsHugeStepsAndLeavesNormalOnesAlone()
        {
            var p = BuildParams();
            p.maxDeltaTime = 1f / 30f;

            Assert.AreEqual(1f / 60f, p.ClampDeltaTime(1f / 60f), "正常帧长不该被动");
            Assert.AreEqual(1f / 30f, p.ClampDeltaTime(1f / 30f), "正好等于上限不算超");
            Assert.AreEqual(1f / 30f, p.ClampDeltaTime(2.5f), "秒级 dt 必须钳到上限");

            p.maxDeltaTime = 0f;
            Assert.AreEqual(2.5f, p.ClampDeltaTime(2.5f), "maxDeltaTime = 0 表示不钳制");
            p.maxDeltaTime = -1f;
            Assert.AreEqual(2.5f, p.ClampDeltaTime(2.5f), "maxDeltaTime < 0 同样不钳制");

            p.maxDeltaTime = -1f;
            Assert.Throws<System.ArgumentOutOfRangeException>(() => p.Validate(),
                "负的 dt 上限应当在 Validate 里就拦住（0 才是不钳制的正确写法）");
        }

        [Test]
        public void FirstFrameHugeDeltaTime_DoesNotLetTheFluidEscapeTheTank()
        {
            // Given:  与演示场景同构型的一池水
            var p = BuildParams();
            p.gravity = new Vector3(0f, -9.81f, 0f);
            var set = FluidVolume.DamBreak(0.5f, 1.0f, 0.5f, Spacing);
            var shift = new Vector3(-0.9f, 0f, -0.25f);
            for (int i = 0; i < set.Count; i++) set.Positions[i] += shift;
            var sim = Make(p, set);
            AddTank(sim, 1.2f, 0.2f, 2.0f);

            // When:  第一帧给出 Unity 进 Play / 卡帧时常见的秒级 deltaTime，之后回到 60 fps。
            //        演示场景就是死在这上面：头几帧把整池水甩到质心 y = −1394 m。
            sim.Step(1.5f);
            for (int i = 0; i < 240; i++) sim.Step(1f / 60f);

            // Then:  水还在桶里，没变成弹丸
            Assert.That(sim.HasNonFiniteState(), Is.False, "秒级 dt 之后状态炸了");
            var b = sim.Bounds();
            Assert.Less(MaxSpeedOf(sim), 5f,
                "最快粒子 " + MaxSpeedOf(sim) + " m/s ⇒ 秒级 dt 没被钳住，水被甩成弹丸");
            Assert.GreaterOrEqual(b.min.y, -0.05f, "有水漏到地板下面：min.y = " + b.min.y);
            Assert.GreaterOrEqual(b.min.x, -1.25f, "有水冲出左墙：" + b.min.x);
            Assert.LessOrEqual(b.max.x, 1.25f, "有水冲出右墙：" + b.max.x);
            Assert.Greater(MeanDensityRatio(sim), 0.6f,
                "密度比 " + MeanDensityRatio(sim) + " ⇒ 水被甩散成云了");
        }

        static float MaxSpeedOf(FluidSimulation sim)
        {
            float v = 0f;
            for (int i = 0; i < sim.ParticleCount; i++) v = Mathf.Max(v, sim.GetVelocity(i).magnitude);
            return v;
        }

        /// <summary>底板 + 四面墙（无顶），和演示场景里的水箱同构型：墙在内腔之外，厚度不占内腔。</summary>
        static void AddTank(FluidSimulation sim, float innerHalf, float thickness, float height)
        {
            // 几何与 FluidDemoTools.BuildTank 一致：墙下探进地板实体、平面上互相搭接。
            // 接缝不封死的话桶就是单向活门（详见 BuildTank 注释）。
            float t2 = thickness * 0.5f;
            float outer = innerHalf + thickness;
            float wallCenter = innerHalf + t2;
            float wallHalf = (height + thickness) * 0.5f;
            float wallY = (height - thickness) * 0.5f;
            sim.Collisions.Add(new BoxCollisionProxy(new Vector3(0f, -t2, 0f),
                new Vector3(outer, t2, outer), Quaternion.identity));                       // 地板
            sim.Collisions.Add(new BoxCollisionProxy(new Vector3(-wallCenter, wallY, 0f),
                new Vector3(t2, wallHalf, outer), Quaternion.identity));                    // 左
            sim.Collisions.Add(new BoxCollisionProxy(new Vector3(wallCenter, wallY, 0f),
                new Vector3(t2, wallHalf, outer), Quaternion.identity));                    // 右
            sim.Collisions.Add(new BoxCollisionProxy(new Vector3(0f, wallY, -wallCenter),
                new Vector3(outer, wallHalf, t2), Quaternion.identity));                    // 后
            sim.Collisions.Add(new BoxCollisionProxy(new Vector3(0f, wallY, wallCenter),
                new Vector3(outer, wallHalf, t2), Quaternion.identity));                    // 前
        }

        // ---------------------------------------------------------------- 规模与性能
        //
        // 这里**不用绝对毫秒数当门槛**。同一份代码、同一台机器，软体基准从文档记录的
        // 2.745 ms/步漂到了 9.4 ms/步（后台一个游戏进程占了 ~1.8 个核），任何绝对阈值
        // 都会被宿主负载打穿。改成同一次运行里的**规模比值**：负载对两档的影响近似相乘，
        // 比值就把负载约掉了 —— 它能测的是"复杂度没退化"，而不是"这台机器有多快"。

        [Test]
        public void Benchmark_NeighbourCostScalesNearLinearlyWithParticleCount()
        {
            // Given:  固定 h 与 d ⇒ 每粒子邻居数与规模无关，总成本应当 ≈ O(N)
            var p = Defaults();
            p.gravity = new Vector3(0f, -9.81f, 0f);

            // When:  三档规模（d=0.05 ⇒ 10³=1000 / 13³=2197 / 16³=4096）
            float small = MeasureMsPerStep(p, new Vector3(0.5f, 0.5f, 0.5f), out int nSmall, out float degreeSmall);
            float large = MeasureMsPerStep(p, new Vector3(0.8f, 0.8f, 0.8f), out int nLarge, out float degreeLarge);
            float ratioTime = large / Mathf.Max(1e-6f, small);
            float ratioCount = (float)nLarge / nSmall;

            // Then:  邻居数与规模无关（这是 O(N) 的前提，也是"cell=h 均匀哈希"的实测证据）
            Assert.That(degreeLarge, Is.EqualTo(degreeSmall).Within(3f),
                "平均邻居数随规模漂了（" + degreeSmall + " → " + degreeLarge + "）⇒ 邻居搜索的支撑半径没管好");

            // 近线性 ⇒ 比值 ≈ 4.1；真退化成 O(N²) ⇒ ≈ 16.8。门槛取 12：
            // 既放得下常数项与缓存效应，也拦得住"每步扫全体粒子找邻居"这种写错。
            Assert.That(ratioTime, Is.LessThan(12f),
                "规模涨 " + ratioCount.ToString("0.0") + " 倍时耗时涨 " + ratioTime.ToString("0.0")
                + " 倍（近线性应为 ~4.1，二次方应为 ~16.8）⇒ 邻居表复杂度退化了");

            string line = "[基准/流体-PBF] d=0.05 h=0.1 | " + nSmall + " 粒子 "
                          + small.ToString("0.000") + " ms/步（平均邻居 " + degreeSmall.ToString("0.0") + "） | "
                          + nLarge + " 粒子 " + large.ToString("0.000") + " ms/步（平均邻居 "
                          + degreeLarge.ToString("0.0") + "） | 比值 " + ratioTime.ToString("0.00")
                          + "（本机有后台负载，绝对值仅供参考）";
            TestContext.Progress.WriteLine(line);
            UnityEngine.Debug.Log(line);
        }

        [Test]
        public void Benchmark_NeighbourTableBuildIsTheDominantCost()
        {
            // Given:  同一批粒子（2197 个）
            var p = Defaults();
            p.gravity = new Vector3(0f, -9.81f, 0f);
            var set = FluidVolume.Box(new Vector3(0.65f, 0.65f, 0.65f), Spacing);
            var sim = Make(p, set);
            for (int i = 0; i < 10; i++) sim.Step(1f / 60f);          // 预热

            // When:  分别量"整步"与"只重建邻居表"
            float stepMs = TotalMs(() => { for (int i = 0; i < 10; i++) sim.Step(1f / 60f); });
            float buildMs = TotalMs(() => { for (int i = 0; i < 10; i++) sim.RebuildNeighborTable(); });

            // Then:  邻居表确实是大头（PBF 的常识，也是以后上 Burst 的第一目标）；
            //        这条只记录不断言"必须超过 50%"——那个比例随规模与实现变化，写死了就是给自己挖坑。
            Assert.That(sim.HasNonFiniteState(), Is.False, "量完性能状态就崩了？");
            string line = "[基准/流体-邻居表] " + sim.ParticleCount + " 粒子 | 整步 "
                          + (stepMs / 10).ToString("0.000") + " ms | 其中重建邻居表 "
                          + (buildMs / 10).ToString("0.000") + " ms（占 "
                          + (100f * buildMs / Mathf.Max(1e-6f, stepMs)).ToString("0") + "%）";
            TestContext.Progress.WriteLine(line);
            UnityEngine.Debug.Log(line);
        }

        /// <summary>建一个点阵、预热若干步，再取 5 批 × 20 步里**最佳**的那一批，返回 ms/步。</summary>
        static float MeasureMsPerStep(FluidParameters proto, Vector3 size, out int count, out float avgDegree)
        {
            var set = FluidVolume.Box(size, Spacing);
            count = set.Count;
            var sim = Make(proto.Clone(), set);
            for (int i = 0; i < 15; i++) sim.Step(1f / 60f);          // 预热：别让 JIT 背锅
            avgDegree = sim.AverageNeighborDegree;

            const int batches = 5, steps = 20;
            float best = float.MaxValue;
            for (int b = 0; b < batches; b++)
                best = Mathf.Min(best, TotalMs(() => { for (int i = 0; i < steps; i++) sim.Step(1f / 60f); }));

            Assert.That(sim.HasNonFiniteState(), Is.False, "基准跑完状态必须仍然有限");
            Assert.That(sim.ParticleCount, Is.EqualTo(count), "基准过程中粒子数变了（越界写?）");
            return best / steps;
        }

        /// <summary>这段工作总共花了多少毫秒（不除以步数 —— 除法是调用方的事，别两头都算）。</summary>
        static float TotalMs(System.Action work)
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            work();
            long ended = System.Diagnostics.Stopwatch.GetTimestamp();
            double seconds = (ended - start) / (double)System.Diagnostics.Stopwatch.Frequency;
            return (float)(seconds * 1000.0);
        }

        // ---------------------------------------------------------------- 外力与碰撞

        [Test]
        public void Gravity_AcceleratesAFreeParticle_AndMovesItDown()
        {
            // Given:  单个粒子（没有邻居 ⇒ 约束不参与）
            var p = Defaults();
            p.gravity = new Vector3(0f, -9.81f, 0f);
            var set = new FluidParticleSet
            {
                Positions = new[] { new Vector3(0f, 5f, 0f) },
                Velocities = new[] { Vector3.zero },
                Count = 1
            };
            var sim = Make(p, set);
            float y0 = sim.GetPosition(0).y;

            // When
            float dt = 1f / 60f;
            sim.Step(dt);

            // Then:  v = g·dt；位置下降 g·dt²，**不是**精确解的 ½g·dt² ——
            //        PBF 是半隐式（先更新速度、再用新速度推位置），一步位移就是 g·dt²。
            //        拿 ½g·dt² 断言会逼着人去"修积分器"，其实积分数值没错，是断言记错了格式。
            Assert.AreEqual(-9.81f * dt, sim.GetVelocity(0).y, 1e-4f,
                "一步之后速度应为 g·dt，实际 " + sim.GetVelocity(0).y);
            Assert.AreEqual(y0 - 9.81f * dt * dt, sim.GetPosition(0).y, 1e-4f,
                "自由落体位移不对：" + sim.GetPosition(0).y);
        }

        [Test]
        public void GroundProxy_KeepsEveryParticleAboveTheFloor()
        {
            // Given:  一坨水从 0.5 米高处砸到半空间地面上
            var p = Defaults();
            p.gravity = new Vector3(0f, -9.81f, 0f);
            var set = FluidVolume.Box(new Vector3(0.3f, 0.3f, 0.3f), Spacing);
            Shift(ref set, new Vector3(0f, 0.5f, 0f));
            var sim = Make(p, set);
            sim.Collisions.Add(new PlaneCollisionProxy(Vector3.zero, Vector3.up));

            // When
            for (int i = 0; i < 120; i++) sim.Step(1f / 60f);

            // Then:  没有任何粒子穿到地面以下（容差 = 碰撞厚度）
            for (int i = 0; i < sim.ParticleCount; i++)
                Assert.GreaterOrEqual(sim.GetPosition(i).y, -p.collisionThickness - 1e-4f,
                    "第 " + i + " 个粒子穿进了地面：" + sim.GetPosition(i).y);

            // And:  水确实摊开了 —— 高度收缩、水平扩张
            Assert.Less(sim.Bounds().max.y, 0.8f, "砸了 2 秒高度还这么高：" + sim.Bounds().max.y);
        }

        [Test]
        public void SolidSphereObstacle_IsNeverOverlapped()
        {
            // Given:  水流过一颗实心球
            var p = Defaults();
            p.gravity = new Vector3(0f, -9.81f, 0f);
            var set = FluidVolume.Box(new Vector3(0.2f, 0.6f, 0.2f), Spacing);
            Shift(ref set, new Vector3(0f, 0.6f, 0f));
            var sim = Make(p, set);
            var ball = new SphereCollisionProxy(new Vector3(0f, 0.4f, 0f), 0.15f);
            sim.Collisions.Add(ball);
            sim.Collisions.Add(new PlaneCollisionProxy(Vector3.zero, Vector3.up));

            // When
            for (int i = 0; i < 90; i++) sim.Step(1f / 60f);

            // Then:  没有粒子留在球壳内
            for (int i = 0; i < sim.ParticleCount; i++)
            {
                float d = Vector3.Distance(sim.GetPosition(i), ball.Center);
                Assert.GreaterOrEqual(d, ball.Radius - p.collisionThickness - 1e-4f,
                    "第 " + i + " 个粒子埋进球里了（距球心 " + d + "）");
            }
        }

        // ---------------------------------------------------------------- 粘度与涡度

        [Test]
        public void XsphViscosity_ReducesRelativeVelocity()
        {
            // Given:  **两个粒子、相向而行**。为什么不用两团水：一团水里压力项一步就能把剪切带抹掉
            //         近三成，粘度的贡献整个淹在里面（我先前就是这么写的，测出来"开粘度反而更抖"，
            //         那不是实现错，是构造没隔离）。两个粒子时：密度远低于静止 ⇒ Ci<0 ⇒ Δλ>0 ⇒ 被拉力
            //         钳制挡掉 ⇒ 约束不出力；x̂ = x + Δt·v ⇒ 重算速度 v=(x̂−x)/Δt 恒等于原速；
            //         于是**唯一能改动速度的就是 XSPH**，而它的权重可以手算。
            var p = Defaults();
            p.xsphViscosity = 0.5f;
            float h = H, r = Spacing;
            var set = new FluidParticleSet
            {
                Positions = new[] { Vector3.zero, new Vector3(r, 0f, 0f) },
                Velocities = new[] { new Vector3(1f, 0f, 0f), new Vector3(-1f, 0f, 0f) },
                Count = 2
            };
            var sim = Make(p, set);

            // 期望值由核函数本身算出来，不写死数字：ṽ_i = v_i + ε Σ_j (m_j/ρ_j)(v_j − v_i) W(|x̂_i − x̂_j|)。
            // 关键点（我第一版算错了就是栽在这）：**核是在预测位置 x̂ 上评估的，不是初始位置 x**。
            // 这两个粒子相向而行，dt=1/60 一步就各自走 1/60 米 ⇒ 间距从 0.05 缩到 0.016667，
            // 权重因此从 0.297 涨到 0.479。拿初始间距推期望值，会得到"粘度比公式更强"的假象。
            float dt = 1f / 60f;
            float predictedGap = r - 2f * dt * 1f;
            Assert.Greater(predictedGap, 0f, "构造让两个粒子一步就交叉了，换个更小的 dt");
            float wSelf = FluidKernel.Poly6(0f, h);
            float wNeighbour = FluidKernel.Poly6(predictedGap * predictedGap, h);
            float weight = wNeighbour / (wSelf + wNeighbour);         // ρ_1 = m(W(0)+W(r̂)) ⇒ m/ρ_1·W = W/(W0+W)
            Assert.Greater(weight, 0.05f, "权重小到可以忽略的话这个构造就测不到粘度：" + weight);
            float expectedX = 1f - p.xsphViscosity * 2f * weight;      // v0 += ε·(v1−v0)·weight

            // When
            sim.Step(dt);

            // Then
            Assert.AreEqual(expectedX, sim.GetVelocity(0).x, 1e-4f,
                "XSPH 没把速度往邻居加权平均拉：期望 " + expectedX + "，实际 " + sim.GetVelocity(0).x);
            Assert.AreEqual(-expectedX, sim.GetVelocity(1).x, 1e-4f, "对称性破了（求和顺序依赖粒子编号？）");
            Assert.Less(Mathf.Abs(sim.GetVelocity(0).x - sim.GetVelocity(1).x), 2f,
                "相对速度一点没降，粘度等于没生效");
        }

        [Test]
        public void VorticityConfinement_WiresIntoVelocity_WhenVorticityIsNonUniform()
        {
            // Given:  剪切带（∂v_x/∂y 不匀 ⇒ |ω| 有梯度）。
            //         刚体式均匀旋转**不能用**：ω 均匀 ⇒ ∇|ω| ≈ 0 ⇒ N = 0 ⇒ 按定义就不该出力，
            //         那是特性不是 bug（涡度约束只在涡的边缘起作用）。原来那条用例正好踩在这个坑上。
            var p = Defaults();
            p.vorticityEpsilon = 4f;
            var set = FluidVolume.Box(new Vector3(0.3f, 0.3f, 0.1f), Spacing);
            for (int i = 0; i < set.Count; i++)
                set.Velocities[i] = set.Positions[i].y < 0.15f
                    ? new Vector3(0.4f, 0f, 0f) : new Vector3(-0.4f, 0f, 0f);

            var withVorticity = Make(p, Clone(set));
            var without = Make(WithoutVorticity(p), Clone(set));

            // When
            withVorticity.Step(1f / 60f);
            without.Step(1f / 60f);

            // Then:  ① 涡量场确实被算出来了
            Assert.Greater(withVorticity.AverageVorticity(), 1e-6f,
                "剪切带里平均涡量为 0 ⇒ ω 根本没算出来，后面的断言都是空的");

            // ② 约束真的写进了速度：同一初始条件下开与关，一步之后速度场必须分岔
            float maxDelta = 0f;
            for (int i = 0; i < withVorticity.ParticleCount; i++)
                maxDelta = Mathf.Max(maxDelta,
                    Vector3.Distance(withVorticity.GetVelocity(i), without.GetVelocity(i)));
            Assert.Greater(maxDelta, 1e-5f,
                "开/关涡度约束的一步之后速度场完全一致 ⇒ vorticityEpsilon 是个死参数");
        }


        [Test]
        public void NeighborDiagnostics_AreExposedForPerformanceDocs()
        {
            // Given:  设计间距下的点阵
            var sim = Make(Defaults(), FluidVolume.Box(new Vector3(0.3f, 0.3f, 0.3f), Spacing));

            // Then:  平均邻居数在合理区间（h=2d 的三维点阵约 26~50 个邻居）
            Assert.Greater(sim.AverageNeighborDegree, 10f, "平均邻居数低到不合理：" + sim.AverageNeighborDegree);
            Assert.Less(sim.AverageNeighborDegree, 120f, "平均邻居数高到不合理：" + sim.AverageNeighborDegree);
            Assert.GreaterOrEqual(sim.MaxNeighborDegree, sim.AverageNeighborDegree);
        }

        // ---------------------------------------------------------------- 辅助

        static int FindInterior(FluidSimulation sim)
        {
            // 找"邻居最多"的那个粒子 —— 它一定在内部，密度才有意义
            int best = 0;
            for (int i = 1; i < sim.ParticleCount; i++)
                if (sim.NeighborDegree(i) > sim.NeighborDegree(best)) best = i;
            return best;
        }

        static FluidParticleSet TwoParticles(float distance)
        {
            return new FluidParticleSet
            {
                Positions = new[] { Vector3.zero, new Vector3(distance, 0f, 0f) },
                Velocities = new[] { Vector3.zero, Vector3.zero },
                Count = 2
            };
        }

        static void Shift(ref FluidParticleSet set, Vector3 offset)
        {
            for (int i = 0; i < set.Count; i++) set.Positions[i] += offset;
        }

        static FluidParticleSet Clone(FluidParticleSet s)
        {
            var p = new Vector3[s.Count];
            var v = new Vector3[s.Count];
            Array.Copy(s.Positions, p, s.Count);
            Array.Copy(s.Velocities, v, s.Count);
            return new FluidParticleSet { Positions = p, Velocities = v, Count = s.Count };
        }

        static FluidParameters WithoutViscosity(FluidParameters p)
        {
            var q = p.Clone(); q.xsphViscosity = 0f; return q;
        }

        static FluidParameters WithoutVorticity(FluidParameters p)
        {
            var q = p.Clone(); q.vorticityEpsilon = 0f; return q;
        }

        static float RelativeSpeedAcrossShear(FluidSimulation sim)
        {
            // 所有邻居对的相对速度**平均**（含速度沿 +x / −x 的分界带）
            float sum = 0f;
            int n = 0;
            for (int i = 0; i < sim.ParticleCount; i++)
            {
                for (int s2 = 0; s2 < sim.NeighborDegree(i); s2++)
                {
                    int j = sim.NeighborAt(i, s2);
                    if (j <= i) continue;
                    sum += Vector3.Distance(sim.GetVelocity(i), sim.GetVelocity(j));
                    n++;
                }
            }
            return n > 0 ? sum / n : 0f;        }

        static float AngularSpeed(FluidSimulation sim, Vector3 center)
        {
            float sum = 0f;
            for (int i = 0; i < sim.ParticleCount; i++)
            {
                var r = sim.GetPosition(i) - center;
                var v = sim.GetVelocity(i);
                float radius = new Vector2(r.x, r.y).magnitude;
                if (radius < 1e-4f) continue;
                // 切向速度 = (r × v) 的 z 分量 / 半径
                sum += Mathf.Abs((r.x * v.y - r.y * v.x) / radius);
            }
            return sum / Mathf.Max(1, sim.ParticleCount);
        }
    }
}
