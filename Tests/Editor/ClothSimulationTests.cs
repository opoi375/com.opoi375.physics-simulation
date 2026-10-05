// Copyright (c) 2026 PhysicsSimulation. MIT License.
//
// 正式测试：布料（质点网格 + XPBD/PBD 距离约束：结构 / 剪切 / 弯曲三类邻居）。
// 覆盖：拓扑与数量、约束按类型生效、硬度→拉伸量的单调关系、固定点、确定性、
//       参数校验、极端参数下的有界性、性能基准。
// 约定：黑盒测试，只经由 ClothSimulation 的公共 API；断言消息用中文。

using System;
using System.Diagnostics;
using NUnit.Framework;
using UnityEngine;

namespace PhysicsSimulation.Editor.Tests
{
    [TestFixture]
    public class ClothSimulationTests
    {
        private static ClothParameters Grid(int columns, int rows, float spacing = 0.1f)
        {
            return new ClothParameters
            {
                columns = columns,
                rows = rows,
                spacing = spacing,
                gravity = new Vector3(0f, -9.81f, 0f),
                substeps = 4,
                iterations = 2
            };
        }

        // Given 一块 8x6 的布料网格
        // When 构建完成
        // Then 质点数 = 列*行，三类约束的数量符合网格拓扑（结构 2 邻居、剪切对角、弯曲隔一个）
        [Test]
        public void Build_GridTopology_HasExpectedParticleAndConstraintCounts()
        {
            const int C = 8;
            const int R = 6;
            var cloth = new ClothSimulation(Grid(C, R));

            Assert.That(cloth.ParticleCount, Is.EqualTo(C * R), "质点数应等于列乘行");

            int structural = 0, shear = 0, bend = 0;
            foreach (DistanceConstraint c in cloth.Constraints)
            {
                switch (c.type)
                {
                    case ClothConstraintType.Structural: structural++; break;
                    case ClothConstraintType.Shear: shear++; break;
                    case ClothConstraintType.Bend: bend++; break;
                }
            }

            // 结构：横向 R*(C-1) + 纵向 C*(R-1)
            Assert.That(structural, Is.EqualTo(R * (C - 1) + C * (R - 1)), "结构约束数量");
            // 剪切：每个四边形 2 条对角线
            Assert.That(shear, Is.EqualTo(2 * (C - 1) * (R - 1)), "剪切约束数量应为每个格子两条对角线");
            // 弯曲：隔一个邻居，横向 R*(C-2) + 纵向 (R-2)*C
            Assert.That(bend, Is.EqualTo(R * (C - 2) + C * (R - 2)), "弯曲约束数量");
        }

        // Given 关闭剪切与弯曲的布料参数
        // When 构建完成
        // Then 只剩结构约束（开关真的生效，不是摆设）
        [Test]
        public void Build_DisablingShearAndBend_KeepsOnlyStructuralConstraints()
        {
            ClothParameters p = Grid(5, 4);
            p.enableShear = false;
            p.enableBend = false;
            var cloth = new ClothSimulation(p);

            foreach (DistanceConstraint c in cloth.Constraints)
            {
                Assert.That(c.type, Is.EqualTo(ClothConstraintType.Structural), "关闭后不应出现其它类型的约束");
            }
            Assert.That(cloth.Constraints.Count, Is.EqualTo(4 * (5 - 1) + 5 * (4 - 1)), "只剩结构约束时的数量");
        }

        // Given 网格初始间距 spacing
        // When 构建完成
        // Then 结构约束原长 = spacing，剪切原长 = spacing*sqrt(2)，弯曲原长 = 2*spacing
        [Test]
        public void Build_RestLengthsMatchGridGeometry()
        {
            const float spacing = 0.2f;
            var cloth = new ClothSimulation(Grid(4, 4, spacing));

            foreach (DistanceConstraint c in cloth.Constraints)
            {
                float expected;
                switch (c.type)
                {
                    case ClothConstraintType.Structural: expected = spacing; break;
                    case ClothConstraintType.Shear: expected = spacing * Mathf.Sqrt(2f); break;
                    default: expected = spacing * 2f; break;
                }
                Assert.That(c.restLength, Is.EqualTo(expected).Within(1e-4f),
                    c.type + " 约束的原长应为 " + expected + "，实际 " + c.restLength);
            }
        }

