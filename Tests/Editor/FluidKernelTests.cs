// Copyright (c) 2026 PhysicsSimulation. MIT License.
//
// v1.5.0 流体（PBF）核函数测试。
//
// 为什么先测核函数：整套 PBF 只有三个标量核（poly6 / spiky 梯度 / 粘度拉普拉斯），
// 它们各自有**闭式归一化常数**，写错一个字母就会让密度整体偏一个倍数，
// 而这种错在"看画面"上是完全看不出来的（水还是会流），只能靠积分恒等式钉住。
//
// Given/When/Then 里的数字全部来自 Macklin & Müller, "Position Based Fluids" (SIGGRAPH 2013)
// 与 Muller et al. 2003 的核表；归一化用数值积分独立复核，不引用实现里的常数。

using System;
using NUnit.Framework;
using UnityEngine;

namespace PhysicsSimulation.Editor.Tests
{
    [TestFixture]
    public class FluidKernelTests
    {
        const float H = 0.1f;                 // 支持半径
        const float Pi = 3.14159265358979f;

        static bool Near(float actual, float expected, float rel)
        {
            float tol = Mathf.Max(1e-6f, Mathf.Abs(expected) * rel);
            return Mathf.Abs(actual - expected) <= tol;
        }

        [Test]
        public void Poly6_AtZero_MatchesClosedFormNormalization()
        {
            // Given: 支持半径 h = 0.1
            // When:  r = 0
            float w = FluidKernel.Poly6(0f, H);

            // Then:  W(0) = 315 / (64 π h⁹) · (h²)³ = 315 / (64 π h³)
            float expected = 315f / (64f * Pi * H * H * H);
            Assert.IsTrue(Near(w, expected, 1e-4f),
                "poly6 峰值应为 " + expected + "，实际 " + w + "（归一化常数写错时画面照样'像水'，只有这里能看出来）");
        }

        [Test]
        public void Poly6_IntegratesToOneOverItsSupport()
        {
            // Given:  三维各向同性 ⇒ ∫₀^h W(r) 4πr² dr 必须等于 1
            // When:   20 万片黎曼和（不用实现里的任何常数）
            const int slices = 200000;
            float dr = H / slices;
            float sum = 0f;
            for (int i = 0; i < slices; i++)
            {
                float r = (i + 0.5f) * dr;
                sum += FluidKernel.Poly6(r * r, H) * 4f * Pi * r * r * dr;
            }

            // Then:  误差 < 1e-3
            Assert.AreEqual(1f, sum, 1e-3f,
                "poly6 的体积积分 = " + sum + "，不是 1 ⇒ 密度会整体偏 " + sum + " 倍");
        }

        [Test]
        public void Poly6_IsExactlyZeroAtAndBeyondSupportRadius()
        {
            // Then:  r = h 与 r > h 都必须是 0（邻居表按 h 建，核在边界必须连续归零，否则粒子进出邻居表会跳变）
            Assert.AreEqual(0f, FluidKernel.Poly6(H * H, H), "r=h 处 poly6 没归零");
            Assert.AreEqual(0f, FluidKernel.Poly6(1.44f * H * H, H), "r=1.2h 处 poly6 应为 0");
            Assert.AreEqual(0f, FluidKernel.Poly6(100f, H), "远超支持半径时 poly6 应为 0");
        }

        [Test]
        public void Poly6_DecaysMonotonicallyWithDistance()
        {
            // When/Then: 从 0 到 h 严格单调下降
            float previous = float.MaxValue;
            for (int i = 0; i <= 10; i++)
            {
                float r = H * i / 10f;
                float w = FluidKernel.Poly6(r * r, H);
                Assert.LessOrEqual(w, previous + 1e-6f, "poly6 在 r=" + r + " 处不是单调的");
                previous = w;
            }
        }

