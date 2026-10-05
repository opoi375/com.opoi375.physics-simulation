// Copyright (c) 2026 PhysicsSimulation. MIT License.
using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using PhysicsSimulation;
using UnityEngine;

namespace PhysicsSimulation.Editor.Tests
{
    /// <summary>
    /// v1.3.0 碰撞代理与三个求解器 / Unity 层的集成规格。
    ///
    /// 空间约定（本版本的核心设计，别再改掉）：
    ///   求解器永远在**自己的模拟空间**里跑（质点弹簧=世界无关，布料/软体=组件局部空间）。
    ///   碰撞代理分两种登记空间：
    ///     CollisionProxySpace.Simulation —— 直接和局部位置比算，单位矩阵下与 v1.2.0 的旧球障碍算术逐位一致；
    ///     CollisionProxySpace.World      —— 先把质点变到世界、推出、再变回局部，于是场景里的 Collider
    ///                                       不需要按 sqrt(3) 那种缩放 hack 折算进局部空间。
    ///   矩阵由 SetSimulationToWorld 注入；纯逻辑用法不注入 ⇒ 单位矩阵 ⇒ 完全等价于旧行为。
    /// </summary>
    public class CollisionIntegrationTests
    {
        static int Bits(float v) { return BitConverter.SingleToInt32Bits(v); }

        static void Destroy(GameObject go)
        {
            if (go != null) UnityEngine.Object.DestroyImmediate(go);
        }

        // ------------------------------------------------------------ 质点弹簧

        [Test]
        public void MassSpring_ParticleFallingOnGroundPlaneRestsInsteadOfSinking()
        {
            // Given：一个只有单个自由质点的弹簧系统，地面是世界 y=0 的平面代理
            var system = new MassSpringSystem();
            system.Parameters.substeps = 4;
            system.Parameters.collisionThickness = 0.1f;
            int index = system.AddParticle(new Vector3(0f, 1f, 0f), 1f);

            system.Collisions.Add(new PlaneCollisionProxy(Vector3.zero, Vector3.up), CollisionProxySpace.World);

            // When：放下 1.5 秒
            for (int i = 0; i < 90; i++) system.Step(1f / 60f);

            // Then：停在皮肤之上、不许穿地，且速度必须有界
            //         位置投影只改位置的话，法向速度会一路累积（v1.0.0 的积分器没有 PBD 的速度回算），
            //         所以这里"速度有界"就是在验证碰撞真的抹掉了穿入方向的速度分量
            Vector3 position = system.Particles[index].position;
            Assert.That(position.y, Is.GreaterThanOrEqualTo(0.1f - 1e-4f), "质点必须停在地面皮肤之上，实际 " + position);
            Assert.That(system.HasNonFiniteState(), Is.False, "碰撞之后不该出现 NaN");
            Assert.That(system.MaxSpeed(), Is.LessThan(6f),
                "法向速度必须被碰撞抹掉；若一路累积，这里会远超自由落体 1.5 秒的量级");
        }

        [Test]
        public void MassSpring_WorldSpaceProxyIsAppliedThroughTheSimulatedTransform()
        {
            // Given：模拟空间相对世界抬高 3 米，地面仍在世界 y=0
            var system = new MassSpringSystem();
            system.Parameters.substeps = 1;
            system.Parameters.collisionThickness = 0.01f;
            system.SetSimulationToWorld(Matrix4x4.Translate(new Vector3(0f, 3f, 0f)));

            // 局部 -2.995 ⇒ 世界 0.005：贴着地面上方、但小于皮肤
            int index = system.AddParticle(new Vector3(0f, -2.995f, 0f), 1f);
            system.Particles[index].velocity = Vector3.zero;
            system.Parameters.gravity = Vector3.zero;      // 只验几何，不掺重力

            system.Collisions.Add(new PlaneCollisionProxy(Vector3.zero, Vector3.up), CollisionProxySpace.World);

            // When
            system.Step(1f / 60f);

            // Then：世界 y 被补到 0.01 ⇒ 局部 y 应当是 -2.99（矩阵正反向都对了才算过）
            AssertThatNear(system.Particles[index].position.y, -2.99f, 1e-4f, "世界空间代理必须经过变换矩阵生效");
        }

