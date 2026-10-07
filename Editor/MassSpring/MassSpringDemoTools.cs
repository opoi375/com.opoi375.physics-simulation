// Copyright (c) 2026 PhysicsSimulation. MIT License.
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace PhysicsSimulation.EditorTools
{
    /// <summary>
    /// 质点弹簧的编辑器工具三件套：
    ///   Tools &gt; Physics Simulation &gt; Create Demo Scene / Build In Current Scene / Dump State
    /// 风格与包内其它工具一致：菜单前缀、静默存盘、诊断打印。
    /// </summary>
    public static class MassSpringDemoTools
    {
        const string RootName = "PHYSICS_DEMO_MassSpring";
        const string ScenePath = "Assets/Scenes/PhysicsDemo.unity";

        // 链子几何：吊点高度、节间距
        const float AnchorY = 3f;
        const float Spacing = 0.45f;
        const int LinkCount = 5;

        // 初始侧偏角（度）：垂挂静止的话看不到任何摆动，侧着开才“上硬下软”的层次才看得清
        const float StartTiltDegrees = 38f;

        // 上硬下软：k 逐节递减，摆起来才有"上面几乎不动、下面甩得欢"的层次
        static readonly float[] Stiffness = { 1600f, 1100f, 750f, 500f, 320f };
        const float SpringDamping = 1.5f;
        const float SegmentMass = 0.8f;
        const float BobMass = 1.2f;

        // ------------------------------------------------------------------
        // Create Demo Scene：新建场景、搭链子、打光、架相机，然后静默存盘。
        // ------------------------------------------------------------------
        [MenuItem("Tools/Physics Simulation/Create Demo Scene", false, 100)]
        public static void CreateDemoScene()
        {
            if (!PrepareForNewScene()) return;

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            BuildChain();
            SetupCameraAndLight();

            var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            EditorSceneManager.SaveScene(active, ScenePath);
            AssetDatabase.Refresh();

            Debug.Log("[PhysicsSimulation] 演示场景已生成：" + ScenePath +
                      "（" + LinkCount + " 节链、k 逐节递减 " + Stiffness[0] + "→" + Stiffness[LinkCount - 1] +
                      "）。播放即可看到上硬下软的摆动，Gizmos 里压缩偏蓝、拉伸偏红。");
        }

        /// <summary>
        /// 【不要】用 SaveCurrentModifiedScenesIfUserWantsTo()：它弹模态框，从脚本 / 自动化
        /// （UnitySkills 的 ExecuteMenuItem、CI）里调用时没人点，编辑器主线程会永久堵死。
        /// 这里改成静默存盘，存不了（未命名场景）就中止并报错。
        /// </summary>
        static bool PrepareForNewScene()
        {
            if (DemoSceneSave.SaveOpenScenesWithoutPrompting()) return true;

            Debug.LogError("[PhysicsSimulation] 有未保存且没有文件路径的场景，无法静默保存，已中止。" +
                           "请先手动保存或另存该场景，再生成演示场景。");
            return false;
        }

        // ------------------------------------------------------------------
        // Build In Current Scene：只在当前场景重建，不写场景文件
        // ------------------------------------------------------------------
        [MenuItem("Tools/Physics Simulation/Build In Current Scene", false, 101)]
        public static void BuildInCurrentScene()
        {
            var existing = Object.FindAnyObjectByType<MassSpringBehaviour>();
            if (existing != null)
            {
                Undo.RecordObject(existing, "Rebuild Mass Spring Demo");
                Undo.DestroyObjectImmediate(existing.gameObject);
            }

            GameObject root = BuildChain().gameObject;
            EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
            if (root != null) EditorGUIUtility.PingObject(root);
        }

        // ------------------------------------------------------------------
        // Dump State：诊断用，把"能不能跑起来"的前提一次打全
        // ------------------------------------------------------------------
        [MenuItem("Tools/Physics Simulation/Dump State", false, 102)]
        public static void DumpState()
        {
            MassSpringBehaviour behaviour = Object.FindAnyObjectByType<MassSpringBehaviour>();
            if (behaviour == null)
            {
                Debug.LogWarning("[PhysicsSimulation] 当前场景里没有 MassSpringBehaviour。" +
                                 "先跑 Tools > Physics Simulation > Build In Current Scene。");
                return;
            }

            MassSpringSystem system = behaviour.System;
            if (system == null)
            {
                Debug.LogError("[PhysicsSimulation] 系统未构建，原因：" + behaviour.LastBuildError);
                return;
            }

            var report = new System.Text.StringBuilder();
            report.AppendLine("[PhysicsSimulation] 系统状态");
            report.AppendLine("  粒子数        = " + system.Particles.Count);
            report.AppendLine("  弹簧数        = " + system.Springs.Count);
            report.AppendLine("  固定点数      = " + CountPinned(system));
            report.AppendLine("  substeps      = " + system.Parameters.substeps +
                              "（生效 " + system.Parameters.EffectiveSubsteps + "）");
            report.AppendLine("  maxDeltaTime  = " + system.Parameters.maxDeltaTime.ToString("F4") + " s");
            report.AppendLine("  gravity       = " + system.Parameters.gravity.ToString("F3"));
            report.AppendLine("  globalDamping = " + system.Parameters.globalDamping.ToString("F3") + " 1/s");
            report.AppendLine("  最大速度      = " + system.MaxSpeed().ToString("F4") + " m/s");
            report.AppendLine("  出现 NaN/Inf  = " + (system.HasNonFiniteState() ? "是（已发散）" : "否"));
            report.AppendLine("  质点位置：");
            for (int i = 0; i < system.Particles.Count; i++)
            {
                Particle p = system.Particles[i];
                report.Append("    [").Append(i).Append("] ")
                      .Append(p.position.ToString("F4"))
                      .Append("  v=").Append(p.velocity.ToString("F4"))
                      .Append("  m=").Append(p.mass.ToString("F3"))
                      .Append(p.pinned ? "  (fixed)" : "")
                      .AppendLine();
            }
            for (int s = 0; s < system.Springs.Count; s++)
            {
                Spring spring = system.Springs[s];
                float strain = spring.Strain(system.Particles[spring.a].position, system.Particles[spring.b].position);
                report.Append("    spring[").Append(s).Append("] ").Append(spring.a).Append("→").Append(spring.b)
                      .Append(" rest=").Append(spring.restLength.ToString("F4"))
                      .Append(" k=").Append(spring.stiffness.ToString("F1"))
                      .Append(" c=").Append(spring.damping.ToString("F2"))
                      .Append(" strain=").Append(strain.ToString("F4"))
                      .AppendLine();
            }
            Debug.Log(report.ToString());
        }

        // ------------------------------------------------------------------
        // 搭建：固定吊点 + 5 节弹簧链（k 逐节递减）+ 每个质点一个可见球
        // ------------------------------------------------------------------
        // MenuItem 方法必须无参且返回 void，所以菜单入口只是一层皮
        [MenuItem("Tools/Physics Simulation/Build Chain Only", false, 103)]
        static void BuildChainOnly()
        {
            BuildChain();
        }

        public static MassSpringBehaviour BuildChain()
        {
            var root = new GameObject(RootName);
            Undo.RegisterCreatedObjectUndo(root, "Build Mass Spring Chain");

            var behaviour = root.AddComponent<MassSpringBehaviour>();
            behaviour.gravity = new Vector3(0f, -9.81f, 0f);
            behaviour.globalDamping = 0.6f;
            behaviour.substeps = 8;
            behaviour.maxDeltaTime = 1f / 15f;
            behaviour.groundReferenceSize = 5f;
            behaviour.particleRadiusBase = 0.05f;

            behaviour.particles.Clear();
            behaviour.springs.Clear();

            // 0 号是吊点，钉死
            behaviour.particles.Add(new MassSpringParticleData
            {
                position = new Vector3(0f, AnchorY, 0f),
                mass = 1f,
                pinned = true
            });
            // 整条链先按 StartTiltDegrees 侧着摆好（节间距不变，因此原长依旧自动等于间距），
            // 从该姿态释放后才会看到：上面几节几乎不形变、下面几节被甩得拉长
            Quaternion tilt = Quaternion.Euler(0f, 0f, -StartTiltDegrees);
            for (int i = 1; i <= LinkCount; i++)
            {
                behaviour.particles.Add(new MassSpringParticleData
                {
                    position = new Vector3(0f, AnchorY, 0f) + tilt * (Vector3.down * (Spacing * i)),
                    mass = i == LinkCount ? BobMass : SegmentMass
                });
            }

            for (int i = 0; i < LinkCount; i++)
            {
                // restLength 留 0：MassSpringSystem.AddSpring 会自动取两端初始距离
                behaviour.springs.Add(new MassSpringSpringData
                {
                    a = i,
                    b = i + 1,
                    stiffness = Stiffness[i],
                    damping = SpringDamping,
                    restLength = 0f
                });
            }

            if (!behaviour.Rebuild())
            {
                Debug.LogError("[PhysicsSimulation] 链条构建失败：" + behaviour.LastBuildError);
                return behaviour;
            }

            // 锚点用一个方块当吊顶，链子上每个活动质点挂一个球
            var anchorMark = GameObject.CreatePrimitive(PrimitiveType.Cube);
            anchorMark.name = "Anchor";
            anchorMark.transform.SetParent(root.transform, false);
            anchorMark.transform.position = new Vector3(0f, AnchorY + 0.08f, 0f);
            anchorMark.transform.localScale = new Vector3(0.3f, 0.12f, 0.3f);

            for (int i = 1; i <= LinkCount; i++)
            {
                var ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                ball.name = "Particle_" + i;
                ball.transform.SetParent(root.transform, false);
                ball.transform.position = behaviour.System.Particles[i].position;
                ball.transform.localScale = Vector3.one * (i == LinkCount ? 0.34f : 0.2f);

                var collider = ball.GetComponent<Collider>();
                if (collider != null) collider.enabled = false;   // 演示里不要让它参与物理

                var link = ball.AddComponent<MassSpringParticleLink>();
                link.target = behaviour;
                link.particleIndex = i;
            }

            return behaviour;
        }

        static void SetupCameraAndLight()
        {
            var cameraObject = new GameObject("Main Camera");
            cameraObject.tag = "MainCamera";
            Undo.RegisterCreatedObjectUndo(cameraObject, "Add Demo Camera");
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.Skybox;
            cameraObject.AddComponent<AudioListener>();

            // 从侧前方看：链子沿 Y 垂下，摆动的平面是 XY
            Vector3 center = new Vector3(0f, AnchorY - Spacing * LinkCount * 0.5f, 0f);
            cameraObject.transform.position = center + new Vector3(1.9f, 0.55f, -3.4f);
            cameraObject.transform.LookAt(center);
            camera.fieldOfView = 42f;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 100f;

            Light light = Object.FindAnyObjectByType<Light>();
            if (light == null)
            {
                var lightObject = new GameObject("Directional Light");
                Undo.RegisterCreatedObjectUndo(lightObject, "Add Demo Light");
                light = lightObject.AddComponent<Light>();
            }
            light.type = LightType.Directional;
            light.color = new Color(1f, 0.96f, 0.9f);
            light.intensity = 1.3f;
            light.shadows = LightShadows.Soft;
            light.transform.rotation = Quaternion.Euler(42f, -28f, 0f);
        }

        static int CountPinned(MassSpringSystem system)
        {
            int count = 0;
            for (int i = 0; i < system.Particles.Count; i++)
            {
                if (system.Particles[i].pinned) count++;
            }
            return count;
        }
    }
}
