// Copyright (c) 2026 PhysicsSimulation. MIT License.
//
// 正式测试：软体演示工具 Tools ▸ Physics Simulation ▸ Soft Body ▸ * 的构建结果。
// 只验证"搭出来的东西结构对不对、跑不跑得稳"，不在测试里写 Assets/Scenes 文件（避免污染用户工程）。

using System;
using System.Collections.Generic;
using NUnit.Framework;
using PhysicsSimulation.EditorTools;
using UnityEngine;

namespace PhysicsSimulation.Editor.Tests
{
    [TestFixture]
    public class SoftBodyDemoToolsTests
    {
        private readonly List<GameObject> _spawned = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _spawned)
                if (go != null) UnityEngine.Object.DestroyImmediate(go);
            _spawned.Clear();
        }

        SoftBodyBehaviour Hide(SoftBodyBehaviour behaviour)
        {
            behaviour.gameObject.hideFlags = HideFlags.DontSave;
            _spawned.Add(behaviour.gameObject);
            return behaviour;
        }

        // Given：菜单工具建出来的果冻（底面钉住 + 可序列化的横向初速度）
        //  When：读它的模拟
        //  Then：焊接出 26 个质点、底面被钉住、闭合网格且有正体积、自由质点带上了初速度
        [Test]
        public void BuildSoftBody_PinsBottomAndKicksFreeParticles()
        {
            var behaviour = Hide(SoftBodyDemoTools.BuildSoftBody());

            Assert.That(behaviour.IsBuilt, Is.True, "演示软体应构建成功，原因：" + behaviour.LastBuildError);
            var system = behaviour.Simulation;

            // 2×2 细分：6 面各 3×3 = 9 个拆分顶点 ⇒ 54 个网格顶点；
            // 焊接后 = 8 个角 + 12 条棱的中点 + 6 个面心 = 26 个质点
            Assert.That(system.ParticleCount, Is.EqualTo(26),
                "2×2 细分长方体焊接后应是 8 角 + 12 棱中点 + 6 面心 = 26 个质点，实际 " + system.ParticleCount);
            Assert.That(system.IsClosed, Is.True, "长方体必须是闭合网格，否则体积约束根本不生效");
            Assert.That(system.RestVolume(), Is.GreaterThan(0f),
                "静止体积必须为正（三角形绕序朝外），实际 " + system.RestVolume());
            Assert.That(system.Volume(), Is.EqualTo(system.RestVolume()).Within(1e-4f),
                "刚建好时当前体积应等于静止体积");

            Assert.That(behaviour.PinnedParticleCount, Is.GreaterThanOrEqualTo(4),
                "底面钉住至少 4 个质点，实际 " + behaviour.PinnedParticleCount);

            float maxPinnedY = float.MinValue, minFreeY = float.MaxValue;
            for (int i = 0; i < system.ParticleCount; i++)
            {
                if (system.IsPinned(i)) maxPinnedY = Mathf.Max(maxPinnedY, system.GetPosition(i).y);
                else minFreeY = Mathf.Min(minFreeY, system.GetPosition(i).y);
            }
            Assert.That(minFreeY, Is.GreaterThan(maxPinnedY), "被钉住的必须严格是最低那一层");

            // 扰动必须是"重建之后还在"的那种：自由质点带上了工具设定的初速度
            for (int i = 0; i < system.ParticleCount; i++)
                if (!system.IsPinned(i))
                    Assert.That(system.GetVelocity(i).x, Is.GreaterThan(0.5f),
                        "自由质点 " + i + " 应带上横向初速度，实际 " + system.GetVelocity(i));
        }

        // Given：工具建出来的软体
        //  When：读它的实例网格
        //  Then：拓扑与源网格一致、顶点数一致、且没有改脏源网格资源
        [Test]
        public void BuildSoftBody_AttachesInstancedMeshMatchingSource()
        {
            var behaviour = Hide(SoftBodyDemoTools.BuildSoftBody());

            var filter = behaviour.gameObject.GetComponent<MeshFilter>();
            Assert.That(filter.sharedMesh, Is.Not.SameAs(behaviour.sourceMesh), "必须是自己那份实例网格");
            Assert.That(filter.sharedMesh.vertexCount, Is.EqualTo(behaviour.sourceMesh.vertexCount));
            Assert.That(filter.sharedMesh.triangles, Is.EqualTo(behaviour.sourceMesh.triangles),
                "拓扑要照抄源网格，否则画面会碎");
            Assert.That(filter.sharedMesh.normals.Length, Is.EqualTo(filter.sharedMesh.vertexCount),
                "实例网格必须有法线，否则光照是黑的");
        }

        // Given：被推了一把的果冻
        //  When：连续 Step 一大段
        //  Then：不炸（无非有限值）、体积保持在静止值附近、最大拉伸比不超过上限的容差
        [Test]
        public void BuildSoftBody_RunsManyStepsWithoutBlowingUp()
        {
            var behaviour = Hide(SoftBodyDemoTools.BuildSoftBody());
            var system = behaviour.Simulation;
            float rest = system.RestVolume();

            for (int i = 0; i < 180; i++) behaviour.Step(1f / 60f);

            Assert.That(system.HasNonFiniteState(), Is.False, "跑了 180 步之后出现 NaN / Infinity");
            Assert.That(system.Volume() / rest, Is.InRange(0.55f, 1.6f),
                "体积保持率跑飞了：" + (system.Volume() / rest));
            Assert.That(system.MaxStretchRatio(), Is.LessThanOrEqualTo(1.8f * 1.05f),
                "拉伸限幅没起作用，最大拉伸比 " + system.MaxStretchRatio());
        }

        // Given：工具建出来的软体
        //  When：读它的渲染器
        //  Then：材质来自当前管线的默认材质（不是猜名字兜底，更不是没材质 ⇒ 否则画面品红）
        [Test]
        public void BuildSoftBody_MaterialComesFromTheActivePipeline()
        {
            var behaviour = Hide(SoftBodyDemoTools.BuildSoftBody());
            var renderer = behaviour.gameObject.GetComponent<MeshRenderer>();

            Assert.That(renderer.sharedMaterial, Is.Not.Null, "演示软体应当有材质");
            Assert.That(SoftBodyDemoTools.LastMaterialPath, Does.StartWith("管线默认材质模板"),
                "应当走管线默认材质这条路，实际：" + SoftBodyDemoTools.LastMaterialPath);
            Assert.That(renderer.sharedMaterial.name, Does.Contain("PhysicsSimulation Demo"));
        }

        // Given：另一块顶面钉住的软体袋
        //  When：Step 一段时间
        //  Then：顶面纹丝不动、整体下垂、体积不塌
        [Test]
        public void BuildHangingBag_PinsTopAndSagsUnderGravity()
        {
            var behaviour = Hide(SoftBodyDemoTools.BuildHangingBag());
            var system = behaviour.Simulation;

            float rest = system.RestVolume();
            Assert.That(rest, Is.GreaterThan(0f), "软体袋也必须是闭合且有正体积");

            float pinnedY = float.MaxValue;
            for (int i = 0; i < system.ParticleCount; i++)
                if (system.IsPinned(i)) pinnedY = Mathf.Min(pinnedY, system.GetPosition(i).y);

            float lowestStart = float.MaxValue;
            for (int i = 0; i < system.ParticleCount; i++)
                lowestStart = Mathf.Min(lowestStart, system.GetPosition(i).y);

            for (int i = 0; i < 90; i++) behaviour.Step(1f / 60f);

            float lowestEnd = float.MaxValue;
            for (int i = 0; i < system.ParticleCount; i++)
            {
                Assert.That(system.IsPinned(i) ? system.GetPosition(i).y : pinnedY,
                    Is.GreaterThanOrEqualTo(pinnedY - 1e-5f), "钉住的质点被移动了");
                if (!system.IsPinned(i)) lowestEnd = Mathf.Min(lowestEnd, system.GetPosition(i).y);
            }

            Assert.That(lowestEnd, Is.LessThan(lowestStart - 0.02f),
                "顶面钉住的软体袋应当下垂，最低点 " + lowestStart + " → " + lowestEnd);
            Assert.That(system.Volume() / rest, Is.GreaterThan(0.5f), "下垂时体积不能塌掉");
        }

        // Given：当前场景里没有任何软体组件
        //  When：调用 Dump State
        //  Then：只给一条警告、不抛异常（自动化脚本可以随手调）
        [Test]
        public void DumpSoftBodyState_WithoutAnySoftBody_OnlyWarnsAndDoesNotThrow()
        {
            var existing = UnityEngine.Object.FindObjectsByType<SoftBodyBehaviour>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            foreach (var b in existing) b.gameObject.hideFlags = HideFlags.HideAndDontSave;

            try
            {
                Assert.DoesNotThrow(() => SoftBodyDemoTools.DumpSoftBodyState(),
                    "没有软体时 Dump State 不该抛异常");
            }
            finally
            {
                foreach (var b in existing) if (b != null) b.gameObject.hideFlags = HideFlags.None;
            }
        }

        // Given：程序化长方体网格工具
        //  When：n = 1 时生成一个最简长方体
        //  Then：24 个顶点 / 12 个三角形，且按外法线绕序（体积为正、等于解析值）
        [Test]
        public void BuildBoxMesh_WithSingleSubdivision_IsAWatertightBox()
        {
            var mesh = SoftBodyDemoTools.BuildBoxMesh(2f, 1f, 0.5f, 1);
            Assert.That(mesh.vertexCount, Is.EqualTo(24), "6 面 × 4 个拆分顶点");
            Assert.That(mesh.triangles.Length, Is.EqualTo(36), "12 个三角形");

            var system = new SoftBodySimulation(new SoftBodyParameters { weldTolerance = 1e-4f });
            system.Build(SoftBodyMeshData.FromMesh(mesh));

            Assert.That(system.ParticleCount, Is.EqualTo(8), "8 个角");
            Assert.That(system.IsClosed, Is.True, "长方体是闭合的");
            Assert.That(system.RestVolume(), Is.EqualTo(1f).Within(1e-4f),
                "2×1×0.5 的体积应当是 1.0（绕序朝外才会得到正值），实际 " + system.RestVolume());

            UnityEngine.Object.DestroyImmediate(mesh);
        }
    }
}