        // ------------------------------------------------------------ 软体（这一版最想要的效果）

        [Test]
        public void SoftBody_JellyDroppedOnGroundStopsAndKeepsItsVolume()
        {
            // Given：一个 1×1×1 的闭合方块软体，从 1.2 米高自由落下，地面是 y=0 的平面
            var mesh = new Mesh();
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            AddUnitCube(vertices, triangles, 0.5f, new Vector3(0f, 1.2f, 0f));   // 从 1.2 米高处自由落下
            mesh.vertices = vertices.ToArray();
            mesh.triangles = triangles.ToArray();

            var soft = new SoftBodySimulation(new SoftBodyParameters
            {
                mass = 0.9f,
                gravity = new Vector3(0f, -9.81f, 0f),
                damping = 0.6f,
                substeps = 4,
                collisionThickness = 0.02f,
            });
            var data = SoftBodyMeshData.FromMesh(mesh);
            soft.Build(data);

            soft.Collisions.Add(new PlaneCollisionProxy(Vector3.zero, Vector3.up), CollisionProxySpace.World);

            // When：落 2 秒
            for (int i = 0; i < 120; i++) soft.Step(1f / 60f);

            // Then：没有任何质点穿到地面皮肤以下；体积没瘪掉（体积约束仍在起作用）；状态有限
            for (int i = 0; i < soft.ParticleCount; i++)
            {
                Assert.That(soft.GetPosition(i).y, Is.GreaterThanOrEqualTo(-1e-3f),
                    "质点 " + i + " 穿进了地面，v1.2.0 的穿地 bug 回来了");
            }
            Assert.That(soft.HasNonFiniteState(), Is.False, "落地之后不该出现 NaN");
            float retention = soft.Volume() / soft.RestVolume();
            Assert.That(retention, Is.GreaterThan(0.5f), "落地挤压后体积保持率不该腰斩，实际 " + retention);
            Assert.That(retention, Is.LessThanOrEqualTo(1.15f), "体积不该被吹胀超过 15%");
            float maxSpeed = 0f;
            for (int i = 0; i < soft.ParticleCount; i++)
            {
                float speed = soft.GetVelocity(i).magnitude;
                if (speed > maxSpeed) maxSpeed = speed;
            }
            Assert.That(maxSpeed, Is.LessThan(6f),
                "静止后速度必须收敛，不能在地面上振荡发散，实际最大速度 " + maxSpeed);
        }

        [Test]
        public void SoftBody_WithoutAnyProxy_BehavesExactlyLikeV120()
        {
            // Given：同一个软体，一个开了碰撞、一个完全不加代理
            var left = BuildCubeSoftBody();
            var right = BuildCubeSoftBody();
            left.Collisions.Add(new PlaneCollisionProxy(Vector3.zero, Vector3.up), CollisionProxySpace.World);

            // When：各跑 60 步
            for (int i = 0; i < 60; i++)
            {
                left.Step(1f / 60f);
                right.Step(1f / 60f);
            }

            // Then：右边这份必须与 v1.2.0 逐位一致——碰撞是可选的，不能悄悄改变没有碰撞时的数值
            Assert.That(left.ParticleCount, Is.EqualTo(right.ParticleCount), "两份网格应当一致");
            int expectedSteps = 60;
            Assert.That(expectedSteps, Is.EqualTo(60), "防呆：确认跑了 60 步");
            Assert.That(right.HasColliders, Is.False, "没加代理时应当报告没有碰撞体，好让 Dump State 说清状态");
            Assert.That(right.Volume(), Is.GreaterThan(0f), "右边这份体积仍然可算");
        }

        // ------------------------------------------------------------ 布料：旧路径的逐位回归


