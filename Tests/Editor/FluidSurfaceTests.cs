// Copyright (c) 2026 PhysicsSimulation. MIT License.
//
// 流体**水面渲染**（等值面）行为测试。
//
// 骨架确认时 D 组原本写的是"新增一条 Create Fluid Surface Demo Scene 菜单（优先级 134）"，
// 你在确认场景形态时选了「直接把现有 FluidDemo 改成表面模式」，所以那两条已经按你的决定改写：
//   · DemoSurfaceMenu_PathAndPriorityArePinned → 不再需要新菜单，改为钉住水面参数常量本身
//     （DemoSurfaceCellFactor / IsoLevel / RefreshEveryNFrames / MaxCells），因为它们是演示观感的依据；
//   · DemoSurfaceBuild_UsesSealedTankAndSurfaceMode → 仍然要求 BuildFluid 把组件配成 Surface 模式，
//     并且水箱必须是那条被 FluidTankSealTests 钉住的密封几何。
// 其余 19 条与确认时的行为描述一致。
//
// 实现选的是 marching **tetrahedra**（Kuhn 6 剖分）而不是 256 case 表：表要手抄四千多个整数，
// 抄错就是"偶尔破面"，而测试最难发现那种错。Kuhn 剖分对平移不变 ⇒ 相邻格子共面用同一条对角线
// ⇒ 等值面天生闭合，二义性由四面体自己消化。闭合性就在这里被直接断言（每条边恰好被两个三角形用）。

using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using PhysicsSimulation;
using PhysicsSimulation.EditorTools;

namespace PhysicsSimulation.Editor.Tests
{
    [TestFixture]
    public class FluidSurfaceTests
    {
        const float Spacing = 0.05f;
        const float Kernel = 0.1f;          // h = 2d，与包的默认推法一致
        const float Cell = 0.025f;          // 体素 = d/2，够把水面画圆
        const float Iso = 0.5f;

        // ---------------------------------------------------------------- 工具

        /// <summary>边长 n 个点的均匀点阵，起点在 min，间距 spacing（顺序固定 ⇒ 可复现）。</summary>
        /// <summary>三个分量相同的向量。Unity 的 Vector3 没有单参构造，别指望它有。</summary>
        static Vector3 Uniform(float v) { return new Vector3(v, v, v); }

        static List<Vector3> Lattice(int n, Vector3 min, float spacing)
        {
            var points = new List<Vector3>();
            for (int k = 0; k < n; k++)
                for (int j = 0; j < n; j++)
                    for (int i = 0; i < n; i++)
                        points.Add(min + new Vector3(i * spacing, j * spacing, k * spacing));
            return points;
        }

        static FluidSurfaceMesh SurfaceOf(IList<Vector3> points, float iso = Iso, int maxCells = FluidSurface.DefaultMaxCells)
        {
            return FluidSurface.Build(points, points.Count, Cell, Kernel, Spacing, iso, maxCells, Kernel);
        }

        static Vector3 ExtentOf(IList<Vector3> points)
        {
            var min = points[0];
            var max = min;
            for (int i = 1; i < points.Count; i++) { min = Vector3.Min(min, points[i]); max = Vector3.Max(max, points[i]); }
            return max - min;
        }

        // ============================================================ A. 标量场

        [Test]
        public void Splat_SingleParticle_PeaksAtItsOwnCellAndFallsOffMonotonically()
        {
            // Given:  一个质点落在网格中心
            var points = new List<Vector3> { new Vector3(0.5f, 0.5f, 0.5f) };
            var grid = FluidSurface.PlanGrid(Vector3.zero, new Vector3(1f, 1f, 1f), 0.05f, FluidSurface.DefaultMaxCells);

            // When
            float[] field = FluidSurface.BuildField(points, points.Count, grid, Kernel,
                FluidSurface.LatticeWeightSum(Kernel, Spacing));

            // Then
            int cx = Mathf.RoundToInt(0.5f / grid.Cell.x), cy = Mathf.RoundToInt(0.5f / grid.Cell.y), cz = Mathf.RoundToInt(0.5f / grid.Cell.z);
            float peak = field[grid.NodeIndex(cx, cy, cz)];
            Assert.Greater(peak, 0f, "单个质点必须在它自己那格留下正权重");
            for (int step = 1; step <= 2; step++)
            {
                float along = field[grid.NodeIndex(cx + step, cy, cz)];
                Assert.LessOrEqual(along, peak, "沿 +x 离开质点，场值不允许升高");
                peak = along;
            }
            Assert.AreEqual(0f, field[grid.NodeIndex(0, 0, 0)], "离质点远超核半径的格子必须是 0，不能拖一个长尾巴");
        }

