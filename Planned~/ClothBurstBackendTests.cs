// Copyright (c) 2026 PhysicsSimulation. MIT License.
using System;
using System.Text;
using NUnit.Framework;
using PhysicsSimulation;
using UnityEngine;

namespace PhysicsSimulation.Editor.Tests
{
    /// <summary>
    /// v1.3.0 布料并行后端的行为规格。
    ///
    /// 设计前提（用户选定 A 方案：严格等价优先）：
    ///   Gauss-Seidel 的约束投影是"就地更新"，第 i 条约束用到第 i-1 条刚改过的顶点，
    ///   天然串行。并行化它必然改变数值结果。所以本包只并行逐粒子互相独立的三段：
    ///   Predict（积分）、ResolveCollisions（障碍推出）、Commit（回算速度）。
    ///   因此"托管与 Burst 逐位一致"是一个可以做到的硬承诺，本文件就是用来锁死它的。
    ///
    /// Burst 是**可选**能力：PhysicsSimulation.Burst 程序集靠 asmdef 的 versionDefines 门控，
    /// 没装 com.unity.burst 时那个程序集编译为空。测试程序集不引用它，只靠反射探测，
    /// 这样"没有 Burst 也能用包"这件事本身就是被测的。
    /// </summary>
    public class ClothBurstBackendTests
    {
        static ClothParameters Grid(int columns, int rows)
        {
            return new ClothParameters
            {
                columns = columns,
                rows = rows,
                spacing = 0.1f,
                iterations = 2,
                substeps = 4,
            };
        }

        /// <summary>逐位比较两个 sim 的全部质点位置与速度（用 BitConverter，测试程序集不允许 unsafe）。</summary>
        static void AssertBitIdentical(ClothSimulation expected, ClothSimulation actual, string scene)
        {
            Assert.That(actual.ParticleCount, Is.EqualTo(expected.ParticleCount),
                scene + "：质点数量应相同");
            for (int i = 0; i < expected.ParticleCount; i++)
            {
                Vector3 a = expected.GetPosition(i);
                Vector3 b = actual.GetPosition(i);
                Assert.That(BitConverter.SingleToInt32Bits(b.x), Is.EqualTo(BitConverter.SingleToInt32Bits(a.x)),
                    scene + "：质点 " + i + " 的位置 x 必须逐位一致（托管 " + a + " vs 后端 " + b + "）");
                Assert.That(BitConverter.SingleToInt32Bits(b.y), Is.EqualTo(BitConverter.SingleToInt32Bits(a.y)),
                    scene + "：质点 " + i + " 的位置 y 必须逐位一致");
                Assert.That(BitConverter.SingleToInt32Bits(b.z), Is.EqualTo(BitConverter.SingleToInt32Bits(a.z)),
                    scene + "：质点 " + i + " 的位置 z 必须逐位一致");

                Vector3 av = expected.GetVelocity(i);
                Vector3 bv = actual.GetVelocity(i);
                Assert.That(BitConverter.SingleToInt32Bits(bv.x), Is.EqualTo(BitConverter.SingleToInt32Bits(av.x)),
                    scene + "：质点 " + i + " 的速度 x 必须逐位一致");
                Assert.That(BitConverter.SingleToInt32Bits(bv.y), Is.EqualTo(BitConverter.SingleToInt32Bits(av.y)),
                    scene + "：质点 " + i + " 的速度 y 必须逐位一致");
                Assert.That(BitConverter.SingleToInt32Bits(bv.z), Is.EqualTo(BitConverter.SingleToInt32Bits(av.z)),
                    scene + "：质点 " + i + " 的速度 z 必须逐位一致");
            }
        }

        static ClothSimulation Build(ClothSolverBackend backend)
        {
            var cloth = new ClothSimulation(Grid(24, 24));
            cloth.SolverBackend = backend;
            cloth.AddObstacle(new Vector3(1.2f, 0.6f, -0.4f), 0.5f);
            return cloth;
        }

        [Test]
        public void DefaultBackend_IsManaged()
        {
            // Given：一个刚建好的布料系统，谁都没改过后端
            var cloth = new ClothSimulation(Grid(8, 8));

            // When：读取它实际使用的后端
            // Then：默认必须是托管——并行是可选项，不是默认项
            Assert.That(cloth.SolverBackend, Is.EqualTo(ClothSolverBackend.Managed),
                "新布料系统默认应当走托管求解，不默认引入任何并行开销");
            Assert.That(cloth.ActiveBackend, Is.EqualTo(ClothSolverBackend.Managed),
                "没请求并行时，生效后端也必须是托管");
        }