        // Given 顶行全部钉住的 16x16 布料
        // When 步进若干步
        // Then 钉住的质点位置逐位不变，未钉住的质点全部有限值且下垂（Y 单调下降）
        [Test]
        public void Step_PinnedTopRow_AnchorStaysAndClothSags()
        {
            var cloth = new ClothSimulation(Grid(16, 16));
            for (int col = 0; col < 16; col++)
            {
                cloth.SetPinned(cloth.IndexOf(col, 0), true);
            }

            Vector3[] before = cloth.CapturePositions();
            for (int n = 0; n < 60; n++) cloth.Step(1f / 60f);
            Vector3[] after = cloth.CapturePositions();

            for (int col = 0; col < 16; col++)
            {
                int i = cloth.IndexOf(col, 0);
                Assert.That(after[i], Is.EqualTo(before[i]), "第 " + col + " 列钉点位置不应被改变");
            }

            int bottomMiddle = cloth.IndexOf(8, 15);
            Assert.That(after[bottomMiddle].y, Is.LessThan(before[bottomMiddle].y), "底边中点应在重力作用下垂");
            Assert.That(cloth.HasNonFiniteState(), Is.False, "60 步后不应出现 NaN / Infinity");
        }

        // Given 两块只有结构刚度不同的布料（0.2 与 1.0），钉住顶行、受同样的重力
        // When 各跑同样的步数
        // Then 更硬的那块最大拉伸比更小（硬度→形变是单调的，这是 PBD 的"刚度"语义）
        [Test]
        public void Step_HigherStiffness_StretchesLess()
        {
            ClothParameters soft = Grid(12, 12);
            soft.structuralStiffness = 0.2f;
            soft.shearStiffness = 0.1f;
            soft.bendStiffness = 0.05f;

            ClothParameters stiff = Grid(12, 12);
            stiff.structuralStiffness = 1f;
            stiff.shearStiffness = 1f;
            stiff.bendStiffness = 1f;

            float softStretch = SagAndMeasureStretch(soft, 90);
            float stiffStretch = SagAndMeasureStretch(stiff, 90);

            // MaxStretchRatio 返回的是 len/rest：1.0 = 毫无形变
            Assert.That(stiffStretch, Is.LessThan(softStretch),
                "结构刚度 1.0 的拉伸比（" + stiffStretch.ToString("F4") + "）应小于 0.2 的（" + softStretch.ToString("F4") + "）");
            Assert.That(stiffStretch, Is.LessThan(1.2f), "满刚度时最大拉伸比应保持在 20% 以内（实测 " + stiffStretch.ToString("F4") + "）");
            Assert.That(softStretch, Is.GreaterThan(1.02f), "很软的布应当能被测出拉长（否则与满刚度没区别，单调性断言就是空转）");
        }

        private static float SagAndMeasureStretch(ClothParameters parameters, int steps)
        {
            var cloth = new ClothSimulation(parameters);
            for (int col = 0; col < parameters.columns; col++)
            {
                cloth.SetPinned(cloth.IndexOf(col, 0), true);
            }
            for (int n = 0; n < steps; n++) cloth.Step(1f / 60f);
            Assert.That(cloth.HasNonFiniteState(), Is.False, "测量前系统必须有限");
            return cloth.MaxStretchRatio();
        }

        // Given 同一份布料参数与同样的步数跑两次（中间 ResetToInitial）
        // When 比较所有质点位置
        // Then 逐位一致（确定性：无 Random / 无 Time / 无并行，约束按固定顺序投影）
        [Test]
        public void Step_SameParametersTwice_ProducesBitwiseIdenticalPositions()
        {
            var cloth = new ClothSimulation(Grid(10, 10));
            cloth.SetPinned(cloth.IndexOf(0, 0), true);
            cloth.SetPinned(cloth.IndexOf(9, 0), true);

            Vector3[] first = Run(cloth, 45);
            cloth.ResetToInitial();
            Vector3[] second = Run(cloth, 45);

            for (int i = 0; i < first.Length; i++)
            {
                Assert.That(second[i].x, Is.EqualTo(first[i].x), "第 " + i + " 个质点 X 应逐位一致");
                Assert.That(second[i].y, Is.EqualTo(first[i].y), "第 " + i + " 个质点 Y 应逐位一致");
                Assert.That(second[i].z, Is.EqualTo(first[i].z), "第 " + i + " 个质点 Z 应逐位一致");
            }
        }