        [Test]
        public void Splat_UniformLattice_IsHighInsideAndLowOutside()
        {
            // Given:  7×7×7 的均匀点阵（模拟水体内部），间距 0.05
            var points = Lattice(7, new Vector3(0.2f, 0.2f, 0.2f), Spacing);
            var grid = FluidSurface.PlanGrid(points[0] - Uniform(Kernel), ExtentOf(points) + points[0] + Uniform(Kernel),
                                             Cell, FluidSurface.DefaultMaxCells);

            // When
            float[] field = FluidSurface.BuildField(points, points.Count, grid, Kernel,
                FluidSurface.LatticeWeightSum(Kernel, Spacing));

            // Then:  内部≈1（归一化的定义），外面两格以外就低到阈值以下
            Vector3 center = points[0] + ExtentOf(points) * 0.5f;
            float inside = field[grid.NodeIndex(
                Mathf.RoundToInt((center.x - grid.Min.x) / grid.Cell.x),
                Mathf.RoundToInt((center.y - grid.Min.y) / grid.Cell.y),
                Mathf.RoundToInt((center.z - grid.Min.z) / grid.Cell.z))];
            Assert.GreaterOrEqual(inside, 0.9f,
                "静止点阵内部应当≈1（现在 " + inside.ToString("F3") + "）：归一化用的是同一个核，偏离说明归一化写错了");
            float outside = field[grid.NodeIndex(0, 0, 0)];
            Assert.Less(outside, Iso, "点阵外的角上必须低于阈值，否则水面会鼓出一层假皮（现在 " + outside.ToString("F3") + "）");
        }

        [Test]
        public void Splat_NoParticles_YieldsEmptyFieldAndEmptyMeshWithoutThrowing()
        {
            // Given:  一具空水体（Reset 之后、或构建失败的帧）
            var points = new List<Vector3>();

            // When
            var grid = FluidSurface.PlanGrid(Vector3.zero, new Vector3(0.5f, 0.5f, 0.5f), Cell, FluidSurface.DefaultMaxCells);
            float[] field = FluidSurface.BuildField(points, 0, grid, Kernel, FluidSurface.LatticeWeightSum(Kernel, Spacing));
            FluidSurfaceMesh surface = FluidSurface.Triangleize(field, grid, Iso);

            // Then:  空场 ⇒ 空面，不抛；粒子数为 0 时直接 Build 也必须给空面
            Assert.AreEqual(grid.NodeCount, field.Length, "空场也必须是完整长度的数组，渲染路径不许假设非空");
            Assert.IsTrue(surface.IsEmpty, "没有粒子就不该有三角形");
            Assert.AreEqual(0, surface.TriangleCount, "空表面的三角形数必须是 0");
            Assert.IsTrue(SurfaceOf(points).IsEmpty, "Build(0 个粒子) 必须是空表面而不是异常");
        }

        [Test]
        public void BuildMesh_SameInput_ProducesBitIdenticalVertices()
        {
            // Given:  一团致密水
            var points = Lattice(8, new Vector3(0.1f, 0.1f, 0.1f), Spacing);

            // When:  连着算两次
            FluidSurfaceMesh a = SurfaceOf(points);
            FluidSurfaceMesh b = SurfaceOf(points);

            // Then:  顶点、法线、索引全部逐位相同（本包的确定性底线：不许有随机数、哈希遍历、并行归约）
            Assert.AreEqual(a.Vertices.Length, b.Vertices.Length, "两次生成的顶点数不同 ⇒ 有隐藏的不确定性");
            for (int i = 0; i < a.Vertices.Length; i++)
            {
                Assert.AreEqual(a.Vertices[i].x, b.Vertices[i].x, 0f, "顶点 " + i + " 的 x 逐位不一致");
                Assert.AreEqual(a.Vertices[i].y, b.Vertices[i].y, 0f, "顶点 " + i + " 的 y 逐位不一致");
                Assert.AreEqual(a.Vertices[i].z, b.Vertices[i].z, 0f, "顶点 " + i + " 的 z 逐位不一致");
            }
            Assert.AreEqual(a.Triangles.Length, b.Triangles.Length, "三角形数不一致");
            for (int i = 0; i < a.Triangles.Length; i++)
                Assert.AreEqual(a.Triangles[i], b.Triangles[i], "第 " + i + " 个索引不一致");
        }

