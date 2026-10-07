// Copyright (c) 2026 PhysicsSimulation. MIT License.
using System;
using UnityEngine;

namespace PhysicsSimulation
{
    /// <summary>一份流体初始条件：位置 + 速度（SoA，长度都是 <see cref="Count"/>）。</summary>
    public struct FluidParticleSet
    {
        public Vector3[] Positions;
        public Vector3[] Velocities;
        public int Count;
    }

    /// <summary>
    /// 流体点阵生成。流体没有"源网格"，初始条件就是一堆按间距排开的粒子，
    /// 而粒子质量 m = ρ0·d³ 又是从同一个 d 推出来的 —— 所以这一步决定了一开场
    /// 密度是不是≈静止密度：差得远的话水会自己炸开或者自己塌下去，跟外力无关。
    ///
    /// 三条硬约定：
    ///  * 点全部落在请求的尺寸**内部**（半格内缩，不压边界）；
    ///  * 生成顺序是 x→y→z 三重循环，**不用任何哈希/集合遍历** ⇒ 同参数逐位可复现；
    ///  * 规模超过 <see cref="MaxParticleCount"/> 直接拒绝，而不是把编辑器 OOM 掉。
    /// </summary>
    public static class FluidVolume
    {
        /// <summary>单次生成的粒子数上限（约 20 万 ⇒ 位置+速度数组约 5 MB，再大就不是"演示"了）。</summary>
        public const int MaxParticleCount = 200000;

        /// <summary>从原点开始的长方体点阵，底面贴 y=0。</summary>
        public static FluidParticleSet Box(Vector3 size, float spacing)
        {
            return BoxAt(Vector3.zero, size, spacing);
        }

        /// <summary>指定最小角的长方体点阵。</summary>
        public static FluidParticleSet BoxAt(Vector3 min, Vector3 size, float spacing)
        {
            RequirePositiveSpacing(spacing);
            RequirePositiveSize(size);

            int nx = Mathf.CeilToInt(size.x / spacing);
            int ny = Mathf.CeilToInt(size.y / spacing);
            int nz = Mathf.CeilToInt(size.z / spacing);
            RequireFits(nx * (long)ny * nz);

            var set = Allocate(nx * ny * nz);
            int w = 0;
            for (int i = 0; i < nx; i++)
            {
                float x = min.x + (i + 0.5f) * spacing;
                if (x > min.x + size.x) x = min.x + size.x - 0.25f * spacing;
                for (int j = 0; j < ny; j++)
                {
                    float y = min.y + (j + 0.5f) * spacing;
                    if (y > min.y + size.y) y = min.y + size.y - 0.25f * spacing;
                    for (int k = 0; k < nz; k++)
                    {
                        float z = min.z + (k + 0.5f) * spacing;
                        if (z > min.z + size.z) z = min.z + size.z - 0.25f * spacing;
                        set.Positions[w] = new Vector3(x, y, z);
                        w++;
                    }
                }
            }
            return set;
        }

        /// <summary>
        /// 经典溃坝（dam break）：水柱贴在 x=0 的"坝"上，底面贴 y=0，z 方向居中。
        /// 坝一撤，水只靠重力自己摊开 —— 这是流体最省参数的正确性演示。
        /// </summary>
        public static FluidParticleSet DamBreak(float width, float height, float depth, float spacing)
        {
            RequirePositiveSpacing(spacing);
            RequirePositiveSize(new Vector3(width, height, depth));

            var min = new Vector3(0f, 0f, -depth * 0.5f);
            return BoxAt(min, new Vector3(width, height, depth), spacing);
        }

        /// <summary>球状水体：只保留落在半径内的格点（含边界）。</summary>
        public static FluidParticleSet Sphere(Vector3 center, float radius, float spacing)
        {
            RequirePositiveSpacing(spacing);
            if (!(radius > 0f) || float.IsInfinity(radius))
                throw new ArgumentOutOfRangeException("radius", "球半径 radius 必须是正的有限值");

            int n = Mathf.CeilToInt(radius / spacing);
            RequireFits((2 * n + 1) * (long)(2 * n + 1) * (2 * n + 1));

            // 先数一遍再分配：避免中途 List 扩容带来的不确定性分配
            int count = 0;
            float r2 = radius * radius;
            for (int i = -n; i <= n; i++)
                for (int j = -n; j <= n; j++)
                    for (int k = -n; k <= n; k++)
                        if (PointInside(center, i, j, k, spacing, r2)) count++;

            var set = Allocate(count);
            int w = 0;
            for (int i = -n; i <= n; i++)
                for (int j = -n; j <= n; j++)
                    for (int k = -n; k <= n; k++)
                        if (PointInside(center, i, j, k, spacing, r2))
                        {
                            set.Positions[w] = Point(center, i, j, k, spacing);
                            w++;
                        }
            return set;
        }

        static bool PointInside(Vector3 center, int i, int j, int k, float spacing, float r2)
        {
            var p = Point(center, i, j, k, spacing);
            return (p - center).sqrMagnitude <= r2;
        }

        static Vector3 Point(Vector3 center, int i, int j, int k, float spacing)
        {
            return center + new Vector3(i * spacing, j * spacing, k * spacing);
        }

        static FluidParticleSet Allocate(int count)
        {
            return new FluidParticleSet
            {
                Positions = new Vector3[count],
                Velocities = new Vector3[count],      // 初速一律为零：流体该靠外力自己动
                Count = count
            };
        }

        static void RequirePositiveSpacing(float spacing)
        {
            if (!(spacing > 0f) || float.IsInfinity(spacing))
                throw new ArgumentOutOfRangeException("spacing",
                    "粒子间距 spacing 必须是正的有限值（它同时决定粒子质量和粒子数）");
        }

        static void RequirePositiveSize(Vector3 size)
        {
            if (!(size.x > 0f) || !(size.y > 0f) || !(size.z > 0f)
                || float.IsInfinity(size.x) || float.IsInfinity(size.y) || float.IsInfinity(size.z))
                throw new ArgumentOutOfRangeException("size", "水体的三个维度都必须是正的有限值");
        }

        static void RequireFits(long count)
        {
            if (count > MaxParticleCount)
                throw new ArgumentOutOfRangeException("spacing",
                    "按这个间距要生成 " + count + " 个粒子，超过上限 " + MaxParticleCount
                    + "；请把 particleSpacing 调大（粒子数随间距三次方增长）");
        }
    }
}
