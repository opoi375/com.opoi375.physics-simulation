// Copyright (c) 2026 PhysicsSimulation. MIT License.
//
// 正式测试：布料演示工具 Tools ▸ Physics Simulation ▸ Cloth ▸ * 的构建结果。
// 只验证"搭出来的东西结构对不对、跑不跑得稳"，不在测试里写 Assets/Scenes 文件（避免污染用户工程）。

using System;
using System.Collections.Generic;
using NUnit.Framework;
using PhysicsSimulation.EditorTools;
using UnityEngine;

namespace PhysicsSimulation.Editor.Tests
{
    [TestFixture]
    public class ClothDemoToolsTests
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

        ClothBehaviour BuildHiddenCloth()
        {
            var behaviour = ClothDemoTools.BuildCloth();
            behaviour.gameObject.hideFlags = HideFlags.DontSave;
            _spawned.Add(behaviour.gameObject);

            // 障碍物是 BuildCloth 里另建的 primitive，一并藏起来免得被别的测试 Find 到
            var obstacle = behaviour.transform.Find("ClothObstacle");
            if (obstacle != null)
            {
                obstacle.gameObject.hideFlags = HideFlags.DontSave;
                _spawned.Add(obstacle.gameObject);      // 它是 root 的子物体，销毁 root 时会一起走
            }
            return behaviour;
        }

        [Test]
        public void BuildCloth_CreatesConfiguredGridWithTopEdgePinned()
        {
            // Given：菜单工具建出来的演示布料
            var behaviour = BuildHiddenCloth();

            // When：读它的系统
            var system = behaviour.System;

            // Then：20x14 = 280 质点，顶边 20 个钉住，三类约束齐备
            Assert.That(behaviour.IsBuilt, Is.True, "演示布料应构建成功，原因：" + behaviour.LastBuildError);
            Assert.That(system.ParticleCount, Is.EqualTo(20 * 14), "演示布料应是 20x14 网格");

            int pinned = 0;
            for (int i = 0; i < system.ParticleCount; i++) if (system.IsPinned(i)) pinned++;
            Assert.That(pinned, Is.EqualTo(20), "只钉顶边 ⇒ 应有 20 个固定点（实测 " + pinned + "）");

            int structural = 0, shear = 0, bend = 0;
            for (int i = 0; i < system.Constraints.Count; i++)
            {
                var c = system.Constraints[i];
                if (c.type == ClothConstraintType.Structural) structural++;
                else if (c.type == ClothConstraintType.Shear) shear++;
                else bend++;
            }
            Assert.That(structural, Is.EqualTo(14 * 19 + 20 * 13), "结构约束数量应与网格拓扑一致");
            Assert.That(shear, Is.EqualTo(2 * 19 * 13), "剪切约束数量应与格子数一致");
            Assert.That(bend, Is.EqualTo(14 * 18 + 20 * 12), "弯曲约束数量应与「隔一个」的邻居数一致");
        }

        [Test]
        public void BuildCloth_AttachesMeshMatchingTheGrid()
        {
            // Given
            var behaviour = BuildHiddenCloth();

            // When
            var filter = behaviour.GetComponent<MeshFilter>();
            var mesh = filter != null ? filter.sharedMesh : null;

            // Then：网格挂上了，而且顶点/三角数量与网格拓扑一致
            Assert.That(filter, Is.Not.Null, "演示布料必须自动挂 MeshFilter");
            Assert.That(mesh, Is.Not.Null, "MeshFilter 必须真的拿到网格（忘了赋值就是「看得见组件、看不见布」）");
            Assert.That(mesh.vertexCount, Is.EqualTo(20 * 14), "顶点数应等于质点数");
            Assert.That(mesh.triangles.Length, Is.EqualTo(6 * 19 * 13), "三角索引数应等于 6*(C-1)*(R-1)");
        }

        [Test]
        public void BuildCloth_ObstacleIsPickedUpWithTheIntendedWorldRadius()
        {
            // Given：带障碍物球的演示布料
            var behaviour = BuildHiddenCloth();
            Transform obstacle = behaviour.transform.Find("ClothObstacle");

            // When：走一步（每步开头才会把 Transform 换算成局部球体）
            behaviour.Step(1f / 60f);

            // Then：系统里正好一个障碍物，且换算回世界半径 ≈ 0.34
            Assert.That(obstacle, Is.Not.Null, "BuildCloth 应在布料物体下建一个 ClothObstacle");
            Assert.That(behaviour.System.ObstacleCount, Is.EqualTo(1), "每步应重新拾取到 1 个障碍物");

            var sphere = obstacle.GetComponent<SphereCollider>();
            float worldRadius = sphere.radius * obstacle.lossyScale.magnitude / 1.7320508f;
            Assert.That(worldRadius, Is.EqualTo(0.34f).Within(1e-3f),
                "球的世界半径应是 0.34（实测 " + worldRadius.ToString("F4") + "），否则演示里的「裹球」高度就变了");
        }

        [Test]
        public void BuildCloth_RunsManyStepsWithoutBlowingUp()
        {
            // Given
            var behaviour = BuildHiddenCloth();
            var system = behaviour.System;

            // When：连续手动推进 2 秒（120 步）
            for (int n = 0; n < 120; n++) behaviour.Step(1f / 60f);

            // Then：状态有限，且拉伸被上限兜住
            Assert.That(system.HasNonFiniteState(), Is.False, "演示布料跑 2 秒不允许出现 NaN/Infinity");
            Assert.That(system.MaxStretchRatio(), Is.LessThanOrEqualTo(system.Parameters.maxStretchRatio * 1.02f),
                "最大拉伸比必须被 maxStretchRatio 兜住（实测 " + system.MaxStretchRatio().ToString("F4") + "）");

            // And：顶边仍然在原来的高度（钉住没被 solver 挪走）
            Vector3 anchor = system.GetPosition(system.IndexOf(5, 0));
            Assert.That(anchor.y, Is.EqualTo(0f).Within(1e-6f), "钉住的顶边必须一动不动");
        }

        [Test]
        public void BuildCloth_MaterialIsAppliedWhenShaderAvailable()
        {
            // Given
            var behaviour = BuildHiddenCloth();

            // When
            var renderer = behaviour.GetComponent<MeshRenderer>();
            var material = renderer != null ? renderer.sharedMaterial : null;

            // Then：有可用着色器时必须有材质；没有材质时也不应报错（工程里没有着色器是可接受降级）
            Shader expected = Shader.Find("Universal Render Pipeline/Lit");
            if (expected != null)
            {
                Assert.That(material, Is.Not.Null, "工程里有 URP Lit 着色器，演示布料就应该带上材质，否则播放时满屏品红");
                Assert.That(material.shader.name, Is.EqualTo("Universal Render Pipeline/Lit"),
                    "材质应使用工程当前管线的着色器");
            }
            else
            {
                Assert.That(renderer, Is.Not.Null, "至少要有 MeshRenderer");
            }
        }

        [Test]
        public void DumpClothState_WithoutAnyCloth_OnlyWarnsAndDoesNotThrow()
        {
            // Given：场景里没有任何布料组件（前面的对象都是 DontSave 且在 TearDown 销毁）
            Assert.That(UnityEngine.Object.FindAnyObjectByType<ClothBehaviour>(), Is.Null,
                "前置条件：当前场景不应残留 ClothBehaviour");

            // When：直接调用菜单方法
            // Then：只打警告，不抛异常（菜单入口不能把编辑器点崩）
            Assert.DoesNotThrow(() => ClothDemoTools.DumpClothState(), "没有布料时 Dump State 应只警告，不应抛异常");
        }
    }
}
