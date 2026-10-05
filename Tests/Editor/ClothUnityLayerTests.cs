// Copyright (c) 2026 PhysicsSimulation. MIT License.
//
// 正式测试：布料的 Unity 层（网格生成 + MonoBehaviour 生命周期 + 障碍物拾取 + Gizmos 数据）。
// 约定：
//   - ClothMeshBuilder 是纯静态层，输入模拟状态、输出网格数据，可直接断言
//   - ClothBehaviour 用 HideFlags.DontSave 的临时对象，TearDown 里 DestroyImmediate
//   - 断言消息用中文

using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace PhysicsSimulation.Editor.Tests
{
    [TestFixture]
    public class ClothMeshBuilderTests
    {
        [Test]
        public void BuildTriangles_QuadSplit_HasTwoTrianglesPerCellAndValidIndices()
        {
            // Given：一个 4 列 3 行的网格
            const int columns = 4;
            const int rows = 3;

            // When：生成三角索引
            int[] indices = ClothMeshBuilder.BuildTriangles(columns, rows);

            // Then：每个格子 2 个三角形 ⇒ 6*(C-1)*(R-1) 个索引，且全部落在顶点范围内
            Assert.That(indices.Length, Is.EqualTo(6 * (columns - 1) * (rows - 1)),
                "4x3 网格应有 6 个格子、12 个三角形、36 个索引（当前 " + indices.Length + "）");
            for (int i = 0; i < indices.Length; i++)
            {
                Assert.That(indices[i], Is.InRange(0, columns * rows - 1),
                    "第 " + i + " 个索引 " + indices[i] + " 越界（顶点数 " + (columns * rows) + "）");
            }
        }

        [Test]
        public void BuildTriangles_WindingIsConsistentEverywhere()
        {
            // Given：同一份 5x5 网格索引
            var indices = ClothMeshBuilder.BuildTriangles(5, 5);

            // When：把每个三角形投到 (col,row) 平面上算带符号面积
            int columns = 5;
            float totalSign = 0f;
            for (int t = 0; t < indices.Length / 3; t++)
            {
                Vector2 a = Cell(indices[t * 3 + 0], columns), b = Cell(indices[t * 3 + 1], columns), c = Cell(indices[t * 3 + 2], columns);
                totalSign += Mathf.Sign((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x));
            }

            // Then：所有三角形同向（否则布会有一半是反的、双面剔除时穿帮）
            Assert.That(Mathf.Abs(totalSign), Is.EqualTo(indices.Length / 3),
                "全部三角形必须同绕序（|Σsign| 应等于三角形数 " + (indices.Length / 3) + "，实测 " + totalSign + "）");
        }

        [Test]
        public void BuildUvs_CoverUnitSquareInOrder()
        {
            // Given：6x4 网格
            var uvs = ClothMeshBuilder.BuildUvs(6, 4);

            // When/Then：uv 数量对齐顶点数，且覆盖 [0,1]；左上角与右下角分别落在两端
            Assert.That(uvs.Length, Is.EqualTo(24), "uv 数应等于顶点数（6x4 = 24）");
            float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
            foreach (var uv in uvs)
            {
                minU = Mathf.Min(minU, uv.x); maxU = Mathf.Max(maxU, uv.x);
                minV = Mathf.Min(minV, uv.y); maxV = Mathf.Max(maxV, uv.y);
                Assert.That(uv.x >= 0f && uv.x <= 1f && uv.y >= 0f && uv.y <= 1f, Is.True, "uv 必须落在 [0,1] 内");
            }
            Assert.That(maxU, Is.EqualTo(1f).Within(1e-5f), "uv 必须铺满 U 方向（贴图/布料 UV 展开才有意义）");
            Assert.That(maxV, Is.EqualTo(1f).Within(1e-5f), "uv 必须铺满 V 方向");
        }

        [Test]
        public void BuildTriangles_MinimumGrid_StillProducesOneQuad()
        {
            // Given：最小的 2x2 网格
            // When
            var indices = ClothMeshBuilder.BuildTriangles(2, 2);

            // Then：正好 2 个三角形（6 个索引）
            Assert.That(indices.Length, Is.EqualTo(6), "2x2 网格只有 1 个格子，应生成 2 个三角形");
        }

        [Test]
        public void BuildTriangles_InvalidGrid_Throws()
        {
            // Given/When/Then：小于 2 的行列没有格子可言，必须直接报错而不是悄悄返回空数组
            Assert.Throws<ArgumentOutOfRangeException>(() => ClothMeshBuilder.BuildTriangles(1, 4),
                "1 列网格无法构成三角形，应抛异常");
            Assert.Throws<ArgumentOutOfRangeException>(() => ClothMeshBuilder.BuildTriangles(4, 0),
                "0 行网格非法，应抛异常");
        }

        [Test]
        public void ApplyPositions_MovesVerticesWithTheCloth()
        {
            // Given：3x3 布，先按初始布局写一次顶点
            var p = new ClothParameters { columns = 3, rows = 3, spacing = 0.2f };
            var cloth = new ClothSimulation(p);
            var vertices = new Vector3[cloth.ParticleCount];
            ClothMeshBuilder.ApplyPositions(cloth, vertices);
            var initial = (Vector3[])vertices.Clone();

            // When：模拟一段，再写一次
            for (int n = 0; n < 20; n++) cloth.Step(1f / 60f);
            ClothMeshBuilder.ApplyPositions(cloth, vertices);

            // Then：自由点跟着走了，且缓冲区被就地复用（没有 NaN）
            Assert.That(vertices[8], Is.Not.EqualTo(initial[8]), "右下角自由质点必须跟着模拟结果移动");
            for (int i = 0; i < vertices.Length; i++)
            {
                Assert.That(float.IsNaN(vertices[i].x) || float.IsNaN(vertices[i].y) || float.IsNaN(vertices[i].z), Is.False,
                    "第 " + i + " 个顶点出现 NaN，网格会被 Unity 直接丢掉");
            }
        }

        static Vector2 Cell(int index, int columns)
        {
            return new Vector2(index % columns, index / columns);
        }
    }

    [TestFixture]
    public class ClothBehaviourTests
    {
        private readonly List<GameObject> _spawned = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _spawned)
            {
                if (go != null) UnityEngine.Object.DestroyImmediate(go);
            }
            _spawned.Clear();
        }

        ClothBehaviour Create(string name, Action<ClothBehaviour> configure = null)
        {
            var go = new GameObject(name);
            go.hideFlags = HideFlags.DontSave;
            _spawned.Add(go);
            var behaviour = go.AddComponent<ClothBehaviour>();
            if (configure != null) configure(behaviour);
            return behaviour;
        }

        [Test]
        public void Rebuild_ValidConfig_BuildsSystemAndMesh()
        {
            // Given：一个默认配置的布料组件
            var behaviour = Create("Cloth");
            behaviour.parameters = new ClothParameters { columns = 8, rows = 6, spacing = 0.1f };
            behaviour.pinEdges = ClothPinEdges.Top;

            // When
            behaviour.Rebuild();

            // Then：系统建起来了，网格顶点数与网格一致，且没有报错
            Assert.That(behaviour.IsBuilt, Is.True, "合法参数应建成系统（LastBuildError = " + behaviour.LastBuildError + "）");
            Assert.That(behaviour.LastBuildError, Is.Null.Or.Empty, "建成时不应留下失败原因");

            var filter = behaviour.GetComponent<MeshFilter>();
            Assert.That(filter, Is.Not.Null, "generateMesh 默认开启，应自动挂上 MeshFilter");
            Assert.That(filter.sharedMesh, Is.Not.Null, "应生成网格");
            Assert.That(filter.sharedMesh.vertexCount, Is.EqualTo(8 * 6), "网格顶点数应等于质点数 8x6 = 48");
            Assert.That(filter.sharedMesh.triangles.Length, Is.EqualTo(6 * (8 - 1) * (6 - 1)),
                "三角索引数应等于 6*(C-1)*(R-1)");
            Assert.That(behaviour.System.HasNonFiniteState(), Is.False, "刚建好的状态必须是有限值");
        }

        [Test]
        public void Rebuild_InvalidConfig_ReportsErrorInsteadOfThrowing()
        {
            // Given：非法参数（1 列）
            var behaviour = Create("BadCloth");
            behaviour.parameters = new ClothParameters { columns = 1, rows = 4 };

            // When：重建（不应抛异常打断播放）
            behaviour.Rebuild();

            // Then：未构建 + 有中文错误说明
            Assert.That(behaviour.IsBuilt, Is.False, "非法参数时不应建成系统");
            Assert.That(string.IsNullOrEmpty(behaviour.LastBuildError), Is.False, "非法参数时必须留下可读的失败原因");
        }

        [Test]
        public void PinEdges_FlagsSelectExactlyTheExpectedParticles()
        {
            // Given：5x5 网格，钉住上边 + 右边
            var behaviour = Create("Pins");
            behaviour.parameters = new ClothParameters { columns = 5, rows = 5, spacing = 0.1f };
            behaviour.pinEdges = ClothPinEdges.Top | ClothPinEdges.Right;
            behaviour.Rebuild();

            // When：数一下钉住的质点
            var system = behaviour.System;
            int pinned = 0;
            for (int i = 0; i < system.ParticleCount; i++) if (system.IsPinned(i)) pinned++;

            // Then：上边 5 个 + 右边 5 个 − 公共角 1 个 = 9 个
            Assert.That(pinned, Is.EqualTo(9), "Top|Right 应钉住 5+5-1=9 个质点（实测 " + pinned + "）");
            Assert.That(system.IsPinned(system.IndexOf(0, 0)), Is.True, "左上角属于上边，必须钉住");
            Assert.That(system.IsPinned(system.IndexOf(4, 4)), Is.True, "右下角属于右边，必须钉住");
            Assert.That(system.IsPinned(system.IndexOf(2, 3)), Is.False, "中间质点不应被钉住");
        }

        [Test]
        public void Step_WhenAutoSimulateOff_RespondsToManualStepOnly()
        {
            // Given：关掉自动模拟
            var behaviour = Create("ManualCloth");
            behaviour.parameters = new ClothParameters { columns = 6, rows = 6, spacing = 0.1f };
            behaviour.autoSimulate = false;
            behaviour.Rebuild();
            var before = behaviour.System.CapturePositions();

            // When：只手动推进 3 步
            for (int n = 0; n < 3; n++) behaviour.Step(1f / 60f);

            // Then：手动步生效了（自由点动过），而组件不会因为 Rebuild 自己偷偷跑
            var after = behaviour.System.CapturePositions();
            Assert.That(after[behaviour.System.ParticleCount - 1], Is.Not.EqualTo(before[before.Length - 1]),
                "手动 Step 之后最后一个质点应该已经动了");
        }

        [Test]
        public void Wind_AppliesLateralDriftOverTime()
        {
            // Given：无重力 + 侧向风
            var behaviour = Create("WindyCloth");
            behaviour.parameters = new ClothParameters
            {
                columns = 6,
                rows = 6,
                spacing = 0.1f,
                gravity = Vector3.zero,
                damping = 0f
            };
            behaviour.pinEdges = ClothPinEdges.Top;
            behaviour.windAcceleration = new Vector3(0f, 0f, 4f);
            behaviour.Rebuild();
            var system = behaviour.System;
            int free = system.IndexOf(3, 5);

            // When
            for (int n = 0; n < 30; n++) behaviour.Step(1f / 60f);

            // Then：风把自由点吹到了 +Z，而钉住的顶边一动不动
            Assert.That(system.GetPosition(free).z, Is.GreaterThan(0.01f),
                "顺风方向必须产生位移（实测 z = " + system.GetPosition(free).z.ToString("F4") + "）");
            Assert.That(system.GetPosition(system.IndexOf(3, 0)).z, Is.EqualTo(0f), "钉住的顶边不应被风吹走");
        }

        [Test]
        public void ObstacleTransform_IsPickedUpInLocalSpace()
        {
            // Given：布料组件 + 一个带 SphereCollider 的障碍物，且布料父级被缩放/平移
            var holder = new GameObject("Holder");
            holder.hideFlags = HideFlags.DontSave;
            _spawned.Add(holder);
            holder.transform.position = new Vector3(2f, 0f, 0f);

            var obstacle = new GameObject("Ball");
            obstacle.hideFlags = HideFlags.DontSave;
            _spawned.Add(obstacle);
            obstacle.transform.SetParent(holder.transform, true);
            obstacle.transform.localPosition = new Vector3(0f, -0.3f, 0f);
            var sphere = obstacle.AddComponent<SphereCollider>();
            sphere.radius = 0.2f;

            var behaviour = Create("ClothWithBall");
            behaviour.parameters = new ClothParameters { columns = 9, rows = 9, spacing = 0.1f, damping = 0f };
            behaviour.pinEdges = ClothPinEdges.None;
            behaviour.obtainObstaclesFromTransforms = true;
            behaviour.obstacles = new List<Transform> { obstacle.transform };
            behaviour.Rebuild();

            // When：模拟几步，检查所有质点都在球的"局部空间"之外
            for (int n = 0; n < 5; n++) behaviour.Step(1f / 60f);
            var system = behaviour.System;
            Vector3 localCenter = behaviour.transform.InverseTransformPoint(obstacle.transform.position);
            float surface = behaviour.transform.InverseTransformVector(obstacle.transform.lossyScale).x * sphere.radius;

            // Then：没有任何质点埋进球里
            int inside = 0;
            for (int i = 0; i < system.ParticleCount; i++)
            {
                if (Vector3.Distance(system.GetPosition(i), localCenter) < surface * 0.98f) inside++;
            }
            Assert.That(inside, Is.EqualTo(0), "障碍物必须按局部空间生效，实测有 " + inside + " 个质点埋进球里");
        }

        [Test]
        public void ResetToInitialLayout_RestoresGridAndStopsMotion()
        {
            // Given：跑了一段、已经晃起来的布
            var behaviour = Create("ResetCloth");
            behaviour.parameters = new ClothParameters { columns = 5, rows = 5, spacing = 0.1f };
            behaviour.pinEdges = ClothPinEdges.Top;
            behaviour.Rebuild();
            for (int n = 0; n < 40; n++) behaviour.Step(1f / 60f);

            // When：复位
            behaviour.ResetToInitialLayout();

            // Then：回到建好时的网格布局（第一点回到原点，顶行仍钉住）
            Assert.That(behaviour.System.GetPosition(0), Is.EqualTo(Vector3.zero), "复位后 0 号质点应回到网格原点");
            Assert.That(behaviour.System.IsPinned(0), Is.True, "复位不应丢掉钉住状态");
            Assert.That(behaviour.System.GetPosition(behaviour.System.IndexOf(2, 4)).y,
                Is.EqualTo(-4 * behaviour.parameters.spacing).Within(1e-5f), "底行（row=4）应回到初始高度 -4*spacing");
        }

        [Test]
        public void GizmoEdges_MatchStructuralConstraintsForWireframeDrawing()
        {
            // Given：6x5 网格
            var behaviour = Create("GizmoCloth");
            behaviour.parameters = new ClothParameters { columns = 6, rows = 5, spacing = 0.1f };
            behaviour.Rebuild();

            // When：向组件要"结构边的端点对"（画线框用）
            var edges = new List<ValueTuple<Vector3, Vector3>>();
            behaviour.CollectStructuralEdges(edges);

            // Then：边的数量 = 网格结构约束数量，每条边两端都非 NaN
            int expectedStructural = 5 * (6 - 1) + 6 * (5 - 1);
            Assert.That(edges.Count, Is.EqualTo(expectedStructural),
                "线框应画出全部结构边（期望 " + expectedStructural + "，实测 " + edges.Count + "）");
            foreach (var edge in edges)
            {
                Assert.That(float.IsNaN(edge.Item1.x) || float.IsNaN(edge.Item2.y), Is.False, "线框端点不能是 NaN");
            }
        }
    }
}
