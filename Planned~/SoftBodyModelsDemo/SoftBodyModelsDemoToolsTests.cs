// Copyright (c) 2026 PhysicsSimulation. MIT License.
//
// v1.5.0「真实模型软体演示」的 BDD 测试。
//
// 设计意图：
//   * 审计页只给数字，看不出观感。这一组用例把"把真实道具摆成一排砸地面"这件事拆成可断言的纯函数：
//       挑哪几个、从多高落、彼此隔多远、相机怎么框 —— 全是可计算的，不靠手调。
//   * 构建核心吃的是 Mesh 列表而不是资产路径，测试因此**不依赖本工程的任何美术资产**
//     （包发布到别人工程里也该绿）。资产路径 → Mesh 的解析单独一条用例覆盖"路径不存在时跳过并上报"。

using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using PhysicsSimulation.EditorTools;
using UnityEditor;
using UnityEngine;

namespace PhysicsSimulation.Editor.Tests
{
    [TestFixture]
    public class SoftBodyModelsDemoToolsTests
    {
        // ==================================================================
        // Feature: 菜单入口
        // ==================================================================

        [Test]
        public void Menus_AreRegisteredAtPriorities125And126()
        {
            // Then:  两个菜单都存在，且优先级接在审计工具（123/124）后面
            Assert.AreEqual(125, SoftBodyModelsDemoTools.CreateMenuPriority, "创建场景菜单优先级");
            Assert.AreEqual(126, SoftBodyModelsDemoTools.BuildMenuPriority, "当前场景构建菜单优先级");

            var create = typeof(SoftBodyModelsDemoTools).GetMethod(
                "CreateRealModelDemoScene", BindingFlags.Public | BindingFlags.Static);
            var build = typeof(SoftBodyModelsDemoTools).GetMethod(
                "BuildRealModelsInCurrentScene", BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(create, "必须有 CreateRealModelDemoScene");
            Assert.IsNotNull(build, "必须有 BuildRealModelsInCurrentScene");

            // Note: 不用反射去读 MenuItem 的字段（Unity 内部属性名不该被测试钉住）；
            //       路径以常量形式暴露，于是这条断言和 [MenuItem] 用的是同一个字符串 ——
            //       而"这个菜单真能被调起来"由生成场景那一步实测验证，不靠测试自证。
            Assert.AreEqual("Tools/Physics Simulation/Soft Body/Create Real Model Demo Scene",
                SoftBodyModelsDemoTools.CreateMenuPath, "创建场景的菜单路径");
            Assert.AreEqual("Tools/Physics Simulation/Soft Body/Build Real Models In Current Scene",
                SoftBodyModelsDemoTools.BuildMenuPath, "当前场景构建的菜单路径");
        }

        // ==================================================================
        // Feature: 默认挑哪几个模型 —— 必须是审计里真的 Healthy 的
        // ==================================================================

        [Test]
        public void DefaultModels_AreEightHealthyProps_AndExcludeTheUnstableOnesINearlyPicked()
        {
            // Then:  8 个、互不重复、都是 Assets 下的 .fbx
            var paths = SoftBodyModelsDemoTools.DefaultModelPaths;
            Assert.AreEqual(8, paths.Length, "默认 8 个（再多就挤满画面，也吃满帧预算）");
            var seen = new HashSet<string>();
            for (int i = 0; i < paths.Length; i++)
            {
                Assert.IsTrue(seen.Add(paths[i]), "默认列表里有重复：" + paths[i]);
                Assert.IsTrue(paths[i].StartsWith("Assets/"), "必须是工程内路径：" + paths[i]);
                Assert.IsTrue(paths[i].EndsWith(".fbx") || paths[i].EndsWith(".FBX"),
                    "默认只挑模型资产：" + paths[i]);
            }

            // And:  含体量最小的桶和最大的桥 —— 尺寸跨度正是这个演示要看的东西
            StringAssert.Contains("prop_barrel", paths[0] + paths[1] + paths[2] + paths[3] + paths[4]
                + paths[5] + paths[6] + paths[7], "桶该在列表里");

            // But:  这两个**不能**出现 —— prop_rock_c 实测 Unstable（体积保持 0.583，12 质点），
            //       prop_clock_tower 实测跑着跑着翻了面（保持率 -1.308）。
            //       它们曾经被我当成 Healthy 写进候选名单，所以这条断言就是那一笔错误的墓碑。
            for (int i = 0; i < paths.Length; i++)
            {
                StringAssert.DoesNotContain("prop_rock_c", paths[i],
                    "prop_rock_c 审计判定是 Unstable，不该进'健康的真实模型'演示：" + paths[i]);
                StringAssert.DoesNotContain("prop_clock_tower", paths[i],
                    "prop_clock_tower 审计判定是 Unstable（翻了面）：" + paths[i]);
            }
        }

        // ==================================================================
        // Feature: 落点高度与间距 —— 由纯函数给，不手调
        // ==================================================================

        [Test]
        public void DropHeights_AreStrictlyIncreasingSoTheyLandAtDifferentTimes()
        {
            // Then:  单调上升、彼此不同、都在合理范围内（太低看不出砸，高过头会跑出相机）
            float previous = -1f;
            for (int i = 0; i < 8; i++)
            {
                float h = SoftBodyModelsDemoTools.DropHeightFor(i);
                Assert.Greater(h, previous, "高度必须逐个递增，第 " + i + " 个：" + h);
                Assert.GreaterOrEqual(h, 1.2f, "落下要看得出形变，至少 1.2 米，第 " + i + " 个：" + h);
                Assert.LessOrEqual(h, 9f, "太高会掉出相机视野，第 " + i + " 个：" + h);
                previous = h;
            }
        }

        [Test]
        public void PlacementFor_SeparatesBodiesByTheirOwnBounds()
        {
            // Given: 三个宽度差很多的网格（0.9 米桶 / 6.5 米桥 / 1.5 米营火）
            var sizes = new[] { new Vector3(0.91f, 0.95f, 0.96f),
                                new Vector3(6.51f, 1.33f, 1.81f),
                                new Vector3(1.50f, 0.73f, 1.51f) };

            // When:  逐个算摆位
            var first = SoftBodyModelsDemoTools.PlacementFor(0, sizes[0], 0f, out float cursor0);
            var second = SoftBodyModelsDemoTools.PlacementFor(1, sizes[1], cursor0, out float cursor1);
            var third = SoftBodyModelsDemoTools.PlacementFor(2, sizes[2], cursor1, out float cursor2);

            // Then:  相邻两个的 x 间隔 ≥ 两者半宽之和（出生就不互相插进对方身体）
            Assert.GreaterOrEqual(second.x - first.x, 0.5f * (sizes[0].x + sizes[1].x),
                "第一个与第二个的重叠了：间距 " + (second.x - first.x));
            Assert.GreaterOrEqual(third.x - second.x, 0.5f * (sizes[1].x + sizes[2].x),
                "第二个与第三个的重叠了：间距 " + (third.x - second.x));

            // And:  y = 落点高度 + 半高 ⇒ 网格**底面**正好在落点高度上，不是中心
            Assert.AreEqual(SoftBodyModelsDemoTools.DropHeightFor(1) + 0.5f * sizes[1].y, second.y, 1e-4f,
                "y 要给底面留出高度，实际 " + second.y);
        }

        // ==================================================================
        // Feature: 构建核心 —— 吃 Mesh 列表，不依赖本工程资产
        // ==================================================================

        [Test]
        public void BuildModels_CreatesOneFreeFallingCollidingSoftBodyPerMesh()
        {
            // Given: 三块程序生成的闭合长方体 + 一个带 BoxCollider 的地面
            var root = new GameObject("RealModelDemo");
            var ground = MakeGround();
            var meshes = new List<Mesh> { MakeBoxMesh(0.9f, 0.9f, 0.9f),
                                          MakeBoxMesh(1.2f, 0.6f, 1.2f),
                                          MakeBoxMesh(0.7f, 1.1f, 0.7f) };

            // When:  构建
            int built = SoftBodyModelsDemoTools.BuildModels(root.transform, meshes, ground);
            var bodies = root.GetComponentsInChildren<SoftBodyBehaviour>(true);

            // Then:  一块网格一个软体，全部"不钉住 + 开地面碰撞"
            Assert.AreEqual(3, built, "一块网格一个软体");
            Assert.AreEqual(3, bodies.Length, "实际建出 " + bodies.Length + " 个");
            var ys = new HashSet<float>();
            for (int i = 0; i < bodies.Length; i++)
            {
                Assert.AreEqual(SoftBodyPinMode.None, bodies[i].pinMode,
                    "演示要的是整块砸地面，不该钉住任何质点：" + bodies[i].name);
                Assert.IsTrue(bodies[i].collideWithSceneColliders,
                    "不开场景碰撞就只是穿过地面的演示：" + bodies[i].name);
                Assert.AreEqual(1, bodies[i].sceneColliders.Count,
                    "地面必须填进列表：" + bodies[i].name);
                Assert.AreSame(ground, bodies[i].sceneColliders[0], "填的就得是那块地面");
                Assert.IsNotNull(bodies[i].sourceMesh, "源网格必须挂上");
                ys.Add(Mathf.Round(bodies[i].transform.position.y * 1000f));
            }
            Assert.AreEqual(3, ys.Count, "三块的高度必须各不相同，否则看不出'逐个落地'的时间差");

            UnityEngine.Object.DestroyImmediate(root);
            UnityEngine.Object.DestroyImmediate(ground.gameObject);
        }

        [Test]
        public void ResolveSpecs_SkipsMissingPathsAndSaysSo()
        {
            // Given: 一个绝对不存在的路径
            // When:  解析成 Mesh 列表
            var meshes = SoftBodyModelsDemoTools.ResolveMeshes(
                new[] { "Assets/This/Does/Not/Exist_no_such_thing.fbx" }, out string error);

            // Then:  返回空而不是抛异常，且原因里带上那个路径
            Assert.AreEqual(0, meshes.Count, "不存在的路径不该造出任何东西");
            Assert.IsNotEmpty(error, "必须报告为什么是空的");
            StringAssert.Contains("Does/Not/Exist", error, "原因要点名是哪个路径：" + error);
        }

        [Test]
        public void CameraPose_FramesWholeRowAndLooksAtIt()
        {
            // Given: 一排摆位（x 拉开、高度不同）
            var positions = new List<Vector3>
            {
                new Vector3(0f, 2f, 0f), new Vector3(6f, 3f, 0f), new Vector3(14f, 4f, 0f)
            };

            // When:  求相机
            SoftBodyModelsDemoTools.CameraPose(positions, out Vector3 camPos, out Quaternion camRot);

            // Then:  看向整排的中心（前方向 = 中心 - 相机位置，归一化后与旋转前方向一致）
            var center = (positions[0] + positions[1] + positions[2]) / 3f;
            var forward = camRot * Vector3.forward;
            var toCenter = (center - camPos).normalized;
            Assert.Greater(Vector3.Dot(forward, toCenter), 0.98f,
                "相机没对着这排模型：" + forward + " vs " + toCenter);
            Assert.Greater(camPos.y, center.y, "演示要俯视一点才能同时看到落地瞬间和地面");
            float spread = positions[2].x - positions[0].x;
            Assert.Greater(Vector3.Distance(camPos, center), spread * 0.5f,
                "整排宽度 " + spread + " 米，相机退得不够远会裁掉两头");

            // And:  一排越宽，相机退得越远（用前两个 = 真更窄的一排做对照）
            var narrowCenter = (positions[0] + positions[1]) * 0.5f;
            SoftBodyModelsDemoTools.CameraPose(new List<Vector3> { positions[0], positions[1] },
                out Vector3 narrowPos, out Quaternion _);
            Assert.Less(Vector3.Distance(narrowPos, narrowCenter),
                Vector3.Distance(camPos, center), "窄排的相机应该更近");
        }

        // ==================================================================
        // 测试辅助
        // ==================================================================

        static Collider MakeGround()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "Ground";
            go.transform.position = new Vector3(0f, -0.5f, 0f);
            go.transform.localScale = new Vector3(40f, 1f, 40f);
            return go.GetComponent<Collider>();
        }

