// Copyright (c) 2026 PhysicsSimulation. MIT License.
//
// 演示水箱的"密封性"测试。这条路径上踩了三个坑，全部是实测钉住的，按时间顺序留在这里：
//
// 根因一（薄板活板门）：盒子代理把埋在体内的质点沿**穿透最浅的那根轴**弹到体外。0.2 m 厚的
//   地板，中线就在上表面下方 0.1 m；而 PBF 单个子步能把水往下压 0.09 m 以上，一旦越过中线，
//   弹出方向就变成**地板下表面** —— 水被从板的另一面挤出去。实测对照（内表面逐位不变，只把板
//   往背离水体的一侧加厚）：薄壁 t=0.2 → 10 步内 44 个粒子穿到板底以下；加厚到 2.5t → 穿板 0 个。
//   当年的处置：壁厚 ×2.5 + 给 `maxSpeed` 上限，把单步位移压到"半个板厚"以内。
//
// 根因二（弹道泼溅）：六面封盖之前，v1.5.0 的水箱故意不开顶（要看得见飞溅）。但 Play 里溃坝
//   浪头直接把水抛过 1.1 m 的墙头，300 帧后包围盒 10.3 × 9.1 × 6.7 m —— 一箱水泼在箱外。
//   处置：加顶盖（`Tank_HasSixWallsFloorSidesAndCeiling`），并把本文件的步数窗口从 10 抬到 300
//   —— 穿板漏水 10 步就能看见，弹道泼溅只有后期才暴露，**时间不够长本身就是一种漏测**。
//
// 根因三（角部传送带 + 背板互推）：加厚之后 300 步仍有 120 个粒子从地板穿出，最早第 16 步，
//   落点 (−0.635, −0.529, −0.964)，速度 3.3→4.1 m/s 递增 = 自由落体。它不是一步跨过去，而是被
//   地板的 −z 边缘横向挤穿，落点同时越过地板与墙的外沿 —— 封缝用的"重叠"做成了一条角部传送带。
//   先试了"给每块板配一块共面背板"：确实 300 步 0 个跑出外框，但水并没有回到桶里 —— 150 个粒子
//   停在墙板靠近外沿内侧 1.4 cm 处（穿透 0.486 m，与 dt 无关：100/400 步、1/60、0.02、1/30 三种
//   dt 数字一模一样），截图里就是贴在桶壁外侧的一条蓝带。背板只是把"漏水"换成"内板与背板互推"。
//
// 现在的根治：**内侧盒子容器代理**（`BoxContainerProxy`）。水必须在盒子里面，越界的每个轴各自
//   钉回内壁 —— 这是凸可行域上的最小位移投影，没有中线、没有"从另一面出去"、也没有两块板互推。
//   六块板退回成纯视觉（只画不碰，顶盖连画都不画）。代理数从 12 降到 1。
using System;
using NUnit.Framework;
using UnityEngine;
using PhysicsSimulation;
using PhysicsSimulation.EditorTools;

namespace PhysicsSimulation.Editor.Tests
{
    public class FluidTankSealTests
    {
        const int Steps = 300;
        const float StepDt = 0.02f;          // 与 Play 里 50 fps 同量级，不靠秒级大 dt 制造问题
        const float Tolerance = 0.02f;       // 2 cm：皮肤厚度（collisionThickness）量级以内的抖动不算漏

        /// <summary>内空（水应该只待在这里头）。地板顶面 y=0，桶口 y=innerHeight。</summary>
        static Bounds TankCavity()
        {
            Vector3 inner = FluidDemoTools.DemoTankInner;
            return new Bounds(new Vector3(0f, inner.y * 0.5f, 0f), inner);
        }

        /// <summary>按演示的同一套常量搭一箱水 + 一只容器代理，不做任何"更好测"的改动。</summary>
        static FluidSimulation BuildDemoTankSimulation()
        {
            FluidParameters parameters = FluidDemoTools.BuildParameters(FluidDemoTools.DemoSpacing);
            FluidParticleSet set = FluidVolume.DamBreak(
                FluidDemoTools.DemoColumnSize.x, FluidDemoTools.DemoColumnSize.y,
                FluidDemoTools.DemoColumnSize.z, parameters.particleSpacing);

            var simulation = new FluidSimulation(parameters, set);
            simulation.SetSimulationToWorld(Matrix4x4.Translate(FluidDemoTools.DemoColumnOffset));
            simulation.Collisions.Add(
                FluidDemoTools.BuildTankContainer(FluidDemoTools.DemoTankInner.x,
                                                  FluidDemoTools.DemoTankInner.y,
                                                  FluidDemoTools.DemoTankInner.z),
                CollisionProxySpace.World);
            return simulation;
        }