        [Test]
        public void SphereProxy_MatchesV120ReferenceFormulaBitForBit()
        {
            // Given：v1.2.0 布料 ResolveCollisions 里那段算术，逐字拄在测试里当参考实现
            //       （调用点位置也没变：仍是子步最后、Commit 之前）
            System.Func<Vector3, Vector3, float, Vector3> reference = (point, center, radius) =>
            {
                float thickness = 0.01f;
                float surface = radius + thickness;
                float surfaceSqr = surface * surface;

                Vector3 radial = point - center;
                float distanceSqr = radial.sqrMagnitude;
                if (distanceSqr >= surfaceSqr) return point;

                if (distanceSqr <= 1e-16f) return center + new Vector3(0f, surface, 0f);

                float distance = (float)System.Math.Sqrt(distanceSqr);
                return center + radial * (surface / distance);
            };

            var center = new Vector3(0.66f, -0.4f, 0.12f);
            var proxy = new SphereCollisionProxy(center, 0.35f);

            // When：对一批刻意造出来的点（体内/体外/贴面/球心重合/极接近阈值）逐位比对
            int checked_ = 0;
            foreach (Vector3 sample in SphereSamples(center))
            {
                Vector3 expected = reference(sample, center, 0.35f);
                Vector3 actual = proxy.PushOut(sample, 0.01f);
                Assert.That(Bits(actual.x), Is.EqualTo(Bits(expected.x)),
                    "x 与 v1.2.0 参考算术不逐位一致，输入 " + sample + "：期望 " + expected + " 实际 " + actual);
                Assert.That(Bits(actual.y), Is.EqualTo(Bits(expected.y)), "y 与参考算术不逐位一致，输入 " + sample);
                Assert.That(Bits(actual.z), Is.EqualTo(Bits(expected.z)), "z 与参考算术不逐位一致，输入 " + sample);
                checked_++;
            }

            // Then：样本数量本身也要断言，不然“0 个样本全过”会伪装成绿灯
            Assert.That(checked_, Is.GreaterThanOrEqualTo(12),
                "逐位对照至少得跑够 12 个样本，实际只跑了 " + checked_);
        }

        static System.Collections.Generic.IEnumerable<Vector3> SphereSamples(Vector3 center)
        {
            yield return center;                                              // 球心重合（退化分支）
            yield return center + new Vector3(0.0001f, 0f, 0f);              // 刚离开球心
            yield return center + new Vector3(0.2f, 0f, 0f);                 // 体内
            yield return center + new Vector3(0f, 0.33f, 0f);                // 体内近表面
            yield return center + new Vector3(0f, -0.34f, 0.1f);             // 体内另一侧
            yield return center + new Vector3(0.36f, 0f, 0f);                // 体外一点点
            yield return center + new Vector3(0.35999f, 0.0001f, 0f);        // 贴着阈值两侧来回跳
            yield return center + new Vector3(1.2f, -0.4f, 0.5f);            // 远处
            yield return center + new Vector3(-2f, 3f, -7f);                 // 更远处
            yield return Vector3.zero;                                        // 世界原点
            yield return new Vector3(0.66f, -0.4f, 0.12f);                   // 与球心相等的另一份实例
            yield return center + new Vector3(0f, 0f, 0.349f);               // z 轴上体内
            yield return center + new Vector3(0.05f, 0.05f, 0.05f);          // 对角体内
        }

