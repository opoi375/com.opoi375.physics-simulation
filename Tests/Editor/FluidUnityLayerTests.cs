// Copyright (c) 2026 PhysicsSimulation. MIT License.
//
// v1.5.0 流体 Unity 层测试。
//
// 约定与布料/软体完全一致：**模拟发生在组件自身的局部空间**，Transform 只是摆位；
// 构建失败不抛异常，原因写进 LastBuildError。这两条是包的一贯契约，流体不能例外。
//
// 渲染走 Graphics.DrawMeshInstanced —— 它有两个必须被测出来的硬约束：
//   1. 单批上限 1023 实例 ⇒ 分批数必须是 ceil(n/1023)，多一个就整批画不出来；
//   2. 粒子网格必须程序生成（包不引任何外部美术资源），而且着色器要跟着渲染管线走
//      （URP 下用内置 Standard 会是一片品红，这是 v1.2.0 就踩过的坑）。

using System;
using NUnit.Framework;
using UnityEngine;

namespace PhysicsSimulation.Editor.Tests
{
    [TestFixture]
    public class FluidUnityLayerTests
    {
        [Test]
        public void Defaults_AreConservativeAndMatchThePackagesContract()
        {
            // Then:  场景碰撞默认关（开上来就改变现有场景行为，比漏一个碰撞更难查）
            var go = new GameObject("Fluid");
            var behaviour = go.AddComponent<FluidBehaviour>();

            Assert.IsFalse(behaviour.collideWithSceneColliders, "collideWithSceneColliders 默认必须关");
            Assert.IsFalse(behaviour.enableBoxContainer, "容器代理默认必须关：它是新增能力，不该改变已发布场景的行为");
            Assert.IsTrue(behaviour.autoSimulate, "autoSimulate 默认开");
            Assert.IsTrue(behaviour.renderParticles, "默认要能看见水");
            Assert.IsTrue(behaviour.parameters.clampTensileLambda, "拉力钳制默认必须开（关掉水会自己缩成球）");
            Assert.GreaterOrEqual(behaviour.parameters.substeps, 2, "流体默认至少 2 子步，1 步会明显漏碰撞");
            Assert.AreEqual(0f, behaviour.parameters.vorticityEpsilon, "涡度约束默认关：它会额外吃一遍邻居表");
            Assert.Greater(behaviour.parameters.kernelRadius, behaviour.parameters.particleSpacing,
                "默认参数必须自带 h > 间距，否则一挂上就报构建失败");
            UnityEngine.Object.DestroyImmediate(go);
        }

        [Test]
        public void Rebuild_GeneratesParticlesFromVolumeSpec()
        {
            // Given:  一个 0.3 × 0.3 × 0.3 的盒子水体，间距 0.05
            var go = new GameObject("Fluid");
            var behaviour = go.AddComponent<FluidBehaviour>();
            behaviour.parameters.particleSpacing = 0.05f;
            behaviour.parameters.kernelRadius = 0.1f;
            behaviour.volumeShape = FluidVolumeShape.Box;
            behaviour.volumeSize = new Vector3(0.3f, 0.3f, 0.3f);

            // When
            behaviour.Rebuild();

            // Then
            Assert.IsTrue(behaviour.IsBuilt, "Rebuild 应当成功，LastBuildError = " + behaviour.LastBuildError);
            Assert.AreEqual(216, behaviour.Simulation.ParticleCount, "6*6*6=216 个粒子");
            Assert.AreEqual(216, behaviour.ParticlePositions.Length, "渲染用的位置数组长度必须等于粒子数");
            UnityEngine.Object.DestroyImmediate(go);
        }

        [Test]
        public void Rebuild_WithInconsistentSpacing_ReportsErrorInsteadOfThrowing()
        {
            // Given:  h ≤ 间距（物理上无解：核支撑里可能一个邻居都没有）
            var go = new GameObject("Fluid");
            var behaviour = go.AddComponent<FluidBehaviour>();
            behaviour.parameters.particleSpacing = 0.1f;
            behaviour.parameters.kernelRadius = 0.1f;

            // When/Then: 不抛异常，原因写清楚
            Assert.DoesNotThrow(() => behaviour.Rebuild(), "构建失败不该打断游戏");
            Assert.IsFalse(behaviour.IsBuilt, "参数无解时不该装作构建成功");
            Assert.IsNotEmpty(behaviour.LastBuildError, "必须给出失败原因");
            StringAssert.Contains("间距", behaviour.LastBuildError, "原因要点名间距与核半径的关系：" + behaviour.LastBuildError);
            UnityEngine.Object.DestroyImmediate(go);
        }