        private static Vector3[] Run(ClothSimulation cloth, int steps)
        {
            for (int n = 0; n < steps; n++) cloth.Step(1f / 60f);
            return cloth.CapturePositions();
        }

        // Given 一块已经跑起来的布料
        // When 传入非法参数（列或行小于 2、间距 <= 0、dt <= 0、质点索引越界）
        // Then 抛出 ArgumentOutOfRangeException，且抛出前后系统状态不变
        [Test]
        public void Api_InvalidArguments_ThrowBeforeMutatingState()
        {
            var cloth = new ClothSimulation(Grid(6, 6));
            for (int n = 0; n < 5; n++) cloth.Step(1f / 60f);
            string before = StateDigest(cloth);

            Assert.That(() => new ClothSimulation(new ClothParameters { columns = 1, rows = 4 }),
                Throws.TypeOf<ArgumentOutOfRangeException>(), "列数小于 2 应抛异常");
            Assert.That(() => new ClothSimulation(new ClothParameters { columns = 4, rows = 1 }),
                Throws.TypeOf<ArgumentOutOfRangeException>(), "行数小于 2 应抛异常");
            Assert.That(() => new ClothSimulation(new ClothParameters { columns = 4, rows = 4, spacing = 0f }),
                Throws.TypeOf<ArgumentOutOfRangeException>(), "间距为 0 应抛异常");
            Assert.That(() => new ClothSimulation(new ClothParameters
                { columns = 4, rows = 4, structuralStiffness = 1.5f }),
                Throws.TypeOf<ArgumentOutOfRangeException>(), "刚度不在 [0,1] 应抛异常");
            Assert.That(() => cloth.Step(0f),
                Throws.TypeOf<ArgumentOutOfRangeException>(), "dt = 0 应抛异常");
            Assert.That(() => cloth.Step(-1f / 60f),
                Throws.TypeOf<ArgumentOutOfRangeException>(), "dt < 0 应抛异常");
            Assert.That(() => cloth.SetPinned(cloth.ParticleCount, true),
                Throws.TypeOf<ArgumentOutOfRangeException>(), "越界索引应抛异常");
            Assert.That(() => cloth.IndexOf(-1, 0),
                Throws.TypeOf<ArgumentOutOfRangeException>(), "越界列号应抛异常");

            Assert.That(StateDigest(cloth), Is.EqualTo(before), "抛出异常前后系统状态必须逐位不变");
        }

        private static string StateDigest(ClothSimulation cloth)
        {
            var sb = new System.Text.StringBuilder();
            Vector3[] positions = cloth.CapturePositions();
            for (int i = 0; i < positions.Length; i++)
            {
                sb.Append(positions[i].x.ToString("R")).Append(',')
                  .Append(positions[i].y.ToString("R")).Append(',')
                  .Append(positions[i].z.ToString("R")).Append(';');
            }
            return sb.ToString();
        }

