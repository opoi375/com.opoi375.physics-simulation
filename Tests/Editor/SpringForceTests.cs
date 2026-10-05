// Copyright (c) 2026 PhysicsSimulation. MIT License.
//
// 正式测试：弹簧力（Hooke + 沿轴向的相对速度阻尼）。
// 弹簧力定义（dir 为 a→b 的单位向量）：
//   d = x_b - x_a ; l = |d| ; dir = l > eps ? d / l : Vector3.zero
//   F_b = (-k * (l - restLength) - c_spring * ((v_b - v_a) · dir)) * dir
//   F_a = -F_b                                   // 等大反向，动量守恒
// 约定：黑盒测试，只经由 MassSpringSystem 的公共 API；断言消息用中文。

using NUnit.Framework;
using UnityEngine;

namespace PhysicsSimulation.Editor.Tests
{
    /// <summary>
    /// 弹簧力的行为：原长零力、拉伸时等大反向的轴向力、质心守恒、加速度按 inverseMass 分配。
    /// </summary>
    [TestFixture]
    public class SpringForceTests
    {
        // Given 一根两端质点间距恰好等于 restLength、且两端都无初速度的弹簧
        // When 施加力（ApplyForces）
        // Then 两端质点累积到的力都是零向量
        [Test]
        public void ApplyForces_AtRestLength_ProducesZeroForceOnBothEnds()
        {
            var system = new MassSpringSystem();
            system.Parameters.gravity = Vector3.zero;
            int a = system.AddParticle(Vector3.zero, 1f);
            int b = system.AddParticle(new Vector3(1f, 0f, 0f), 1f);
            system.AddSpring(a, b, restLength: 1f, stiffness: 10f, damping: 0f);

            system.ApplyForces();

            Assert.That(system.Particles[a].force, Is.EqualTo(Vector3.zero), "原长时 a 端受力应为零向量");
            Assert.That(system.Particles[b].force, Is.EqualTo(Vector3.zero), "原长时 b 端受力应为零向量");
        }

        // Given 一根被沿对角方向拉伸（长度大于 restLength）的弹簧
        // When 施加力（ApplyForces）
        // Then 两端受力大小相等、方向相反，且都严格落在弹簧轴向（45 度对角线）上
        [Test]
        public void ApplyForces_StretchedSpring_ProducesEqualOppositeAxialForces()
        {
            const float k = 10f;
            var system = new MassSpringSystem();
            system.Parameters.gravity = Vector3.zero;
            int a = system.AddParticle(Vector3.zero, 1f);
            int b = system.AddParticle(new Vector3(1f, 1f, 0f), 1f);   // 长度 sqrt(2)
            system.AddSpring(a, b, restLength: 1f, stiffness: k, damping: 0f);

            system.ApplyForces();

            Vector3 forceA = system.Particles[a].force;
            Vector3 forceB = system.Particles[b].force;

            // b 被拉回 a：F_b 应指向 -（1,1,0)/sqrt(2)；a 受力方向相反，且都在轴线上
            Assert.That(forceA.x, Is.EqualTo(2.9289322f).Within(1e-4f), "a 端沿轴向被拉向 b，X 分量应为 +k*(l-rest)/sqrt(2)");
            Assert.That(forceA.y, Is.EqualTo(forceA.x).Within(1e-6f), "a 端受力应与 X 分量相等（严格 45 度轴向）");
            Assert.That(forceA.z, Is.EqualTo(0f).Within(1e-6f), "a 端受力不应有垂直于弹簧轴线的分量");
            Assert.That(forceB.x, Is.EqualTo(-forceA.x).Within(1e-6f), "b 端与 a 端 X 分量应等大反向");
            Assert.That(forceB.y, Is.EqualTo(-forceA.y).Within(1e-6f), "b 端与 a 端 Y 分量应等大反向");
            Assert.That(forceB.z, Is.EqualTo(-forceA.z).Within(1e-6f), "b 端与 a 端 Z 分量应等大反向");
            Assert.That(forceA.magnitude, Is.EqualTo(forceB.magnitude).Within(1e-5f), "两端受力大小应相等（动量守恒的前提）");
        }

        // Given 一根被拉伸的弹簧连接两个质点，除弹簧力外没有任何外力（重力关闭）
        // When 步进足够多步
        // Then 两端互相靠近，并且质量加权质心位置保持不变
        [Test]
        public void Step_StretchedSpring_PullsEndsTogetherAndKeepsCenterOfMass()
        {
            var system = new MassSpringSystem();
            system.Parameters.gravity = Vector3.zero;
            system.Parameters.globalDamping = 0f;
            system.Parameters.substeps = 8;
            int a = system.AddParticle(new Vector3(-1f, 0f, 0f), 1f);
            int b = system.AddParticle(new Vector3(1f, 0f, 0f), 1f);
            system.AddSpring(a, b, restLength: 1f, stiffness: 10f, damping: 0f);

            const float dt = 1f / 60f;
            for (int n = 0; n < 20; n++)
            {
                system.Step(dt);
                Vector3 center = (system.Particles[a].position + system.Particles[b].position) * 0.5f;
                Assert.That(center.magnitude, Is.LessThan(1e-4f), "第 " + n + " 步：无外力时质量加权质心不应漂移");
            }

            float distance = Vector3.Distance(system.Particles[a].position, system.Particles[b].position);
            Assert.That(distance, Is.LessThan(1.9f), "拉伸的弹簧应让两端互相靠近（初始间距 2.0）");
            Assert.That(distance, Is.GreaterThan(0f), "两端不应重合（数值异常）");
        }

        // Given 两个质量不同（1kg 与 4kg）的质点由一根被拉伸的弹簧相连，无外力
        // When 施加力并推进一步
        // Then 两端加速度大小按 inverseMass 之比（1/m）分配，系统总动量仍为零
        [Test]
        public void Step_DifferentMasses_DistributeAccelerationByInverseMassAndKeepMomentumZero()
        {
            var system = new MassSpringSystem();
            system.Parameters.gravity = Vector3.zero;
            system.Parameters.globalDamping = 0f;
            system.Parameters.substeps = 1;
            int light = system.AddParticle(Vector3.zero, 1f);
            int heavy = system.AddParticle(new Vector3(2f, 0f, 0f), 4f);
            system.AddSpring(light, heavy, restLength: 1f, stiffness: 10f, damping: 0f);

            system.ApplyForces();
            float accelLight = system.Particles[light].force.x * system.Particles[light].inverseMass;
            float accelHeavy = system.Particles[heavy].force.x * system.Particles[heavy].inverseMass;

            Assert.That(accelLight, Is.EqualTo(10f).Within(1e-4f), "1kg 端加速度应为 F/m = 10/1");
            Assert.That(accelHeavy, Is.EqualTo(-2.5f).Within(1e-4f), "4kg 端加速度应为 -F/m = -10/4");
            Assert.That(Mathf.Abs(accelLight) / Mathf.Abs(accelHeavy), Is.EqualTo(4f).Within(1e-3f),
                "加速度大小应按质量的反比分配");

            system.Step(1f / 1000f);
            Vector3 momentum = system.Particles[light].velocity * system.Particles[light].mass
                             + system.Particles[heavy].velocity * system.Particles[heavy].mass;
            Assert.That(momentum.x, Is.EqualTo(0f).Within(1e-5f), "内力不应改变系统总动量");
            Assert.That(momentum.y, Is.EqualTo(0f).Within(1e-6f), "Y 方向总动量应为零");
        }
    }
}