        [Test]
        public void Simulation_StaysInLocalSpace_WhenTransformIsMovedAndScaled()
        {
            // Given:  同样的水体，一个摆在原点，一个被搬走并放大 3 倍
            var a = new GameObject("A");
            var ba = a.AddComponent<FluidBehaviour>();
            ba.parameters.substeps = 1;
            ba.parameters.gravity = new Vector3(0f, -9.81f, 0f);
            ba.volumeShape = FluidVolumeShape.Box;
            ba.volumeSize = new Vector3(0.2f, 0.2f, 0.2f);
            ba.Rebuild();

            var b = new GameObject("B");
            b.transform.position = new Vector3(5f, -2f, 9f);
            b.transform.rotation = Quaternion.Euler(23f, 61f, -14f);
            b.transform.localScale = new Vector3(3f, 3f, 3f);
            var bb = b.AddComponent<FluidBehaviour>();
            bb.parameters.substeps = 1;
            bb.parameters.gravity = new Vector3(0f, -9.81f, 0f);
            bb.volumeShape = FluidVolumeShape.Box;
            bb.volumeSize = new Vector3(0.2f, 0.2f, 0.2f);
            bb.Rebuild();

            // When:  各跑 20 步
            for (int i = 0; i < 20; i++) { ba.Step(1f / 60f); bb.Step(1f / 60f); }

            // Then:  局部空间逐位相同 —— Transform 只是摆位，不参与力学（与布料/软体同一条契约）
            for (int i = 0; i < ba.Simulation.ParticleCount; i++)
                Assert.AreEqual(BitConverter.SingleToInt32Bits(ba.Simulation.GetPosition(i).y),
                                BitConverter.SingleToInt32Bits(bb.Simulation.GetPosition(i).y),
                    "第 " + i + " 个粒子的局部位置受 Transform 影响 ⇒ 搬动物体会把水弹飞");
            a.SetActive(false); b.SetActive(false);
            UnityEngine.Object.DestroyImmediate(a);
            UnityEngine.Object.DestroyImmediate(b);
        }

        [Test]
        public void AutoSimulate_OffMeansNothingMovesUntilStepIsCalled()
        {
            var go = new GameObject("Fluid");
            var behaviour = go.AddComponent<FluidBehaviour>();
            behaviour.autoSimulate = false;
            behaviour.Rebuild();
            var snapshot = new Vector3[behaviour.ParticlePositions.Length];
            Array.Copy(behaviour.ParticlePositions, snapshot, snapshot.Length);

            behaviour.Update();            // 手动调一次帧更新（autoSimulate 关时它必须什么都不做）

            for (int i = 0; i < snapshot.Length; i++)
                Assert.AreEqual(BitConverter.SingleToInt32Bits(snapshot[i].y),
                                BitConverter.SingleToInt32Bits(behaviour.ParticlePositions[i].y),
                    "关掉 autoSimulate 之后 Update 还在推进模拟，定步长/回放就没法用了");
            UnityEngine.Object.DestroyImmediate(go);
        }

        [Test]
        public void SceneColliders_AreBridgedIntoCollisionSetOnce()
        {
            // Given:  一个地面 Cube + 一个球
            var ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ground.name = "Ground";
            var ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            ball.transform.position = new Vector3(0f, 1f, 0f);

            var go = new GameObject("Fluid");
            var behaviour = go.AddComponent<FluidBehaviour>();
            behaviour.collideWithSceneColliders = true;
            behaviour.sceneColliders = new System.Collections.Generic.List<Collider>
            {
                ground.GetComponent<Collider>(), ball.GetComponent<Collider>()
            };
            behaviour.Rebuild();

            // Then:  两个 Collider 都进了碰撞集，且没有每帧重算（列表负一次同步）
            Assert.AreEqual(2, behaviour.Simulation.Collisions.Count,
                "碰撞代理个数应为 2，实际 " + behaviour.Simulation.Collisions.Count);

            UnityEngine.Object.DestroyImmediate(ground);
            UnityEngine.Object.DestroyImmediate(ball);
            UnityEngine.Object.DestroyImmediate(go);
        }

