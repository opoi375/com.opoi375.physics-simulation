// Copyright (c) 2026 PhysicsSimulation. MIT License.
//
// v1.5.0 流体演示工具测试。
//
// 水箱那五块板是"能不能看出流体在动"的关键：没有侧壁，水一冲就散成一条线，
// 演示就变成了"粒子往下掉"。所以布局、参数推导、预算警告都得能被断言，
// 而不是靠人在编辑器里手摆一遍。

using System;
using System.Collections.Generic;
using NUnit.Framework;
using PhysicsSimulation.EditorTools;
using UnityEngine;

namespace PhysicsSimulation.Editor.Tests
{
    [TestFixture]
    public class FluidDemoToolsTests
    {
        [Test]
        public void Menus_UsePriorities130To132_AndSitInTheFluidGroup()
        {
            // Then:  接在软体审计（123/124）之后，单独一个 Fluid 分组
            Assert.AreEqual(130, FluidDemoTools.CreateMenuPriority);
            Assert.AreEqual(131, FluidDemoTools.BuildMenuPriority);
            Assert.AreEqual(132, FluidDemoTools.DumpMenuPriority);
            StringAssert.StartsWith("Tools/Physics Simulation/Fluid/", FluidDemoTools.CreateMenuPath,
                "菜单要单独分组，别和软体混在一起");
            StringAssert.StartsWith("Tools/Physics Simulation/Fluid/", FluidDemoTools.DumpMenuPath);
            Assert.AreNotEqual(FluidDemoTools.CreateMenuPath, FluidDemoTools.BuildMenuPath, "两条菜单路径不能撞");
        }

