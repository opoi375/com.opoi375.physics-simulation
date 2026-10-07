// Copyright (c) 2026 PhysicsSimulation. MIT License.
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering;

namespace PhysicsSimulation.EditorTools
{
    /// <summary>
    /// 布料演示工具：Tools ▸ Physics Simulation ▸ Cloth ▸ *
    ///
    /// 只负责"把一块能看的布摆进场景"，不参与模拟本身；
    /// 网格、约束、钉边、风、障碍物都用运行时组件的默认 API 配好，
    /// 这样工具生成出来的东西，和用户手写出来的东西是同一份配置（可复现、可Dump）。
    /// </summary>
    public static class ClothDemoTools
    {
        const string ScenePath = "Assets/Scenes/ClothDemo.unity";

        // 演示参数：一面 20x14 的旗，顶边钉住，往相机方向鼓，中间裹住一个球
        const int Columns = 20;
        const int Rows = 14;
        const float Spacing = 0.08f;
        const float ObstacleRadius = 0.34f;

        /// <summary>最近一次 BuildCloth 用到的材质来源（诊断用：品红 = 着色器不对，这里能一眼看出走了哪条路径）。</summary>
        public static string LastMaterialPath { get; private set; }

        static readonly Vector3 ObstacleLocalCenter = new Vector3(
            (Columns - 1) * 0.5f * Spacing,        // 水平居中
            -Rows * Spacing * 0.62f,               // 挂在偏下：布能裹出鼓包，但球还露得出来
            0.22f);                               // 朝相机一侧：布被风吹过去裹住球，轮廓才看得出来

        // ------------------------------------------------------------------
        // 菜单：新建一整张演示场景（静默存盘，绝不弹模态框）
        // ------------------------------------------------------------------
        [MenuItem("Tools/Physics Simulation/Cloth/Create Cloth Demo Scene", false, 110)]
        public static void CreateClothDemoScene()
        {
            if (!PrepareForNewScene()) return;

            EditorSceneManager_NewSceneEmpty();

            var behaviour = BuildCloth();
            SetupCameraAndLight(behaviour);

            var active = SceneManager.GetActiveScene();
            EditorSceneManager.SaveScene(active, ScenePath);
            AssetDatabase.Refresh();

            var system = behaviour.System;
            Debug.Log("[PhysicsSimulation] 布料演示场景已生成：" + ScenePath + "（材质来源 ⇒ " + LastMaterialPath + "；" +
                      Columns + "x" + Rows + " = " + system.ParticleCount + " 质点、" +
                      system.Constraints.Count + " 条距离约束、顶边钉住、风 +Z、障碍物球半径 " + ObstacleRadius +
                      "）。播放即可看到鼓风与裹球，选中布料物体可以看线框 Gizmos。");
        }

        /// <summary>
        /// 【不要】用 SaveCurrentModifiedScenesIfUserWantsTo()：它会弹模态框，从脚本/自动化里调用时
        /// 没人点，编辑器主线程永久堵死。这里静默存盘，存不了就中止并明确报错。
        /// </summary>
        static bool PrepareForNewScene()
        {
            if (DemoSceneSave.SaveOpenScenesWithoutPrompting()) return true;

            Debug.LogError("[PhysicsSimulation] 有未保存且没有文件路径的场景，无法静默保存，已中止。" +
                           "请先手动保存该场景，再生成布料演示场景。");
            return false;
        }

