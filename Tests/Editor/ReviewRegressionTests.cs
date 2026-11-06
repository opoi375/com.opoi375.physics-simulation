// Copyright (c) 2026 PhysicsSimulation. MIT License.
using System.Collections.Generic;
using NUnit.Framework;
using PhysicsSimulation;
using PhysicsSimulation.EditorTools;
using UnityEngine;
using UnityEngine.TestTools;

namespace PhysicsSimulation.Editor.Tests
{
    /// <summary>
    /// v1.6.0 第三方评审查出的回归（每一条都先用探针实测确认过，不是照抄报告）。
    ///
    /// 这一组测试的共同点：它们锁的都是**"看起来能用、实际静默失效"**的路径。
    /// 之所以会漏，是因为已发布测试都在验证"设对了参数时算得对"，而没人验证
    /// "播放中改参数还算不算"、"重置之后还守不守约束"、"钉住之后还动不动"。
    /// </summary>
    public class ReviewRegressionTests
    {
        static readonly List<UnityEngine.Object> Trash = new List<UnityEngine.Object>();

        static T Track<T>(T o) where T : UnityEngine.Object { Trash.Add(o); return o; }

        GameObject Spawn(string name)
        {
            var go = new GameObject(name);
            Trash.Add(go);
            return go;
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = Trash.Count - 1; i >= 0; i--)
            {
                if (Trash[i] != null) UnityEngine.Object.DestroyImmediate(Trash[i]);
            }
            Trash.Clear();
        }

        // ============================================================ ① 钉住 = 不动（P0）

        [Test]
        public void Pin_WithInitialVelocity_DoesNotMoveAtAll()
        {
            // Given 一个建好就钉住、却带着初速的质点（MassSpringConfig 允许这种组合）
            var system = new MassSpringSystem();
            system.Parameters.gravity = Vector3.zero;
            system.Parameters.globalDamping = 0f;
            system.Parameters.substeps = 1;
            int index = system.AddParticle(Vector3.zero, 1f, true, 0f);
            system.Particles[index].velocity = new Vector3(1f, 0f, 0f);

            // When 走 10 步
            for (int i = 0; i < 10; i++) system.Step(1f / 60f);

            // Then 位置逐位不变。旧实现只让加速度归零（inverseMass=0），却照常做
            //      position += velocity*dt，于是钉住的点会以 1 m/s 匀速飘走（实测 0.1667 m）。
            Assert.That(system.Particles[index].position, Is.EqualTo(Vector3.zero),
                "钉住的质点必须一动不动，实测飘到了 " + system.Particles[index].position);
            Assert.That(system.Particles[index].velocity, Is.EqualTo(Vector3.zero),
                "钉住的质点速度必须归零，实测仍是 " + system.Particles[index].velocity);
        }

        [Test]
        public void Pin_AfterMoving_StopsImmediately()
        {
            // Given 一个正在飞的质点
            var system = new MassSpringSystem();
            system.Parameters.gravity = Vector3.zero;
            system.Parameters.globalDamping = 0f;
            system.Parameters.substeps = 1;
            int index = system.AddParticle(Vector3.zero, 1f, false, 0f);
            system.Particles[index].velocity = new Vector3(3f, 0f, 0f);
            for (int i = 0; i < 5; i++) system.Step(1f / 60f);

            // When 运行期把它钉住
            system.Pin(index);
            Vector3 atPin = system.Particles[index].position;
            for (int i = 0; i < 10; i++) system.Step(1f / 60f);

            // Then 钉住那一刻之后位移必须为 0（旧实现又飘了 0.5 m）。
            //      这是"吊点被拖走"的现场：Pin 只改 inverseMass，速度还留着。
            Assert.That(system.Particles[index].position, Is.EqualTo(atPin),
                "Pin() 之后不许再移动，实测从 " + atPin + " 飘到 " + system.Particles[index].position);
        }