        [Test]
        public void Tank_HasSixWallsFloorSidesAndCeiling()
        {
            // Given:  内空 2 × 1 × 1 米，壁厚 0.2
            var walls = FluidDemoTools.BuildTank(2f, 1f, 1f, 0.2f);

            // Then:  底 + 四面侧墙 + 顶 = 6。**这是一次行为改动，不是笔误**：v1.5.0 本来故意不开顶
            // 为了“看得出飞溅”，但 Play 里溃坝浪头直接把水抛过 1.1 m 的墙头，300 帧后包围盒
            // 10.3 × 9.1 × 6.7 m，一箱水泼在箱外、演示不可用。而“墙”本身没有 Mesh、只有 Collider，
            // 开不开顶不影响取景，只影响水能不能跳出去。
            Assert.AreEqual(6, walls.Length, "水箱应有地板 + 四面墙 + 顶盖共 6 块板，实际 " + walls.Length);

            // And:  地板的上表面正好在 y=0
            var floor = walls[0];
            Assert.AreEqual(0f, floor.Center.y + floor.Size.y * 0.5f, 1e-4f,
                "地板上表面应在 y=0，实际 " + (floor.Center.y + floor.Size.y * 0.5f));

            // And:  顶盖的下表面必须正好贴桶口 y = innerHeight，而且只往上加厚：
            //        内空高度一旦因为开顶而被压低，水的落点与桶深就全变了
            var lid = walls[5];
            Assert.AreEqual(1f, lid.Center.y - lid.Size.y * 0.5f, 1e-4f,
                "顶盖下表面应正好在桶口 y=1，实际 " + (lid.Center.y - lid.Size.y * 0.5f));
            Assert.Greater(lid.Center.y, 1f, "顶盖必须整个在桶口上方（只往外侧加厚），否则它就把内空压低了");

            // And:  顶盖必须**整片盖住四面墙顶**，否则墙顶与顶盖之间的缝就是新开口
            float xOuter = walls[2].Center.x + walls[2].Size.x * 0.5f;      // +x 墙外沿
            float zOuter = walls[4].Center.z + walls[4].Size.z * 0.5f;      // +z 墙外沿
            Assert.LessOrEqual(lid.Center.x - lid.Size.x * 0.5f, -xOuter + 1e-4f, "顶盖没盖到 −x 墙外沿");
            Assert.GreaterOrEqual(lid.Center.x + lid.Size.x * 0.5f, xOuter - 1e-4f, "顶盖没盖到 +x 墙外沿");
            Assert.LessOrEqual(lid.Center.z - lid.Size.z * 0.5f, -zOuter + 1e-4f, "顶盖没盖到 −z 墙外沿");
            Assert.GreaterOrEqual(lid.Center.z + lid.Size.z * 0.5f, zOuter - 1e-4f, "顶盖没盖到 +z 墙外沿");

            // And:  内空里不能有任何墙（水就生在里头的点阵，出生即穿墙的话第一帧就被顶飞）
            var interior = new Bounds(new Vector3(0f, 0.5f, 0f), new Vector3(2f - 1e-3f, 1f - 1e-3f, 1f - 1e-3f));
            for (int i = 0; i < walls.Length; i++)
                Assert.IsFalse(Intersects(interior, walls[i]),
                    "第 " + i + " 块墙伸进了内空：中心 " + walls[i].Center + " 尺寸 " + walls[i].Size);

            // And:  六块板**只画不碰**：兜水的是内侧盒子容器代理。实体板当碰撞体会踩
            //        “最近面投影”的坑 —— 实测六面封桶 + 壁厚 0.5 m + 背板，300 步仍有 150 个粒子
            //        停在墙板外表面内侧 1.4 cm（穿透 0.486 m，与 dt 无关），截图里就是贴在桶壁外侧
            //        的一条蓝带。板退回成纯视觉，代理从 12 个降回 1 个。
            var tankRoot = new GameObject("tank-geometry-probe");
            try
            {
                FluidDemoTools.CreateTank(walls, tankRoot.transform, FluidDemoTools.DemoCameraDirection);
                Assert.AreEqual(0, tankRoot.GetComponentsInChildren<Collider>(true).Length,
                    "水箱板不该带任何 Collider：流体用 BoxContainerProxy 兜水，实体板碰撞体会重新引入活板门与角部传送带");
                Assert.AreEqual(6, tankRoot.transform.childCount, "六块板都该在（藏掉的是画面不是物体），实际 " + tankRoot.transform.childCount);

                // 顶盖一律不画，挡镜头的墙也不画：内空 1.5 × 1.1 × 0.9 的桶里水深只有 0.26 m，
                // 不藏的话相机到水面的连线会穿过 −z 墙的上表面，截图里就只剩一个空盒子
                var lidPlate = tankRoot.transform.Find("Tank_Lid");
                Assert.IsNotNull(lidPlate, "最后一块板应当叫 Tank_Lid，实际没找到（命名变了演示的取景断言就查不出来）");
                Assert.IsNull(lidPlate.GetComponent<Renderer>(), "顶盖不该有 Renderer");
                Assert.IsNotNull(tankRoot.transform.Find("Tank_Floor").GetComponent<Renderer>(), "地板必须画着，否则水像是悬在空中");
                int visible = 0;
                for (int i = 1; i < walls.Length - 1; i++)     // 四面侧墙（0 是地板，最后一块是顶盖）
                    if (tankRoot.transform.Find("Tank_Wall_" + i).GetComponent<Renderer>() != null) visible++;
                Assert.AreEqual(2, visible, "四面侧墙里应当藏掉挡镜头的两面、留下两面当背景，实际留下 " + visible);

                // 真正的判据：相机到水面四个角点的连线，不能被任何**还画着**的板挡住
                var camPos = FluidDemoTools.DemoCameraPosition;
                float poolY = FluidDemoTools.DemoPoolDepth;
                float halfX = FluidDemoTools.DemoTankInner.x * 0.5f - 0.02f;
                float halfZ = FluidDemoTools.DemoTankInner.z * 0.5f - 0.02f;
                Vector3[] corners =
                {
                    new Vector3(halfX, poolY, halfZ), new Vector3(halfX, poolY, -halfZ),
                    new Vector3(-halfX, poolY, halfZ), new Vector3(-halfX, poolY, -halfZ),
                };
                for (int c = 0; c < corners.Length; c++)
                {
                    for (int i = 0; i < walls.Length; i++)
                    {
                        if (!FluidDemoTools.SegmentIntersectsPlate(camPos, corners[c], walls[i])) continue;
                        var plateGo = PlateGameObject(tankRoot.transform, i, walls.Length);
                        Assert.IsNotNull(plateGo.GetComponent<Renderer>() == null ? plateGo : null,
                            "相机到水面角点 " + corners[c] + " 的连线被第 " + i + " 块板挡住，而它还画着，"
                            + "截图里就是一箱看不见的水");
                    }
                }
            }
            finally
            {
                // EditMode 里只能用 DestroyImmediate：Destroy 会打一条 Error 日志，
                // 而 Unity 测试框架把任何未预期的 Error 判成测试失败（本项目踩过两次）
                UnityEngine.Object.DestroyImmediate(tankRoot);
            }

            // And:  容器代理的内空必须与画出来的桶逐位对齐（这条在 FluidTankSealTests 里按六块板逐面查）
            var container = FluidDemoTools.BuildTankContainer(2f, 1f, 1f);
            Assert.AreEqual(new Vector3(0f, 0.5f, 0f), container.Center, "内空 2×1×1、地板顶面在 y=0 ⇒ 中心 y 应为 0.5");
            Assert.AreEqual(new Vector3(1f, 0.5f, 0.5f), container.HalfExtents, "半尺寸应为内空尺寸的一半");
            Assert.IsTrue(container.Contains(new Vector3(0.99f, 0.99f, 0.49f), 0f), "内空角落里的点不该被推");
            Assert.IsFalse(container.Contains(new Vector3(1.01f, 0.5f, 0f), 0f), "越出 x 内壁的点必须算在外面");
            Assert.IsFalse(container.Contains(new Vector3(0f, 0.5f, 0.51f), 0f), "越出 z 内壁的点必须算在外面");
            Assert.Throws<System.ArgumentOutOfRangeException>(
                () => FluidDemoTools.BuildTankContainer(0f, 1f, 1f), "内空尺寸非正必须报错，不能默默建出一个退化容器");
        }