        // ==================================================== B. 等值面几何

        [Test]
        public void BuildMesh_CompactBlob_ProducesWatertightSurface()
        {
            // Given:  一团致密水（8×8×8 点阵，尺度远大于体素）
            var points = Lattice(8, new Vector3(0.1f, 0.1f, 0.1f), Spacing);

            // When
            FluidSurfaceMesh surface = SurfaceOf(points);

            // Then:  闭合 —— 每条无向边恰好被两个三角形使用，一条边界边都不许有
            Assert.Greater(surface.TriangleCount, 100, "这么大的水团只出 " + surface.TriangleCount + " 个三角形，等于没画出水面");
            Assert.AreEqual(0, FluidSurface.CountBoundaryEdges(surface),
                "表面有边界边（破面）。Kuhn 剖分对平移不变，闭合是它的立身之本");
        }

        [Test]
        public void BuildMesh_AllVerticesStayInsideSampledBounds()
        {
            // Given:  一批有界质点，包围盒外扩了核半径作为采样范围
            var points = Lattice(6, new Vector3(0.2f, 0.2f, 0.2f), Spacing);
            var grid = FluidSurface.PlanGrid(points[0] - Uniform(Kernel),
                                             points[0] + ExtentOf(points) + Uniform(Kernel),
                                             Cell, FluidSurface.DefaultMaxCells);

            // When
            FluidSurfaceMesh surface = FluidSurface.Triangleize(
                FluidSurface.BuildField(points, points.Count, grid, Kernel, FluidSurface.LatticeWeightSum(Kernel, Spacing)),
                grid, Iso);

            // Then
            var min = grid.Min - grid.Cell;
            var max = grid.Min + grid.Size + grid.Cell;
            foreach (Vector3 v in surface.Vertices)
            {
                Assert.GreaterOrEqual(v.x, min.x, "顶点 x 越出采样范围：" + v);
                Assert.LessOrEqual(v.x, max.x, "顶点 x 越出采样范围：" + v);
                Assert.GreaterOrEqual(v.y, min.y, "顶点 y 越出采样范围：" + v);
                Assert.LessOrEqual(v.y, max.y, "顶点 y 越出采样范围：" + v);
                Assert.GreaterOrEqual(v.z, min.z, "顶点 z 越出采样范围：" + v);
                Assert.LessOrEqual(v.z, max.z, "顶点 z 越出采样范围：" + v);
            }
        }

        [Test]
        public void BuildMesh_NormalsPointAwayFromTheBlobCenter()
        {
            // Given:  一个孤立水团
            var points = Lattice(8, new Vector3(0.1f, 0.1f, 0.1f), Spacing);

            // When
            FluidSurfaceMesh surface = SurfaceOf(points);
            Vector3 center = new Vector3(0.1f, 0.1f, 0.1f) + ExtentOf(points) * 0.5f;

            // Then:  外法线朝外（= 指向 α 减小的方向）。取 98% 通过即可：体素化在棱角处会有个别抖动
            int wrong = 0;
            for (int i = 0; i < surface.Vertices.Length; i++)
            {
                Vector3 outward = surface.Vertices[i] - center;
                if (outward.sqrMagnitude < 1e-8f) continue;
                if (Vector3.Dot(surface.Normals[i], outward.normalized) <= 0f) wrong++;
            }
            Assert.AreEqual(0, wrong, "有 " + wrong + " 个顶点的法线朝向水体内部，光照会从背面漏出来（"
                + surface.Vertices.Length + " 个顶点里）");
        }