        [Test]
        public void Unpin_StillFallsAgainAfterwards()
        {
            // Given 一个钉住的质点
            var system = new MassSpringSystem();
            system.Parameters.gravity = new Vector3(0f, -9.81f, 0f);
            system.Parameters.globalDamping = 0f;
            system.Parameters.substeps = 1;
            int index = system.AddParticle(new Vector3(0f, 5f, 0f), 1f, true, 0f);
            for (int i = 0; i < 10; i++) system.Step(1f / 60f);
            Assert.That(system.Particles[index].position.y, Is.EqualTo(5f), "钉住期间不该下落");

            // When 解开
            system.Unpin(index);
            for (int i = 0; i < 10; i++) system.Step(1f / 60f);

            // Then 该重新受重力（防止"修 Pin"修成"永久冻结"）
            Assert.That(system.Particles[index].position.y, Is.LessThan(5f), "解开之后必须重新受重力");
        }

        [Test]
        public void SoftBody_TopPinned_DoesNotDriftOrStretch()
        {
            // Given 一个钉住顶面的软体（挂着的皮袋）：钉住的质点必须真的不动
            var go = Spawn("Hanging");
            var soft = go.AddComponent<SoftBodyBehaviour>();
            soft.sourceMesh = Track<Mesh>(SoftBodyDemoTools.BuildBoxMesh(0.4f, 0.4f, 0.4f, 2));
            soft.pinMode = SoftBodyPinMode.TopVertices;
            soft.autoSimulate = false;
            soft.Rebuild();
            Assert.That(soft.IsBuilt, Is.True, soft.LastBuildError);

            var sim = soft.Simulation;
            var pinned = new List<int>();
            var snapshot = new List<Vector3>();
            for (int i = 0; i < sim.ParticleCount; i++)
            {
                if (!sim.IsPinned(i)) continue;
                pinned.Add(i);
                snapshot.Add(sim.GetPosition(i));
            }
            Assert.That(pinned.Count, Is.GreaterThan(0), "顶面一个质点都没钉住，测试前提不成立");

            // When 挂着走 120 步
            for (int i = 0; i < 120; i++) soft.Step(1f / 60f);

            // Then 钉住的那批质点逐位不动
            for (int k = 0; k < pinned.Count; k++)
            {
                Assert.That(sim.GetPosition(pinned[k]), Is.EqualTo(snapshot[k]),
                    "钉住的质点 " + pinned[k] + " 飘走了："
                    + snapshot[k] + " -> " + sim.GetPosition(pinned[k]));
            }
        }

        // ============================================================ ② 重置不许丢碰撞代理（P1）

        [Test]
        public void Fluid_ResetToInitialLayout_KeepsBoxContainer()
        {
            // Given 一个带内侧盒子容器的流体
            var go = Spawn("Tank");
            var fluid = go.AddComponent<FluidBehaviour>();
            fluid.autoSimulate = false;
            fluid.volumeSize = new Vector3(0.3f, 0.3f, 0.3f);
            fluid.enableBoxContainer = true;
            fluid.containerCenter = Vector3.zero;
            fluid.containerHalfSize = new Vector3(0.6f, 0.6f, 0.6f);
            fluid.Rebuild();
            Assert.That(fluid.Simulation, Is.Not.Null, fluid.LastBuildError);
            Assert.That(fluid.Simulation.Collisions.Count, Is.EqualTo(1), "Rebuild 之后容器应该在");

            // When 回到初始布局（演示里的“重来一次”走的就是这条路）
            fluid.ResetToInitialLayout();

            // Then 容器必须还在。旧实现 new 了一个 FluidSimulation，只把场景 Collider 桥接回来，
            //      BoxContainerProxy 当场蒸发 —— 正是 v1.5.0 CHANGELOG 记下的那个现场
            //      （碰撞代理 0、一箱水直落到 y=-92、动能 16904），只修了 Rebuild 那一半。
            Assert.That(fluid.Simulation, Is.Not.Null);
            Assert.That(fluid.Simulation.Collisions.Count, Is.EqualTo(1),
                "ResetToInitialLayout 把容器代理弄丢了，实测代理数 "
                + fluid.Simulation.Collisions.Count + "（应为 1）");
        }

