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
    /// 把**真实美术模型**摆成一排软体、从不同高度自由落体砸地面。
    ///
    /// 为什么要有这个工具：审计页（<see cref="SoftBodyModelAuditTools"/>）只给数字，
    /// 而"Healthy 到底长什么样、翻面到底长什么样"必须看出来。默认挑的就是审计判定为
    /// Healthy 的 8 个道具，所以这个场景同时是**对审计报告的一次可视复核**。
    ///
    /// 设计上刻意分成两层：
    ///  * 纯函数（<see cref="DropHeightFor"/> / <see cref="PlacementFor"/> / <see cref="CameraPose"/>）
    ///    —— 落点多高、彼此隔多远、相机怎么框，全部可断言，不靠手调；
    ///  * 资产层（<see cref="ResolveMeshes"/>）—— 路径 → Mesh，缺资产只上报不抛。
    /// 构建核心 <see cref="BuildModels"/> 吃 Mesh 列表而不是资产路径，
    /// 所以这个包的测试在**没有这批美术资产的工程里照样能跑**。
    /// </summary>
    public static class SoftBodyModelsDemoTools
    {
        public const int CreateMenuPriority = 125;
        public const int BuildMenuPriority = 126;

        /// <summary>菜单路径提成常量：属性参数必须是编译期常量，同时测试也能钉住它。</summary>
        public const string CreateMenuPath = "Tools/Physics Simulation/Soft Body/Create Real Model Demo Scene";
        public const string BuildMenuPath = "Tools/Physics Simulation/Soft Body/Build Real Models In Current Scene";
        public const string ScenePath = "Assets/Scenes/SoftBodyModelsDemo.unity";

        /// <summary>
        /// 默认名单 = 审计里判定 **Healthy** 且体量跨度大的 8 个道具（数字来自 Logs/SoftBodyMeshAudit.md）：
        /// 桶 60 / 凳 32 / 灌木 66 / 花盆 180 / 营火 160 / 弯道铁轨 216 / 桥 168 / 棕榈 148 质点，
        /// 宽度从 0.75 米跨到 6.51 米 —— 尺寸跨度正是这个演示要看的东西。
        ///
        /// **不含** <c>prop_rock_c</c>（实测 Unstable，保持率 0.583）与 <c>prop_clock_tower</c>
        /// （实测跑着跑着翻了面，保持率 -1.308）：那两个属于"想看失败长什么样"时另开一批。
        /// </summary>
        public static readonly string[] DefaultModelPaths =
        {
            "Assets/ArtRes/Showcase/Models/prop_barrel.fbx",
            "Assets/ArtRes/Showcase/Models/prop_bench.fbx",
            "Assets/ArtRes/Showcase/Models/prop_bush.fbx",
            "Assets/ArtRes/Showcase/Models/prop_flower_pot.fbx",
            "Assets/ArtRes/Showcase/Models/prop_campfire.fbx",
            "Assets/ArtRes/Showcase/Models/prop_mine_track_curve.fbx",
            "Assets/ArtRes/Showcase/Models/prop_bridge.fbx",
            "Assets/ArtRes/Showcase/Models/prop_tree_palm.fbx",
        };

        /// <summary>最近一次构建用到的材质来源（诊断用：品红 = 着色器不对）。</summary>
        public static string LastMaterialPath { get; private set; }

        /// <summary>相邻两个软体之间留的空隙（米）。撞开之后不能互相穿插。</summary>
        const float SideGap = 0.8f;

        /// <summary>第一个落点高度（米）与逐个增量。</summary>
        const float FirstDropHeight = 1.5f;
        const float DropHeightStep = 0.9f;

        static readonly Color[] Palette =
        {
            new Color(0.30f, 0.72f, 0.95f, 1f),
            new Color(0.95f, 0.62f, 0.25f, 1f),
            new Color(0.45f, 0.85f, 0.45f, 1f),
            new Color(0.90f, 0.45f, 0.55f, 1f),
            new Color(0.70f, 0.55f, 0.95f, 1f),
            new Color(0.95f, 0.85f, 0.35f, 1f),
            new Color(0.35f, 0.85f, 0.80f, 1f),
            new Color(0.80f, 0.80f, 0.88f, 1f),
        };

        // ==================================================================
        // 纯函数：落点高度 / 摆位 / 相机
        // ==================================================================

        /// <summary>
        /// 第 <paramref name="index"/> 个软体的**底面**离地高度（米）。逐个递增，
        /// 这样八块会在不同时刻砸到地面，一眼就能比较"谁弹、谁瘪、谁翻面"。
        /// 上限 9 米：再高就掉出相机视野，而且落地速度大到拉伸限幅都救不回来。
        /// </summary>
        public static float DropHeightFor(int index)
        {
            if (index < 0) index = 0;
            return Mathf.Min(FirstDropHeight + DropHeightStep * index, 9f);
        }

        /// <summary>
        /// 第 <paramref name="index"/> 个软体的摆位（假定网格**绕自身中心**）。
        /// x 沿 <paramref name="cursorX"/> 往后排，间隔至少是两者半宽之和再加 <see cref="SideGap"/>；
        /// y 让底面正好落在 <see cref="DropHeightFor"/> 上。
        /// </summary>
        public static Vector3 PlacementFor(int index, Vector3 size, float cursorX, out float nextCursorX)
        {
            float halfX = size.x * 0.5f;
            float x = cursorX + halfX + (cursorX > 0f ? SideGap : 0f);
            nextCursorX = x + halfX;
            return new Vector3(x, DropHeightFor(index) + size.y * 0.5f, 0f);
        }

        /// <summary>
        /// 由整排摆位算相机位姿：对准中心、退够远（越宽越退）、略俯视。
        /// 单独抽出来是因为"演示没拍全"是最容易被忽略的失败 —— 它也得能被断言。
        /// </summary>
        public static void CameraPose(IList<Vector3> positions, out Vector3 cameraPosition, out Vector3 cameraRotation)
        {
            Vector3 center = Vector3.zero;
            float minX = float.MaxValue, maxX = float.MinValue, topY = 0f;
            int n = positions == null ? 0 : positions.Count;
            for (int i = 0; i < n; i++)
            {
                center += positions[i];
                minX = Mathf.Min(minX, positions[i].x - 1f);
                maxX = Mathf.Max(maxX, positions[i].x + 1f);
                topY = Mathf.Max(topY, positions[i].y);
            }
            if (n == 0) { cameraPosition = new Vector3(0f, 3f, -8f); cameraRotation = new Vector3(20f, 0f, 0f); return; }
            center /= n;

            float spread = maxX - minX;                       // 整排宽度（含两侧各 1 米余量）
            float distance = 4f + spread * 0.75f + topY * 0.45f;
            var direction = new Vector3(0.16f, 0.42f, -1f).normalized;

            cameraPosition = center + direction * distance;
            cameraRotation = Quaternion.LookRotation(center - cameraPosition).eulerAngles;
        }

        /// <summary>重载：把欧拉角输出成 Quaternion 版本，方便直接用。</summary>
        public static void CameraPose(IList<Vector3> positions, out Vector3 cameraPosition, out Quaternion cameraRotation)
        {
            Vector3 e;
            CameraPose(positions, out cameraPosition, out e);
            cameraRotation = Quaternion.Euler(e);
        }

        // ==================================================================
        // 资产层：路径 → Mesh（缺资产不抛，只上报）
        // ==================================================================

        /// <summary>
        /// 把资产路径解析成 Mesh（模型取第一个带 MeshFilter 的子节点）。
        /// 找不到的**跳过并写进 error** —— 扫描/构建工具最怕中途抽风，一次跑不完就等于没结果。
        /// </summary>
        public static List<Mesh> ResolveMeshes(IList<string> assetPaths, out string error)
        {
            var meshes = new List<Mesh>();
            var missing = new List<string>();
            int count = assetPaths == null ? 0 : assetPaths.Count;
            for (int i = 0; i < count; i++)
            {
                string path = assetPaths[i];
                var mesh = MeshAt(path);
                if (mesh == null) missing.Add(path);
                else meshes.Add(mesh);
            }

            var sb = new StringBuilder();
            if (missing.Count > 0)
                sb.Append("有 ").Append(missing.Count).Append(" 个路径找不到网格：")
                  .Append(string.Join("、", missing.ToArray()));
            if (missing.Count > 0 && meshes.Count == 0)
                sb.Append("（一个都没成，默认名单属于别的工程的美术资产时就是这个情况）");
            error = sb.ToString();
            return meshes;
        }

        static Mesh MeshAt(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var main = AssetDatabase.LoadMainAssetAtPath(path);
            if (main == null) return null;

            var direct = main as Mesh;
            if (direct != null) return direct;

            var go = main as GameObject;
            if (go != null)
            {
                var filter = go.GetComponentInChildren<MeshFilter>();
                if (filter != null) return filter.sharedMesh;
            }

            // 兜底：从子资产里捞第一个 Mesh（有些导入器不把 Mesh 设为 main asset）
            foreach (var sub in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                var m = sub as Mesh;
                if (m != null && m.vertexCount > 0) return m;
            }
            return null;
        }

        // ==================================================================
        // 构建核心：Mesh 列表 → 场景里的软体
        // ==================================================================

        /// <summary>
        /// 在 <paramref name="parent"/> 下为每个网格建一个软体：不钉任何质点、开地面碰撞、零初速（纯自由落体）。
        /// 返回建出的个数；网格为 null 的槽位跳过。
        /// </summary>
        public static int BuildModels(Transform parent, IList<Mesh> meshes, Collider ground)
        {
            if (parent == null || meshes == null) return 0;

            float cursor = 0f;
            int built = 0;
            for (int i = 0; i < meshes.Count; i++)
            {
                var mesh = meshes[i];
                if (mesh == null) continue;

                var size = mesh.bounds.size;
                var placement = PlacementFor(built, size, cursor, out cursor);
                // 摆位公式假定网格绕中心；美术模型常把轴心放在底面，所以按 bounds.min.y 修正一次
                float pivotCorrection = size.y * 0.5f - mesh.bounds.min.y;
                placement.y += pivotCorrection;

                var go = new GameObject("Jelly_" + SanitizeName(mesh.name) + "_" + built);
                go.transform.SetParent(parent, false);
                go.transform.position = placement;

                var filter = go.AddComponent<MeshFilter>();
                filter.sharedMesh = mesh;

                var renderer = go.AddComponent<MeshRenderer>();
                string materialPath;
                DemoMaterialHelper.Apply(renderer, Palette[built % Palette.Length], null, out materialPath);
                LastMaterialPath = materialPath;

                var body = go.AddComponent<SoftBodyBehaviour>();
                body.sourceMesh = mesh;
                body.pinMode = SoftBodyPinMode.None;           // 整块自由落体才有说服力
                body.initialVelocity = Vector3.zero;           // 零初速：纯靠重力，比较的是"谁扛得住自己"
                body.collideWithSceneColliders = ground != null;
                body.sceneColliders = ground != null ? new List<Collider> { ground } : new List<Collider>();
                built++;
            }
            return built;
        }

        static string SanitizeName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "mesh";
            int cut = raw.IndexOfAny(new[] { '(', ' ', '[' });
            return cut > 0 ? raw.Substring(0, cut) : raw;
        }

        // ==================================================================
        // 菜单
        // ==================================================================

        [MenuItem(CreateMenuPath, false, CreateMenuPriority)]
        public static void CreateRealModelDemoScene()
        {
            // 静默存盘：绝不能用 SaveCurrentModifiedScenesIfUserWantsTo —— 它弹模态框，
            // 从 ExecuteMenuItem 里调用会把编辑器主线程永久堵死。
            if (!EditorSceneManager.SaveOpenScenes())
                Debug.LogWarning("[PhysicsSimulation] 有场景没能保存，仍继续生成真实模型软体场景。");

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // 地面先建：软体要从 Rebuild 就拿到碰撞体，否则第一帧会穿过去
            var ground = SetupGround();
            string error;
            var meshes = ResolveMeshes(DefaultModelPaths, out error);

            var root = new GameObject("RealModelJellies");
            int built = BuildModels(root.transform, meshes, ground);

            var positions = CollectPositions(root.transform);
            SetupCameraAndLight(positions);

            AssetDatabase.Refresh();
            EditorSceneManager.SaveOpenScenes();
            EditorSceneManager.SaveScene(scene, ScenePath);

            var sb = new StringBuilder();
            sb.Append("[PhysicsSimulation] 真实模型软体场景已生成：").Append(ScenePath)
              .Append("（").Append(built).Append(" 块，全部不钉质点 + 零初速 + 开地面碰撞；材质来源 ⇒ ")
              .Append(LastMaterialPath).Append("）\n");
            for (int i = 0; i < positions.Count; i++)
                sb.Append("  ").Append(i).Append(" 底面高 ").Append(DropHeightFor(i).ToString("0.00"))
                  .Append(" m @ x=").Append(positions[i].x.ToString("0.00")).Append('\n');
            if (!string.IsNullOrEmpty(error)) sb.Append("注意：").Append(error).Append('\n');
            sb.Append("播放后会看到它们错峰砸到地面；出问题时用 Tools/Physics Simulation/Soft Body/Dump State 看每块的体积保持率。");
            Debug.Log(sb.ToString());
        }

        [MenuItem(BuildMenuPath, false, BuildMenuPriority)]
        public static void BuildRealModelsInCurrentScene()
        {
            var ground = FindExistingGround();
            string error;
            var meshes = ResolveMeshes(DefaultModelPaths, out error);
            var root = new GameObject("RealModelJellies");
            int built = BuildModels(root.transform, meshes, ground);
            Debug.Log("[PhysicsSimulation] 已在当前场景生成 " + built + " 块真实模型软体（不写盘）。"
                      + (string.IsNullOrEmpty(error) ? "" : "注意：" + error));
        }

        // ==================================================================
        // 场景辅助
        // ==================================================================

        static List<Vector3> CollectPositions(Transform parent)
        {
            var list = new List<Vector3>();
            foreach (var body in parent.GetComponentsInChildren<SoftBodyBehaviour>(true))
                list.Add(body.transform.position);
            return list;
        }

        static Collider SetupGround()
        {
            // 地面必须是 Cube + BoxCollider：CreatePrimitive(Plane) 自带 MeshCollider，
            // 而本版只做 primitive 解析碰撞、明确不支持 MeshCollider（也不拿包围盒冒充）。
            var ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ground.name = "Ground";
            ground.transform.position = new Vector3(12f, -0.5f, 0f);
            ground.transform.localScale = new Vector3(70f, 1f, 24f);
            return ground.GetComponent<Collider>();
        }

        static Collider FindExistingGround()
        {
            var existing = UnityEngine.Object.FindObjectOfType<BoxCollider>();
            return existing != null ? existing : SetupGround();
        }

        static void SetupCameraAndLight(IList<Vector3> positions)
        {
            Vector3 camPos; Quaternion camRot;
            CameraPose(positions, out camPos, out camRot);

            var cameraObject = new GameObject("Main Camera");
            cameraObject.tag = "MainCamera";
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.Skybox;
            cameraObject.transform.position = camPos;
            cameraObject.transform.rotation = camRot;

            var lightObject = new GameObject("Directional Light");
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            lightObject.transform.rotation = Quaternion.Euler(52f, -28f, 0f);
        }
    }
}