        [Test]
        public void BuildParameters_KeepsKernelRadiusAtTwiceTheSpacing()
        {
            // Given/Then:  h = 2d 是 PBF 的标准配置（h=1d 时核支撑里几乎没邻居）
            var p = FluidDemoTools.BuildParameters(0.05f);
            Assert.AreEqual(0.1f, p.kernelRadius, 1e-6f, "核半径应为间距的 2 倍");
            Assert.Greater(p.kernelRadius, p.particleSpacing, "h 必须大于间距");
            Assert.IsTrue(p.clampTensileLambda, "演示默认必须开着拉力钳制");
            Assert.GreaterOrEqual(p.substeps, 2, "演示至少 2 子步，1 步会漏碰撞");
            Assert.AreEqual(0f, p.vorticityEpsilon, "涡度默认关（要好看再手动开）");
            Assert.DoesNotThrow(() => p.Validate(), "推出来的参数必须自检通过");
        }

        [Test]
        public void BudgetWarning_NamesTheCountAndTheLimit()
        {
            // Then:  预算内不吭声
            Assert.AreEqual("", FluidDemoTools.BudgetWarning(2000, 6000), "预算内不该有警告");

            // And:  超预算要报出具体数字（v1.4.0 的教训：一个 5329 质点的水面单帧吃掉 74% 预算，
            //        报告里不含数字就等于没说）
            string warn = FluidDemoTools.BudgetWarning(9000, 6000);
            Assert.IsNotEmpty(warn, "超预算必须警告");
            StringAssert.Contains("9000", warn, "警告里要有实际粒子数：" + warn);
            StringAssert.Contains("6000", warn, "警告里要有上限：" + warn);
        }

        [Test]
        public void BuildFluid_WiresTankColliders_AndKeepsCollisionOffByDefault()
        {
            // Given:  三面墙当碰撞体
            var colliders = new List<Collider>();
            for (int i = 0; i < 3; i++)
            {
                var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
                wall.name = "Wall_" + i;
                wall.transform.position = new Vector3(i * 2f, -0.5f, 0f);
                colliders.Add(wall.GetComponent<Collider>());
            }
            var root = new GameObject("FluidDemo");

            // When:  建一坨水
            int count = FluidDemoTools.BuildFluid(root.transform, colliders, 0.3f, 0.3f, 0.3f, 0.05f);
            var behaviour = root.GetComponentInChildren<FluidBehaviour>();

            // Then
            Assert.AreEqual(1, count, "应建出一坨水体");
            Assert.IsNotNull(behaviour, "根下要有 FluidBehaviour");
            Assert.IsTrue(behaviour.collideWithSceneColliders, "演示场景里就该开碰撞，否则水穿墙");
            Assert.AreEqual(3, behaviour.sceneColliders.Count, "三面墙都要填进去");
            Assert.IsTrue(behaviour.IsBuilt, "构建失败：" + behaviour.LastBuildError);
            Assert.AreEqual(216, behaviour.Simulation.ParticleCount, "6³ = 216 个粒子");

            UnityEngine.Object.DestroyImmediate(root);
            for (int i = 0; i < colliders.Count; i++) UnityEngine.Object.DestroyImmediate(colliders[i].gameObject);
        }