        [Test]
        public void SpikyGradient_HasClosedFormMagnitudeAndPointsAwayFromNeighbour()
        {
            // Given: 粒子 i 在 (0.03,0,0)，j 在原点 ⇒ delta = xi - xj
            var delta = new Vector3(0.03f, 0f, 0f);
            float r = delta.magnitude;

            // When
            var grad = FluidKernel.SpikyGradient(delta, r, H);

            // Then: |∇W| = 45/(π h⁶) (h-r)² / r · r = 45/(π h⁶)(h-r)²，方向沿 delta
            float expectedMagnitude = 45f / (Pi * Mathf.Pow(H, 6)) * (H - r) * (H - r);
            Assert.IsTrue(Near(grad.magnitude, expectedMagnitude, 1e-3f),
                "spiky 梯度模长应为 " + expectedMagnitude + "，实际 " + grad.magnitude);
            // ∇W 指向"W 增大的方向"，也就是从 i 指向 j ⇒ 与 delta = xi−xj **反向**。
            // PBF 里压缩时 Δλ 也是负的，两个负号相乘才把粒子推开；这里翻一次符号，水就自己吸成一坨。
            Assert.Less(Vector3.Dot(grad.normalized, delta.normalized), -0.999f,
                "spiky 梯度方向应从 i 指向 j（W 增大的方向），即与 delta 反向");
        }

        [Test]
        public void SpikyGradient_VanishesAtSupportRadius()
        {
            // Then: r = h 处梯度为 0（与 poly6 同一支撑边界）
            var grad = FluidKernel.SpikyGradient(new Vector3(H, 0f, 0f), H, H);
            Assert.AreEqual(0f, grad.magnitude, 1e-5f, "r=h 处 spiky 梯度应为 0");
        }

        [Test]
        public void ViscosityLaplacian_IsPositiveAndClosedForm()
        {
            // Then: ∇²W_visc = 45/(π h⁶)(h - r)，在 r=0 取最大、在 r=h 归零、全程非负
            float atZero = FluidKernel.ViscosityLaplacian(0f, H);
            float atHalf = FluidKernel.ViscosityLaplacian(0.5f * H, H);
            float atEdge = FluidKernel.ViscosityLaplacian(H, H);

            Assert.IsTrue(Near(atZero, 45f / (Pi * Mathf.Pow(H, 6)) * H, 1e-3f),
                "粘度拉普拉斯在 r=0 应为 " + 45f / (Pi * Mathf.Pow(H, 6)) * H + "，实际 " + atZero);
            Assert.Greater(atZero, atHalf, "粘度拉普拉斯应随距离下降");
            Assert.AreEqual(0f, atEdge, 1e-5f, "r=h 处粘度拉普拉斯应为 0");
            Assert.Greater(atHalf, 0f, "粘度拉普拉斯为负会让 XSPH 变成'反粘度'（越算越抖）");
        }

        [Test]
        public void Kernels_ScaleAsInverseCubeOfSupportRadius()
        {
            // Given:  同比例放大 h ⇒ W 必须按 1/h³ 缩（质量守恒要求核函数是 δ 的近似）
            float wSmall = FluidKernel.Poly6(0f, 0.1f);
            float wLarge = FluidKernel.Poly6(0f, 0.2f);

            // Then:  h 翻倍 ⇒ 峰值变 1/8
            Assert.IsTrue(Near(wLarge * 8f, wSmall, 1e-3f),
                "h 从 0.1 到 0.2 时峰值应缩 8 倍，实际 " + wSmall + " → " + wLarge);
        }

        [Test]
        public void Poly6_TakesSquaredDistance_AndRejectsNegativeSilently()
        {
            // Then:  传平方距离（热路径不开方）；负数按 0 处理而不是返回 NaN
            Assert.AreEqual(FluidKernel.Poly6(0f, H), FluidKernel.Poly6(-1f, H), 1e-6f,
                "负平方距离应按 r=0 处理，不能出 NaN");
            Assert.IsFalse(float.IsNaN(FluidKernel.Poly6(-1f, H)), "核函数返回了 NaN");
            Assert.IsFalse(float.IsNaN(FluidKernel.SpikyGradient(Vector3.zero, 0f, H).magnitude),
                "重合粒子的 spiky 梯度出了 NaN（除零没防住）");
        }
    }
}