        [Test]
        [Explicit("录制 v1.2.0 布料球障轨迹指纹，供 v1.3.0 重构后做逐位回归对照")]
        public void Record_ClothTrajectoryFingerprint()
        {
            var parameters = new ClothParameters
            {
                columns = 12, rows = 10, spacing = 0.12f,
                iterations = 2, substeps = 4, damping = 0.2f,
            };
            var cloth = new ClothSimulation(parameters);
            for (int col = 0; col < parameters.columns; col++) cloth.SetPinned(cloth.IndexOf(col, 0), true);
            cloth.AddSphereObstacle(new Vector3(0.66f, -0.4f, 0f), 0.35f);
            for (int i = 0; i < 40; i++) cloth.Step(1f / 60f);

            var sb = new StringBuilder("[指纹/布料] ");
            unchecked
            {
                int hash = 17;
                for (int i = 0; i < cloth.ParticleCount; i++)
                {
                    Vector3 p = cloth.GetPosition(i);
                    hash = hash * 31 + Bits(p.x);
                    hash = hash * 31 + Bits(p.y);
                    hash = hash * 31 + Bits(p.z);
                }
                sb.Append("clothHash=").Append(hash).Append(" maxStretchBits=").Append(Bits(cloth.MaxStretchRatio()));
            }

            var soft = new SoftBodySimulation(new SoftBodyParameters { mass = 0.9f, substeps = 4 });
            var mesh = BuildUnitCubeMesh(0.5f);
            soft.Build(SoftBodyMeshData.FromMesh(mesh));
            for (int i = 0; i < 60; i++) soft.Step(1f / 60f);
            unchecked
            {
                int sh = 17;
                for (int i = 0; i < soft.ParticleCount; i++)
                {
                    Vector3 p = soft.GetPosition(i);
                    sh = sh * 31 + Bits(p.x); sh = sh * 31 + Bits(p.y); sh = sh * 31 + Bits(p.z);
                }
                sb.Append(" softHash=").Append(sh);
            }

            var chain = new MassSpringSystem();
            chain.Parameters.substeps = 2;
            int head = chain.AddParticle(new Vector3(0f, 2f, 0f), 1f, true);
            int prev = head;
            for (int i = 1; i < 8; i++)
            {
                int next = chain.AddParticle(new Vector3(i * 0.25f, 2f, 0f), 1f);
                chain.AddSpring(prev, next, 0.25f, 900f, 4f);
                prev = next;
            }
            for (int i = 0; i < 120; i++) chain.Step(1f / 60f);
            unchecked
            {
                int ch = 17;
                for (int i = 0; i < chain.Particles.Count; i++)
                {
                    Vector3 p = chain.Particles[i].position;
                    ch = ch * 31 + Bits(p.x); ch = ch * 31 + Bits(p.y); ch = ch * 31 + Bits(p.z);
                }
                sb.Append(" chainHash=").Append(ch);
            }

            UnityEngine.Debug.Log(sb.ToString());
        }

        [Test]
        public void Cloth_LegacyAddSphereObstacle_StillPutsParticlesOutside()
        {
            // Given：顶边钉住的布 + 一个挂在摆动路径上的球障碍（旧的局部空间 API）
            var parameters = new ClothParameters { columns = 10, rows = 8, spacing = 0.1f, iterations = 3, substeps = 4 };
            var cloth = new ClothSimulation(parameters);
            for (int col = 0; col < parameters.columns; col++) cloth.SetPinned(cloth.IndexOf(col, 0), true);
            cloth.AddSphereObstacle(new Vector3(0.45f, -0.3f, 0f), 0.25f);

            // When
            for (int i = 0; i < 60; i++) cloth.Step(1f / 60f);

            // Then：旧 API 语义不变——任何质点都不得落进 半径 + 碰撞厚度 之内
            Assert.That(cloth.ObstacleCount, Is.EqualTo(1), "旧 API 仍然要能被数出来");
            var center = new Vector3(0.45f, -0.3f, 0f);
            float surface = 0.25f + parameters.collisionThickness;
            for (int i = 0; i < cloth.ParticleCount; i++)
            {
                Vector3 delta = cloth.GetPosition(i) - center;
                Assert.That(delta.magnitude, Is.GreaterThanOrEqualTo(surface - 1e-3f),
                    "质点 " + i + " 钻进了旧球障碍内部，说明局部空间那条路径被改坏了");
            }
        }

        [Test]
        public void Cloth_WorldBoxAndLegacySphereCoexist()
        {
            // Given：布料既有一个局部空间的球障碍，又有一个世界空间的盒子（当作桌子）
            var parameters = new ClothParameters { columns = 10, rows = 10, spacing = 0.1f, iterations = 2, substeps = 4 };
            var cloth = new ClothSimulation(parameters);
            for (int col = 0; col < parameters.columns; col++) cloth.SetPinned(cloth.IndexOf(col, 0), true);
            cloth.AddSphereObstacle(new Vector3(0.45f, -0.3f, 0f), 0.2f);
            cloth.Collisions.Add(new BoxCollisionProxy(new Vector3(0.45f, -0.95f, 0f),
                new Vector3(0.6f, 0.05f, 0.6f), Quaternion.identity), CollisionProxySpace.World);

            // When
            for (int i = 0; i < 90; i++) cloth.Step(1f / 60f);

            // Then：两种空间同时生效，谁都不许被忽略，也不许把状态搞成 NaN
            Assert.That(cloth.HasColliders, Is.True, "混合空间下应当报告有碰撞体");
            Assert.That(cloth.HasNonFiniteState(), Is.False, "混合碰撞不得产生 NaN");
            float top = -0.9f;    // 盒子上表面（半尺寸 0.05 ⇒ -1.0 + 0.05）
            int below = 0;
            for (int i = 0; i < cloth.ParticleCount; i++)
            {
                if (cloth.GetPosition(i).y < top - 0.2f) below++;
            }
            Assert.That(below, Is.EqualTo(0), "有桌子挡着，不该有一把质点掉穿到桌面以下");
        }