        [Test]
        public void RequestingBurst_FallsBackWhenBurstNotPresent()
        {
            // Given：请求 Burst 后端
            var cloth = Build(ClothSolverBackend.Burst);

            // When：读取生效后端
            // Then：装了 Burst 就真用 Burst，没装就静默退回托管，两种情况下都绝不允许炸
            if (ClothSolverOptions.IsBurstAvailable)
            {
                Assert.That(cloth.ActiveBackend, Is.EqualTo(ClothSolverBackend.Burst),
                    "Burst 程序集在场时，请求 Burst 就应当真的用上 Burst，而不是悄悄退回托管");
            }
            else
            {
                Assert.That(cloth.ActiveBackend, Is.EqualTo(ClothSolverBackend.Managed),
                    "Burst 缺席时必须静默回退到托管：不装 Burst 也要能用这个包");
            }
        }

        [Test]
        public void BurstAndManaged_AreBitIdentical()
        {
            // Given：两份参数完全相同的布料，一份强制托管、一份请求并行后端
            var managed = Build(ClothSolverBackend.Managed);
            var parallel = Build(ClothSolverBackend.Burst);

            if (!ClothSolverOptions.IsBurstAvailable)
            {
                Assert.Ignore("本工程未安装 com.unity.burst，并行后端缺席，逐位一致断言无从验证（不是失败，是未覆盖）");
            }

            // When：各推进 30 步
            for (int i = 0; i < 30; i++)
            {
                managed.Step(1f / 60f);
                parallel.Step(1f / 60f);
            }

            // Then：每个质点的位置与速度必须逐位一致
            //         这条是本版本的命门——若有人把约束投影改成 Jacobi 来"顺便并行"，这里会红
            Assert.That(parallel.ActiveBackend, Is.EqualTo(ClothSolverBackend.Burst),
                "先确认对照真的跑在并行后端上，否则这条断言是在自己骗自己");
            AssertBitIdentical(managed, parallel, "30 步后");
        }

        [Test]
        public void SwappingBackendMidRun_ContinuesTheSameTrajectory()
        {
            // Given：同样两份布料，一份全程托管，一份前 12 步托管、后 12 步切到并行后端
            var alwaysManaged = Build(ClothSolverBackend.Managed);
            var swapped = Build(ClothSolverBackend.Managed);

            if (!ClothSolverOptions.IsBurstAvailable)
            {
                Assert.Ignore("本工程未安装 com.unity.burst，后端热切换无从验证");
            }

            for (int i = 0; i < 12; i++)
            {
                alwaysManaged.Step(1f / 60f);
                swapped.Step(1f / 60f);
            }

            // When：跑到一半换后端，再跑 12 步
            swapped.SolverBackend = ClothSolverBackend.Burst;
            Assert.That(swapped.ActiveBackend, Is.EqualTo(ClothSolverBackend.Burst),
                "换后端之后应当立刻生效，不需要重建");
            for (int i = 0; i < 12; i++)
            {
                alwaysManaged.Step(1f / 60f);
                swapped.Step(1f / 60f);
            }

            // Then：换后端不该让轨迹跳变——状态是普通数组，后端只是"怎么算"
            AssertBitIdentical(alwaysManaged, swapped, "中途换后端 12 步后");
        }

        [Test]
        public void ParallelBackend_IsStillDeterministic()
        {
            // Given：两个都用并行后端的布料
            var a = Build(ClothSolverBackend.Burst);
            var b = Build(ClothSolverBackend.Burst);

            // When：同参数同步数各跑一遍
            for (int i = 0; i < 20; i++)
            {
                a.Step(1f / 60f);
                b.Step(1f / 60f);
            }

            // Then：逐位一致。并行只发生在"互相独立的质点"上，所以线程数与调度顺序不得影响结果
            AssertBitIdentical(a, b, "并行后端跑两遍");
        }

        [Test]
        public void BackendChoice_DoesNotChangePhysicalBehaviour()
        {
            // Given：一面顶边钉住、往下垂的布，两份，只差后端
            var managed = new ClothSimulation(Grid(16, 16));
            var parallel = new ClothSimulation(Grid(16, 16));
            parallel.SolverBackend = ClothSolverBackend.Burst;
            for (int col = 0; col < 16; col++)
            {
                managed.SetPinned(managed.IndexOf(col, 0), true);
                parallel.SetPinned(parallel.IndexOf(col, 0), true);
            }

            if (!ClothSolverOptions.IsBurstAvailable)
            {
                Assert.Ignore("本工程未安装 com.unity.burst，后端物理等价性无从验证");
            }

            // When：各跑 60 步
            for (int i = 0; i < 60; i++)
            {
                managed.Step(1f / 60f);
                parallel.Step(1f / 60f);
            }

            // Then：最低点、最大拉伸比这些宏观量也必须一模一样（逐位一致的直接推论）
            AssertBitIdentical(managed, parallel, "60 步后");
            Assert.That(parallel.MaxStretchRatio(), Is.EqualTo(managed.MaxStretchRatio()),
                "后端不该改变最大拉伸比");
        }