        [Test]
        public void Fluid_ResetToInitialLayout_RestoresTheSameParticleCount()
        {
            // Given
            var go = Spawn("Tank2");
            var fluid = go.AddComponent<FluidBehaviour>();
            fluid.autoSimulate = false;
            fluid.volumeSize = new Vector3(0.2f, 0.2f, 0.2f);
            fluid.Rebuild();
            int built = fluid.Simulation.ParticleCount;

            // When 跑一段再重置
            for (int i = 0; i < 20; i++) fluid.Step(1f / 60f);
            fluid.ResetToInitialLayout();

            // Then 粒子数回到初始值（重置不能把水变少）
            Assert.That(fluid.Simulation.ParticleCount, Is.EqualTo(built),
                "重置后粒子数变了：" + built + " -> " + fluid.Simulation.ParticleCount);
        }

        // ============================================================ ③ 运行期参数必须实时生效（P1）

        [Test]
        public void MassSpringBehaviour_RuntimeGravityChange_TakesEffect()
        {
            // Given 一个正在自由落体的质点弹簧（播放中改 gravity 是最常见的调参动作）
            var go = Spawn("MS");
            var body = go.AddComponent<MassSpringBehaviour>();
            body.autoSimulate = false;
            body.gravity = new Vector3(0f, -9.81f, 0f);
            body.globalDamping = 0f;
            body.particles.Add(new MassSpringParticleData { position = Vector3.zero, mass = 1f });
            Assert.That(body.Rebuild(), Is.True, body.LastBuildError);
            for (int i = 0; i < 10; i++) body.Step(1f / 60f);
            float speedBefore = body.System.Particles[0].velocity.y;

            // When 关掉重力再走 10 步
            body.gravity = Vector3.zero;
            for (int i = 0; i < 10; i++) body.Step(1f / 60f);
            float speedAfter = body.System.Particles[0].velocity.y;

            // Then 立刻生效。判据是**加速度**不是位移：关掉重力之后质点仍带着之前攒下的
            //      速度继续走（那是正确的物理，实测还会走 0.27 m），变的是速度不再增加。
            //      旧实现把四个参数一次性拷进 MassSpringSystem 之后再也不管，而 OnValidate 在
            //      播放模式故意跳过重建 ⇒ 拖 Inspector 完全没反应、也不报错。
            Assert.That(body.System.Parameters.gravity, Is.EqualTo(Vector3.zero),
                "Step 之后内层系统仍拿着旧重力 " + body.System.Parameters.gravity);
            Assert.That(speedAfter - speedBefore, Is.EqualTo(0f).Within(1e-4f),
                "关掉重力后速度仍在增加 " + (speedAfter - speedBefore).ToString("F4")
                + " m/s，说明参数没同步（内力/重力还在旧值上算）");
        }

        [Test]
        public void MassSpringBehaviour_RuntimeSubstepsAndDamping_AreMirrored()
        {
            // Given
            var go = Spawn("MS2");
            var body = go.AddComponent<MassSpringBehaviour>();
            body.autoSimulate = false;
            body.gravity = Vector3.zero;
            body.particles.Add(new MassSpringParticleData { position = Vector3.zero, mass = 1f });
            Assert.That(body.Rebuild(), Is.True, body.LastBuildError);

            // When 运行期改三个非拓扑参数
            body.substeps = 3;
            body.globalDamping = 2.5f;
            body.maxDeltaTime = 1f / 45f;
            body.Step(1f / 60f);

            // Then 内层系统逐项对齐（这四项都是"每步怎么算"，跟拓扑无关，不该冻在 Build 里）
            Assert.That(body.System.Parameters.substeps, Is.EqualTo(3), "substeps 没同步");
            Assert.That(body.System.Parameters.globalDamping, Is.EqualTo(2.5f), "globalDamping 没同步");
            Assert.That(body.System.Parameters.maxDeltaTime, Is.EqualTo(1f / 45f).Within(1e-6f),
                "maxDeltaTime 没同步");
        }