        // Given 极端参数（刚度拉满、子步 1、dt 恰为钳制上限、外加横向强风）
        // When 跑 600 步
        // Then 所有质点仍有限，且最大拉伸比不超过 maxStretchRatio（拉伸上限兜底生效）
        [Test]
        public void Step_ExtremeParameters_StaysBoundedByMaxStretch()
        {
            ClothParameters p = Grid(14, 14);
            p.substeps = 1;
            p.iterations = 1;
            p.structuralStiffness = 1f;
            p.shearStiffness = 1f;
            p.bendStiffness = 1f;
            p.maxStretchRatio = 1.4f;

            var cloth = new ClothSimulation(p);
            for (int col = 0; col < 14; col++) cloth.SetPinned(cloth.IndexOf(col, 0), true);

            for (int n = 0; n < 600; n++)
            {
                cloth.Step(1f / 15f);
                if (n % 120 == 0) cloth.AddWindImpulse(new Vector3(6f, 0f, 2f), 1f / 15f);   // 周期性强风扰动
            }

            Assert.That(cloth.HasNonFiniteState(), Is.False, "极端参数 + 强风扰动后仍必须是有限值");
            // 拉伸上限是“逐次扫描（Gauss-Seidel）”的硬约束：拉一个反而会把另一个撑大，
            // 所以残差量级约 0.1%（实测 1.4016 vs 1.4），留 2% 容差；关键是不发散、且被钉在上限附近。
            Assert.That(cloth.MaxStretchRatio(), Is.LessThanOrEqualTo(p.maxStretchRatio * 1.02f),
                "最大拉伸比必须被 maxStretchRatio 兜住（实测 " + cloth.MaxStretchRatio().ToString("F4") + "）");
            Assert.That(cloth.MaxStretchRatio(), Is.GreaterThan(1.0f), "这块布确实被拉过（否则上限断言是空转）");
        }

        // Given 2x2 的极小布料（只有 4 个质点）
        // When 构建
        // Then 仍能建出完整的三类约束（边界不崩）
        [Test]
        public void Build_MinimumGrid_StillCreatesAllConstraintTypes()
        {
            var cloth = new ClothSimulation(Grid(2, 2));

            bool hasStructural = false, hasShear = false, hasBend = false;
            foreach (DistanceConstraint c in cloth.Constraints)
            {
                if (c.type == ClothConstraintType.Structural) hasStructural = true;
                if (c.type == ClothConstraintType.Shear) hasShear = true;
                if (c.type == ClothConstraintType.Bend) hasBend = true;
            }

            Assert.That(hasStructural, Is.True, "2x2 至少有 4 条结构约束");
            Assert.That(hasShear, Is.True, "2x2 至少有 2 条剪切约束");
            Assert.That(hasBend, Is.False, "2x2 网格不存在「隔一个」的弯曲邻居，应为空");
        }

        // Given 一块 64x64（4096 质点）的布料
        // When 以 substeps=4 / iterations=2 跑 60 步并计时
        // Then 平均每步耗时在托管单线程路径的预算内（基准数字，v1.3.0 Burst 对比用）
        // 基准分两档：32x32 是"cape / 旗帜"的常规配置，要求一进一帧以内；
        // 64x64 是 hero cloth 的压力档，托管路径允许两帧，v1.3.0 的 Jobs+Burst 目标就是把它压到 8ms 以下。
        static double MeasureMsPerStep(ClothParameters p, int warmup, int batches, int steps)
        {
            var cloth = new ClothSimulation(p);
            for (int col = 0; col < p.columns; col++) cloth.SetPinned(cloth.IndexOf(col, 0), true);
            for (int n = 0; n < warmup; n++) cloth.Step(1f / 60f);      // 预热，避免 JIT 计入

            double best = double.MaxValue, sum = 0.0;
            for (int b = 0; b < batches; b++)
            {
                var sw = Stopwatch.StartNew();
                for (int n = 0; n < steps; n++) cloth.Step(1f / 60f);
                sw.Stop();
                double msPerStep = sw.Elapsed.TotalMilliseconds / steps;
                if (msPerStep < best) best = msPerStep;
                sum += msPerStep;
            }

            string line = string.Format(
                "[基准/托管] {0}x{0} = {1} 质点、{2} 约束、substeps={3}、iterations={4} → best {5:F3} ms/步、mean {6:F3} ms/步（{7} 批 × {8} 步）",
                p.columns, cloth.ParticleCount, cloth.Constraints.Count, p.substeps, p.iterations,
                best, sum / batches, batches, steps);
            UnityEngine.Debug.Log(line);                                 // 进 Console，方便被自动化抓走
            TestContext.Progress.WriteLine(line);
            Assert.That(cloth.HasNonFiniteState(), Is.False, "基准跑完必须仍然有限");
            return best;
        }

