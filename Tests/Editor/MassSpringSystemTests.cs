// Copyright (c) 2026 PhysicsSimulation. MIT License.
//
// 正式测试：质点弹簧系统的整体行为（固定点、确定性、参数校验、稳定性、状态复位、dt 钳制）。
// 约定：
//   - 黑盒测试，只经由 MassSpringSystem 的公共 API（AddParticle / AddSpring / Pin / Unpin /
//     ApplyForces / Step / ResetToInitial / Particles / Springs / Parameters）
//   - 非法输入必须抛 ArgumentOutOfRangeException，且抛出前后系统状态逐位不变
//   - 断言消息用中文

using System;
using NUnit.Framework;
using UnityEngine;

namespace PhysicsSimulation.Editor.Tests
{
    /// <summary>
    /// 系统行为：固定点、确定性、参数校验、高刚度 + 子步的数值稳定性、复位与 dt 钳制。
    /// </summary>
    [TestFixture]
    public class MassSpringSystemTests
    {
        private const float Dt = 1f / 60f;

        // Given 一根弹簧的一端被 Pin 成固定点（inverseMass = 0），另一端带初速度
        // When 连续步进若干步
        // Then 固定点位置始终不变，另一端绕它摆动
        [Test]
        public void Step_PinnedParticle_StaysPutWhileOtherEndSwings()
        {
            var system = new MassSpringSystem();
            system.Parameters.gravity = Vector3.zero;
            system.Parameters.globalDamping = 0f;
            system.Parameters.substeps = 8;
            int anchor = system.AddParticle(Vector3.zero, 1f);
            int bob = system.AddParticle(new Vector3(1f, 0f, 0f), 1f);
            system.AddSpring(anchor, bob, restLength: 1f, stiffness: 50f, damping: 0f);

            system.Pin(bob);                                  // 先把摆端钉住……
            Assert.That(system.Particles[bob].inverseMass, Is.EqualTo(0f), "Pin 之后 inverseMass 必须为 0");
            system.Unpin(bob);                                // ……再解开，改用锚点固定
            Assert.That(system.Particles[bob].inverseMass, Is.EqualTo(1f), "Unpin 之后应恢复 1/mass");

            system.Pin(anchor);
            system.Particles[bob].velocity = new Vector3(0f, 5f, 0f);   // 切向速度 -> 绕锚点摆动

            Vector3 anchorPosition = system.Particles[anchor].position;
            for (int n = 0; n < 200; n++)
            {
                system.Step(Dt);
                Assert.That(system.Particles[anchor].position, Is.EqualTo(anchorPosition),
                    "第 " + n + " 步：固定点位置不应被任何力改变");
                Assert.That(system.Particles[anchor].velocity, Is.EqualTo(Vector3.zero),
                    "第 " + n + " 步：固定点速度应保持为零");
                float radius = system.Particles[bob].position.magnitude;
                Assert.That(radius, Is.LessThan(3f), "第 " + n + " 步：摆动的半径应有界");
            }

            Assert.That(system.Particles[bob].position, Is.Not.EqualTo(new Vector3(1f, 0f, 0f)),
                "另一端应绕固定点运动起来");
        }

        // Given 同一个系统用完全相同的参数与步数跑两次（中间 ResetToInitial）
        // When 比较两次的全部质点位置
        // Then 结果逐位一致（确定性：不引入 Random / Time / 并行求和）
        [Test]
        public void Step_SameParametersAndStepCount_ProducesBitwiseIdenticalPositions()
        {
            var system = BuildRopeLikeSystem();
            Vector3[] first = RunAndCapture(system, 50);

            system.ResetToInitial();
            Vector3[] second = RunAndCapture(system, 50);

            bool moved = false;
            for (int i = 0; i < first.Length; i++)
            {
                Assert.That(second[i].x, Is.EqualTo(first[i].x), "第 " + i + " 个质点 X 分量两次运行应逐位一致");
                Assert.That(second[i].y, Is.EqualTo(first[i].y), "第 " + i + " 个质点 Y 分量两次运行应逐位一致");
                Assert.That(second[i].z, Is.EqualTo(first[i].z), "第 " + i + " 个质点 Z 分量两次运行应逐位一致");
                if (second[i] != Vector3.zero) moved = true;
            }
            Assert.That(moved, Is.True, "系统应真的演化过（否则确定性断言是空转）");
        }

