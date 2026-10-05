// Copyright (c) 2026 PhysicsSimulation. MIT License.
//
// v1.2.0 软体 Unity 层 BDD 测试：SoftBodyBehaviour + 网格写回 + 钉住 + Gizmos 数据。
//
// 约定（先写清楚，测试按这个断言）：
//   * 模拟发生在**组件自身的局部空间**：源网格顶点原样进求解器，Transform 的移动不参与力学。
//     所以"把 GameObject 搬走"不会让软体受任何影响，这点必须锁死，否则用户没法用动画摆位。
//   * 网格写回是"按原始网格顶点展开"的：源网格 24 个顶点，写回后还是 24 个，拓扑（三角形索引）一字不改。
//   * 构建失败时不抛异常打断游戏，而是把原因写进 LastBuildError，IsBuilt 保持 false。

using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace PhysicsSimulation.Editor.Tests
{
    [TestFixture]
    public class SoftBodyUnityLayerTests
    {
        // Given: 一个带 MeshFilter / MeshRenderer 的 GameObject 与一份三角化球体网格
        //  When: Rebuild
        //  Then: IsBuilt 为真、质点数等于焊接后的顶点数、MeshFilter 拿到一份与源网格同拓扑的实例网格
        [Test]
        public void Rebuild_ValidMesh_BuildsSystemAndInstancedMesh()
        {
            var go = NewGameObject();
            var behaviour = go.AddComponent<SoftBodyBehaviour>();
            behaviour.sourceMesh = MakeSphereMesh(20);          // 正二十面体：20 个三角形、12 个顶点
            behaviour.Rebuild();

            Assert.That(behaviour.IsBuilt, Is.True, "Rebuild 应当成功，LastBuildError = " + behaviour.LastBuildError);
            Assert.That(behaviour.LastBuildError, Is.Null.Or.Empty, "成功时不该留下错误信息");
            Assert.That(behaviour.Simulation.ParticleCount, Is.EqualTo(12), "二十面体球 12 个顶点互不重合 ⇒ 12 个质点");

            var filter = go.GetComponent<MeshFilter>();
            Assert.That(filter, Is.Not.Null, "Rebuild 应当补上 MeshFilter");
            Assert.That(filter.sharedMesh, Is.Not.Null, "MeshFilter 必须挂上实例网格");
            Assert.That(filter.sharedMesh.vertexCount, Is.EqualTo(behaviour.sourceMesh.vertexCount),
                "写回网格的顶点数必须与源网格一致，否则索引会错位");
            Assert.That(filter.sharedMesh.triangles, Is.EqualTo(behaviour.sourceMesh.triangles),
                "拓扑必须一字不改地沿用源网格的三角形索引");
            Assert.That(filter.sharedMesh, Is.Not.SameAs(behaviour.sourceMesh),
                "不能直接改用户的网格资源，必须是自己那份实例网格");

            UnityEngine.Object.DestroyImmediate(go);
        }

        // Given: 建好的软体 + 重力
        //  When: Step 若干次
        //  Then: 实例网格的顶点真的跟着质点动了（写回链路通），且法线被重算
        [Test]
        public void Step_Gravity_WritesPositionsBackIntoInstancedMesh()
        {
            var go = NewGameObject();
            var behaviour = go.AddComponent<SoftBodyBehaviour>();
            behaviour.sourceMesh = MakeOctahedronMesh();
            behaviour.parameters.gravity = new Vector3(0f, -9.81f, 0f);
            behaviour.parameters.springStiffness = 150f;
            behaviour.parameters.volumeStiffness = 800f;
            behaviour.Rebuild();

            var filter = go.GetComponent<MeshFilter>();
            var before = filter.sharedMesh.vertices;
            for (int i = 0; i < 30; i++) behaviour.Step(1f / 60f);
            var after = filter.sharedMesh.vertices;

            Assert.That(after.Length, Is.EqualTo(before.Length));
            bool moved = false;
            for (int i = 0; i < before.Length; i++)
                if ((after[i] - before[i]).sqrMagnitude > 1e-8f) moved = true;
            Assert.That(moved, Is.True, "Step 之后实例网格的顶点必须跟着质点动，否则画面完全不动");

            float minY = float.MaxValue;
            for (int i = 0; i < after.Length; i++) minY = Mathf.Min(minY, after[i].y);
            Assert.That(minY, Is.LessThan(-1f), "重力下底部应当掉到 -1 以下，实际 " + minY);

            var normals = filter.sharedMesh.normals;
            Assert.That(normals.Length, Is.EqualTo(after.Length), "recalculateNormals 打开时必须给出法线");
            for (int i = 0; i < normals.Length; i++)
                Assert.That(normals[i].sqrMagnitude, Is.GreaterThan(0.5f), "法线 " + i + " 退化成零向量");

            UnityEngine.Object.DestroyImmediate(go);
        }

        // Given: 建好的软体，跑了一段时间
        //  When: ResetToInitialLayout
        //  Then: 质点与网格顶点都回到构建时的位置，速度为 0
        [Test]
        public void ResetToInitialLayout_RestoresMeshVerticesAndStopsMotion()
        {
            var go = NewGameObject();
            var behaviour = go.AddComponent<SoftBodyBehaviour>();
            behaviour.sourceMesh = MakeOctahedronMesh();
            behaviour.parameters.gravity = new Vector3(0f, -9.81f, 0f);
            behaviour.parameters.springStiffness = 150f;
            behaviour.Rebuild();

            var filter = go.GetComponent<MeshFilter>();
            var original = filter.sharedMesh.vertices.Clone() as Vector3[];
            for (int i = 0; i < 30; i++) behaviour.Step(1f / 60f);

            behaviour.ResetToInitialLayout();
            var restored = filter.sharedMesh.vertices;
            for (int i = 0; i < original.Length; i++)
            {
                Assert.That(restored[i].x, Is.EqualTo(original[i].x), "顶点 " + i + " 的 x 没回到初始值");
                Assert.That(restored[i].y, Is.EqualTo(original[i].y), "顶点 " + i + " 的 y 没回到初始值");
                Assert.That(restored[i].z, Is.EqualTo(original[i].z), "顶点 " + i + " 的 z 没回到初始值");
            }
            for (int i = 0; i < behaviour.Simulation.ParticleCount; i++)
                Assert.That(behaviour.Simulation.GetVelocity(i).sqrMagnitude, Is.EqualTo(0f),
                    "复位之后质点 " + i + " 的速度必须为 0");

            UnityEngine.Object.DestroyImmediate(go);
        }

        // Given: 顶面质点被钉住的长方体软体
        //  When: 施重力 Step
        //  Then: 钉住的顶点纹丝不动，其余顶点下坠；体积保持在静止值附近
        [Test]
        public void PinTopVertices_StaysPutWhileBodySags()
        {
            var go = NewGameObject();
            var behaviour = go.AddComponent<SoftBodyBehaviour>();
            behaviour.sourceMesh = MakeBoxMesh(2f, 1f, 0.5f);
            behaviour.pinMode = SoftBodyPinMode.TopVertices;
            behaviour.parameters.gravity = new Vector3(0f, -9.81f, 0f);
            behaviour.parameters.springStiffness = 120f;
            behaviour.parameters.bendStiffness = 20f;
            behaviour.parameters.volumeStiffness = 600f;
            behaviour.Rebuild();

            Assert.That(behaviour.PinnedParticleCount, Is.EqualTo(4), "长方体顶面 4 个角应当被钉住");

            var pinnedPositions = new List<Vector3>();
            for (int i = 0; i < behaviour.Simulation.ParticleCount; i++)
                if (behaviour.Simulation.IsPinned(i)) pinnedPositions.Add(behaviour.Simulation.GetPosition(i));

            for (int i = 0; i < 60; i++) behaviour.Step(1f / 60f);

            int k = 0;
            for (int i = 0; i < behaviour.Simulation.ParticleCount; i++)
            {
                if (!behaviour.Simulation.IsPinned(i)) continue;
                Assert.That((behaviour.Simulation.GetPosition(i) - pinnedPositions[k++]).sqrMagnitude,
                    Is.EqualTo(0f), "钉住的质点 " + i + " 被移动了");
            }

            float lowest = float.MaxValue;
            for (int i = 0; i < behaviour.Simulation.ParticleCount; i++)
                lowest = Mathf.Min(lowest, behaviour.Simulation.GetPosition(i).y);
            Assert.That(lowest, Is.LessThan(-0.55f), "底面应当明显下坠，实际最低 " + lowest);

            float rest = behaviour.Simulation.RestVolume();
            Assert.That(rest, Is.GreaterThan(0f), "长方体是闭合网格，必须有静止体积");
            Assert.That(behaviour.Simulation.Volume(), Is.GreaterThan(rest * 0.6f), "下垂时体积不能塌掉");

            UnityEngine.Object.DestroyImmediate(go);
        }

        // Given: 一个源网格为 null（或没有三角形）的组件
        //  When: Rebuild / Update
        //  Then: 不抛异常打断流程，IsBuilt 为 false，LastBuildError 写清楚原因，Step 直接返回
        [Test]
        public void Rebuild_InvalidMesh_RecordsErrorWithoutThrowing()
        {
            var go = NewGameObject();
            var behaviour = go.AddComponent<SoftBodyBehaviour>();
            behaviour.sourceMesh = null;

            behaviour.Rebuild();
            Assert.That(behaviour.IsBuilt, Is.False, "没有源网格时不该声称构建成功");
            Assert.That(behaviour.LastBuildError, Is.Not.Null.And.Not.Empty, "必须把失败原因写进 LastBuildError");
            Assert.DoesNotThrow(() => behaviour.Step(1f / 60f), "没建好时 Step 应当静默返回而不是抛异常");

            var empty = new Mesh();
            empty.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };   // 有顶点没三角形
            behaviour.sourceMesh = empty;
            behaviour.Rebuild();
            Assert.That(behaviour.IsBuilt, Is.False, "没有三角形的网格不能软体化");
            Assert.That(behaviour.LastBuildError, Does.Contain("三角形"), "错误信息要指出是三角形的问题");

            UnityEngine.Object.DestroyImmediate(go);
        }

        // Given: 建好的软体
        //  When: 把 GameObject 搬走 / 旋转 / 缩放
        //  Then: 质点位置（局部空间）一字不变 —— 力学完全在局部空间，Transform 只是摆位
        [Test]
        public void MovingTheTransform_DoesNotDisturbLocalSimulation()
        {
            var go = NewGameObject();
            var behaviour = go.AddComponent<SoftBodyBehaviour>();
            behaviour.sourceMesh = MakeOctahedronMesh();
            behaviour.parameters.gravity = Vector3.zero;
            behaviour.Rebuild();

            var before = behaviour.Simulation.CapturePositions();
            go.transform.position = new Vector3(12f, -3f, 7f);
            go.transform.rotation = Quaternion.Euler(37f, 118f, -55f);
            go.transform.localScale = new Vector3(2.5f, 0.4f, 1.7f);
            behaviour.Step(1f / 60f);
            var after = behaviour.Simulation.CapturePositions();

            for (int i = 0; i < before.Length; i++)
            {
                Assert.That(after[i].x, Is.EqualTo(before[i].x), "搬动 Transform 之后质点 " + i + " 的 x 变了（局部空间模拟被污染）");
                Assert.That(after[i].y, Is.EqualTo(before[i].y), "搬动 Transform 之后质点 " + i + " 的 y 变了");
                Assert.That(after[i].z, Is.EqualTo(before[i].z), "搬动 Transform 之后质点 " + i + " 的 z 变了");
            }

            UnityEngine.Object.DestroyImmediate(go);
        }

        // Given: 建好的软体
        //  When: CollectEdges 收集结构边
        //  Then: 条数等于结构弹簧数，每条线段两端都是有限值，且端点确实落在质点位置上
        [Test]
        public void CollectEdges_ReturnsFiniteStructuralSegments()
        {
            var go = NewGameObject();
            var behaviour = go.AddComponent<SoftBodyBehaviour>();
            behaviour.sourceMesh = MakeOctahedronMesh();
            behaviour.Rebuild();

            var segments = new List<ValueTuple<Vector3, Vector3>>();
            behaviour.CollectEdges(segments, false);
            Assert.That(segments.Count, Is.EqualTo(behaviour.Simulation.StructuralSpringCount),
                "只收集结构边时条数必须等于结构弹簧数");
            foreach (var segment in segments)
            {
                Assert.That(float.IsNaN(segment.Item1.x) || float.IsNaN(segment.Item2.y), Is.False, "线段含 NaN");
                Assert.That(segment.Item1, Is.Not.EqualTo(segment.Item2), "线段两端重合说明索引串了");
            }

            var withBend = new List<ValueTuple<Vector3, Vector3>>();
            behaviour.CollectEdges(withBend, true);
            Assert.That(withBend.Count,
                Is.EqualTo(behaviour.Simulation.StructuralSpringCount + behaviour.Simulation.BendSpringCount),
                "includeBend 为真时还要加上弯曲弹簧");

            UnityEngine.Object.DestroyImmediate(go);
        }

        // Given: 组件上勾掉了 generateMesh
        //  When: Rebuild
        //  Then: 只建求解器、不生成实例网格（用户想自己画网格 / 只要物理时不必浪费一份 Mesh）
        [Test]
        public void GenerateMeshDisabled_BuildsSolverWithoutMesh()
        {
            var go = NewGameObject();
            var behaviour = go.AddComponent<SoftBodyBehaviour>();
            behaviour.sourceMesh = MakeOctahedronMesh();
            behaviour.generateMesh = false;
            behaviour.Rebuild();

            Assert.That(behaviour.IsBuilt, Is.True, "关掉网格生成也应当有物理");
            Assert.That(behaviour.Mesh, Is.Null, "generateMesh 为假时不该造实例网格");
            Assert.That(go.GetComponent<MeshFilter>().sharedMesh, Is.Null, "更不该往 MeshFilter 上塞网格");

            UnityEngine.Object.DestroyImmediate(go);
        }

        // Given: 两个组件实例
        //  When: 各自 Rebuild 同一个源网格
        //  Then: 各自持有独立的模拟与实例网格，互不干扰（同一份网格做两块布的常见用法）
        [Test]
        public void TwoInstances_SimulateIndependently()
        {
            var mesh = MakeOctahedronMesh();

            var a = NewGameObject();
            var ba = a.AddComponent<SoftBodyBehaviour>();
            ba.sourceMesh = mesh;
            ba.parameters.gravity = new Vector3(0f, -9.81f, 0f);
            ba.Rebuild();

            var b = NewGameObject();
            var bb = b.AddComponent<SoftBodyBehaviour>();
            bb.sourceMesh = mesh;
            bb.parameters.gravity = Vector3.zero;
            bb.Rebuild();

            for (int i = 0; i < 30; i++) { ba.Step(1f / 60f); bb.Step(1f / 60f); }

            Assert.That(ba.Simulation.GetPosition(0).y, Is.LessThan(bb.Simulation.GetPosition(0).y - 0.01f),
                "有重力的那个实例应当明显更低");
            Assert.That(ba.Mesh, Is.Not.SameAs(bb.Mesh), "两个实例不能共用同一份可写网格");
            Assert.That(mesh.vertices[0].y, Is.EqualTo(0f).Within(1e-5f), "绝不能把用户的源网格资源改脏");

            UnityEngine.Object.DestroyImmediate(a);
            UnityEngine.Object.DestroyImmediate(b);
            UnityEngine.Object.DestroyImmediate(mesh);
        }

        // Given: autoSimulate 关掉的组件
        //  When: Rebuild 后什么都不做，再显式 Step
        //  Then: Rebuild 不会偷偷推进模拟；只有显式 Step 才改变姿态（定步长 / 回放 / 网络同步要靠这个）
        [Test]
        public void AutoSimulateDisabled_LeavesControlToCaller()
        {
            var go = NewGameObject();
            var behaviour = go.AddComponent<SoftBodyBehaviour>();
            behaviour.sourceMesh = MakeOctahedronMesh();
            behaviour.parameters.gravity = new Vector3(0f, -9.81f, 0f);
            behaviour.autoSimulate = false;
            behaviour.Rebuild();

            var afterBuild = behaviour.Simulation.CapturePositions();
            Assert.That(behaviour.Simulation.GetPosition(0).y, Is.EqualTo(0f).Within(1e-5f),
                "Rebuild 之后不该已经被推进过一帧");

            behaviour.Step(1f / 60f);
            // 质点 3 是 -Y 那个顶点：显式 Step 之后必须被重力拽下去
            Assert.That(behaviour.Simulation.GetPosition(3).y, Is.LessThan(0f),
                "显式 Step 之后，底部质点必须被重力拽下去");
            Assert.That(behaviour.Simulation.GetPosition(3).y,
                Is.GreaterThan(behaviour.Simulation.GetPosition(2).y - 2f),
                "一步的位移不该超过 2 米（子步与 dt 钳制没生效）");

            UnityEngine.Object.DestroyImmediate(go);
        }

        // ==================================================================
        // 辅助
        // ==================================================================

        static GameObject NewGameObject()
        {
            var go = new GameObject("SoftBodyTestSubject");
            go.AddComponent<MeshFilter>();
            go.AddComponent<MeshRenderer>();
            return go;
        }

        // Given：带初速度的软体组件 —— 演示里"推一把"必须可序列化，
        //        否则 Play 时 Awake 用源网格重建模拟，扰动当场丢光，画面就成了静态
        //  When：Rebuild
        //  Then：未钉住的质点带上了这个速度，钉住的仍然是零
        [Test]
        public void InitialVelocity_IsAppliedToFreeParticlesOnRebuild()
        {
            var go = NewGameObject();
            var behaviour = go.AddComponent<SoftBodyBehaviour>();
            behaviour.sourceMesh = MakeOctahedronMesh();
            behaviour.parameters.gravity = new Vector3(0f, -9.81f, 0f);
            behaviour.pinMode = SoftBodyPinMode.TopVertices;
            behaviour.initialVelocity = new Vector3(0.7f, 0f, -0.3f);
            behaviour.Rebuild();

            Assert.That(behaviour.IsBuilt, Is.True, "构建应成功，原因：" + behaviour.LastBuildError);
            Assert.That(behaviour.PinnedParticleCount, Is.EqualTo(1), "八面体只有 +Y 那个顶点在顶面");

            var system = behaviour.Simulation;
            Assert.That(system.GetVelocity(2), Is.EqualTo(Vector3.zero), "钉住的质点不该被推");

            int free = 0;
            for (int i = 0; i < system.ParticleCount; i++)
            {
                if (system.IsPinned(i)) continue;
                Assert.That(system.GetVelocity(i), Is.EqualTo(new Vector3(0.7f, 0f, -0.3f)),
                    "未钉住的质点 " + i + " 应带上 initialVelocity");
                free++;
            }
            Assert.That(free, Is.EqualTo(system.ParticleCount - 1), "所有自由质点都要覆盖到");

            UnityEngine.Object.DestroyImmediate(go);
        }

        static Mesh MakeOctahedronMesh()
        {
            var mesh = new Mesh
            {
                vertices = new[]
                {
                    new Vector3(1f, 0f, 0f), new Vector3(-1f, 0f, 0f),
                    new Vector3(0f, 1f, 0f), new Vector3(0f, -1f, 0f),
                    new Vector3(0f, 0f, 1f), new Vector3(0f, 0f, -1f)
                },
                triangles = new[]
                {
                    0, 2, 4, 4, 2, 1, 1, 2, 5, 5, 2, 0,
                    4, 3, 0, 1, 3, 4, 5, 3, 1, 0, 3, 5
                }
            };
            mesh.RecalculateBounds();
            return mesh;
        }

        static Mesh MakeBoxMesh(float width, float height, float depth)
        {
            float hx = width * 0.5f, hy = height * 0.5f, hz = depth * 0.5f;
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
            mesh.vertices = verts.ToArray();
            mesh.triangles = tris.ToArray();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>正二十面体球（12 个顶点位置互不重合 ⇒ 焊接后正好 12 个质点）。</summary>
        static Mesh MakeSphereMesh(int trianglesTarget)
        {
            float t = (1f + Mathf.Sqrt(5f)) * 0.5f;
            var verts = new List<Vector3>
            {
                new Vector3(-1f, t, 0f), new Vector3(1f, t, 0f), new Vector3(-1f, -t, 0f), new Vector3(1f, -t, 0f),
                new Vector3(0f, -1f, t), new Vector3(0f, 1f, t), new Vector3(0f, -1f, -t), new Vector3(0f, 1f, -t),
                new Vector3(t, 0f, -1f), new Vector3(t, 0f, 1f), new Vector3(-t, 0f, -1f), new Vector3(-t, 0f, 1f)
            };
            var tris = new List<int>
            {
                0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11,
                1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
                3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9,
                4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1
            };
            for (int i = 0; i < verts.Count; i++) verts[i] = verts[i].normalized;

            var mesh = new Mesh();
            mesh.vertices = verts.ToArray();
            mesh.triangles = tris.ToArray();
            mesh.RecalculateBounds();
            Assert.That(mesh.triangles.Length / 3, Is.EqualTo(trianglesTarget), "测试网格面数不符");
            return mesh;
        }
    }
}