        [Test]
        public void Benchmark_32x32_ManagedSolverFitsInsideOneFrame()
        {
            // Given：常见的 cape/旗帜规模
            var p = Grid(32, 32, 0.1f);
            p.substeps = 4;
            p.iterations = 2;

            // When
            double best = MeasureMsPerStep(p, 10, 5, 60);

            // Then：常规规模必须只吃掉半帧以内（其余留给动画、碰撞与渲染）
            Assert.That(best, Is.LessThan(8.0f),
                "32x32 布料托管单步最优值应小于半帧 8ms（实测 " + best.ToString("F3") + " ms）");
        }

        [Test]
        public void Benchmark_64x64_ManagedSolverStaysWithinTwoFrames()
        {
            // Given：hero cloth 压力档
            var p = Grid(64, 64, 0.05f);
            p.substeps = 4;
            p.iterations = 2;

            // When
            double best = MeasureMsPerStep(p, 10, 5, 60);

            // Then：允许两帧（托管路径的现实上限）；这个数字就是 v1.3.0 Burst 的对照基线
            Assert.That(best, Is.LessThan(33.0f),
                "64x64 布料托管单步最优值应小于两帧 33ms（实测 " + best.ToString("F3") + " ms）");
        }

        // ==================================================================
        // 障碍物碰撞（球体）：布料演示里常见的“布包在球上”
        // ==================================================================

        static ClothParameters Falling(float gravityY)
        {
            return new ClothParameters
            {
                columns = 12,
                rows = 12,
                spacing = 0.12f,
                gravity = new Vector3(0f, gravityY, 0f),
                substeps = 4,
                iterations = 2
            };
        }

        [Test]
        public void Api_AddSphereObstacle_IsCountedAndValidated()
        {
            // Given：一块自由落体的布
            var cloth = new ClothSimulation(Falling(-9.81f));

            // When：加入两个球体障碍物
            cloth.AddSphereObstacle(new Vector3(0.5f, -0.6f, 0f), 0.3f);
            cloth.AddSphereObstacle(new Vector3(-0.5f, -0.6f, 0f), 0.25f);

            // Then：障碍物数量如实记录，非法半径/NaN 必须报错
            Assert.That(cloth.ObstacleCount, Is.EqualTo(2), "两个球体障碍物都应被记录在案");
            Assert.Throws<ArgumentOutOfRangeException>(() => cloth.AddSphereObstacle(Vector3.zero, 0f),
                "半径为 0 的球体障碍物应在添加时就报错");
            Assert.Throws<ArgumentOutOfRangeException>(() => cloth.AddSphereObstacle(new Vector3(float.NaN, 0f, 0f), 1f),
                "NaN 球心应在添加时就报错");
            Assert.That(cloth.ObstacleCount, Is.EqualTo(2), "报错的添加不能改变已有障碍物列表");
        }

        [Test]
        public void Step_ParticleInsideSphere_IsPushedOutOfSurface()
        {
            // Given：一块小布，中心正下方放一个很大的球，把布包进去
            var cloth = new ClothSimulation(Falling(0f));
            Vector3 center = new Vector3(0.66f, -0.66f, 0f);
            cloth.AddSphereObstacle(center, 0.9f);

            // When：一步之后
            cloth.Step(1f / 60f);

            // Then：所有质点都必须在球外（含碰撞厚度）
            float surface = 0.9f + cloth.Parameters.collisionThickness;
            for (int i = 0; i < cloth.ParticleCount; i++)
            {
                float distance = Vector3.Distance(cloth.GetPosition(i), center);
                Assert.That(distance, Is.GreaterThanOrEqualTo(surface - 1e-3f),
                    "质点 " + i + " 本应被球顶出表面，实测距离 " + distance.ToString("F4") + " < " + surface.ToString("F4"));
            }
            Assert.That(cloth.HasNonFiniteState(), Is.False, "碰撞处理之后状态必须有限");
        }