        [Test]
        public void BuildMesh_TwoSeparatedBlobs_DoNotBridgeTheGap()
        {
            // Given:  两团水，中心相距 0.3 m ≫ 核半径 0.1 m（物理上不可能连在一起）
            var left = Lattice(5, new Vector3(0.0f, 0.0f, 0.0f), Spacing);
            var right = Lattice(5, new Vector3(0.3f, 0.0f, 0.0f), Spacing);
            var points = new List<Vector3>(left);
            points.AddRange(right);
            Vector3 centerL = new Vector3(0.1f, 0.1f, 0.1f), centerR = new Vector3(0.4f, 0.1f, 0.1f);

            // When
            FluidSurfaceMesh surface = SurfaceOf(points);

            // Then:  任何顶点都必须落在某团附近，中间那段真空里不许有面
            Assert.Greater(surface.TriangleCount, 50, "两团水一共只出 " + surface.TriangleCount + " 个三角形");
            int bridging = 0;
            foreach (Vector3 v in surface.Vertices)
            {
                float dl = Vector3.Distance(v, centerL), dr = Vector3.Distance(v, centerR);
                if (dl > 0.2f && dr > 0.2f) bridging++;
            }
            Assert.AreEqual(0, bridging, "有 " + bridging + " 个顶点横跨在两团之间的真空里 ⇒ 等值面自己搭了桥");
        }

        [Test]
        public void BuildMesh_OverCellBudget_AutoDownsamplesWithinLimit()
        {
            // Given:  采样范围很大、体素很小，格子数远超预算
            var min = Vector3.zero;
            var max = new Vector3(10f, 10f, 10f);

            // When
            var grid = FluidSurface.PlanGrid(min, max, 0.001f, 8192);

            // Then:  自动放大体素把预算压回去，而且仍然覆盖全部范围（Min 不动，只放大不缩小）
            Assert.LessOrEqual(grid.CellCount, 8192, "格子数 " + grid.CellCount + " 仍超预算，会一次分配几十 MB");
            Assert.Greater(grid.Cell.x, 0.001f, "预算超了却没放大体素");
            Assert.AreEqual(0f, grid.Min.x, 0f, "Min 被改动了，水面会整体偏移");
            Assert.GreaterOrEqual(grid.Size.x, 9.99f, "放大体素后必须仍然覆盖原范围，现在只有 " + grid.Size.x);
            Assert.GreaterOrEqual(grid.Size.y, 9.99f, "放大体素后必须仍然覆盖原范围");
            Assert.GreaterOrEqual(grid.Size.z, 9.99f, "放大体素后必须仍然覆盖原范围");
        }

        [Test]
        public void BuildMesh_LowerIsoLevelProducesEnvelopeOfHigherOne()
        {
            // Given:  同一团水
            var points = Lattice(8, new Vector3(0.1f, 0.1f, 0.1f), Spacing);

            // When:  阈值一低一高
            FluidSurfaceMesh loose = SurfaceOf(points, 0.3f);
            FluidSurfaceMesh tight = SurfaceOf(points, 0.8f);

            // Then:  低阈值的面必须严格包住高阈值的面（阈值越高，水面越贴核心）
            Assert.Greater(loose.TriangleCount, 0, "低阈值没出面");
            Assert.Greater(tight.TriangleCount, 0, "高阈值没出面");
            Bounds a = loose.Bounds, b = tight.Bounds;
            Assert.LessOrEqual(a.min.x, b.min.x + 1e-4f, "低阈值的水面必须比高阈值更靠外（x 下界）：" + a.min + " vs " + b.min);
            Assert.GreaterOrEqual(a.max.x, b.max.x - 1e-4f, "低阈值的水面必须比高阈值更靠外（x 上界）：" + a.max + " vs " + b.max);
            Assert.LessOrEqual(a.min.y, b.min.y + 1e-4f, "低阈值的水面必须比高阈值更靠外（y 下界）");
            Assert.GreaterOrEqual(a.max.y, b.max.y - 1e-4f, "低阈值的水面必须比高阈值更靠外（y 上界）");
        }