        [Test]
        public void Fluid_WithSubstepsZero_StillAdvancesInsteadOfFreezing()
        {
            // Given 一个正常建好的流体，然后 Inspector 上把 substeps 拖到 0
            var go = Spawn("F");
            var fluid = go.AddComponent<FluidBehaviour>();
            fluid.autoSimulate = false;
            fluid.volumeSize = new Vector3(0.2f, 0.2f, 0.2f);
            fluid.Rebuild();
            Assert.That(fluid.Simulation, Is.Not.Null, fluid.LastBuildError);
            fluid.parameters.substeps = 0;

            // When 走 10 步
            Vector3 before = fluid.Simulation.Positions[0];
            for (int i = 0; i < 10; i++) fluid.Step(1f / 60f);
            float moved = (fluid.Simulation.Positions[0] - before).magnitude;

            // Then 必须当成 1 个子步照常推进，而不是被 +Infinity 的子步长坑成"循环体一次都不进"。
            //      旧实现 subStep = dt / 0 = Inf，然后 for (s < 0) 一次不执行 ⇒ 水整帧冻结、也不报错。
            Assert.That(moved, Is.GreaterThan(1e-4f),
                "substeps=0 时水被静默冻结了（位移 " + moved.ToString("F6") + "）");
            Assert.That(float.IsNaN(fluid.Simulation.Positions[0].x), Is.False, "不许出现 NaN");
        }

        [Test]
        public void Fluid_EffectiveSubstepsAndIterations_NeverGoBelowOne()
        {
            // Given
            var p = new FluidParameters { substeps = 0, solverIterations = -3 };

            // Then 与另外三个模块同口径：非法值钳到 1，而不是靠 Validate 抛（运行期改字段不会走 Validate）
            Assert.That(p.EffectiveSubsteps, Is.EqualTo(1), "EffectiveSubsteps 下界必须是 1");
            Assert.That(p.EffectiveSolverIterations, Is.EqualTo(1), "EffectiveSolverIterations 下界必须是 1");
        }

        // ============================================================ ④ 布料质量（P2）

        [Test]
        public void Cloth_SetPinnedAfterMassChange_StaysFinite()
        {
            // Given 一块布，运行期把 mass 改成 0（Inspector 上就在那儿，没有任何“需重建”提示）
            var parameters = new ClothParameters { columns = 4, rows = 4, spacing = 0.1f };
            var cloth = new ClothSimulation(parameters);
            parameters.mass = 0f;

            // When 重新 pin / unpin 一个质点再步进
            cloth.SetPinned(1, false);
            for (int i = 0; i < 3; i++) cloth.Step(1f / 60f);

            // Then 不许出现 Infinity / NaN。
            //      旧实现里构造用 parameters.mass，SetPinned 又去读活的 Parameters.mass，
            //      mass=0 时写出 1/0=+Infinity，投影里 Inf*0 直接变 NaN。
            Assert.That(cloth.HasNonFiniteState(), Is.False,
                "mass=0 之后重新 pin/unpin 把布变成了 NaN（逆质量写成了 Infinity）");
        }

        [Test]
        public void Cloth_MassChange_IsConsistentAcrossAllParticles()
        {
            // Given
            var parameters = new ClothParameters { columns = 3, rows = 3, spacing = 0.1f, mass = 2f };
            var cloth = new ClothSimulation(parameters);

            // When 运行期改质量，然后只把一个质点重新 pin/unpin
            parameters.mass = 5f;
            cloth.SetPinned(4, true);
            cloth.SetPinned(4, false);

            // Then 同一块布上不许出现两种质量：逆质量必须仍是构造时那一份（1/2）。
            //      否则投影按 wA/(wA+wB) 分配修正量，质量亏掉的那一半会把形变算歪。
            Assert.That(cloth.InverseMass(4), Is.EqualTo(0.5f).Within(1e-6f),
                "重新 pin/unpin 的质点拿到了新质量（逆质量 " + cloth.InverseMass(4).ToString("F4")
                + "，其余质点仍是 0.5）—— 同一块布上出现了两种质量");
        }

        // ============================================================ ⑤ 其它（P2/P3）