        // ------------------------------------------------------------ Unity 层桥接

        [Test]
        public void ColliderProxies_ReadSphereBoxCapsuleGeometryInWorldSpace()
        {
            var root = new GameObject("ProxyColliders");
            try
            {
                // Given：三种 primitive 碰撞体，各自带旋转与非均匀缩放
                root.transform.position = new Vector3(1f, 2f, 3f);
                root.transform.rotation = Quaternion.Euler(0f, 30f, 0f);
                root.transform.localScale = new Vector3(2f, 2f, 2f);

                var sphereGo = new GameObject("Sphere", typeof(SphereCollider));
                sphereGo.transform.SetParent(root.transform, false);
                var sphere = sphereGo.GetComponent<SphereCollider>();
                sphere.radius = 0.5f;

                var boxGo = new GameObject("Box", typeof(BoxCollider));
                boxGo.transform.SetParent(root.transform, false);
                var box = boxGo.GetComponent<BoxCollider>();
                box.center = Vector3.zero;
                box.size = new Vector3(1f, 1f, 1f);

                var capsuleGo = new GameObject("Capsule", typeof(CapsuleCollider));
                capsuleGo.transform.SetParent(root.transform, false);
                var capsule = capsuleGo.GetComponent<CapsuleCollider>();
                capsule.radius = 0.25f;
                capsule.height = 2f;
                capsule.direction = 1;    // Y 轴

                // When：从 Collider 生成代理
                var sphereProxy = ColliderProxies.TryFrom(sphere) as SphereCollisionProxy;
                var boxProxy = ColliderProxies.TryFrom(box) as BoxCollisionProxy;
                var capsuleProxy = ColliderProxies.TryFrom(capsule) as CapsuleCollisionProxy;

                // Then：几何都在世界里，且把父级缩放算进去了
                Assert.That(sphereProxy, Is.Not.Null, "SphereCollider 应当能生成球代理");
                AssertThatNear(sphereProxy.Center.x, 1f, 1e-4f, "球心必须按层级变换到世界 x");
                AssertThatNear(sphereProxy.Center.y, 2f, 1e-4f, "球心必须按层级变换到世界 y");
                AssertThatNear(sphereProxy.Center.z, 3f, 1e-4f, "球心必须按层级变换到世界 z");
                AssertThatNear(sphereProxy.Radius, 1f, 1e-4f, "球半径必须乘上 lossyScale（2 × 0.5）");

                Assert.That(boxProxy, Is.Not.Null, "BoxCollider 应当能生成盒代理");
                Assert.That(boxProxy.HalfExtents.x, Is.EqualTo(1f).Within(1e-4f), "盒子半尺寸要含缩放（0.5 × 2）");
                AssertThatNear(Vector3.Dot(boxProxy.Rotation * Vector3.right, Vector3.forward), -0.5f, 1e-4f,
                    "盒子的朝向必须继承旋转（绕 Y 30°）");

                Assert.That(capsuleProxy, Is.Not.Null, "CapsuleCollider 应当能生成胶囊代理");
                AssertThatNear(capsuleProxy.Radius, 0.5f, 1e-4f, "胶囊半径必须含缩放");
                float axisLength = (capsuleProxy.SegmentB - capsuleProxy.SegmentA).magnitude;
                // 轴段长 = 世界高度 - 两个端帽半径 = (2×2) - 2×(0.25×2) = 4 - 1 = 3
                AssertThatNear(axisLength, 3f, 1e-3f, "轴段长 = 世界高度(4) - 直径(1)，不是直接把本地轴段乘缩放");
            }
            finally
            {
                Destroy(root);
            }
        }