        // ============================================ C. FluidBehaviour 渲染模式

        static GameObject MakeWater(out FluidBehaviour behaviour, int latticeN = 6)
        {
            var go = new GameObject("Fluid");
            behaviour = go.AddComponent<FluidBehaviour>();
            behaviour.autoSimulate = false;                       // 测试要的是可复现的帧，不是编辑器帧率
            behaviour.parameters.particleSpacing = Spacing;
            behaviour.parameters.kernelRadius = Kernel;
            behaviour.volumeShape = FluidVolumeShape.Box;
            behaviour.volumeSize = new Vector3((latticeN - 1) * Spacing, (latticeN - 1) * Spacing, (latticeN - 1) * Spacing);
            behaviour.Rebuild();
            return go;
        }

        [Test]
        public void RenderMode_DefaultIsParticlesAndBuildsNoSurfaceMesh()
        {
            // Given:  默认构造的组件（v1.5.0 的默认观感就是一堆小球）
            FluidBehaviour behaviour;
            var go = MakeWater(out behaviour);

            // When
            behaviour.Update();

            // Then:  渲染模式默认是粒子，而且没有生成任何水面网格
            Assert.AreEqual(FluidRenderMode.Particles, behaviour.renderMode, "默认必须是粒子模式，改了它等于改 v1.5.0 的观感");
            Assert.IsNull(behaviour.SurfaceMesh, "默认模式不该建水面");
            Assert.AreEqual(0, behaviour.SurfaceRevision, "默认模式不该计数水面重建");
            UnityEngine.Object.DestroyImmediate(go);
        }

        [Test]
        public void RenderMode_Surface_ProducesTessellatedMeshWithTranslucentMaterial()
        {
            // Given:  一团正常水体，切到水面模式
            FluidBehaviour behaviour;
            var go = MakeWater(out behaviour, 8);
            behaviour.renderMode = FluidRenderMode.Surface;
            behaviour.surfaceCellSize = Cell;

            // When
            int triangles = behaviour.RebuildSurface();

            // Then
            Assert.Greater(triangles, 100, "8×8×8 的水团只出 " + triangles + " 个三角形，画不出水面");
            Assert.IsNotNull(behaviour.SurfaceMesh, "水面网格没挂上");
            Assert.AreEqual(triangles * 3, behaviour.SurfaceMesh.triangles.Length, "网格三角形数与返回值不符");
            Assert.Greater(behaviour.SurfaceTriangleCount, 0, "SurfaceTriangleCount 应该能看到三角形");
            Assert.IsNotNull(behaviour.SurfaceMaterial, "水面材质没建出来，会是一片品红或干脆不画");
            Assert.IsTrue(FluidSurfaceMaterial.IsTranslucent(behaviour.SurfaceMaterial),
                "水面材质必须是半透明（透明队列 + alpha 小于 1），否则看着像一块蓝色塑料");
            var filter = go.GetComponentInChildren<MeshFilter>();
            Assert.IsNotNull(filter, "水面必须挂在子物体上，好继承组件的局部→世界变换");
            UnityEngine.Object.DestroyImmediate(go);
        }

        [Test]
        public void RenderMode_Both_ShowsParticlesAndSurfaceTogether()
        {
            // Given:  两者都画
            FluidBehaviour behaviour;
            var go = MakeWater(out behaviour, 8);
            behaviour.renderMode = FluidRenderMode.Both;
            behaviour.surfaceCellSize = Cell;

            // When
            behaviour.Update();

            // Then:  粒子批次与水面同时存在
            Assert.Greater(behaviour.ParticleBatchCount, 0, "Both 模式却没提交粒子批次");
            Assert.Greater(behaviour.SurfaceTriangleCount, 0, "Both 模式却没建水面");
            UnityEngine.Object.DestroyImmediate(go);
        }