        [Test]
        public void Fluid_SphereVolume_IsCenteredOnItsOwnOrigin()
        {
            // Given 一个被摆到 (3,0,0) 的流体组件，初始形状选球
            var go = Spawn("SphereAt");
            go.transform.position = new Vector3(3f, 0f, 0f);
            var fluid = go.AddComponent<FluidBehaviour>();
            fluid.autoSimulate = false;
            fluid.volumeShape = FluidVolumeShape.Sphere;
            fluid.volumeRadius = 0.2f;
            fluid.parameters.particleSpacing = 0.05f;
            fluid.Rebuild();
            Assert.That(fluid.Simulation, Is.Not.Null, fluid.LastBuildError);

            // When 算粒子质心（模拟在**局部空间**，所以原点就是组件自己）
            Vector3 mean = Vector3.zero;
            for (int i = 0; i < fluid.Simulation.ParticleCount; i++) mean += fluid.Simulation.Positions[i];
            mean /= Mathf.Max(1, fluid.Simulation.ParticleCount);

            // Then 质心必须≈0。旧实现把 transform.localPosition 当球心：组件在 (3,0,0) 时
            //      球被生成在局部 (3,0,0)（世界 (6,0,0)），水凭空漂到别处。
            Assert.That(mean.x, Is.EqualTo(0f).Within(0.02f),
                "球体积被摆到了局部 x=" + mean.x.ToString("F3")
                + "，它该以组件自身原点为中心（盒/溃坝都是从原点生成的）");
        }

        [Test]
        public void SoftBody_FullyPinnedOverstretchedEdge_IsReportedNotSilentlySpun()
        {
            // Given 一个全钉死、且有一条边被拉到远超上限的软体
            var mesh = Track<Mesh>(SoftBodyDemoTools.BuildBoxMesh(0.3f, 0.3f, 0.3f, 2));
            var soft = new SoftBodySimulation(new SoftBodyParameters { maxStretchRatio = 1.2f });
            soft.Build(SoftBodyMeshData.FromMesh(mesh));
            for (int i = 0; i < soft.System.Particles.Count; i++) soft.System.Pin(i);
            soft.System.Particles[0].position = new Vector3(4f, 0f, 0f);

            // When
            soft.Step(1f / 60f);

            // Then 修不好的边要被**报出来**（计数 > 0），而不是让 clean 永远 false、
            //      每个子步白跑满 8 轮却什么都不改、也不告诉任何人。
            Assert.That(soft.IrreparableStretchEdges, Is.GreaterThan(0),
                "两端都钉死的超限边必须被计数上报，否则限幅在空转");
        }

        [Test]
        public void ClothBehaviour_DestroyingInEditMode_DoesNotLogError()
        {
            // Given 一个自己建了网格的布料（_ownsMesh = true）
            var go = new GameObject("ClothDestroy");
            var cloth = go.AddComponent<ClothBehaviour>();
            cloth.autoSimulate = false;
            cloth.Rebuild();
            Assert.That(cloth.IsBuilt, Is.True, cloth.LastBuildError);

            // When 在 EditMode 里销毁（测试 TearDown 天天这么做）
            // Then 不许打 Error 日志。Unity 测试框架把任何未预期的 Error 直接算成失败，
            //      流体为此专门写了 DisposeAsset，布料却还在无条件 Destroy。
            UnityEngine.Object.DestroyImmediate(go);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void FluidBehaviour_DisposesEveryRuntimeAssetIncludingSurfaceMaterial()
        {
            // Given 一个建过水面材质的流体
            var go = new GameObject("FluidDispose");
            var fluid = go.AddComponent<FluidBehaviour>();
            fluid.autoSimulate = false;
            fluid.renderMode = FluidRenderMode.Surface;
            fluid.volumeSize = new Vector3(0.2f, 0.2f, 0.2f);
            fluid.parameters.particleSpacing = 0.06f;
            fluid.Rebuild();
            Assert.That(fluid.Simulation, Is.Not.Null, fluid.LastBuildError);
            fluid.Step(1f / 60f);   // 触发水面网格与材质的创建

            Material surfaceMaterial = fluid.SurfaceMaterial;
            Mesh surfaceMesh = fluid.SurfaceMesh;

            // When
            UnityEngine.Object.DestroyImmediate(go);

            // Then 两者都必须被释放（旧的 OnDestroy 漏了 _surfaceMaterial）
            Assert.That(surfaceMesh == null, Is.True, "水面网格没被释放");
            Assert.That(surfaceMaterial == null, Is.True, "水面材质没被释放（OnDestroy 漏了这一项）");
        }
    }
}

// touch-for-reload
