// Copyright (c) 2026 PhysicsSimulation. MIT License.
using System;
using UnityEngine;

namespace PhysicsSimulation
{
    /// <summary>
    /// PBF 用到的三个 SPH 核函数（三维、各向同性、紧支撑于 r ≤ h）。
    ///
    /// 常数取自 Macklin &amp; Müller, "Position Based Fluids" (SIGGRAPH 2013) 与
    /// Muller et al. 2003 的核表：
    /// <code>
    ///   poly6(r,h)      = 315 / (64 π h⁹) · (h² − r²)³
    ///   spiky(r,h)      = 15  / (π h⁶)    · (h − r)³        → ∇ = −45/(π h⁶)(h−r)² · δ/r
    ///   viscosity ∇²W   = 45  / (π h⁶)    · (h − r)
    /// </code>
    /// 三个核都在 r = h 处归零，所以"邻居表按 r ≤ h 收，核在边界为 0"这条约定是安全的：
    /// 边界内收还是外收都不会造成力的跳变（见 FluidNeighborSearchTests）。
    ///
    /// 注意 <see cref="Poly6"/> 收的是**平方距离**（热路径不开方），
    /// 而 <see cref="SpikyGradient"/> / <see cref="ViscosityLaplacian"/> 收的是已经开过方的 r。
    /// 求解器的热循环请用 <see cref="FluidKernelSet"/>（系数只算一次），这里的静态版本是"定义参照"，
    /// 测试断言的是它，<see cref="FluidKernelSet"/> 必须与它逐位一致。
    /// </summary>
    public static class FluidKernel
    {
        public const float Pi = 3.14159265358979f;

        /// <summary>重合粒子的最小距离阈值：低于它方向没有意义，直接给零向量，绝不出 NaN。</summary>
        public const float MinDistance = 1e-8f;

        /// <summary>poly6 归一化常数 315/(64 π h⁹)。h⁹ 少乘一个 h 就是 h⁷ —— 密度会整体偏 h² 倍。</summary>
        public static float Poly6Coefficient(float h)
        {
            float h2 = h * h;
            return 315f / (64f * Pi * h2 * h2 * h2 * h2 * h);
        }

        /// <summary>spiky 梯度前置常数 −45/(π h⁶)（带负号，见 <see cref="SpikyGradient"/>）。</summary>
        public static float SpikyGradientCoefficient(float h)
        {
            float h2 = h * h;
            // h⁶ 而不是 h⁷：h2*h2*h2 = h⁶。写成 h2*h2*h2*h 会让梯度整整大 1/h 倍
            // （h=0.1 时就是 10 倍），λ 又按它平方归一，最后位移小一个量级。
            return -45f / (Pi * h2 * h2 * h2);
        }

        /// <summary>粘度拉普拉斯常数 45/(π h⁶)，恒正（为负就成了"反粘度"，越算越抖）。</summary>
        public static float ViscosityLaplacianCoefficient(float h)
        {
            float h2 = h * h;
            return 45f / (Pi * h2 * h2 * h2);                  // 同上：h⁶
        }

        /// <summary>
        /// poly6 核值。<paramref name="sqrDist"/> 是距离平方；负数按 r=0 处理（不返回 NaN）。
        /// </summary>
        public static float Poly6(float sqrDist, float h)
        {
            float r2 = sqrDist > 0f ? sqrDist : 0f;
            float h2 = h * h;
            float d = h2 - r2;
            if (d <= 0f) return 0f;
            return Poly6Coefficient(h) * d * d * d;
        }

        /// <summary>
        /// spiky 核梯度 ∇_i W(x_i − x_j)。
        /// <paramref name="delta"/> = x_i − x_j，<paramref name="r"/> = |delta|。
        /// 结果指向 **W 增大的方向**（也就是从 i 指向 j，与 delta 反向）；
        /// PBF 的压缩修正里 Δλ 同样是负的，两个负号相乘才把粒子推开。符号在这里翻一次，水就会自己吸成一坨。
        /// </summary>
        public static Vector3 SpikyGradient(Vector3 delta, float r, float h)
        {
            if (r >= h || r < MinDistance) return Vector3.zero;
            float hr = h - r;
            return delta * (SpikyGradientCoefficient(h) * hr * hr / r);
        }

        /// <summary>粘度项拉普拉斯 ∇²W_visc = 45/(π h⁶)(h − r)，恒非负。</summary>
        public static float ViscosityLaplacian(float r, float h)
        {
            if (r >= h || r < 0f) return 0f;
            return ViscosityLaplacianCoefficient(h) * (h - r);
        }
    }

    /// <summary>
    /// 把某个支撑半径 h 的核系数**提前算一次**。
    ///
    /// 为什么需要它：静态 <see cref="FluidKernel"/> 每次求值都要重算归一化常数（含除法与 h⁶/h⁹ 的
    /// 连乘），而 PBF 一个粒子一步要在密度、λ、位置修正三轮遍历里各调若干次核。实测 1000 粒子
    /// 32 ms/步时，这部分是看得见的开销。这里把除法挪到构造时，求值只剩"减、乘、比较"。
    ///
    /// 与静态版本逐位一致：系数用同一个函数取，乘除顺序也照搬（−45/(πh⁶)·(h−r)²/r 再乘 delta），
    /// 重合粒子的守卫也同一个阈值。所以"确定性"用例不会因为它而漂。
    /// </summary>
    public struct FluidKernelSet
    {
        public readonly float H;
        public readonly float H2;
        readonly float _poly6Coeff;
        readonly float _spikyGradCoeff;
        readonly float _viscLapCoeff;

        public FluidKernelSet(float h)
        {
            H = h;
            H2 = h * h;
            _poly6Coeff = FluidKernel.Poly6Coefficient(h);
            _spikyGradCoeff = FluidKernel.SpikyGradientCoefficient(h);
            _viscLapCoeff = FluidKernel.ViscosityLaplacianCoefficient(h);
        }

        /// <summary>poly6(r²)：输入是**平方距离**（省一次开方），超出支撑返回 0。</summary>
        public float Poly6(float sqrDist)
        {
            float d = H2 - (sqrDist > 0f ? sqrDist : 0f);
            if (d <= 0f) return 0f;
            return _poly6Coeff * d * d * d;
        }

        /// <summary>∇W_spiky：方向从 i 指向 j（与 delta = x_i − x_j 反向），重合或越支撑返回零向量。</summary>
        public Vector3 SpikyGradient(Vector3 delta, float dist)
        {
            if (dist >= H || dist < FluidKernel.MinDistance) return Vector3.zero;
            float hr = H - dist;
            return delta * (_spikyGradCoeff * hr * hr / dist);
        }

        public float ViscosityLaplacian(float dist)
        {
            if (dist >= H || dist < 0f) return 0f;
            return _viscLapCoeff * (H - dist);
        }
    }
}