        // Given 一个已经建好的质点弹簧系统，并记录调用前的完整状态
        // When 传入越界的弹簧端点索引、零或负质量的质点、以及 dt <= 0
        // Then 每种非法输入都抛出异常，并且抛出前后系统状态逐位不变
        [Test]
        public void Api_InvalidArguments_ThrowBeforeMutatingSystemState()
        {
            var system = BuildRopeLikeSystem();
            for (int n = 0; n < 3; n++) system.Step(Dt);          // 让状态非平凡
            string before = CaptureState(system);
            int particleCount = system.Particles.Count;
            int springCount = system.Springs.Count;

            Assert.That(() => system.AddSpring(0, particleCount, 1f, 10f, 0f),
                Throws.TypeOf<ArgumentOutOfRangeException>(), "越界的弹簧端点索引应抛 ArgumentOutOfRangeException");
            Assert.That(() => system.AddSpring(-1, 0, 1f, 10f, 0f),
                Throws.TypeOf<ArgumentOutOfRangeException>(), "负索引应抛 ArgumentOutOfRangeException");
            Assert.That(() => system.AddParticle(Vector3.zero, 0f),
                Throws.TypeOf<ArgumentOutOfRangeException>(), "零质量应抛 ArgumentOutOfRangeException");
            Assert.That(() => system.AddParticle(Vector3.zero, -2f),
                Throws.TypeOf<ArgumentOutOfRangeException>(), "负质量应抛 ArgumentOutOfRangeException");
            Assert.That(() => system.Step(0f),
                Throws.TypeOf<ArgumentOutOfRangeException>(), "dt = 0 应抛 ArgumentOutOfRangeException");
            Assert.That(() => system.Step(-Dt),
                Throws.TypeOf<ArgumentOutOfRangeException>(), "dt < 0 应抛 ArgumentOutOfRangeException");
            Assert.That(() => system.Pin(particleCount),
                Throws.TypeOf<ArgumentOutOfRangeException>(), "Pin 越界索引应抛 ArgumentOutOfRangeException");

            Assert.That(CaptureState(system), Is.EqualTo(before), "抛出异常前不得改变系统状态");
            Assert.That(system.Particles.Count, Is.EqualTo(particleCount), "非法 AddParticle 不应把质点加进列表");
            Assert.That(system.Springs.Count, Is.EqualTo(springCount), "非法 AddSpring 不应把弹簧加进列表");
        }

        // Given 一根高刚度弹簧（k * dt^2 / m 远超显式积分的稳定阈值）并开启子步
        // When 以固定 dt 步进 1000 步
        // Then 所有质点位置与速度都有限（非 NaN、非 Infinity），且不发散
        [Test]
        public void Step_HighStiffnessWithSubsteps_StaysFiniteAfterThousandSteps()
        {
            const float k = 50000f;
            const float mass = 1f;
            const float dt = 1f / 15f;               // 恰为默认 dt 上限，不触发钳制

            var system = new MassSpringSystem();
            system.Parameters.gravity = Vector3.zero;
            system.Parameters.globalDamping = 0f;
            system.Parameters.substeps = 16;
            system.Parameters.maxDeltaTime = dt;
            int anchor = system.AddParticle(Vector3.zero, mass, pinned: true);
            int bob = system.AddParticle(new Vector3(1.5f, 0f, 0f), mass);
            system.AddSpring(anchor, bob, restLength: 1f, stiffness: k, damping: 0f);

            Assert.That(k * dt * dt / mass, Is.GreaterThan(4f),
                "前提：k*dt^2/m 必须超过单步稳定阈值 4，否则这个场景没有意义");

            for (int n = 0; n < 1000; n++)
                system.Step(dt);

            Assert.That(IsFinite(system.Particles[anchor].position) && IsFinite(system.Particles[anchor].velocity),
                Is.True, "固定点应保持有限值");
            Assert.That(IsFinite(system.Particles[bob].position), Is.True, "1000 步后位置必须是有限值（无 NaN / Infinity）");
            Assert.That(IsFinite(system.Particles[bob].velocity), Is.True, "1000 步后速度必须是有限值");
            Assert.That(system.Particles[bob].position.magnitude, Is.LessThan(5f),
                "开启子步后系统不应发散（拉伸量 0.5 的振动，半径应保持在个位数）");
        }

        // Given 一个已经步进若干步、力累积器非零的系统
        // When 调用 ResetToInitial
        // Then 位置与速度回到初始值、力累积器清零，再次步进的结果与全新系统等价
        [Test]
        public void ResetToInitial_RestoresInitialStateAndClearsForces()
        {
            var system = BuildRopeLikeSystem();
            Vector3[] initialPositions = new Vector3[system.Particles.Count];
            Vector3[] initialVelocities = new Vector3[system.Particles.Count];
            for (int i = 0; i < system.Particles.Count; i++)
            {
                initialPositions[i] = system.Particles[i].position;
                initialVelocities[i] = system.Particles[i].velocity;
            }

            Vector3[] evolved = RunAndCapture(system, 7);        // 步进 7 步

            system.ResetToInitial();
            for (int i = 0; i < system.Particles.Count; i++)
            {
                Assert.That(system.Particles[i].position, Is.EqualTo(initialPositions[i]),
                    "第 " + i + " 个质点复位后位置应回到初始值");
                Assert.That(system.Particles[i].velocity, Is.EqualTo(initialVelocities[i]),
                    "第 " + i + " 个质点复位后速度应回到初始值");
                Assert.That(system.Particles[i].force, Is.EqualTo(Vector3.zero),
                    "第 " + i + " 个质点复位后力累积器应清零");
            }

            Vector3[] again = RunAndCapture(system, 7);
            for (int i = 0; i < again.Length; i++)
                Assert.That(again[i], Is.EqualTo(evolved[i]), "复位后再步进 7 步应与第一次逐位一致");
        }