        /// <summary>面拆顶点的闭合长方体（24 顶点 / 12 三角形，绕序朝外）—— 和软体测试里同一套。</summary>
        static Mesh MakeBoxMesh(float w, float h, float d)
        {
            float hx = w * 0.5f, hy = h * 0.5f, hz = d * 0.5f;
            var c = new[]
            {
                new Vector3(-hx, -hy, -hz), new Vector3(hx, -hy, -hz),
                new Vector3(hx, hy, -hz), new Vector3(-hx, hy, -hz),
                new Vector3(-hx, -hy, hz), new Vector3(hx, -hy, hz),
                new Vector3(hx, hy, hz), new Vector3(-hx, hy, hz)
            };
            int[][] faces =
            {
                new[] { 0, 3, 2, 1 }, new[] { 4, 5, 6, 7 }, new[] { 0, 4, 7, 3 },
                new[] { 1, 2, 6, 5 }, new[] { 0, 1, 5, 4 }, new[] { 3, 7, 6, 2 }
            };

            var verts = new List<Vector3>();
            var tris = new List<int>();
            foreach (var face in faces)
            {
                int start = verts.Count;
                for (int i = 0; i < 4; i++) verts.Add(c[face[i]]);
                tris.Add(start); tris.Add(start + 1); tris.Add(start + 2);
                tris.Add(start); tris.Add(start + 2); tris.Add(start + 3);
            }

            var mesh = new Mesh();
            mesh.vertices = verts.ToArray();       // 必须先 vertices 后 triangles
            mesh.triangles = tris.ToArray();
            mesh.RecalculateNormals();
            return mesh;
        }
    }
}