        [Test]
        public void BuildFluid_IsRepeatable_SameArgumentsGiveSameLayout()
        {
            // Given/Then:  两次构建逐位相同（演示工具不许藏随机数，否则"复现 bug"无从谈起）
            var rootA = new GameObject("A");
            var rootB = new GameObject("B");
            FluidDemoTools.BuildFluid(rootA.transform, null, 0.3f, 0.3f, 0.3f, 0.05f);
            FluidDemoTools.BuildFluid(rootB.transform, null, 0.3f, 0.3f, 0.3f, 0.05f);

            var a = rootA.GetComponentInChildren<FluidBehaviour>();
            var b = rootB.GetComponentInChildren<FluidBehaviour>();
            Assert.AreEqual(a.Simulation.ParticleCount, b.Simulation.ParticleCount);
            for (int i = 0; i < a.Simulation.ParticleCount; i++)
                Assert.AreEqual(BitConverter.SingleToInt32Bits(a.Simulation.GetPosition(i).x),
                                BitConverter.SingleToInt32Bits(b.Simulation.GetPosition(i).x),
                    "第 " + i + " 个粒子两次构建位置不同 ⇒ 工具里有随机性");

            UnityEngine.Object.DestroyImmediate(rootA);
            UnityEngine.Object.DestroyImmediate(rootB);
        }

        static bool Intersects(Bounds a, FluidDemoTools.WallSpec wall)
        {
            // 只贴边不算伸进内空（墙本来就该正好贴在内空边界上）。
            // 自己算重叠区间而不用 Bounds 的交集 API：Unity 各版本这个方法名换过（Intersect / Intersection），
            // 测试不该为一个改名而红。
            var b = new Bounds(wall.Center, wall.Size);
            const float eps = 1e-6f;
            return a.min.x < b.max.x - eps && b.min.x < a.max.x - eps
                && a.min.y < b.max.y - eps && b.min.y < a.max.y - eps
                && a.min.z < b.max.z - eps && b.min.z < a.max.z - eps;
        }

        static GameObject PlateGameObject(Transform root, int index, int count)
        {
            string name = index == 0 ? "Tank_Floor" : (index == count - 1 ? "Tank_Lid" : "Tank_Wall_" + index);
            var found = root.Find(name);
            Assert.IsNotNull(found, "找不到板 " + name);
            return found.gameObject;
        }

        /// <summary>板的法向轴 = 三个轴里最薄的那个（水箱六块板都是薄板）。</summary>
        static int NormalAxis(FluidDemoTools.WallSpec wall)
        {
            var he = wall.Size * 0.5f;
            int axis = 0;
            if (he.y < he[axis]) axis = 1;
            if (he.z < he[axis]) axis = 2;
            return axis;
        }