        // Given 一个把 dt 钳制上限设为 1/15 秒的系统
        // When 用超过上限的 dt（例如 1 秒）步进一次
        // Then 实际推进的物理时间不超过钳制上限（按 dtMax 计算，位置位移有界）
        [Test]
        public void Step_DtAboveClamp_UsesClampedDt()
        {
            const float g = 9.81f;
            const float dtMax = 1f / 15f;

            var system = new MassSpringSystem();
            system.Parameters.gravity = new Vector3(0f, -g, 0f);
            system.Parameters.globalDamping = 0f;
            system.Parameters.substeps = 1;
            system.Parameters.maxDeltaTime = dtMax;
            int i = system.AddParticle(Vector3.zero, 1f);

            system.Step(1f);                                     // 远超上限的 dt

            float expected = -g * dtMax * dtMax;                 // 单步半隐式：v=g*dtMax, x=v*dtMax
            Assert.That(system.Particles[i].position.y, Is.EqualTo(expected).Within(1e-4f),
                "dt 应按 maxDeltaTime 钳制后再积分");
            Assert.That(Mathf.Abs(system.Particles[i].position.y), Is.LessThan(0.1f),
                "若 dt 未被钳制，本步位移会达到 ~9.8 米");
        }

        // 3 个质点 + 2 根弹簧 + 重力的竖直链条，固定顶部，用于确定性/校验类场景
        private static MassSpringSystem BuildRopeLikeSystem()
        {
            var system = new MassSpringSystem();
            system.Parameters.gravity = new Vector3(0f, -9.81f, 0f);
            system.Parameters.globalDamping = 0.5f;
            system.Parameters.substeps = 3;
            int p0 = system.AddParticle(new Vector3(0f, 3f, 0f), 1f, pinned: true);
            int p1 = system.AddParticle(new Vector3(0.1f, 2f, 0f), 1f);
            int p2 = system.AddParticle(new Vector3(0.2f, 1f, 0f), 1.5f);
            system.AddSpring(p0, p1, restLength: 1f, stiffness: 200f, damping: 1f);
            system.AddSpring(p1, p2, restLength: 1f, stiffness: 120f, damping: 1f);
            return system;
        }

        // 步进 steps 次并捕获当前所有质点位置
        private static Vector3[] RunAndCapture(MassSpringSystem system, int steps)
        {
            for (int n = 0; n < steps; n++) system.Step(Dt);
            var capture = new Vector3[system.Particles.Count];
            for (int i = 0; i < capture.Length; i++) capture[i] = system.Particles[i].position;
            return capture;
        }

        // 把系统当前状态压成可比较的字符串（用于"抛异常前不改变状态"）。
        // 必须用 "R" 往返格式：Vector3.ToString() 默认只保留 2 位小数，会漏掉微小位移而使断言空转。
        private static string CaptureState(MassSpringSystem system)
        {
            var sb = new System.Text.StringBuilder();
            AppendNum(sb, system.Parameters.substeps);
            AppendVec(sb, system.Parameters.gravity);
            AppendNum(sb, system.Parameters.globalDamping);
            AppendNum(sb, system.Parameters.maxDeltaTime);
            foreach (Particle p in system.Particles)
            {
                AppendVec(sb, p.position);
                AppendVec(sb, p.velocity);
                AppendVec(sb, p.force);
                sb.Append(';').Append(p.mass.ToString("R")).Append(',').Append(p.inverseMass.ToString("R"))
                  .Append(',').Append(p.damping.ToString("R")).Append(',').Append(p.pinned ? "1" : "0");
            }
            foreach (Spring s in system.Springs)
            {
                sb.Append('/').Append(s.a.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',').Append(s.b.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                  .Append(s.restLength.ToString("R")).Append(',').Append(s.stiffness.ToString("R")).Append(',')
                  .Append(s.damping.ToString("R"));
            }
            return sb.ToString();
        }

        private static void AppendVec(System.Text.StringBuilder sb, Vector3 v)
        {
            sb.Append('|').Append(v.x.ToString("R")).Append(',').Append(v.y.ToString("R")).Append(',').Append(v.z.ToString("R"));
        }

        private static void AppendNum(System.Text.StringBuilder sb, float value)
        {
            sb.Append('|').Append(value.ToString("R"));
        }

        private static void AppendNum(System.Text.StringBuilder sb, int value)
        {
            // "R" 只对浮点类型合法，int 用它会在运行期抛 FormatException
            sb.Append('|').Append(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        private static bool IsFinite(Vector3 v)
        {
            return !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z)
                && !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);
        }
    }
}
