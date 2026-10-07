// Copyright (c) 2026 PhysicsSimulation. MIT License.
//
// v1.5.0 流体邻居搜索测试。
//
// 邻居表是整套 PBF 里最容易"错了但看不出来"的一环：漏一个邻居只是少一点压力，
// 画面依然像水；多一个邻居也不会崩。所以这里不测"看起来对不对"，只测**与 O(n²) 暴力枚举逐粒子等价**，
// 以及三条结构性契约（不含自身、对称、可复现）。
//
// 实现走的是与软体焊接同一套思路：均匀空间哈希、cell = h、27 邻域探查。
// 之所以 cell 取 h 而不是 2h：邻居关系要求"距离 ≤ h"，cell = h 时 27 格恰好覆盖且不多查，
// 每格平均点数 ≈ 4πh³/3 / h³ · 填充率，比 2h 的 8 倍体积少一个数量级的候选。

using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace PhysicsSimulation.Editor.Tests
{
    [TestFixture]
    public class FluidNeighborSearchTests
    {
        const float H = 0.1f;

        [Test]
        public void Build_MatchesBruteForce_OnRandomCloud()
        {
            // Given:  固定种子撒 400 个粒子（种子固定 ⇒ 失败可复现）
            UnityEngine.Random.InitState(20260105);
            int n = 400;
            var positions = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                positions[i] = new Vector3(
                    UnityEngine.Random.Range(-0.5f, 0.5f),
                    UnityEngine.Random.Range(0f, 0.5f),
                    UnityEngine.Random.Range(-0.5f, 0.5f));
            }

            var search = new FluidNeighborSearch(n);

            // When
            search.Build(positions, n, H);

            // Then:  每个粒子的邻居集合与暴力枚举**完全一致**（不多不少）
            for (int i = 0; i < n; i++)
            {
                var expected = new HashSet<int>();
                float h2 = H * H;
                for (int j = 0; j < n; j++)
                {
                    if (j == i) continue;
                    if ((positions[i] - positions[j]).sqrMagnitude <= h2) expected.Add(j);
                }

                var actual = new HashSet<int>();
                for (int slot = 0; slot < search.Degree(i); slot++)
                    actual.Add(search.NeighborAt(i, slot));

                Assert.IsTrue(SameSets(expected, actual),
                    "粒子 " + i + " 的邻居表与暴力枚举不一致：暴力 " + expected.Count + " 个，实际 " + actual.Count
                    + " 个（漏邻居=少压力，多邻居=假斥力，画面上都看不出来）");
            }
        }

        [Test]
        public void NeighborList_ExcludesSelf_IsSymmetric_AndCountsPairsOnce()
        {
            // Given:  三个两两相邻的粒子
            var positions = new[]
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(0.05f, 0f, 0f),
                new Vector3(0f, 0.05f, 0f)
            };
            var search = new FluidNeighborSearch(3);

            // When
            search.Build(positions, 3, H);

            // Then:  自己的列表里不含自己
            for (int i = 0; i < 3; i++)
                for (int slot = 0; slot < search.Degree(i); slot++)
                    Assert.AreNotEqual(i, search.NeighborAt(i, slot), "粒子 " + i + " 的邻居表里出现了自己");

            // And:  对称 —— j 在 i 的表里 ⇔ i 在 j 的表里
            for (int i = 0; i < 3; i++)
                for (int slot = 0; slot < search.Degree(i); slot++)
                {
                    int j = search.NeighborAt(i, slot);
                    Assert.IsTrue(Contains(search, j, i), "邻居关系不对称：" + i + "→" + j + " 有，反向没有");
                }

            // And:  每对只计一次（密度循环要靠这个数分配工作量）
            Assert.AreEqual(3, search.TotalPairs, "三对邻居应计 3，实际 " + search.TotalPairs);
        }

        [Test]
        public void BoundaryConvention_CannotChangeResultsBecauseKernelIsZeroAtH()
        {
            // Given:  一对粒子分别放在 0.99h 与 1.01h —— 边界内外各一
            var inside = new[] { Vector3.zero, new Vector3(0.99f * H, 0f, 0f) };
            var outside = new[] { Vector3.zero, new Vector3(1.01f * H, 0f, 0f) };
            var search = new FluidNeighborSearch(2);

            // Then:  内收外排（具体收不收 r=h 无所谓，因为核在 r=h 处为 0，见 FluidKernelTests）
            search.Build(inside, 2, H);
            Assert.AreEqual(1, search.Degree(0), "0.99h 的两个粒子应互为邻居");
            search.Build(outside, 2, H);
            Assert.AreEqual(0, search.Degree(0), "1.01h 的两个粒子不该互为邻居");
            Assert.AreEqual(0f, FluidKernel.Poly6(H * H, H), "边界约定之所以安全，前提是核在 r=h 处为 0");
        }

        [Test]
        public void Degree_MatchesLatticeCombinatorics()
        {
            // Given:  3×3×3 点阵，间距 d=0.05，h=0.1 ⇒ 中心粒子到 26 个邻居的距离都 ≤ 0.0866 < h，
            //         而次近邻（轴向 2d=0.1）正好在边界外（0.1 > 0.0999...）
            float d = 0.05f;
            var positions = new List<Vector3>();
            for (int x = -1; x <= 1; x++)
                for (int y = -1; y <= 1; y++)
                    for (int z = -1; z <= 1; z++)
                        positions.Add(new Vector3(x * d, y * d, z * d));
            int center = 13;                                     // (0,0,0) 是第 14 个
            var search = new FluidNeighborSearch(positions.Count);

            // When
            search.Build(positions.ToArray(), positions.Count, 0.099f);

            // Then:  26 个（27 减去自己），一个不多一个不少
            Assert.AreEqual(26, search.Degree(center), "3×3×3 点阵中心粒子的邻居数不对");

            // 角上的粒子（(-d,-d,-d)）只看得见自己那一坨 2×2×2 = 7 个邻居，
            // 所以"总对数 = 27×26/2"是错的（我先写错了一次）；正确的不变量是自洽：
            Assert.AreEqual(7, search.Degree(0), "角上粒子的邻居数应为 7，实际 " + search.Degree(0));
            int sumDegrees = 0;
            for (int i = 0; i < positions.Count; i++) sumDegrees += search.Degree(i);
            Assert.AreEqual(2 * search.TotalPairs, sumDegrees,
                "有向条目必须正好是每对两次：Σdegree=" + sumDegrees + "，TotalPairs=" + search.TotalPairs);
        }

        [Test]
        public void Build_IsDeterministic_AcrossRepeatedCalls()
        {
            // Given:  同一批位置（含大量同格点）
            UnityEngine.Random.InitState(777);
            int n = 300;
            var positions = new Vector3[n];
            for (int i = 0; i < n; i++)
                positions[i] = new Vector3(UnityEngine.Random.Range(0f, 0.2f),
                                            UnityEngine.Random.Range(0f, 0.2f),
                                            UnityEngine.Random.Range(0f, 0.2f));

            var a = new FluidNeighborSearch(n);
            var b = new FluidNeighborSearch(n);

            // When:  分别构建
            a.Build(positions, n, H);
            b.Build(positions, n, H);

            // Then:  邻居序列逐个相同（顺序也算）—— 求解器要能复现，回放/联网同步才有意义
            for (int i = 0; i < n; i++)
            {
                Assert.AreEqual(a.Degree(i), b.Degree(i), "第 " + i + " 个粒子的邻居数在两次构建之间变了");
                for (int slot = 0; slot < a.Degree(i); slot++)
                    Assert.AreEqual(a.NeighborAt(i, slot), b.NeighborAt(i, slot),
                        "第 " + i + " 个粒子第 " + slot + " 个邻居在两次构建之间变了（哈希遍历顺序不稳定）");
            }
        }

        [Test]
        public void RepeatedBuilds_DoNotAllocateSteadily()
        {
            // Given:  1000 个粒子的稳定规模
            int n = 1000;
            var positions = new Vector3[n];
            for (int i = 0; i < n; i++)
                positions[i] = new Vector3(i % 10 * 0.04f, (i / 10) % 10 * 0.04f, i / 100 * 0.04f);
            var search = new FluidNeighborSearch(n);
            search.Build(positions, n, H);           // 先热身，把一次性分配算掉

            // When:  再连续构建 300 次
            long before = GC.GetTotalMemory(false);
            for (int k = 0; k < 300; k++) search.Build(positions, n, H);
            long after = GC.GetTotalMemory(false);

            // Then:  增量必须远小于"每帧重建对象"的量级（每帧 new 一个 int[27000] × 300 就是几十 MB）
            Assert.Less(after - before, 4L * 1024 * 1024,
                "300 次构建涨了 " + (after - before) / 1024 + " KB ⇒ 邻居表在每帧重新分配，流体一跑就卡 GC");
        }

        [Test]
        public void Grow_AcceptsMoreParticles_AndKeepsWorking()
        {
            // Given:  容量 4，实际来了 64 个
            var search = new FluidNeighborSearch(4);
            var positions = new Vector3[64];
            for (int i = 0; i < 64; i++)
                positions[i] = new Vector3(i * 0.01f, 0f, 0f);

            // When:  扩容后构建
            search.Grow(64);
            search.Build(positions, 64, H);

            // Then:  容量与结果都对
            Assert.GreaterOrEqual(search.Capacity, 64, "Grow 之后容量仍不够");
            Assert.Greater(search.Degree(32), 0, "扩容后邻居没建出来");
        }

        [Test]
        public void NullAndEmptyInputs_AreHandledWithoutThrowing()
        {
            // Then:  空输入不抛、度为 0
            var search = new FluidNeighborSearch(8);
            Assert.AreEqual(0, search.Build(new Vector3[0], 0, H), "空集合应返回 0 对");
            Assert.AreEqual(0, search.Degree(0));
            Assert.AreEqual(0, search.Build(null, 0, H), "null 位置数组应安全返回 0");
        }

        static bool SameSets(HashSet<int> a, HashSet<int> b)
        {
            if (a.Count != b.Count) return false;
            foreach (int v in a) if (!b.Contains(v)) return false;
            return true;
        }

        static bool Contains(FluidNeighborSearch search, int i, int j)
        {
            for (int slot = 0; slot < search.Degree(i); slot++)
                if (search.NeighborAt(i, slot) == j) return true;
            return false;
        }
    }
}
