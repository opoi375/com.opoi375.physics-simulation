// Copyright (c) 2026 PhysicsSimulation. MIT License.
using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace PhysicsSimulation.EditorTools
{
    /// <summary>
    /// 软体演示工具：Tools ▸ Physics Simulation  Soft Body ▸ *（优先级 120~122，排在质点弹簧 100~103、布料 110~112 之后）。
    ///
    /// 演示内容是两块"果冻"：一块底面钉住、顶部被横向推偏后自己晃回来（看体积保持），
    /// 一块顶面钉住、被重力拽着下垂（看软体被拉长但不漏气）。
    /// </summary>
    public static class SoftBodyDemoTools
    {
        public const string ScenePath = "Assets/Scenes/SoftBodyDemo.unity";

        const int BoxSubdivisions = 2;            // 每面切成 2×2 ⇒ 顶点更多、形变更细腻
        const float BoxSize = 0.9f;
        // 扰动必须走 SoftBodyBehaviour.initialVelocity（可序列化），不能只改运行时质点位置：
        // Play 时 Awake 会拿源网格重建模拟，任何"编辑期直接推一把"都会当场丢光，画面就成了静态。
        static readonly Vector3 JellyKick = new Vector3(2.4f, 0f, 0f);      // 果冻横向甩一下再晃回来
        static readonly Vector3 BagKick = new Vector3(0f, 0f, 2.2f);        // 袋子像钟摆一样荡

        /// <summary>最近一次构建用到的材质来源（诊断用：品红 = 着色器不对，这里能一眼看出走了哪条路径）。</summary>
        public static string LastMaterialPath { get; private set; }

        [MenuItem("Tools/Physics Simulation/Soft Body/Create Soft Body Demo Scene", false, 120)]
        public static void CreateSoftBodyDemoScene()
        {
            // 静默存盘：绝不能用 SaveCurrentModifiedScenesIfUserWantsTo —— 它会弹模态框，
            // 从自动化（ExecuteMenuItem / CI）里调用时没人点那个框，编辑器主线程就永久堵死了。
            if (!EditorSceneManager.SaveOpenScenes())
            {
                Debug.LogWarning("[PhysicsSimulation] 有场景没能保存，仍继续生成软体演示场景。");
            }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var jelly = BuildSoftBody();
            var bag = BuildHangingBag();

            SetupGround();
            SetupCameraAndLight(jelly.transform.position, bag.transform.position);

            AssetDatabase.Refresh();
            EditorSceneManager.SaveOpenScenes();
            EditorSceneManager.SaveScene(scene, ScenePath);

            var system = jelly.Simulation;
            Debug.Log("[PhysicsSimulation] 软体演示场景已生成：" + ScenePath
                + "（材质来源 ⇒ " + LastMaterialPath + "；"
                + "果冻 " + system.ParticleCount + " 质点 / " + system.StructuralSpringCount + " 结构弹簧 / "
                + system.BendSpringCount + " 弯曲弹簧 / " + system.TriangleCount + " 三角形，"
                + "静止体积 " + system.RestVolume().ToString("0.0000") + "，底面钉住 + 初速度 " + JellyKick + "；"
                + "另一块顶面钉住、初速度 " + BagKick + "）。播放即可看到果冻回弹、袋子荡摆，选中物体可看弹簧 Gizmos。");
        }

        [MenuItem("Tools/Physics Simulation/Soft Body/Build In Current Scene", false, 121)]
        public static void BuildSoftBodyInCurrentScene()
        {
            BuildSoftBody();
            BuildHangingBag();
            Debug.Log("[PhysicsSimulation] 已在当前场景生成两块软体（不写盘）。");
        }

        [MenuItem("Tools/Physics Simulation/Soft Body/Dump State", false, 122)]
        public static void DumpSoftBodyState()
        {
            var all = UnityEngine.Object.FindObjectsByType<SoftBodyBehaviour>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            if (all.Length == 0)
            {
                Debug.LogWarning("[PhysicsSimulation] 当前场景里没有 SoftBodyBehaviour，先执行 Build In Current Scene。");
                return;
            }

            foreach (var found in all)
            {
                var sb = new StringBuilder();
                sb.Append("[PhysicsSimulation] 软体状态 —— 物体: ").Append(found.gameObject.name).Append('\n');
                // 这几个开关决定"为什么画面不动"，出问题时第一眼就要看到
                sb.Append("  enabled: ").Append(found.enabled)
                  .Append(" | autoSimulate: ").Append(found.autoSimulate)
                  .Append(" | activeInHierarchy: ").Append(found.gameObject.activeInHierarchy).Append('\n');
                sb.Append("  IsBuilt: ").Append(found.IsBuilt);
                if (!found.IsBuilt)
                {
                    sb.Append(" | 失败原因: [").Append(found.LastBuildError ?? "null（说明 Rebuild 压根没跑过）").Append(']');
                    Debug.Log(sb.ToString());
                    continue;
                }

                var system = found.Simulation;
                sb.Append('\n').Append("  质点 ").Append(system.ParticleCount)
                  .Append(" | 结构弹簧 ").Append(system.StructuralSpringCount)
                  .Append(" | 弯曲弹簧 ").Append(system.BendSpringCount)
                  .Append(" | 三角形 ").Append(system.TriangleCount)
                  .Append(" | 闭合 ").Append(system.IsClosed)
                  .Append(" | 钉住 ").Append(found.PinnedParticleCount).Append('\n');
                sb.Append("  静止体积 ").Append(system.RestVolume().ToString("0.00000"))
                  .Append(" | 当前体积 ").Append(system.Volume().ToString("0.00000"))
                  .Append(" | 体积保持率 ").Append((system.RestVolume() > 0f
                      ? (system.Volume() / system.RestVolume()).ToString("0.000") : "n/a")).Append('\n');
                sb.Append("  最大拉伸比 ").Append(system.MaxStretchRatio().ToString("0.0000"))
                  .Append(" | 最大速度 ").Append(system.System.MaxSpeed().ToString("0.0000")).Append('\n');
                sb.Append("  非有限状态 ").Append(system.HasNonFiniteState())
                  .Append(" | 实例网格顶点 ").Append(found.Mesh == null ? 0 : found.Mesh.vertexCount)
                  .Append(" | 世界坐标 ").Append(found.transform.position.ToString("F3"));
                Debug.Log(sb.ToString());
            }
        }

        /// <summary>
        /// 造一块"底面钉住 + 顶部横向推偏"的果冻。返回组件，调用方可以直接 Step。
        /// </summary>
        public static SoftBodyBehaviour BuildSoftBody()
        {
            var root = new GameObject("SoftBodyJelly");
            root.transform.position = new Vector3(-0.85f, 0.62f, 0f);

            var filter = root.AddComponent<MeshFilter>();
            var renderer = root.AddComponent<MeshRenderer>();
            var behaviour = root.AddComponent<SoftBodyBehaviour>();

            behaviour.sourceMesh = BuildBoxMesh(BoxSize, BoxSize, BoxSize, BoxSubdivisions);
            behaviour.pinMode = SoftBodyPinMode.BottomVertices;
            behaviour.parameters = JellyParameters();
            behaviour.initialVelocity = JellyKick;
            behaviour.Rebuild();

            string path;
            DemoMaterialHelper.Apply(renderer, new Color(0.30f, 0.72f, 0.95f, 1f), null, out path);
            LastMaterialPath = path;
            return behaviour;
        }

        /// <summary>造一块"顶面钉住、被重力拽着下垂"的软体袋。</summary>
        public static SoftBodyBehaviour BuildHangingBag()
        {
            var root = new GameObject("SoftBodyBag");
            root.transform.position = new Vector3(0.95f, 1.75f, 0f);

            var filter = root.AddComponent<MeshFilter>();
            var renderer = root.AddComponent<MeshRenderer>();
            var behaviour = root.AddComponent<SoftBodyBehaviour>();

            behaviour.sourceMesh = BuildBoxMesh(0.8f, 1.1f, 0.8f, BoxSubdivisions);
            behaviour.pinMode = SoftBodyPinMode.TopVertices;
            behaviour.parameters = JellyParameters();
            behaviour.parameters.damping = 1.2f;
            behaviour.initialVelocity = BagKick;
            behaviour.Rebuild();

            string path;
            DemoMaterialHelper.Apply(renderer, new Color(0.95f, 0.62f, 0.25f, 1f), null, out path);
            LastMaterialPath = path;
            return behaviour;
        }

        static SoftBodyParameters JellyParameters()
        {
            return new SoftBodyParameters
            {
                mass = 0.9f,
                gravity = new Vector3(0f, -9.81f, 0f),
                // 演示要一眼看出"软"，所以刚度刻意调低：k=130 时推一把能歪十几度再晃回来，
                // 之前用 260 看着像块木头（最大拉伸比才 1.017）。
                damping = 0.6f,
                springStiffness = 130f,
                springDamping = 6f,
                bendStiffness = 25f,
                bendDamping = 3f,
                volumeStiffness = 1400f,
                volumeDamping = 26f,
                substeps = 4,
                maxDeltaTime = 1f / 15f,
                weldTolerance = 1e-4f,
                enableStretchLimit = true,
                maxStretchRatio = 1.8f
            };
        }

        /// <summary>
        /// 程序化长方体网格：每个面 subdiv×subdiv 格，顶点按面拆开（正是软体要焊接去重的典型输入）。
        /// </summary>
        public static Mesh BuildBoxMesh(float width, float height, float depth, int subdivisions)
        {
            int n = Mathf.Max(1, subdivisions);
            float hx = width * 0.5f, hy = height * 0.5f, hz = depth * 0.5f;

            var verts = new List<Vector3>();
            var tris = new List<int>();

            // 六个面：给出"角点 + 两个切向"，面内铺 n×n 格。
            // 绕序必须统一朝外：AddFace 里三角形是 (a,c,b)/(b,c,d)，其法线 = cross(vEdge, uEdge)，
            // 所以每个面的 (uEdge, vEdge) 要挑成 cross(vEdge, uEdge) == 该面外法线。
            // 之前随手写导致 4 个面朝外、2 个面朝内，散度定理算出来的体积只剩解析值的 1/3。
            AddFace(verts, tris, new Vector3(-hx, -hy, -hz), new Vector3(0f, 2f * hy, 0f), new Vector3(0f, 0f, 2f * hz), n);   // -x：u=+Y v=+Z
            AddFace(verts, tris, new Vector3(hx, -hy, -hz), new Vector3(0f, 0f, 2f * hz), new Vector3(0f, 2f * hy, 0f), n);     // +x：u=+Z v=+Y
            AddFace(verts, tris, new Vector3(-hx, -hy, -hz), new Vector3(0f, 0f, 2f * hz), new Vector3(2f * hx, 0f, 0f), n);    // -y：u=+Z v=+X
            AddFace(verts, tris, new Vector3(-hx, hy, -hz), new Vector3(2f * hx, 0f, 0f), new Vector3(0f, 0f, 2f * hz), n);     // +y：u=+X v=+Z
            AddFace(verts, tris, new Vector3(-hx, -hy, -hz), new Vector3(2f * hx, 0f, 0f), new Vector3(0f, 2f * hy, 0f), n);    // -z：u=+X v=+Y
            AddFace(verts, tris, new Vector3(-hx, -hy, hz), new Vector3(0f, 2f * hy, 0f), new Vector3(2f * hx, 0f, 0f), n);     // +z：u=+Y v=+X

            var mesh = new Mesh();
            if (verts.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = verts.ToArray();
            mesh.triangles = tris.ToArray();
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        static void AddFace(List<Vector3> verts, List<int> tris, Vector3 corner, Vector3 uEdge, Vector3 vEdge, int n)
        {
            int start = verts.Count;
            for (int v = 0; v <= n; v++)
                for (int u = 0; u <= n; u++)
                    verts.Add(corner + uEdge * ((float)u / n) + vEdge * ((float)v / n));

            int stride = n + 1;
            for (int v = 0; v < n; v++)
            {
                for (int u = 0; u < n; u++)
                {
                    int a = start + v * stride + u;
                    int b = a + 1;
                    int c = a + stride;
                    int d = c + 1;
                    // 绕序固定为 (a,c,b) / (b,c,d)：从外面看是逆时针 ⇒ 散度定理给出正体积
                    tris.Add(a); tris.Add(c); tris.Add(b);
                    tris.Add(b); tris.Add(c); tris.Add(d);
                }
            }
        }

        static void SetupGround()
        {
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.position = new Vector3(0f, -0.02f, 0f);
            ground.transform.localScale = new Vector3(4f, 1f, 4f);
        }

        static void SetupCameraAndLight(Vector3 focusA, Vector3 focusB)
        {
            var cameraObject = new GameObject("Main Camera");
            cameraObject.tag = "MainCamera";
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.Skybox;

            Vector3 focus = (focusA + focusB) * 0.5f;
            cameraObject.transform.position = focus + new Vector3(0.35f, 0.75f, -3.3f);
            cameraObject.transform.LookAt(focus + new Vector3(0f, -0.15f, 0f));

            var lightObject = new GameObject("Directional Light");
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            lightObject.transform.rotation = Quaternion.Euler(52f, -28f, 0f);
        }
    }
}