        [Test]
        public void BuildTank_WallsSealTheFloorSeamAndEachOther()
        {
            // Given:  演示默认那套水箱（内空 2.4×1.2×1.2，壁厚 0.2）
            var walls = FluidDemoTools.BuildTank(2.4f, 1.2f, 1.2f, 0.2f);
            Assert.AreEqual(6, walls.Length, "地板 + 四面墙 + 顶盖");

            var floor = walls[0];
            float floorTop = floor.Center.y + floor.Size.y * 0.5f;
            float floorBottom = floor.Center.y - floor.Size.y * 0.5f;

            // Then:  ① 每面**侧墙**的下边缘必须**低于地板顶面**（探进地板实体里）。否则贴着地板
            //         边缘被"最浅轴侧向挤出"的粒子会掉进墙底下的空隙（实测漏到 -65 m）。
            //         顶盖不算在这一条里：它的下表面正好贴桶口，靠的是“整片盖住四面墙外沿”封缝，
            //         自己往上加厚，不会去碰地板。
            for (int i = 1; i <= 4; i++)
            {
                float wallBottom = walls[i].Center.y - walls[i].Size.y * 0.5f;
                Assert.Less(wallBottom, floorTop - 1e-5f,
                    "墙 " + i + " 的下边缘在 " + wallBottom + "，没探进地板（地板顶 " + floorTop
                    + "）⇒ 接缝是单向活门，水会漏出去");
                Assert.LessOrEqual(wallBottom, floorBottom + 1e-5f,
                    "墙 " + i + " 没盖到地板底面：" + wallBottom);
            }

            // ② 相邻墙必须在平面上互相搭接（有面积，不是角上碰一下）
            for (int a = 1; a < walls.Length; a++)
            {
                var wa = walls[a];
                float aMinX = wa.Center.x - wa.Size.x * 0.5f, aMaxX = wa.Center.x + wa.Size.x * 0.5f;
                float aMinZ = wa.Center.z - wa.Size.z * 0.5f, aMaxZ = wa.Center.z + wa.Size.z * 0.5f;
                int overlaps = 0;
                for (int b = 1; b < walls.Length; b++)
                {
                    if (b == a) continue;
                    var wb = walls[b];
                    float ox = Mathf.Min(aMaxX, wb.Center.x + wb.Size.x * 0.5f)
                             - Mathf.Max(aMinX, wb.Center.x - wb.Size.x * 0.5f);
                    float oz = Mathf.Min(aMaxZ, wb.Center.z + wb.Size.z * 0.5f)
                             - Mathf.Max(aMinZ, wb.Center.z - wb.Size.z * 0.5f);
                    if (ox > 1e-4f && oz > 1e-4f) overlaps++;
                }
                Assert.GreaterOrEqual(overlaps, 2,
                    "墙 " + a + " 只与 " + overlaps + " 面墙有搭接面积 ⇒ 竖直接缝漏水");
            }

            // ③ 地板必须盖住全部墙的外沿，墙根底下不能悬空
            float hx = floor.Size.x * 0.5f, hz = floor.Size.z * 0.5f;
            for (int i = 1; i <= 4; i++)
            {
                var w = walls[i];
                Assert.LessOrEqual(Mathf.Abs(w.Center.x) + w.Size.x * 0.5f, hx + 1e-4f,
                    "墙 " + i + " 伸出地板之外（x）");
                Assert.LessOrEqual(Mathf.Abs(w.Center.z) + w.Size.z * 0.5f, hz + 1e-4f,
                    "墙 " + i + " 伸出地板之外（z）");
            }
        }

        [Test]
        public void DemoConfig_PoolIsDeepEnoughAndCameraLooksIntoTheTank()
        {
            // Then:  ① 水量必须在预算内
            float volume = FluidDemoTools.DemoColumnSize.x * FluidDemoTools.DemoColumnSize.y
                         * FluidDemoTools.DemoColumnSize.z;
            int particles = Mathf.CeilToInt(volume / Mathf.Pow(FluidDemoTools.DemoSpacing, 3));
            Assert.LessOrEqual(particles, FluidDemoTools.DemoParticleBudget,
                "演示水量 " + particles + " 粒超出预算 " + FluidDemoTools.DemoParticleBudget);

            // ② 溃坝铺平之后至少要有 4 层粒子深，否则摊成一张膜，根本不像水
            //    （实测踩过：2.4×1.2 的底板 + 0.32 m³ 水 ⇒ 包围盒 y 尺寸为 0，只有一层）
            float layers = FluidDemoTools.DemoPoolDepth / FluidDemoTools.DemoSpacing;
            Assert.GreaterOrEqual(layers, 4f,
                "静水深只有 " + layers.ToString("F1") + " 层粒子（" + FluidDemoTools.DemoPoolDepth
                + " m），水会摊成一张膜 ⇒ 缩小水箱底面积或加大水量");

            // ③ 相机必须俯视：前墙比水高，平视就只拍得到一堵墙
            var camPos = FluidDemoTools.DemoCameraPosition;
            var toPool = new Vector3(0f, FluidDemoTools.DemoPoolDepth * 0.5f, 0f) - camPos;
            var forward = toPool.normalized;
            Assert.Less(forward.y, -0.3f,
                "相机视线朝 y " + forward.y + "，不是俯视 ⇒ 截图里看不见池底的水");

            // ④ 水（静水面 + 溃坝前那面水墙的顶角）必须整个落在默认 60° 竖直视锥里
            float halfFov = 30f * Mathf.Deg2Rad;
            var targets = new[]
            {
                new Vector3(0f, FluidDemoTools.DemoPoolDepth * 0.5f, 0f),
                new Vector3(
                    FluidDemoTools.DemoColumnOffset.x + FluidDemoTools.DemoColumnSize.x,
                    FluidDemoTools.DemoColumnSize.y, 0f),
            };
            foreach (var target in targets)
            {
                float angle = Vector3.Angle(toPool, (target - camPos).normalized);
                Assert.LessOrEqual(angle, halfFov * Mathf.Rad2Deg,
                    "目标点 " + target + " 偏离视轴 " + angle + "°，超出视锥");
            }

            // ⑤ 机位必须高过墙顶，否则视线被前墙挡住（俯角也就白设了）
            Assert.Greater(camPos.y, FluidDemoTools.DemoTankInner.y + FluidDemoTools.DemoWallThickness,
                "相机高度 " + camPos.y + " 没超过墙顶，拍不到水面");
        }