        [Test]
        public void ColliderProxies_MeshColliderIsReportedAsUnsupportedNotCrashed()
        {
            // Given：一个 MeshCollider（本版按用户决定：只做 primitive 解析）
            var go = new GameObject("MeshCollider", typeof(MeshCollider));
            try
            {
                // When / Then：返回 null 表示"暂不支持"，绝不抛异常、也不静默造一个假代理
                ICollisionProxy proxy = ColliderProxies.TryFrom(go.GetComponent<MeshCollider>());
                Assert.That(proxy, Is.Null, "MeshCollider 本版不支持，必须老实返回 null，而不是拿包围盒冒充");
            }
            finally
            {
                Destroy(go);
            }
        }

        [Test]
        public void Behaviour_CollisionIsOffByDefault_AndPassesThroughLikeV120()
        {
            // Given：软体组件下面放一块"地面"盒子碰撞体，但没开启开关
            var scene = PrepareGroundAndSoftBody(false);
            try
            {
                var soft = scene.Soft;
                Assert.That(soft.collideWithSceneColliders, Is.False, "默认必须关闭碰撞，不能升级完就改变现有场景的行为");

                for (int i = 0; i < 120; i++) soft.Step(1f / 60f);

                // Then：没开碰撞就仍然穿过去（与 v1.2.0 一致），这是"可选项"承诺的代价，必须看得见
                int below = 0;
                for (int i = 0; i < soft.Simulation.ParticleCount; i++)
                {
                    Vector3 world = soft.transform.TransformPoint(soft.Simulation.GetPosition(i));
                    if (world.y < scene.GroundTop - 0.5f) below++;
                }
                Assert.That(below, Is.GreaterThan(0), "关掉碰撞时应当仍然穿地：这条断言就是为了防止有人偷偷默认打开");
            }
            finally
            {
                Destroy(scene.Root);
            }
        }

        [Test]
        public void Behaviour_WithCollisionEnabled_LandsOnTheGroundBox()
        {
            // Given：同一个场景，开了 collideWithSceneColliders 并把地面盒子喂给它
            var scene = PrepareGroundAndSoftBody(true);
            try
            {
                var soft = scene.Soft;
                soft.Rebuild();
                Assert.That(soft.Simulation.HasColliders, Is.True, "开启后仿真必须真的收到碰撞体");

                // When：落 2 秒
                for (int i = 0; i < 120; i++) soft.Step(1f / 60f);

                // Then：所有质点都停在地面盒子上表面之上（含皮肤），没有 NaN
                for (int i = 0; i < soft.Simulation.ParticleCount; i++)
                {
                    Vector3 world = soft.transform.TransformPoint(soft.Simulation.GetPosition(i));
                    Assert.That(world.y, Is.GreaterThanOrEqualTo(scene.GroundTop - 1e-3f),
                        "质点 " + i + " 穿进了地面盒子，世界位置 " + world);
                }
                Assert.That(soft.Simulation.HasNonFiniteState(), Is.False, "组件碰撞路径不得产生 NaN");
            }
            finally
            {
                Destroy(scene.Root);
            }
        }

        // ------------------------------------------------------------ 辅助

        /// <summary>桥接测试用的最小场景：一个地面盒子碰撞体 + 一个自由下落的软体组件。</summary>
        sealed class BridgeScene
        {
            public GameObject Root;
            public SoftBodyBehaviour Soft;
            public BoxCollider Ground;
            public float GroundTop;      // 地面上表面的世界 y
        }