        static void EditorSceneManager_NewSceneEmpty()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,
                                                    NewSceneMode.Single);
        }

        // ------------------------------------------------------------------
        // 菜单：在当前场景里重建演示布料（不写场景文件）
        // ------------------------------------------------------------------
        [MenuItem("Tools/Physics Simulation/Cloth/Build In Current Scene", false, 111)]
        public static void BuildClothInCurrentScene()
        {
            var existing = UnityEngine.Object.FindAnyObjectByType<ClothBehaviour>();
            if (existing != null)
            {
                Undo.RecordObject(existing, "Rebuild Cloth Demo");
                Undo.DestroyObjectImmediate(existing.gameObject);
            }

            GameObject root = BuildCloth().gameObject;
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            if (root != null) EditorGUIUtility.PingObject(root);
        }

        // ------------------------------------------------------------------
        // 菜单：把当前布料系统的状态打全，方便一眼看出"为什么不动"
        // ------------------------------------------------------------------
        [MenuItem("Tools/Physics Simulation/Cloth/Dump State", false, 112)]
        public static void DumpClothState()
        {
            ClothBehaviour behaviour = UnityEngine.Object.FindAnyObjectByType<ClothBehaviour>();
            if (behaviour == null)
            {
                Debug.LogWarning("[PhysicsSimulation] 当前场景里没有 ClothBehaviour。" +
                                 "先跑 Tools ▸ Physics Simulation ▸ Cloth ▸ Build In Current Scene。");
                return;
            }

            var lines = new List<string>();
            lines.Add("=== PhysicsSimulation 布料状态 ===");
            lines.Add("物体: " + GetPath(behaviour.transform) + "  IsBuilt: " + behaviour.IsBuilt);
            if (!behaviour.IsBuilt)
            {
                lines.Add("构建失败原因: " + behaviour.LastBuildError);
                Debug.LogWarning(string.Join("\n", lines));
                return;
            }

            ClothSimulation system = behaviour.System;
            ClothParameters p = system.Parameters;
            lines.Add("网格: " + p.columns + " x " + p.rows + " 间距 " + p.spacing +
                      " ⇒ 质点 " + system.ParticleCount + "、约束 " + system.Constraints.Count);
            lines.Add("约束分解: 结构 " + CountType(system, ClothConstraintType.Structural) +
                      " / 剪切 " + CountType(system, ClothConstraintType.Shear) +
                      " / 弯曲 " + CountType(system, ClothConstraintType.Bend));
            lines.Add("硬度: 结构 " + p.structuralStiffness + " 剪切 " + p.shearStiffness +
                      " 弯曲 " + p.bendStiffness + "；substeps " + p.substeps + "、iterations " + p.iterations);
            lines.Add("钉住: " + CountPinned(system) + " 个（pinEdges = " + behaviour.pinEdges + "）");
            lines.Add("障碍物: " + system.ObstacleCount + " 个（列表 " + behaviour.obstacles.Count + " 项，" +
                      (behaviour.obtainObstaclesFromTransforms ? "自动换算局部空间" : "未启用") + "）");
            lines.Add("风: " + behaviour.windAcceleration + "  重力: " + p.gravity);
            lines.Add("最大拉伸比: " + system.MaxStretchRatio().ToString("F4") +
                      "（上限 " + p.maxStretchRatio + "）  非有限状态: " + system.HasNonFiniteState());

            var filter = behaviour.GetComponent<MeshFilter>();
            lines.Add("网格物体: " + (filter == null ? "无 MeshFilter" :
                      (filter.sharedMesh == null ? "MeshFilter 为空" : filter.sharedMesh.name +
                       "（顶点 " + filter.sharedMesh.vertexCount + "、三角 " + filter.sharedMesh.triangles.Length / 3 + "）")));

            Debug.Log(string.Join("\n", lines));
        }

        // ------------------------------------------------------------------
        // 可复用构建：给菜单用，也给测试用（测试里会把它标成 DontSave 再销毁）
        // ------------------------------------------------------------------
        public static ClothBehaviour BuildCloth()
        {
            var root = new GameObject("ClothDemo");
            Undo.RegisterCreatedObjectUndo(root, "Build Cloth Demo");
            root.transform.position = new Vector3(0f, 1.4f, 0f);

            var behaviour = root.AddComponent<ClothBehaviour>();
            behaviour.parameters = new ClothParameters
            {
                columns = Columns,
                rows = Rows,
                spacing = Spacing,
                mass = 0.6f,
                gravity = new Vector3(0f, -9.81f, 0f),
                damping = 0.35f,
                structuralStiffness = 1f,
                shearStiffness = 0.7f,
                bendStiffness = 0.25f,
                substeps = 4,
                iterations = 2,
                maxStretchRatio = 1.6f,
                collisionThickness = 0.015f
            };
            behaviour.pinEdges = ClothPinEdges.Top;
            behaviour.windAcceleration = new Vector3(0.35f, 0f, 1.1f);       // 只朝相机（+Z）吹：带向上分量会把整面旗掀到球上面去
            behaviour.obtainObstaclesFromTransforms = true;
            behaviour.drawGizmoWireframe = true;

            // 障碍物：一个纯碰撞球（不需要 Renderer 也能当障碍物；这里加个可见球方便看效果）
            var obstacle = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            obstacle.name = "ClothObstacle";
            Undo.RegisterCreatedObjectUndo(obstacle, "Add Cloth Obstacle");
            obstacle.transform.SetParent(root.transform, false);
            obstacle.transform.localPosition = ObstacleLocalCenter;
            obstacle.transform.localScale = Vector3.one * ObstacleRadius * 2f;

            var obstacleCollider = obstacle.GetComponent<SphereCollider>();
            if (obstacleCollider != null) obstacleCollider.radius = 0.5f;   // 缩放后的局部半径 ⇒ 世界半径 = ObstacleRadius

            behaviour.obstacles = new List<Transform> { obstacle.transform };

            // AddComponent 不会自动跑 Awake（编辑模式），所以这里显式构建一次
            behaviour.Rebuild();

            string materialPath;
            ApplyDemoMaterial(root, obstacle.GetComponent<Renderer>(), out materialPath);
            LastMaterialPath = materialPath;
            return behaviour;
        }

        /// <summary>
        /// 演示材质：按"一定能画对"的顺序取模板，最后才退回猜着色器名字。
        ///
        /// 踩过的坑：内置 primitive 的 MeshRenderer.sharedMaterial 其实是 **null**（灰色来自管线默认材质），
        /// 所以"抄旁边那个球"抄不到东西；而 new Material(Shader.Find("Universal Render Pipeline/Lit"))
        /// 在这种工程里会画成品红（缺 URP 需要的关键字/变体设置）。
        /// 正确做法是直接拿当前管线的 defaultMaterial 当模板 —— 它就是 Unity 给新物体用的那个材质。
        /// </summary>
        static void ApplyDemoMaterial(GameObject root, Renderer reference, out string usedPath)
        {
            usedPath = "无（保持无材质，走管线默认）";
            var renderer = root.GetComponent<MeshRenderer>();
            if (renderer == null) return;

            Material template = PipelineDefaultMaterial()
                             ?? (reference != null ? reference.sharedMaterial : null);

            if (template != null)
            {
                // 注意：这里【不能】标 HideFlags.DontSave。
                // DontSave 的材质不会随场景存盘，Play 模式一重载引用就变成 missing material ⇒ 整块布画成品红。
                // 保持默认 hideFlags，材质就作为"场景内嵌对象"写进 .unity 文件：既不往 Assets/ 里塞文件，又能活过重载。
                var material = new Material(template)
                {
                    name = "PhysicsSimulation Cloth Demo"
                };
                SetColor(material, new Color(0.92f, 0.34f, 0.30f, 1f));
                renderer.sharedMaterial = material;
                usedPath = "管线默认材质模板: " + template.name + " / shader: " + template.shader.name;
                return;
            }

            string[] candidates = { "Universal Render Pipeline/Lit", "HDRP/Lit", "Standard", "Diffuse" };
            Shader shader = null;
            foreach (string candidate in candidates)
            {
                shader = Shader.Find(candidate);
                if (shader != null) break;
            }
            if (shader == null)
            {
                Debug.LogWarning("[PhysicsSimulation] 既拿不到管线默认材质也没找到可用着色器，演示布料将不带材质。");
                return;
            }

            var fallback = new Material(shader)
            {
                name = "PhysicsSimulation Cloth Demo"
            };
            SetColor(fallback, new Color(0.92f, 0.34f, 0.30f, 1f));
            renderer.sharedMaterial = fallback;
            usedPath = "猜名字兜底: " + shader.name;
        }

        /// <summary>
        /// 用反射拿当前渲染管线的 defaultMaterial：包本身不依赖 URP/HDRP 程序集，
        /// 但在装了管线的工程里能拿到"画得对"的那个材质（内置管线时返回 null）。
        /// </summary>
        static Material PipelineDefaultMaterial()
        {
            var asset = GraphicsSettings.currentRenderPipeline;
            if (asset == null) return null;

            var property = asset.GetType().GetProperty("defaultMaterial",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (property == null) return null;

            try
            {
                return property.GetValue(asset, null) as Material;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PhysicsSimulation] 读取管线 defaultMaterial 失败：" + e.Message);
                return null;
            }
        }

        /// <summary>按各管线的属性名依次尝试设色：URP 用 _BaseColor，内置用 _Color，另外也设 material.color。</summary>
        static void SetColor(Material material, Color color)
        {
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            else if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            material.color = color;
        }

        static void SetupCameraAndLight(ClothBehaviour behaviour)
        {
            var cameraObject = new GameObject("Main Camera");
            cameraObject.tag = "MainCamera";
            Undo.RegisterCreatedObjectUndo(cameraObject, "Add Demo Camera");
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.Skybox;
            cameraObject.AddComponent<AudioListener>();

            // 布在局部 XY 平面、法线朝 +Z，所以相机必须站在 +Z 一侧，否则看到的是背面 + 侧刃
            Vector3 center = behaviour.transform.TransformPoint(new Vector3(
                (Columns - 1) * 0.5f * Spacing, -Rows * Spacing * 0.5f, 0f));
            cameraObject.transform.position = center + new Vector3(0.35f, 0.3f, 3.5f);   // 退一点，整面旗 + 球 + 地面都在画面里
            cameraObject.transform.LookAt(center);
            camera.fieldOfView = 44f;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 100f;

            Light light = UnityEngine.Object.FindAnyObjectByType<Light>();
            if (light == null)
            {
                var lightObject = new GameObject("Directional Light");
                Undo.RegisterCreatedObjectUndo(lightObject, "Add Demo Light");
                light = lightObject.AddComponent<Light>();
            }
            light.type = LightType.Directional;
            light.color = new Color(1f, 0.96f, 0.9f);
            light.intensity = 1.35f;
            light.shadows = LightShadows.Soft;
            light.transform.rotation = Quaternion.Euler(38f, -18f, 0f);

            // 地面：让"掉下去"这件事有参照物
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            Undo.RegisterCreatedObjectUndo(ground, "Add Demo Ground");
            ground.transform.position = new Vector3(0.8f, -0.4f, 0f);
            ground.transform.localScale = new Vector3(2.4f, 1f, 2.4f);
        }

        static int CountPinned(ClothSimulation cloth)
        {
            int count = 0;
            for (int i = 0; i < cloth.ParticleCount; i++) if (cloth.IsPinned(i)) count++;
            return count;
        }

        static int CountType(ClothSimulation cloth, ClothConstraintType type)
        {
            int count = 0;
            for (int i = 0; i < cloth.Constraints.Count; i++)
            {
                if (cloth.Constraints[i].type == type) count++;
            }
            return count;
        }

        static string GetPath(Transform t)
        {
            string path = t.name;
            while (t.parent != null)
            {
                t = t.parent;
                path = t.name + "/" + path;
            }
            return path;
        }
    }
}