        [Test]
        public void Behaviour_ExposesBackendToTheInspector()
        {
            // Given：一个挂了 ClothBehaviour 的对象
            var go = new GameObject("ClothBackendBehaviour");
            try
            {
                var behaviour = go.AddComponent<ClothBehaviour>();
                behaviour.Parameters = Grid(6, 6);
                behaviour.PinTop = true;

                // When：在组件上请求并行后端并构建
                behaviour.SolverBackend = ClothSolverBackend.Burst;
                behaviour.Rebuild();

                // Then：组件把这个选择透传给底层系统，并报告实际生效的后端
                Assert.That(behaviour.IsBuilt, Is.True, "应当构建成功，实际错误：" + behaviour.LastBuildError);
                Assert.That(behaviour.Simulation.SolverBackend, Is.EqualTo(ClothSolverBackend.Burst),
                    "组件上的后端选择必须真的传到 ClothSimulation");
                if (ClothSolverOptions.IsBurstAvailable)
                {
                    Assert.That(behaviour.ActiveBackend, Is.EqualTo(ClothSolverBackend.Burst),
                        "Burst 在场时组件应报告它真的在用 Burst");
                }
                else
                {
                    Assert.That(behaviour.ActiveBackend, Is.EqualTo(ClothSolverBackend.Managed),
                        "Burst 缺席时组件应如实报告退回托管，而不是谎报用了 Burst");
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void BurstAvailability_Probe_IsStableAndReportsPath()
        {
            // Given/When：连续探测两次 Burst 是否可用
            bool first = ClothSolverOptions.IsBurstAvailable;
            bool second = ClothSolverOptions.IsBurstAvailable;

            // Then：探测结果必须稳定（不该第一次说有第二次说没有），并且能说清是哪条路
            Assert.That(second, Is.EqualTo(first), "Burst 可用性探测必须稳定，否则回退行为不可预期");
            string note = ClothSolverOptions.DescribeBackend();
            Assert.That(string.IsNullOrEmpty(note), Is.False, "应当能报告当前后端与可用性来源，供 Dump State 使用");
            Assert.That(note.Contains("burst", StringComparison.OrdinalIgnoreCase) || note.Contains("托管"), Is.True,
                "诊断字符串应当说明用的是并行还是托管，实际：" + note);
        }

        [Test]
        [Explicit("64×64 布料并行加速比基准，只在发布前手动跑")]
        public void Benchmark_Cloth64x64_ManagedVsBurst()
        {
            var sb = new StringBuilder();
            sb.Append("[基准/布料后端] Burst 可用=").Append(ClothSolverOptions.IsBurstAvailable).Append('\n');

            foreach (ClothSolverBackend backend in new[] { ClothSolverBackend.Managed, ClothSolverBackend.Burst })
            {
                if (backend == ClothSolverBackend.Burst && !ClothSolverOptions.IsBurstAvailable)
                {
                    sb.Append("[基准/布料后端] 跳过 Burst 对照（未安装 com.unity.burst）\n");
                    continue;
                }

                var cloth = new ClothSimulation(Grid(64, 64));
                cloth.SolverBackend = backend;
                cloth.AddObstacle(new Vector3(1.2f, 0.6f, -0.4f), 0.8f);

                float dt = 1f / 60f;
                double bestMs = double.PositiveInfinity;
                double totalMs = 0d;
                int batches = 5;
                int steps = 60;
                for (int b = 0; b < batches; b++)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    for (int i = 0; i < steps; i++) cloth.Step(dt);
                    sw.Stop();
                    double msPerStep = sw.Elapsed.TotalMilliseconds / steps;
                    if (msPerStep < bestMs) bestMs = msPerStep;
                    totalMs += msPerStep;
                }
                sb.Append("[基准/布料后端] ").Append(backend).Append(" 实际生效=").Append(cloth.ActiveBackend)
                  .Append(" | 64x64 = ").Append(cloth.ParticleCount).Append(" 质点、")
                  .Append(cloth.ConstraintCount).Append(" 约束 | 最佳 ").Append(bestMs.ToString("F3")).Append(" ms/步")
                  .Append(" | 均值 ").Append((totalMs / batches).ToString("F3")).Append(" ms/步\n");
            }

            UnityEngine.Debug.Log(sb.ToString());
        }
    }
}