        [Test]
        public void DemoConfig_ColumnClearsEveryWallSoTheProxyHasNoTies()
        {
            // Given:  演示用的水箱内空与水柱摆放
            var inner = FluidDemoTools.DemoTankInner;
            var size = FluidDemoTools.DemoColumnSize;
            var offset = FluidDemoTools.DemoColumnOffset;
            float d = FluidDemoTools.DemoSpacing;

            // Given:  范围**靠真生成一遍点阵来量**，不按"offset 就是最小角"推算：DamBreak 的 z
            //         是居中的，以前这条测试按最小角算，于是 z 轴整整算错一个深度，水柱有 560 个
            //         粒子生在 −z 墙里而测试全绿，直到换成容器代理、由"出生必须在内空里"抓出来
            var set = FluidVolume.DamBreak(size.x, size.y, size.z, d);
            var lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var hi = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < set.Count; i++)
            {
                Vector3 w = offset + set.Positions[i];
                lo = Vector3.Min(lo, w);
                hi = Vector3.Max(hi, w);
            }

            // Then:  ① 水柱整体必须在内腔里（含 +y：不能高出墙顶）
            Assert.GreaterOrEqual(lo.x, -inner.x * 0.5f, "水柱穿出 −x 墙，最低 x = " + lo.x);
            Assert.LessOrEqual(hi.x, inner.x * 0.5f, "水柱穿出 +x 墙，最高 x = " + hi.x);
            Assert.GreaterOrEqual(lo.z, -inner.z * 0.5f, "水柱穿出 −z 墙，最低 z = " + lo.z);
            Assert.LessOrEqual(hi.z, inner.z * 0.5f, "水柱穿出 +z 墙，最高 z = " + hi.z);
            Assert.Less(hi.y, inner.y, "水柱高出墙顶，溃坝直接漫出去");

            // ② 与四面墙各留至少一个粒子间距的间隙。贴着墙面摆放时，粒子到"墙面"和到"地板面"
            //    的距离同时为 0，实体板代理只能按最浅轴 tie-break，推出去的方向是任意的（实测把
            //    整批水从地板底下塞出去，质心 y = −267 m）。容器代理没有这个歧义，但间隙仍然要留：
            //    它是"初始状态合法"的证据，不是碰运气
            Assert.GreaterOrEqual(lo.x - (-inner.x * 0.5f), d, "水柱贴 −x 墙");
            Assert.GreaterOrEqual(inner.x * 0.5f - hi.x, d, "水柱贴 +x 墙（+x 是溃坝出口，也要留间隙免得初始就贴面）");
            Assert.GreaterOrEqual(lo.z - (-inner.z * 0.5f), d, "水柱贴 −z 墙");
            Assert.GreaterOrEqual(inner.z * 0.5f - hi.z, d, "水柱贴 +z 墙");

            // ③ 起始离地板也要有间隙（第一帧就会落下，但不许与地板构成多重 tie）
            Assert.Greater(lo.y, 0f, "水柱最底一层粒子必须高于地板面，实际 " + lo.y);

            // ④ 水量仍然在预算内，且溃坝铺平后至少 4 层深
            float volume = size.x * size.y * size.z;
            int particles = Mathf.CeilToInt(volume / Mathf.Pow(d, 3));
            Assert.LessOrEqual(particles, FluidDemoTools.DemoParticleBudget,
                "演示水量 " + particles + " 粒超出预算 " + FluidDemoTools.DemoParticleBudget);
            Assert.GreaterOrEqual(FluidDemoTools.DemoPoolDepth / d, 4f,
                "静水深 " + FluidDemoTools.DemoPoolDepth + " m 只有 " +
                (FluidDemoTools.DemoPoolDepth / d).ToString("F1") + " 层粒子");
        }
    }
}