        // ---------------------------------------------------------------- 渲染

        [Test]
        public void RenderBatchCount_RespectsUnitysInstanceLimit()
        {
            // Then:  单批 1023 是 DrawMeshInstanced 的硬上限，分批数 = ceil(n / 1023)
            Assert.AreEqual(0, FluidBehaviour.RenderBatchCount(0, 1023));
            Assert.AreEqual(1, FluidBehaviour.RenderBatchCount(1, 1023));
            Assert.AreEqual(1, FluidBehaviour.RenderBatchCount(1023, 1023), "1023 个应正好一批");
            Assert.AreEqual(2, FluidBehaviour.RenderBatchCount(1024, 1023), "1024 个必须两批，否则最后一个画不出来");
            Assert.AreEqual(5, FluidBehaviour.RenderBatchCount(4096, 1023), "4096 个应分 5 批");
            Assert.AreEqual(0, FluidBehaviour.RenderBatchCount(-5, 1023), "负数不该抛也不该出负批次");
        }

        [Test]
        public void ParticleMesh_IsGeneratedProcedurallyAndFinite()
        {
            // Given:  半径 0.05、细分 2
            var mesh = FluidParticleMesh.Build(0.05f, 2);

            // Then:  有顶点、有三角形、没有 NaN、包围盒就是那个小球
            Assert.IsNotNull(mesh, "粒子网格必须程序生成（包不引外部美术资源）");
            Assert.Greater(mesh.vertexCount, 4, "网格顶点太少：" + mesh.vertexCount);
            Assert.Greater(mesh.triangles.Length, 2, "网格没有三角形");
            foreach (var v in mesh.vertices)
            {
                Assert.IsFalse(float.IsNaN(v.x) || float.IsInfinity(v.x), "顶点里有 NaN");
                Assert.LessOrEqual(Mathf.Abs(v.y), 0.05f + 1e-4f, "顶点跑出半径了：" + v);
            }
            Assert.AreEqual(0f, mesh.bounds.center.magnitude, 1e-4f, "粒子网格应绕自身中心");
            UnityEngine.Object.DestroyImmediate(mesh);
        }

        [Test]
        public void ShaderSelection_FollowsTheActiveRenderPipeline()
        {
            // Then:  URP 下必须用 URP/Lit —— 内置 Standard 在 URP 里是一片品红（v1.2.0 的老坑）
            Assert.AreEqual("Universal Render Pipeline/Lit",
                FluidParticleMaterial.SelectShaderName(true), "URP 活动时应选 URP/Lit");
            Assert.AreEqual("Standard",
                FluidParticleMaterial.SelectShaderName(false), "无管线时应选内置 Standard");
        }

        [Test]
        public void ParticleRadius_FollowsKernelRadiusByDefault()
        {
            // Given/Then:  粒子画多大默认由 h 推出来（太大会糊成一坨，太小看不见）
            var go = new GameObject("Fluid");
            var behaviour = go.AddComponent<FluidBehaviour>();
            behaviour.parameters.kernelRadius = 0.2f;
            behaviour.autoParticleSize = true;

            Assert.Greater(behaviour.EffectiveParticleRadius, 0f, "自动粒子半径算出了 0");
            Assert.LessOrEqual(behaviour.EffectiveParticleRadius, 0.2f,
                "自动半径不该大于核半径，否则水会糊成一整块：" + behaviour.EffectiveParticleRadius);

            behaviour.autoParticleSize = false;
            behaviour.particleRenderScale = 0.01f;
            Assert.AreEqual(0.01f, behaviour.EffectiveParticleRadius, 1e-6f, "手动模式该直接用给的尺度");
            UnityEngine.Object.DestroyImmediate(go);
        }

