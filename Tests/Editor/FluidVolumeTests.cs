// Copyright (c) 2026 PhysicsSimulation. MIT License.
//
// v1.5.0 流体初始化（点阵生成）测试。
//
// 流体没有"源网格"，它的初始条件就是一堆按间距排开的粒子。这一步错了后面全错：
//  * 粒子质量取 ρ0·d³，间距与核半径的关系决定密度是否≈静止密度 —— 见 FluidSimulationTests；
//  * 点阵必须**贴地、在请求的包围盒内、顺序稳定**，否则同一份参数两次运行会给出不同的水。

using System;
using NUnit.Framework;
using UnityEngine;

namespace PhysicsSimulation.Editor.Tests
{
    [TestFixture]
    public class FluidVolumeTests
    {
        [Test]
        public void Box_GeneratesLatticeThatFitsInsideRequestedSize()
        {
            // Given:  1.0 × 0.5 × 0.4 的盒子，间距 0.1
            var set = FluidVolume.Box(new Vector3(1.0f, 0.5f, 0.4f), 0.1f);

            // Then:  每轴 ceil(size/d) 个 ⇒ 10 × 5 × 4 = 200
            Assert.AreEqual(200, set.Count, "点阵数量应为 10*5*4=200，实际 " + set.Count);
            Assert.AreEqual(200, set.Positions.Length);
            Assert.AreEqual(200, set.Velocities.Length);

            // And:  所有点都落在请求的尺寸内。注意粒子是**格心**：第一个点在 d/2=0.05 处，
            //       不是 0 —— 把格心摆到边界上等于把粒子压在碰撞面上，一开始就被皮肤厚度顶一下。
            var b = new Bounds(set.Positions[0], Vector3.zero);
            for (int i = 1; i < set.Count; i++) b.Encapsulate(set.Positions[i]);
            Assert.AreEqual(0.05f, b.min.x, 1e-5f, "x 方向第一个粒子应在半格处，实际 " + b.min.x);
            Assert.GreaterOrEqual(b.min.y, 0f, "底面不该沉到 y<0");
            Assert.AreEqual(0.05f, b.min.y, 1e-5f, "y 方向第一个粒子应在半格处，实际 " + b.min.y);
            Assert.LessOrEqual(b.max.x, 1.0f, "x 方向超出请求尺寸：" + b.max);
            Assert.LessOrEqual(b.max.y, 0.5f, "y 方向超出请求尺寸：" + b.max);
            Assert.LessOrEqual(b.max.z, 0.4f, "z 方向超出请求尺寸：" + b.max);
        }

        [Test]
        public void DamBreak_ColumnStartsAtOrigin_AndBottomRowRestsOnGround()
        {
            // Given:  经典溃坝：宽 0.4、高 1.0、深 0.4，间距 0.1
            var set = FluidVolume.DamBreak(0.4f, 1.0f, 0.4f, 0.1f);

            // Then:  底面贴着 y=d/2（格心离地半格：不算悬空，也不算埋进地里）
            float minY = float.MaxValue, minX = float.MaxValue, maxY = float.MinValue;
            for (int i = 0; i < set.Count; i++)
            {
                minY = Mathf.Min(minY, set.Positions[i].y);
                minX = Mathf.Min(minX, set.Positions[i].x);
                maxY = Mathf.Max(maxY, set.Positions[i].y);
            }
            Assert.AreEqual(0.05f, minY, 1e-4f, "底面应贴在 y=d/2=0.05，实际 " + minY);
            Assert.GreaterOrEqual(minX, 0f - 1e-4f, "水柱应从 x=0 开始（溃坝的'坝'就在 x=0）");
            Assert.Greater(maxY, 0.8f, "高度 1.0 的水柱最高点只到 " + maxY + "，点阵没排满");

            // And:  初速全为零 —— 溃坝靠重力自己动，不该有隐藏初速
            for (int i = 0; i < set.Count; i++)
                Assert.AreEqual(Vector3.zero, set.Velocities[i], "第 " + i + " 个粒子初速不为零");
        }

        [Test]
        public void Sphere_KeepsOnlyPointsInsideTheRadius()
        {
            // Given:  半径 0.3 的球，间距 0.1
            var center = new Vector3(0f, 0.5f, 0f);
            var set = FluidVolume.Sphere(center, 0.3f, 0.1f);

            // Then:  全部在球内（含边界），且不是空的
            Assert.Greater(set.Count, 20, "球内点数太少：" + set.Count);
            for (int i = 0; i < set.Count; i++)
                Assert.LessOrEqual((set.Positions[i] - center).magnitude, 0.3f + 1e-4f,
                    "第 " + i + " 个点跑到球外了");
        }

        [Test]
        public void SameArguments_ProduceBitIdenticalArrays()
        {
            // Then:  顺序与数值都逐位相同（生成顺序是 x→y→z 三重循环，任何 HashSet/字典遍历都会破坏它）
            var a = FluidVolume.Box(new Vector3(0.5f, 0.5f, 0.5f), 0.1f);
            var b = FluidVolume.Box(new Vector3(0.5f, 0.5f, 0.5f), 0.1f);

            Assert.AreEqual(a.Count, b.Count);
            for (int i = 0; i < a.Count; i++)
            {
                Assert.AreEqual(BitConverter.SingleToInt32Bits(a.Positions[i].x),
                                BitConverter.SingleToInt32Bits(b.Positions[i].x),
                    "第 " + i + " 个点 x 逐位不同 ⇒ 生成顺序不稳定");
                Assert.AreEqual(BitConverter.SingleToInt32Bits(a.Positions[i].z),
                                BitConverter.SingleToInt32Bits(b.Positions[i].z));
            }
        }

        [Test]
        public void NonPositiveSizeOrSpacing_IsRejectedWithChineseMessage()
        {
            // Then:  间距 ≤ 0 会直接除爆，尺寸 ≤ 0 会生成空集合 —— 两种都该明确拒绝
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                FluidVolume.Box(new Vector3(1f, 1f, 1f), 0f), "间距为 0 必须抛异常");
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                FluidVolume.Box(new Vector3(1f, 1f, 1f), -0.1f), "负间距必须抛异常");
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                FluidVolume.Box(new Vector3(0f, 1f, 1f), 0.1f), "非正尺寸必须抛异常");

            try { FluidVolume.Box(new Vector3(1f, 1f, 1f), 0f); }
            catch (ArgumentOutOfRangeException e)
            {
                StringAssert.Contains("间距", e.Message, "报错要指名是哪个参数：" + e.Message);
            }
        }

        [Test]
        public void HugeLattice_IsRejectedInsteadOfEatingAllMemory()
        {
            // Given:  1 米见方、间距 0.001 ⇒ 10 亿个点
            // Then:  必须拒绝（有上限），而不是让编辑器 OOM
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                FluidVolume.Box(new Vector3(1f, 1f, 1f), 0.001f), "超大点阵必须拒绝而不是撑爆内存");
        }
    }
}
