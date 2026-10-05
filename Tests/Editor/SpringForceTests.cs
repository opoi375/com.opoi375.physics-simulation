// Copyright (c) 2026 PhysicsSimulation. MIT License.
//
// BDD 行为骨架：弹簧力（Hooke + 沿轴向的相对速度阻尼）。
// 本文件只有 Given/When/Then 行为注释与空函数体，**没有任何断言**。
// 目标 API（阶段 D 实现）：PhysicsSimulation.Spring（a / b / restLength / stiffness k / damping c_spring）
// 与 MassSpringSystem.ApplyForces() / Step(dt)。
// 弹簧力定义（dir 为 a→b 的单位向量）：
//   d = x_b - x_a ; l = |d| ; dir = l > eps ? d / l : Vector3.zero
//   F_b = (-k * (l - restLength) - c_spring * ((v_b - v_a) · dir)) * dir
//   F_a = -F_b                                   // 等大反向，动量守恒

using NUnit.Framework;

namespace PhysicsSimulation.Editor.Tests
{
    /// <summary>
    /// 弹簧力的行为：原长时为零力、拉伸时为等大反向的轴向力、系统质心与总动量的守恒性。
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
            // 待用户确认后在阶段 C 实现
        }

        // Given 一根被沿 +X 方向拉伸（长度大于 restLength）的弹簧
        // When 施加力（ApplyForces）
        // Then 两端受力大小相等、方向相反，且都严格落在弹簧轴向（+X / -X）上
        [Test]
        public void ApplyForces_StretchedSpring_ProducesEqualOppositeAxialForces()
        {
            // 待用户确认后在阶段 C 实现
        }

        // Given 一根被拉伸的弹簧连接两个质点，除弹簧力外没有任何外力（重力关闭）
        // When 步进足够多步
        // Then 两端互相靠近，并且质量加权质心位置保持不变
        [Test]
        public void Step_StretchedSpring_PullsEndsTogetherAndKeepsCenterOfMass()
        {
            // 待用户确认后在阶段 C 实现
        }

        // Given 两个质量不同（m1 = 1kg、m2 = 4kg）的质点由一根被拉伸的弹簧相连，无外力
        // When 施加力并推进一步
        // Then 两端加速度大小按 inverseMass 之比（1/m）分配，系统总动量仍为零
        [Test]
        public void Step_DifferentMasses_DistributeAccelerationByInverseMassAndKeepMomentumZero()
        {
            // 待用户确认后在阶段 C 实现
        }
    }
}
