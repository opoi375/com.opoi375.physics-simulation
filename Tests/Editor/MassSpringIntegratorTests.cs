// Copyright (c) 2026 PhysicsSimulation. MIT License.
//
// 正式测试：半隐式欧拉积分器（Runtime/MassSpring/）。
// 积分定义（断言据此推导，见 docs/PhysicsSimulation-Package-Prompt.md）：
//   a     = force * inverseMass
//   v_new = (v + a * dt) / (1 + c_global * dt)     // 隐式阻尼：任意 c_global*dt 都不反号
//   v_new = v_new * (1 - c_particle * dt)          // 粒子级阻尼，钳到 [0,1]
//   x_new = x + v_new * dt
// 约定：黑盒测试，只经由 MassSpringSystem 的公共 API；断言消息用中文。

using NUnit.Framework;
using UnityEngine;

namespace PhysicsSimulation.Editor.Tests
{
    /// <summary>
    /// 积分器行为：不受力、重力自由落体、线性阻尼、粒子级阻尼钳制、以及 dt 变小时的收敛性。
    /// </summary>
    [TestFixture]
    public class MassSpringIntegratorTests
    {
        private const float Dt = 1f / 120f;

        // Given 两个不受任何力、重力与阻尼都关闭的质点：一个初速为零，一个带恒定初速度
        // When 步进 7 步
        // Then 静止质点的位置与速度都保持不变；运动质点速度逐位不变、位置按 x = x0 + v * 7 * dt 匀速前进
        [Test]
        public void Step_UnforcedAndUndampedParticle_KeepsPositionAndVelocity()
        {
            var system = new MassSpringSystem();
            system.Parameters.gravity = Vector3.zero;
            system.Parameters.globalDamping = 0f;
            system.Parameters.substeps = 1;
            const int steps = 7;

            int rest = system.AddParticle(new Vector3(1.5f, 2.25f, -3f), 2f);
            int moving = system.AddParticle(new Vector3(-2f, 0f, 5f), 3f);
            Vector3 movingVelocity = new Vector3(0.75f, -1.25f, 4f);
            system.Particles[moving].velocity = movingVelocity;

            Vector3 restPosition = system.Particles[rest].position;
            Vector3 movingPosition = system.Particles[moving].position;

            for (int n = 0; n < steps; n++)
                system.Step(Dt);

            Assert.That(system.Particles[rest].position.x, Is.EqualTo(restPosition.x), "静止质点的 X 坐标不应改变");
            Assert.That(system.Particles[rest].position.y, Is.EqualTo(restPosition.y), "静止质点的 Y 坐标不应改变");
            Assert.That(system.Particles[rest].position.z, Is.EqualTo(restPosition.z), "静止质点的 Z 坐标不应改变");
            Assert.That(system.Particles[rest].velocity, Is.EqualTo(Vector3.zero), "静止质点速度应保持为零");

            Assert.That(system.Particles[moving].velocity.x, Is.EqualTo(movingVelocity.x), "不受力运动质点的 X 速度不应改变");
            Assert.That(system.Particles[moving].velocity.y, Is.EqualTo(movingVelocity.y), "不受力运动质点的 Y 速度不应改变");
            Assert.That(system.Particles[moving].velocity.z, Is.EqualTo(movingVelocity.z), "不受力运动质点的 Z 速度不应改变");
            Vector3 expectedMoving = movingPosition + movingVelocity * (steps * Dt);
            Assert.That(system.Particles[moving].position.x, Is.EqualTo(expectedMoving.x).Within(1e-4f), "X 应按惯性匀速前进");
            Assert.That(system.Particles[moving].position.y, Is.EqualTo(expectedMoving.y).Within(1e-4f), "Y 应按惯性匀速前进");
            Assert.That(system.Particles[moving].position.z, Is.EqualTo(expectedMoving.z).Within(1e-4f), "Z 应按惯性匀速前进");
        }

        // Given 一个只受恒力重力作用的单个自由质点（无弹簧、无阻尼、inverseMass 非零）
        // When 以固定 dt 步进 n 步
        // Then 位置等于半隐式欧拉的离散闭式解 x_n = g * dt^2 * n * (n + 1) / 2（而不是连续解 1/2 * g * t^2）
        [Test]
        public void Step_FreeFallMatchesDiscreteClosedForm()
        {
            const float g = 9.81f;
            const int n = 60;

            var system = new MassSpringSystem();
            system.Parameters.gravity = new Vector3(0f, -g, 0f);
            system.Parameters.globalDamping = 0f;
            system.Parameters.substeps = 1;
            system.Parameters.maxDeltaTime = 1f; // 本场景不触发钳制
            int i = system.AddParticle(Vector3.zero, 3f);

            for (int k = 0; k < n; k++)
                system.Step(Dt);

            float expectedY = -g * Dt * Dt * n * (n + 1) * 0.5f;   // 离散闭式解
            float continuousY = -0.5f * g * (n * Dt) * (n * Dt);    // 连续解，仅用于证明两者不同
            Assert.That(expectedY, Is.Not.EqualTo(continuousY).Within(1e-3f), "前提：离散解与连续解在本步长下可区分");

            Assert.That(system.Particles[i].position.y, Is.EqualTo(expectedY).Within(1e-4f),
                "自由落体 n 步后应命中半隐式欧拉的离散闭式解 g*dt^2*n*(n+1)/2");
            Assert.That(system.Particles[i].position.x, Is.EqualTo(0f).Within(1e-6f), "X 方向不应有位移");
            Assert.That(system.Particles[i].position.z, Is.EqualTo(0f).Within(1e-6f), "Z 方向不应有位移");
            Assert.That(system.Particles[i].velocity.y, Is.EqualTo(-g * Dt * n).Within(1e-4f), "速度应为 g*dt*n");
        }