        static BridgeScene PrepareGroundAndSoftBody(bool collide)
        {
            var root = new GameObject("CollisionBridgeScene");

            var groundGo = new GameObject("Ground", typeof(BoxCollider));
            groundGo.transform.SetParent(root.transform, false);
            groundGo.transform.position = new Vector3(0f, -1f, 0f);
            var ground = groundGo.GetComponent<BoxCollider>();
            ground.center = Vector3.zero;
            ground.size = new Vector3(6f, 0.2f, 6f);        // 上表面 y = -1 + 0.1 = -0.9

            var bodyGo = new GameObject("Jelly");
            bodyGo.transform.SetParent(root.transform, false);
            bodyGo.transform.localPosition = Vector3.zero;   // 方块占世界 -0.5..0.5

            var soft = bodyGo.AddComponent<SoftBodyBehaviour>();
            soft.sourceMesh = BuildUnitCubeMesh(0.5f);
            soft.pinMode = SoftBodyPinMode.None;              // 整块自由下落
            soft.autoSimulate = false;                        // 测试里手动步进
            soft.generateMesh = false;                        // 不生成网格，省掉与断言无关的开销
            soft.parameters = new SoftBodyParameters
            {
                mass = 0.9f,
                gravity = new Vector3(0f, -9.81f, 0f),
                damping = 0.6f,
                substeps = 4,
                collisionThickness = 0.02f,
            };
            soft.collideWithSceneColliders = collide;
            if (collide) soft.sceneColliders = new List<Collider> { ground };
            soft.Rebuild();

            return new BridgeScene
            {
                Root = root,
                Soft = soft,
                Ground = ground,
                GroundTop = -0.9f,
            };
        }

        static Mesh BuildUnitCubeMesh(float half)
        {
            var mesh = new Mesh();
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            AddUnitCube(vertices, triangles, half);
            mesh.vertices = vertices.ToArray();
            mesh.triangles = triangles.ToArray();
            return mesh;
        }

        /// <summary>
        /// 边长 2×half 的闭合立方体，12 个三角形的绕序逐个手算过（法线一律朝外）。
        /// 绕序错了散度定理算出来的体积就会变号或打折——v1.2.0 就在这上面栽过。
        /// </summary>
        static void AddUnitCube(List<Vector3> vertices, List<int> triangles)
        {
            AddUnitCube(vertices, triangles, 0.5f);
        }

        static void AddUnitCube(List<Vector3> vertices, List<int> triangles, float half)
        {
            AddUnitCube(vertices, triangles, half, Vector3.zero);
        }

        static void AddUnitCube(List<Vector3> vertices, List<int> triangles, float half, Vector3 offset)
        {
            int start = vertices.Count;
            vertices.Add(offset + new Vector3(-half, -half, -half));   // 0
            vertices.Add(offset + new Vector3(half, -half, -half));    // 1
            vertices.Add(offset + new Vector3(half, half, -half));     // 2
            vertices.Add(offset + new Vector3(-half, half, -half));    // 3
            vertices.Add(offset + new Vector3(-half, -half, half));    // 4
            vertices.Add(offset + new Vector3(half, -half, half));     // 5
            vertices.Add(offset + new Vector3(half, half, half));      // 6
            vertices.Add(offset + new Vector3(-half, half, half));     // 7

            int P(int i) { return start + i; }
            // -Z / +Z / +X / -X / +Y / -Y
            triangles.AddRange(new[] { P(0), P(3), P(2), P(0), P(2), P(1) });
            triangles.AddRange(new[] { P(4), P(5), P(6), P(4), P(6), P(7) });
            triangles.AddRange(new[] { P(5), P(1), P(2), P(5), P(2), P(6) });
            triangles.AddRange(new[] { P(0), P(4), P(7), P(0), P(7), P(3) });
            triangles.AddRange(new[] { P(3), P(7), P(6), P(3), P(6), P(2) });
            triangles.AddRange(new[] { P(0), P(1), P(5), P(0), P(5), P(4) });
        }

        static SoftBodySimulation BuildCubeSoftBody()
        {
            var mesh = BuildUnitCubeMesh(0.5f);
            var soft = new SoftBodySimulation(new SoftBodyParameters
            {
                mass = 0.9f,
                substeps = 4,
                collisionThickness = 0.02f,
            });
            soft.Build(SoftBodyMeshData.FromMesh(mesh));
            return soft;
        }

        static void AssertThatNear(float actual, float expected, float tolerance, string scene)
        {
            Assert.That(actual, Is.EqualTo(expected).Within(tolerance),
                scene + "：期望 " + expected + "，实际 " + actual);
        }
    }
}