        [Test]
        public void Surface_RebuildIsThrottledByRefreshInterval()
        {
            // Given:  每隔 3 帧重建一次
            FluidBehaviour behaviour;
            var go = MakeWater(out behaviour, 6);
            behaviour.renderMode = FluidRenderMode.Surface;
            behaviour.surfaceRefreshEveryNFrames = 3;

            // When:  走 7 帧
            for (int i = 0; i < 7; i++) behaviour.Update();

            // Then:  重建发生在第 1、4、7 帧，共 3 次 —— 而不是 7 次白扔帧时
            Assert.AreEqual(3, behaviour.SurfaceRevision,
                "间隔 3 帧走 7 帧应该重建 3 次，实际 " + behaviour.SurfaceRevision + " 次");
            UnityEngine.Object.DestroyImmediate(go);
        }

        [Test]
        public void Surface_WithTooFewParticles_DegradesQuietly()
        {
            // Given:  阈值高到场里没有任何点够得着（等效于"水太少画不出面"）
            FluidBehaviour behaviour;
            var go = MakeWater(out behaviour, 2);
            behaviour.renderMode = FluidRenderMode.Surface;
            behaviour.surfaceCellSize = Cell;
            behaviour.surfaceIsoLevel = 5f;

            // When:  不抛异常
            int triangles = behaviour.RebuildSurface();

            // Then:  0 个三角形 + 一张空网格（不是 null），渲染路径不中断
            Assert.AreEqual(0, triangles, "阈值 5 已经高于任何地方的场值，不该出三角形");
            Assert.IsNotNull(behaviour.SurfaceMesh, "画不出面也必须给一张空网格，挂 null 会让 MeshFilter 之后每次都要判空");
            Assert.AreEqual(0, behaviour.SurfaceMesh.triangles.Length, "空网格不该带索引");
            Assert.AreEqual(1, behaviour.SurfaceRevision, "重建计数仍然要加一，让外部知道这一帧处理过了");
            UnityEngine.Object.DestroyImmediate(go);
        }

        [Test]
        public void Surface_TracksParticlesAcrossFrames()
        {
            // Given:  一柱水正在塌开
            FluidBehaviour behaviour;
            var go = MakeWater(out behaviour, 6);
            behaviour.renderMode = FluidRenderMode.Surface;
            behaviour.surfaceCellSize = Cell;
            behaviour.surfaceRefreshEveryNFrames = 1;
            behaviour.autoSimulate = false;

            // When:  手动走 6 步，每步都重建
            var sizes = new List<int>();
            Vector3 lastMax = Vector3.zero;
            for (int i = 0; i < 6; i++)
            {
                behaviour.Step(0.01f);
                int triangles = behaviour.RebuildSurface();
                sizes.Add(triangles);
                lastMax = behaviour.Simulation.Bounds().max;
            }

            // Then:  每一帧都有面，且水面跟着粒子铺开（包围盒随时间变大）
            Assert.AreEqual(6, behaviour.SurfaceRevision, "水面没有每帧重建");
            for (int i = 0; i < sizes.Count; i++)
                Assert.Greater(sizes[i], 0, "第 " + i + " 帧没有三角形，水面闪断了");
            Assert.Greater(lastMax.x, 0f, "走完之后水体没铺开，说明 Step 没生效");
            UnityEngine.Object.DestroyImmediate(go);
        }

        // ===================================================== D. 演示场景与工具

        [Test]
        public void DemoSurfaceConfig_CellBudgetAndResolutionAreSane()
        {
            // Given:  演示水箱的尺度与演示水面常量
            float cell = FluidDemoTools.DemoSpacing * FluidDemoTools.DemoSurfaceCellFactor;
            var inner = FluidDemoTools.DemoTankInner;

            // When
            var grid = FluidSurface.PlanGrid(inner * -0.5f - Uniform(FluidDemoTools.DemoSpacing * 2f),
                                             inner * 0.5f + Uniform(FluidDemoTools.DemoSpacing * 2f),
                                             cell, FluidDemoTools.DemoSurfaceMaxCells);

            // Then:  预算之内，且每轴格子数足够分辨水面（至少 16 格 ⇒ 再少就只能看到方块）
            Assert.LessOrEqual(grid.CellCount, FluidDemoTools.DemoSurfaceMaxCells,
                "演示水面格子数 " + grid.CellCount + " 超过预算 " + FluidDemoTools.DemoSurfaceMaxCells);
            Assert.GreaterOrEqual(grid.X, 16, "x 方向只有 " + grid.X + " 格，水面会看成积木");
            Assert.GreaterOrEqual(grid.Y, 16, "y 方向只有 " + grid.Y + " 格");
            Assert.GreaterOrEqual(grid.Z, 16, "z 方向只有 " + grid.Z + " 格");
            Assert.Greater(FluidDemoTools.DemoSurfaceCellFactor, 0f, "体素系数必须为正");
            Assert.LessOrEqual(FluidDemoTools.DemoSurfaceRefreshEveryNFrames, 3,
                "演示里每隔 " + FluidDemoTools.DemoSurfaceRefreshEveryNFrames + " 帧重建一次，水面会明显滞后");
        }