        // Given 一个有初速度、无外力、只有全局线性阻尼 c_global 的质点
        // When 连续步进 n 步
        // Then 速度按 (1 / (1 + c_global * dt))^n 单调衰减，且方向永不反转
        [Test]
        public void Step_GlobalDamping_DecaysSpeedMonotonicallyWithoutSignFlip()
        {
            const float c = 2f;
            const float dt = 1f / 60f;
            const int n = 30;

            var system = new MassSpringSystem();
            system.Parameters.gravity = Vector3.zero;
            system.Parameters.globalDamping = c;
            system.Parameters.substeps = 1;
            int i = system.AddParticle(Vector3.zero, 1f);
            system.Particles[i].velocity = new Vector3(1f, 0f, 0f);

            float previous = system.Particles[i].velocity.x;
            for (int k = 0; k < n; k++)
            {
                system.Step(dt);
                float current = system.Particles[i].velocity.x;
                Assert.That(current, Is.LessThan(previous), "隐式阻尼下每步速度都应严格变小");
                Assert.That(current, Is.GreaterThan(0f), "隐式阻尼不应让速度反号（显式写法在 c*dt>=1 时会反号）");
                previous = current;
            }

            float expectedVx = 1f * (float)System.Math.Pow(1.0 / (1.0 + c * dt), n);
            Assert.That(system.Particles[i].velocity.x, Is.EqualTo(expectedVx).Within(1e-5f),
                "n 步后速度应等于 (1/(1+c*dt))^n");
        }

        // Given 同一个质点带初速度，粒子级阻尼 c_particle 大到 c_particle * dt 远大于 1
        // When 步进一次
        // Then 阻尼系数被钳到 [0,1]：速度只会被削到 0，不会被反向放大
        [Test]
        public void Step_ParticleDamping_IsClampedAndNeverFlipsSign()
        {
            var system = new MassSpringSystem();
            system.Parameters.gravity = Vector3.zero;
            system.Parameters.globalDamping = 0f;
            system.Parameters.substeps = 1;
            int i = system.AddParticle(Vector3.zero, 1f);
            system.Particles[i].damping = 1000f;            // c_particle * dt = 16.7 >> 1
            system.Particles[i].velocity = new Vector3(1f, -1f, 0f);

            system.Step(1f / 60f);

            Assert.That(system.Particles[i].velocity.x, Is.EqualTo(0f).Within(1e-6f), "钳到 0 后 X 速度应归零");
            Assert.That(system.Particles[i].velocity.y, Is.EqualTo(0f).Within(1e-6f), "钳到 0 后 Y 速度应归零（不得反号）");
            Assert.That(system.Particles[i].position.x, Is.EqualTo(0f).Within(1e-6f), "速度归零后不应再位移");
        }

        // Given 同一个重力自由落体问题，分别用 dt 与 dt / 4 步进到相同物理时刻
        // When 比较两者与连续解 1/2 * g * t^2 的误差
        // Then 误差约缩小到 1/4（一阶收敛）
        [Test]
        public void Step_FreeFallErrorQuartersWhenDtQuarters()
        {
            const float g = 9.81f;
            const float time = 1f;

            float coarse = FreeFallDisplacement(g, 1f / 100f, (int)(time * 100f));
            float fine = FreeFallDisplacement(g, 1f / 400f, (int)(time * 400f));
            float exact = 0.5f * g * time * time;

            float coarseError = Mathf.Abs(coarse - exact);
            float fineError = Mathf.Abs(fine - exact);

            Assert.That(coarseError, Is.GreaterThan(1e-4f), "粗步长应存在可测量的一阶误差");
            float ratio = coarseError / fineError;
            Assert.That(ratio, Is.EqualTo(4f).Within(0.2f), "dt 缩小到 1/4 时误差应约缩小到 1/4（一阶收敛）");
        }

        // 用 dt 步进 n 步后的下落距离（取绝对值，方向恒为 -Y）
        private static float FreeFallDisplacement(float g, float dt, int steps)
        {
            var system = new MassSpringSystem();
            system.Parameters.gravity = new Vector3(0f, -g, 0f);
            system.Parameters.globalDamping = 0f;
            system.Parameters.substeps = 1;
            system.Parameters.maxDeltaTime = 1f;
            int i = system.AddParticle(Vector3.zero, 1f);
            for (int k = 0; k < steps; k++)
                system.Step(dt);
            return Mathf.Abs(system.Particles[i].position.y);
        }
    }
}