        [Test]
        public void BoxContainer_SurvivesEveryRebuild()
        {
            // Given：一坨小水 + 一个把它关住的容器
            var go = new GameObject("Fluid");
            try
            {
                var behaviour = go.AddComponent<FluidBehaviour>();
                behaviour.autoSimulate = false;
                behaviour.volumeShape = FluidVolumeShape.Box;
                behaviour.volumeSize = new Vector3(0.2f, 0.2f, 0.2f);
                behaviour.enableBoxContainer = true;
                behaviour.containerCenter = new Vector3(0f, 0.5f, 0f);
                behaviour.containerHalfSize = new Vector3(0.75f, 0.55f, 0.45f);

                // When/Then：第一次构建就有容器
                behaviour.Rebuild();
                Assert.IsTrue(behaviour.IsBuilt, "构建失败：" + behaviour.LastBuildError);
                Assert.AreEqual(1, behaviour.Simulation.Collisions.Count, "开起来的容器应当登记 1 个代理");
                Assert.IsInstanceOf<BoxContainerProxy>(behaviour.Simulation.Collisions[0].Proxy,
                    "登记的必须是内侧盒子（容器），不是实体板");
                Assert.AreEqual(CollisionProxySpace.World, behaviour.Simulation.Collisions[0].Space,
                    "容器按世界坐标表达，与本组件的缩放无关");

                // When：再 Rebuild 一次 —— 进 Play 模式就是走这条路（OnEnable → Rebuild）
                // Then：容器还在。**这条就是那个 bug 的现场**：以前容器代理是从外面往
                //        Simulation.Collisions 里塞的，Rebuild 换掉整个 CollisionSet 之后就静默消失，
                //        实测 Play 里"碰撞代理 0"、一箱水直落到 y = −92、动能 16904
                behaviour.Rebuild();
                Assert.AreEqual(1, behaviour.Simulation.Collisions.Count,
                    "容器必须在每次 Rebuild 之后都还在；手工往 Simulation.Collisions 塞的代理活不过重建");
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        [Test]
        public void BoxContainer_KeepsWaterInsideAcrossSteps()
        {
            // Given：一坨水被关在一个比它小一点的容器里（初始就有一部分越界）
            var go = new GameObject("Fluid");
            try
            {
                var behaviour = go.AddComponent<FluidBehaviour>();
                behaviour.autoSimulate = false;
                behaviour.volumeShape = FluidVolumeShape.Box;
                behaviour.volumeSize = new Vector3(0.3f, 0.3f, 0.3f);
                behaviour.transform.position = new Vector3(0.9f, 0.4f, 0f);   // 故意摆在容器外头一侧
                var parameters = new FluidParameters();
                parameters.particleSpacing = 0.1f;
                parameters.kernelRadius = 0.2f;
                behaviour.parameters = parameters;
                behaviour.enableBoxContainer = true;
                behaviour.containerCenter = Vector3.zero;
                behaviour.containerHalfSize = new Vector3(0.5f, 0.5f, 0.5f);

                // When
                behaviour.Rebuild();
                Assert.IsTrue(behaviour.IsBuilt, "构建失败：" + behaviour.LastBuildError);
                for (int i = 0; i < 5; i++) behaviour.Step(0.016f);

                // Then：所有粒子都在容器内（越界轴各自钉回内壁，没有"从另一面出去"）
                var container = new BoxContainerProxy(behaviour.containerCenter, behaviour.containerHalfSize,
                                                      Quaternion.identity);
                var sim = behaviour.Simulation;
                Matrix4x4 toWorld = behaviour.transform.localToWorldMatrix;   // 组件用 transform.localToWorldMatrix 建空间，测试照抄同一份
                int outside = 0;
                for (int i = 0; i < sim.ParticleCount; i++)
                {
                    Vector3 world = toWorld.MultiplyPoint3x4(sim.GetPosition(i));
                    if (!container.Contains(world, 1e-3f)) outside++;
                }
                Assert.AreEqual(0, outside, "有 " + outside + " 个粒子待在容器外：内侧盒子应当把每个越界轴钉回内壁");
                Assert.Less(sim.TotalKineticEnergy(), 1e5f, "动能爆掉说明投影在制造能量而不是消耗它");
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }
    }
}