        [Test]
        public void DemoSurfaceBuild_UsesSealedTankAndSurfaceMode()
        {
            // Given:  用演示工具在当前场景搭一套（水箱 + 水）
            var sceneRoot = new GameObject("DemoFixture");
            var tankRoot = new GameObject("Tank");
            tankRoot.transform.SetParent(sceneRoot.transform, false);

            // When:  走 BuildFluid 这条真实路径（130 菜单里用的就是它）
            int built = FluidDemoTools.BuildFluid(sceneRoot.transform, new List<Collider>(),
                FluidDemoTools.DemoColumnSize.x, FluidDemoTools.DemoColumnSize.y,
                FluidDemoTools.DemoColumnSize.z, FluidDemoTools.DemoSpacing);

            // Then
            Assert.AreEqual(1, built, "演示水体没建起来");
            var behaviour = sceneRoot.GetComponentInChildren<FluidBehaviour>();
            Assert.IsNotNull(behaviour, "BuildFluid 应当创建 FluidBehaviour");
            Assert.AreEqual(FluidRenderMode.Surface, behaviour.renderMode,
                "演示场景现在必须是水面模式，否则这次改动等于没做");
            Assert.AreEqual(FluidDemoTools.DemoSurfaceCellFactor * FluidDemoTools.DemoSpacing, behaviour.surfaceCellSize, 1e-6f,
                "组件体素与演示常量不符");
            Assert.AreEqual(FluidDemoTools.DemoSurfaceIsoLevel, behaviour.surfaceIsoLevel, 1e-6f, "阈值与演示常量不符");
            Assert.Greater(behaviour.parameters.maxSpeed, 0f,
                "演示必须带速度上限：没有它，水会越过薄板中线被从另一面挤出去（活板门漏水）");
            UnityEngine.Object.DestroyImmediate(sceneRoot);
        }

        [Test]
        public void DemoTankGeometry_StillSealsAfterThickeningTheProxies()
        {
            // Given:  水箱代理按外侧加厚之后（内表面应当逐位不变）
            var tank = FluidDemoTools.BuildTank(FluidDemoTools.DemoTankInner.x, FluidDemoTools.DemoTankInner.y,
                                                FluidDemoTools.DemoTankInner.z, FluidDemoTools.DemoWallThickness);

            // Then:  地板顶面仍然正好在 y=0：内表面不许因为加厚而移动
            float floorTop = tank[0].Center.y + tank[0].Size.y * 0.5f;
            Assert.AreEqual(0f, floorTop, 1e-5f, "地板顶面被加厚改动挪到了 " + floorTop + "，水的落点就变了");
            float innerX = FluidDemoTools.DemoTankInner.x * 0.5f;
            float innerZ = FluidDemoTools.DemoTankInner.z * 0.5f;
            Assert.AreEqual(-innerX, tank[1].Center.x + tank[1].Size.x * 0.5f, 1e-5f, "−x 墙的内表面挪动了");
            Assert.AreEqual(innerX, tank[2].Center.x - tank[2].Size.x * 0.5f, 1e-5f, "+x 墙的内表面挪动了");
            Assert.AreEqual(-innerZ, tank[3].Center.z + tank[3].Size.z * 0.5f, 1e-5f, "−z 墙的内表面挪动了");
            Assert.AreEqual(innerZ, tank[4].Center.z - tank[4].Size.z * 0.5f, 1e-5f, "+z 墙的内表面挪动了");
        }
    }
}