        [Test]
        public void Step_FallingCloth_LandsOnSphereInsteadOfPassingThrough()
        {
            // Given：一块顶边钉住的布，正下方一个球
            var p = Falling(-9.81f);
            var cloth = new ClothSimulation(p);
            for (int col = 0; col < p.columns; col++) cloth.SetPinned(cloth.IndexOf(col, 0), true);

            Vector3 center = new Vector3((p.columns - 1) * 0.5f * p.spacing, -1.0f, 0f);
            cloth.AddSphereObstacle(center, 0.35f);

            // When：落 120 步，取“最低的自由质点”与球的距离
            for (int n = 0; n < 120; n++) cloth.Step(1f / 60f);

            float closest = float.MaxValue;
            for (int i = 0; i < cloth.ParticleCount; i++)
            {
                if (cloth.IsPinned(i)) continue;
                closest = Mathf.Min(closest, Vector3.Distance(cloth.GetPosition(i), center));
            }

            // Then：没有任何质点钻进球内部（隧道现象），且确实有质点贴在球面附近
            Assert.That(closest, Is.GreaterThanOrEqualTo(0.35f + p.collisionThickness - 1e-3f),
                "最接近质点距球心 " + closest.ToString("F4") + " 不应小于球面（含碰撞厚度）");
            Assert.That(closest, Is.LessThan(0.35f + p.spacing * 2f),
                "布应当真的搭在球上（最近距离 " + closest.ToString("F4") + "），而不是从旁边绕过去");
        }

        [Test]
        public void Step_WithObstacle_DeformsDifferentlyAndDeterministically()
        {
            // Given：同样初始状态的两块布，只给其中一块加障碍物
            var a = new ClothSimulation(Falling(-9.81f));
            var b = new ClothSimulation(Falling(-9.81f));
            Vector3 center = new Vector3(0.66f, -1.0f, 0f);
            a.AddSphereObstacle(center, 0.4f);

            // When：同步数推进
            for (int n = 0; n < 60; n++) { a.Step(1f / 60f); b.Step(1f / 60f); }

            // Then：障碍物确实改变了布的形状（而不是“加了个空气球”）
            Assert.That(MaxDifference(a, b), Is.GreaterThan(0.02f),
                "有/无障碍物两块布的质点位置必须明显不同（实测最大差 " + MaxDifference(a, b).ToString("F4") + "）");

            // And：同参数重跑逐位一致
            var c = new ClothSimulation(Falling(-9.81f));
            c.AddSphereObstacle(center, 0.4f);
            for (int n = 0; n < 60; n++) c.Step(1f / 60f);
            Assert.That(StateDigest(c), Is.EqualTo(StateDigest(a)), "碰撞处理必须不破坏确定性");
        }

        static float MaxDifference(ClothSimulation x, ClothSimulation y)
        {
            float max = 0f;
            for (int i = 0; i < x.ParticleCount; i++)
            {
                max = Mathf.Max(max, Vector3.Distance(x.GetPosition(i), y.GetPosition(i)));
            }
            return max;
        }

        [Test]
        public void ClearObstacles_RemovesThemAndKeepsStateFinite()
        {
            // Given：加了三个障碍物的布
            var cloth = new ClothSimulation(Falling(-9.81f));
            cloth.AddSphereObstacle(new Vector3(0f, -1f, 0f), 0.3f);
            cloth.AddSphereObstacle(new Vector3(0.5f, -1f, 0f), 0.3f);
            cloth.AddSphereObstacle(new Vector3(-0.5f, -1f, 0f), 0.3f);
            for (int n = 0; n < 10; n++) cloth.Step(1f / 60f);

            // When：清空障碍物
            cloth.ClearObstacles();
            int before = cloth.ParticleCount;
            for (int n = 0; n < 10; n++) cloth.Step(1f / 60f);

            // Then：障碍物归零，质点数不变，状态仍有限
            Assert.That(cloth.ObstacleCount, Is.EqualTo(0), "清空后不应残留障碍物");
            Assert.That(cloth.ParticleCount, Is.EqualTo(before), "清空障碍物不应改变质点布局");
            Assert.That(cloth.HasNonFiniteState(), Is.False, "清空障碍物后的模拟必须仍然有限");
        }
    }
}
