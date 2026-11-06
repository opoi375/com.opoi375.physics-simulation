// Copyright (c) 2026 PhysicsSimulation. MIT License.
//
// 互碰演示工具：Tools ▸ Physics Simulation ▸ Interaction ▸ *
// 造两个重叠的软体 + 一块自由落体的布，全部放进同一个 ParticleInteractionGroup，
// 让"体与体之间会互相挤开"这件事在 Play 里一眼可见。
//
// 刻意不与 SoftBodyDemoTools 共用构建代码：那个工具已发布，改它的摆位就是改别人的演示。

using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace PhysicsSimulation.EditorTools
{
    /// <summary>互碰演示场景与实体的构建入口。</summary>
    public static class ParticleInteractionDemoTools
    {
        /// <summary>演示场景落地路径。固定路径、显式保存，绝不弹模态框。</summary>
        public const string ScenePath = "Assets/Scenes/ParticleInteractionDemo.unity";

        /// <summary>互碰组的物体名（测试与 Dump 都按这个名字找）。</summary>
        public const string GroupName = "InteractionGroup";

        /// <summary>果冻边长（米）。比间距大 ⇒ 开局必然穿透，演示才有东西可演。</summary>
        public const float JellySize = 0.5f;

        /// <summary>
        /// 两个果冻的中心间距（米）。旧值 0.32 是“开局就深互插 0.18 m”，看着像“一定有东西可演”，
        /// 但实测那个深度会把两个体一次性弹开到 2 m 外（重叠量接近一个质点间距，补完就是冲量）。
        /// 0.56 是量出来的：面间隙 0.06 m，**小于接触判定距离 r+skin = 0.105 m**，所以互碰会真的
        /// 参与；但需要补的量只有 0.045 m，不会把演示变成“两个果冻互相弹飞”。
        /// </summary>
        public const float JellySpacing = 0.56f;

        /// <summary>每个果冻每面切成 3×3 ⇒ 间距 ≈ 边长/3，球云才能真正近似表面（8 个角点的网格会把体吹成气球）。</summary>
        public const int JellySubdivisions = 3;

        static readonly Vector3 JellyAOffset = new Vector3(-JellySpacing * 0.5f, 1.25f, 0f);
        static readonly Vector3 JellyBOffset = new Vector3(JellySpacing * 0.5f, 1.25f, 0f);
        static readonly Vector3 ClothOffset = new Vector3(0f, 2.05f, 0f);

        [MenuItem("Tools/Physics Simulation/Interaction/Create Interaction Demo Scene", false, 130)]
        public static void CreateInteractionDemoScene()
        {
            // 先保存已经打开且**有路径**的场景，再开新场景：
            // NewScene(Single) 遇到未保存场景会弹系统模态框，自动化路径里没人点，主线程就永久堵死。
            if (!DemoSceneSave.SaveOpenScenesWithoutPrompting())
            {
                Debug.LogError("[PhysicsSimulation] 有未保存且无路径的场景，先手动存一下再建演示。");
                return;
            }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var ground = SetupGround();
            var group = BuildBodies(ground);
            SetupCameraAndLight();

            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log("[PhysicsSimulation] 互碰演示场景已生成：" + ScenePath
                      + " | 组成员 " + group.MemberCount
                      + " | 两个果冻初始面间隙 " + (JellySpacing - JellySize).ToString("F2")
                      + " m（小于接触判定距离 ⇒ 会互推，但不至于一次弹飞）"
                      + " | 播放就能看到它们互相挤开、布压在它们上面");
        }

        [MenuItem("Tools/Physics Simulation/Interaction/Build In Current Scene", false, 131)]
        public static ParticleInteractionGroup BuildInCurrentScene()
        {
            var ground = SetupGround();
            var group = BuildBodies(ground);
            Debug.Log("[PhysicsSimulation] 已往当前场景加入互碰演示：组成员 " + group.MemberCount);
            return group;
        }

        [MenuItem("Tools/Physics Simulation/Interaction/Dump State", false, 132)]
        public static void DumpState()
        {
            // 把“到底分开了没有”写成可比较的数字：穿透深度（米）。
            // 靠截图目测判断互碰行不行，是上一轮水箱漏水查了三遍才查明的原因。
            var group = GameObject.Find(GroupName)?.GetComponent<ParticleInteractionGroup>();
            if (group == null)
            {
                Debug.LogError("[PhysicsSimulation] 找不到互碰组（" + GroupName + "），先构建演示场景");
                return;
            }
            var sb = new System.Text.StringBuilder("[PhysicsSimulation] 互碰状态 | 组成员 ").Append(group.MemberCount);
            for (int i = 0; i < group.Members.Count; i++)
            {
                var b = group.Members[i];
                sb.Append(" | ").Append(b is ClothBehaviour ? "布" : "软体")
                  .Append(i).Append(" 质点 ").Append(b.CloudParticleCount)
                  .Append(" 半径 ").Append(b.CloudRadius.ToString("F4"));
            }
            if (group.Members.Count >= 2)
            {
                var a0 = group.Members[0];
                var a1 = group.Members[1];
                float gap = float.MaxValue;
                var pa = a0.CloudWorldPositions();
                var pb = a1.CloudWorldPositions();
                bool finite = true;
                for (int i = 0; i < a0.CloudParticleCount; i++)
                {
                    if (!IsFinite(pa[i])) finite = false;
                    for (int j = 0; j < a1.CloudParticleCount; j++)
                    {
                        float d = (pa[i] - pb[j]).magnitude;
                        if (d < gap) gap = d;
                    }
                }
                if (!IsFinite(pb[0])) finite = false;
                float sum = a0.CloudRadius + a1.CloudRadius;
                sb.Append(" | 两个果冻最近球心距 ").Append(gap.ToString("F4"))
                  .Append(" m 半径和 ").Append(sum.ToString("F4"))
                  .Append(" m 穿透 ").Append((sum - gap).ToString("F4"))
                  .Append(" m | 状态 ").Append(finite ? "有限" : "出 NaN 了");
            }
            Debug.Log(sb.ToString());
        }

        static bool IsFinite(Vector3 v)
        {
            return !float.IsNaN(v.x) && !float.IsInfinity(v.x)
                && !float.IsNaN(v.y) && !float.IsInfinity(v.y)
                && !float.IsNaN(v.z) && !float.IsInfinity(v.z);
        }

        /// <summary>
        /// 造出「两个重叠果冻 + 一块自由落体的布 + 一个把它们串起来的互碰组」。
        /// <paramref name="groundCollider"/> 为 null 就不接地面（测试里常用）。
        /// 三个体一律走 <c>interactionGroup</c> 字段登记，**不碰任何已发布演示的参数**。
        /// </summary>
        public static ParticleInteractionGroup BuildBodies(Collider groundCollider)
        {
            var groupGo = new GameObject(GroupName);
            var group = groupGo.AddComponent<ParticleInteractionGroup>();

            var a = BuildJelly("JellyA", JellyAOffset, new Color(0.30f, 0.72f, 0.95f, 1f), groundCollider);
            var b = BuildJelly("JellyB", JellyBOffset, new Color(0.95f, 0.62f, 0.25f, 1f), groundCollider);
            var c = BuildScarf("Scarf", ClothOffset, new Color(0.55f, 0.85f, 0.45f, 1f), groundCollider);

            group.Add(a);
            group.Add(b);
            group.Add(c);
            return group;
        }

        static SoftBodyBehaviour BuildJelly(string name, Vector3 localOffset, Color color, Collider ground)
        {
            var root = new GameObject(name);
            root.transform.position = localOffset;

            var filter = root.AddComponent<MeshFilter>();
            var renderer = root.AddComponent<MeshRenderer>();
            var behaviour = root.AddComponent<SoftBodyBehaviour>();

            behaviour.sourceMesh = SoftBodyDemoTools.BuildBoxMesh(JellySize, JellySize, JellySize, JellySubdivisions);
            behaviour.pinMode = SoftBodyPinMode.None;          // 整块自由，才看得见互相挤开
            behaviour.parameters.mass = 0.9f;

            // “像果冻一样撑得住”靠这几个旋钮，不是靠互碰。实测（同一个 0.6 m 方块落地板、420 步）：
            // volumeStiffness 4000 → 40000 时，体质心从 0.02 m 抬到 0.16 m，不再摊成一张饼。
            // 弹簧/弯曲刚度是烤进 Spring 里的（Spring.stiffness 是 readonly），所以必须在
            // 下面那次 Rebuild() **之前**设好；体积刚度与阻尼是每步实时读的，放哪儿都行。
            // 刚度上去了 substeps 必须跟着上（显式积分的稳定性几乎全押在这里），否则直接 NaN。
            behaviour.parameters.volumeStiffness = 20000f;
            behaviour.parameters.volumeDamping = 20f;
            behaviour.parameters.springStiffness = 4000f;
            behaviour.parameters.bendStiffness = 600f;
            behaviour.parameters.damping = 1.5f;
            behaviour.parameters.substeps = 8;
            behaviour.parameters.maxSpeed = 20f;

            if (ground != null)
            {
                behaviour.collideWithSceneColliders = true;
                behaviour.sceneColliders = new System.Collections.Generic.List<Collider> { ground };
            }

            behaviour.Rebuild();
            DemoMaterialHelper.Apply(renderer, color, null, out _);
            return behaviour;
        }

        static ClothBehaviour BuildScarf(string name, Vector3 localOffset, Color color, Collider ground)
        {
            var root = new GameObject(name);
            root.transform.position = localOffset;

            var filter = root.AddComponent<MeshFilter>();
            var renderer = root.AddComponent<MeshRenderer>();
            var behaviour = root.AddComponent<ClothBehaviour>();

            behaviour.parameters.columns = 10;
            behaviour.parameters.rows = 6;
            behaviour.parameters.spacing = 0.12f;
            behaviour.pinEdges = ClothPinEdges.None;           // 不钉：让它落下来压在两个果冻上
            behaviour.generateMesh = true;

            if (ground != null)
            {
                behaviour.collideWithSceneColliders = true;
                behaviour.sceneColliders = new System.Collections.Generic.List<Collider> { ground };
            }

            behaviour.Rebuild();
            DemoMaterialHelper.Apply(renderer, color, null, out _);
            return behaviour;
        }

        // ------------------------------------------------------------------ 场景配件

        static Collider SetupGround()
        {
            var ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ground.name = "Ground";
            ground.transform.position = new Vector3(0f, -0.5f, 0f);
            ground.transform.localScale = new Vector3(12f, 1f, 12f);
            return ground.GetComponent<Collider>();
        }

        static void SetupCameraAndLight()
        {
            // 构图要容得下“分开之后”的位置：两个体会向左右弹开，布还会叠在上边，
            // 所以取景中点比初始几何中心低一点、距离留余量，不拍成两个小点。
            var cameraGo = new GameObject("Main Camera");
            cameraGo.tag = "MainCamera";
            var camera = cameraGo.AddComponent<Camera>();
            // 两个果冻现在落在 y≈0.3，布垂到 y≈1，取景中心抬到 0.85，否则体贴在画面顶边上。
            camera.transform.position = new Vector3(0.05f, 1.15f, -2.35f);
            camera.transform.LookAt(new Vector3(0.05f, 0.85f, 0f));
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.10f, 0.11f, 0.14f, 1f);

            var lightGo = new GameObject("Directional Light");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.transform.rotation = Quaternion.Euler(52f, -30f, 0f);
            light.intensity = 1.1f;
        }
    }
}