        [Test]
        public void DemoTank_NoParticleEndsUpOutsideTheTankAfterTenSteps()
        {
            FluidSimulation simulation = BuildDemoTankSimulation();
            Bounds cavity = TankCavity();
            Assert.AreEqual(1, simulation.Collisions.Count,
                "演示水箱应该只有 1 个容器代理；实体板只画不碰，代理数一多就说明又退回薄板方案了");

            for (int s = 0; s < Steps; s++) simulation.Step(StepDt);

            Matrix4x4 toWorld = Matrix4x4.Translate(FluidDemoTools.DemoColumnOffset);
            int outside = 0;
            float worst = 0f;
            Vector3 worstAt = Vector3.zero;
            for (int i = 0; i < simulation.ParticleCount; i++)
            {
                Vector3 d = toWorld.MultiplyPoint3x4(simulation.GetPosition(i)) - cavity.center;
                float ox = Mathf.Abs(d.x) - cavity.extents.x;
                float oy = Mathf.Abs(d.y) - cavity.extents.y;
                float oz = Mathf.Abs(d.z) - cavity.extents.z;
                float pen = Mathf.Max(ox, Mathf.Max(oy, oz));
                if (pen <= Tolerance) continue;
                outside++;
                if (pen > worst) { worst = pen; worstAt = d; }
            }

            Assert.AreEqual(0, outside,
                "演示水箱漏水：" + outside + " 个粒子跑到内空外 " + Tolerance.ToString("F2")
                + " m 以上（最深 " + worst.ToString("F3") + " m @ " + worstAt + "）。容器代理是对凸可行域的"
                + "最小位移投影，越界只会被钉回内壁；出现这个数就说明有人又把容器换成了实体薄板，"
                + "或者把水生成在了容器外面");
        }

        [Test]
        public void DemoTank_WaterIsBornInsideTheContainerAndFits()
        {
            var container = FluidDemoTools.BuildTankContainer(FluidDemoTools.DemoTankInner.x,
                                                              FluidDemoTools.DemoTankInner.y,
                                                              FluidDemoTools.DemoTankInner.z);
            Bounds cavity = TankCavity();

            // Then:  容器的每一面内壁必须与"画出来的那块板"的内表面共面 —— 视觉与物理一旦漂移，
            //        看到的桶和实际挡住水的壁就不是同一个东西
            FluidDemoTools.WallSpec[] tank = FluidDemoTools.BuildTank(
                FluidDemoTools.DemoTankInner.x, FluidDemoTools.DemoTankInner.y,
                FluidDemoTools.DemoTankInner.z, FluidDemoTools.DemoWallThickness);
            for (int i = 0; i < tank.Length; i++)
            {
                int axis = ThinnestAxis(tank[i]);
                float outward = tank[i].Center[axis] >= 0f ? 1f : -1f;
                float plateInnerFace = tank[i].Center[axis] - outward * tank[i].Size[axis] * 0.5f;
                float cavityFace = cavity.center[axis] + outward * cavity.extents[axis];
                Assert.AreEqual(cavityFace, plateInnerFace, 1e-4f,
                    "第 " + i + " 块板的内表面（" + plateInnerFace.ToString("F4") + "）与容器第 " + axis
                    + " 轴的内壁（" + cavityFace.ToString("F4") + "）不共面：看到的桶和挡住水的桶不是同一个");
            }

            // And:  初始水柱必须整个生在内空里（生在外面 = 第一帧就被钉回，等于凭空给一冲量）
            Matrix4x4 toWorld = Matrix4x4.Translate(FluidDemoTools.DemoColumnOffset);
            FluidParameters p = FluidDemoTools.BuildParameters(FluidDemoTools.DemoSpacing);
            FluidParticleSet set = FluidVolume.DamBreak(FluidDemoTools.DemoColumnSize.x,
                FluidDemoTools.DemoColumnSize.y, FluidDemoTools.DemoColumnSize.z, p.particleSpacing);
            int bornOutside = 0;
            for (int i = 0; i < set.Count; i++)
                if (!container.Contains(toWorld.MultiplyPoint3x4(set.Positions[i]), p.collisionThickness)) bornOutside++;
            Assert.AreEqual(0, bornOutside, "有 " + bornOutside + " 个粒子出生就在容器外，第一帧会被硬钉回内壁");

            // And:  单步位移必须明显小于内空最短轴的一半，否则"钉回内壁"等于把水从桶的一头搬到另一头
            float dt = p.ClampDeltaTime(StepDt) / Mathf.Max(1, p.substeps);
            float excursion = p.maxSpeed * dt
                              + FluidSimulation.MaxCorrectionPerIterationFactor * p.kernelRadius * p.solverIterations;
            Assert.Greater(p.maxSpeed, 0f, "演示参数必须给出速度上限：没有它下面这条余量检查没意义");
            float shortest = Mathf.Min(cavity.extents.x, Mathf.Min(cavity.extents.y, cavity.extents.z));
            Assert.Greater(shortest, excursion * 2f,
                "内空最短的半轴只有 " + shortest.ToString("F3") + " m，而单步最大位移 " + excursion.ToString("F4")
                + " m 的两倍已经接近它：一步就能横穿半个桶，容器投影会失真。压低 maxSpeed / solverIterations"
                + "，或者把桶做大");
        }

        /// <summary>板的法向轴 = 三个轴里最薄的那个（水箱六块板都是薄板）。</summary>
        static int ThinnestAxis(FluidDemoTools.WallSpec plate)
        {
            Vector3 he = plate.Size * 0.5f;
            int axis = 0;
            if (he.y < he[axis]) axis = 1;
            if (he.z < he[axis]) axis = 2;
            return axis;
        }
    }
}
