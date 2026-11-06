// Copyright (c) 2026 PhysicsSimulation. MIT License.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace PhysicsSimulation
{
    /// <summary>软体的钉住方式。</summary>
    public enum SoftBodyPinMode
    {
        /// <summary>不钉，整块自由下落。</summary>
        None = 0,
        /// <summary>钉住最高的那一批质点（顶面）—— 挂着的皮袋、吊着的石头。</summary>
        TopVertices = 1,
        /// <summary>钉住最低的那一批质点（底面）—— 踩在地上不变形的底座。</summary>
        BottomVertices = 2,
        /// <summary>按 <see cref="SoftBodyBehaviour.pinVertexIndices"/> 里的网格顶点下标精确钉住。</summary>
        ExplicitIndices = 3
    }

    /// <summary>
    /// 把**任意网格**变成软体的挂载组件：拖一份 Mesh 进 <see cref="sourceMesh"/> 就能跑。
    ///
    /// 约定（和 ClothBehaviour 一致）：
    ///  * 模拟发生在组件自身的**局部空间** —— Transform 的移动/旋转/缩放只是摆位，不参与力学，
    ///    所以可以一边用动画搬物体一边让软体自己晃。
    ///  * 绝不改用户的源网格资源：写回的是自己那份实例网格，拓扑沿用源网格的三角形索引。
    ///  * 构建失败不抛异常打断游戏，原因写进 <see cref="LastBuildError"/>。
    /// </summary>
    [RequireComponent(typeof(MeshFilter))]
    [AddComponentMenu("Physics Simulation/Soft Body")]
    public sealed class SoftBodyBehaviour : MonoBehaviour, IParticleBody
    {
        [Tooltip("源网格：顶点会被焊接成质点，三角形边变成弹簧")]
        public Mesh sourceMesh;

        [Tooltip("软体参数（质量、重力、弹簧/弯曲/体积刚度、子步等）")]
        public SoftBodyParameters parameters = new SoftBodyParameters();

        [Tooltip("钉住方式")]
        public SoftBodyPinMode pinMode = SoftBodyPinMode.None;

        [Tooltip("pinMode = ExplicitIndices 时，按源网格顶点下标钉住")]
        public List<int> pinVertexIndices = new List<int>();

        [Tooltip("Top/BottomVertices 判定“这一层”的厚度容差（米）")]
        public float pinLayerThickness = 1e-3f;

        [Tooltip("让场景里的 Collider 参与碰撞（本版只解析球/盒/胶囊；MeshCollider 会被跳过）。" +
                 "默认关：开上来就改变现有场景的行为，比漏一个碰撞更难查")]
        public bool collideWithSceneColliders = false;

        [Tooltip("collideWithSceneColliders 为真时参与碰撞的 Collider 列表")]
        public List<Collider> sceneColliders = new List<Collider>();

        [Tooltip("每步重读一次 Collider 列表（会分配内存）；碰撞体会动、会开关时再开")]
        public bool updateCollidersEveryStep = false;

        [Tooltip("关掉后由外部调 Step(dt)，用于定步长、回放或网络同步")]
        public bool autoSimulate = true;

        [Tooltip("关掉则只跑物理、不生成实例网格")]
        public bool generateMesh = true;

        [Tooltip("每帧重算法线（关掉能省一点时间，但光照会跟着旧法线走）")]
        public bool recalculateNormals = true;

        [Tooltip("Scene 视图里画弹簧线框")]
        public bool drawGizmoWireframe = true;

        [Tooltip("每次 Rebuild 时给所有未钉住的质点施加的初速度（局部空间，米/秒）。"
                 + "演示用的“推一把”必须走这里：只改运行时质点位置的话，"
                 + "Play 时 Awake 会拿源网格重建模拟，扰动当场丢失，画面就成了静态。")]
        public Vector3 initialVelocity = Vector3.zero;

        [Tooltip("加入这个互碰组后，同组的体之间会互相挤开（把质点当成一朵球云的近似）。\n"
                 + "留空 = 完全不参与互碰，行为与之前版本逐位一致。默认关：不该因为升级就改变现有场景。")]
        public ParticleInteractionGroup interactionGroup;

        SoftBodySimulation _simulation;
        bool _collidersSynced;        // Collider 列表负一次同步（重建时重置，免得拿着旧列表不放）
        Mesh _mesh;
        Vector3[] _scratch;

        /// <summary>底层模拟（可能为 null，配合 IsBuilt 用）。</summary>
        public SoftBodySimulation Simulation { get { return _simulation; } }

        public bool IsBuilt { get { return _simulation != null && _simulation.IsBuilt; } }

        /// <summary>最近一次 Rebuild 的失败原因；成功时为 null。</summary>
        public string LastBuildError { get; private set; }

        /// <summary>本组件自己那份可写实例网格（generateMesh 为假时是 null）。</summary>
        public Mesh Mesh { get { return _mesh; } }

        /// <summary>被钉住的质点数量。</summary>
        public int PinnedParticleCount { get; private set; }

        void Awake()
        {
            if (!IsBuilt) Rebuild();
        }

        void FixedUpdate()
        {
            if (autoSimulate && IsBuilt) Step(Time.fixedDeltaTime);
        }

        void OnDestroy()
        {
            if (interactionGroup != null) interactionGroup.Remove(this);
            if (_mesh != null)
            {
                RuntimeAssets.Dispose(_mesh);
                _mesh = null;
            }
        }

        /// <summary>用当前参数与源网格重建模拟（Inspector 改完点这个）。</summary>
        public void Rebuild()
        {
            LastBuildError = null;
            _simulation = null;
            PinnedParticleCount = 0;

            if (sourceMesh == null)
            {
                LastBuildError = "sourceMesh 为空：软体需要一份源网格";
                return;
            }
            int[] sourceTriangles = sourceMesh.triangles;
            if (sourceTriangles == null || sourceTriangles.Length == 0)
            {
                LastBuildError = "sourceMesh 没有三角形，无法建立弹簧拓扑";
                return;
            }

            try
            {
                var data = SoftBodyMeshData.FromMesh(sourceMesh);
                var simulation = new SoftBodySimulation(parameters);
                simulation.Build(data);

                _simulation = simulation;
                _collidersSynced = false;
                ApplyPinning();
                ApplyInitialVelocity();
                SyncColliders();
                EnsureInstanceMesh();
                WriteBack();

                // Rebuild 换掉了整个碰撞集合，手工挂的互碰代理会跟着没。
                // 流体那边的容器代理已经踩过一次（编辑器里看着正常，一进 Play 代理数变 0）。
                if (interactionGroup != null) interactionGroup.Reattach(this);
            }
            catch (Exception e)
            {
                _simulation = null;
                LastBuildError = e.Message;
                Debug.LogWarning("[PhysicsSimulation] 软体构建失败：" + e.Message, this);
            }
        }

        void ApplyPinning()
        {
            int pinned = 0;
            switch (pinMode)
            {
                case SoftBodyPinMode.TopVertices:
                case SoftBodyPinMode.BottomVertices:
                {
                    float extreme = pinMode == SoftBodyPinMode.TopVertices ? float.MinValue : float.MaxValue;
                    for (int i = 0; i < _simulation.ParticleCount; i++)
                    {
                        float y = _simulation.GetPosition(i).y;
                        if (pinMode == SoftBodyPinMode.TopVertices) { if (y > extreme) extreme = y; }
                        else { if (y < extreme) extreme = y; }
                    }
                    float tolerance = Mathf.Max(0f, pinLayerThickness);
                    for (int i = 0; i < _simulation.ParticleCount; i++)
                    {
                        float y = _simulation.GetPosition(i).y;
                        bool onLayer = pinMode == SoftBodyPinMode.TopVertices
                            ? y >= extreme - tolerance
                            : y <= extreme + tolerance;
                        if (!onLayer) continue;
                        _simulation.SetPinned(i, true);
                        pinned++;
                    }
                    break;
                }
                case SoftBodyPinMode.ExplicitIndices:
                {
                    for (int i = 0; i < pinVertexIndices.Count; i++)
                    {
                        int vertexIndex = pinVertexIndices[i];
                        if (vertexIndex < 0 || vertexIndex >= sourceMesh.vertexCount)
                        {
                            Debug.LogWarning("[PhysicsSimulation] 钉住顶点下标 " + vertexIndex + " 越界，已跳过", this);
                            continue;
                        }
                        int particle = _simulation.IndexOfVertex(vertexIndex);
                        if (_simulation.IsPinned(particle)) continue;
                        _simulation.SetPinned(particle, true);
                        pinned++;
                    }
                    break;
                }
            }
            PinnedParticleCount = pinned;
        }

        void ApplyInitialVelocity()
        {
            if (initialVelocity.sqrMagnitude < 1e-12f) return;
            for (int i = 0; i < _simulation.ParticleCount; i++)
                if (!_simulation.IsPinned(i)) _simulation.SetVelocity(i, initialVelocity);
        }

        void EnsureInstanceMesh()
        {
            if (!generateMesh)
            {
                _mesh = null;
                _scratch = null;
                return;
            }

            var filter = GetComponent<MeshFilter>();
            if (filter == null) filter = gameObject.AddComponent<MeshFilter>();

            if (_mesh == null)
            {
                _mesh = new Mesh { name = "SoftBodyMesh (" + gameObject.name + ")" };
#if UNITY_2017_4_OR_NEWER
                _mesh.MarkDynamic();
#endif
            }

            // 拓扑照抄源网格，顶点数组按源网格长度开 —— 写回时用焊接映射展开
            _mesh.Clear();
            _mesh.subMeshCount = 1;
            // 顺序不能反：先给顶点再给三角形。反过来 Unity 会直接拒绝这批索引
            // （"Failed setting triangles... VertexCount: 0"），于是网格没有面、法线全零、画面啥也看不见。
            _mesh.vertices = sourceMesh.vertices;
            _mesh.triangles = sourceMesh.triangles;
            if (sourceMesh.uv != null && sourceMesh.uv.Length == sourceMesh.vertexCount) _mesh.uv = sourceMesh.uv;
            if (sourceMesh.colors != null && sourceMesh.colors.Length == sourceMesh.vertexCount) _mesh.colors = sourceMesh.colors;

            _scratch = new Vector3[sourceMesh.vertexCount];
            filter.sharedMesh = _mesh;
        }

        void WriteBack()
        {
            if (!IsBuilt || _mesh == null || _scratch == null) return;

            _simulation.WritePositionsTo(_scratch);
            _mesh.vertices = _scratch;
            if (recalculateNormals) _mesh.RecalculateNormals();
            _mesh.RecalculateBounds();
        }

        /// <summary>推进一个时间步并写回网格；没建好时静默返回。</summary>
        public void Step(float deltaTime)
        {
            if (!IsBuilt) return;
            SyncColliders();
            _simulation.Step(deltaTime);
            _cloudVersion++;                        // 质点动了：让别人的球云索引知道该重建
            WriteBack();
        }

        // ------------------------------------------------------------------
        // 互碰（IParticleBody）：把本体的质点当成一朵**世界空间**的球云，供同组的其他体碰撞。
        //
        // 版本号是这套接线的命门。三个 Behaviour 的步进时机本来就不一致（软体与质点弹簧走
        // FixedUpdate、布料走 Update），而 EditMode 测试里 Time.frameCount 根本不动 ——
        // 用帧号当刷新守卫会把第二次 Step 悄悄变成空操作，测出来一片假绿。
        // ------------------------------------------------------------------
        const float CloudRadiusFallback = 0.05f;

        // 上界系数：球半径不得超过静息尺度（cbrt(体积)）的这个比例，防止稀网格把体吹成气球。
        const float CloudRadiusExtentFactor = 0.25f;
        int _cloudVersion = 1;
        Matrix4x4 _cloudMatrix = Matrix4x4.zero;
        Vector3[] _cloudWorld = new Vector3[0];

        /// <summary>
        /// 互碰球半径 = 平均质点间距 × 这个系数。<see cref="cloudRadiusFactor"/> 是它的 Inspector 入口。
        /// 实测标定（240 步零载荷，两个 0.6 m 软体，质点间距不同）：半径 0.114→漂移 2.94 m，
        /// 0.085→1.96 m，0.068→1.55 m，0.057→1.36 m——**漂移跟着半径走，半径减半漂移几乎减半**。
        /// 因为半径决定"两个体的表面至少隔多远才算接触"，硬摆得更近就必须把壳掰凹那么多，
        /// 凹进去的量存成弹簧能，回弹把整个体甩走。这是球云接触的固有代价，不是算法缺陷
        /// （质点弹簧的单个质点 peakOut = 0.000 已经排除了"修正量转成速度"这条路径）。
        /// 所以：0.5 = 半个间距 = "相邻两球刚好相切"，是两体表面**真的碰上**才算接触的那个点；
        /// 调大 → 隔着距离就互推、并且弹得更狠；调小 → 缝隙会漏（质点之间露出洞）。
        /// 想要两个体安静地堆在一起，唯一有效的办法是**加密网格**（间距变小，半径跟着变小）。
        /// </summary>
        [Tooltip("互碰球半径 = 平均质点间距 × 此系数。0.5 = 两体表面真碰上才算接触。调大=隔空互推+弹得更狠，调小=表面漏缝。想安静堆叠请加密网格。")]
        public float cloudRadiusFactor = 0.5f;

        /// <summary>参与互碰的质点数（含钉住的：钉住的质点该推开别人，只是自己不动）。</summary>
        public int CloudParticleCount { get { return _simulation == null ? 0 : _simulation.ParticleCount; } }

        /// <summary>
        /// 球半径 = min（平均结构弹簧静止长度 × <see cref="cloudRadiusFactor"/>, 静息尺度 × 0.25）。
        /// 取间距的一半是“封得住粒子缝隙”的下限；而**上界同样重要**：
        /// 网格太稀时间距会大于体本身，球就变成比自己还大的气球，两团气球互相卡死——
        /// 从任何一个球里退出来都会立刻掉进另一个球，表现为“穿透深度一帧都不降”。
        /// 上界用静息体积的立方根（不是当前包围盒），后者会形成反馈环：一分开半径就缩，一缩就漏。
        /// 网格稀到两个约束冲突时，宁可漏也不吹气球：漏了还能调细分，胖了整个互碰就锁死了。
        /// </summary>
        public float CloudRadius
        {
            get
            {
                if (_simulation == null) return 0f;
                float spacing = _simulation.AverageStructuralRestLength;
                if (spacing <= 0f) return CloudRadiusFallback;
                float restScale = Mathf.Max(0f, _simulation.RestVolume());
                float extentCap = restScale > 0f ? Mathf.Pow(restScale, 1f / 3f) * CloudRadiusExtentFactor : float.MaxValue;
                float factor = cloudRadiusFactor > 0f ? cloudRadiusFactor : 0f;
                float r = Mathf.Min(spacing * factor, extentCap);
                return r > 0f ? r : CloudRadiusFallback;
            }
        }

        /// <summary>质点或变换每变一次自增。读取幂等，但会顺手自检变换矩阵（拖走组件而没步进也算变了）。</summary>
        public int CloudVersion
        {
            get
            {
                Matrix4x4 now = transform.localToWorldMatrix;
                if (now != _cloudMatrix) { _cloudMatrix = now; _cloudVersion++; }
                return _cloudVersion;
            }
        }

        /// <summary>当前世界坐标下的质点位置（前 <see cref="CloudParticleCount"/> 个有效）。缓冲区会被复用，调用方必须自己拷。</summary>
        public Vector3[] CloudWorldPositions()
        {
            int count = _simulation == null ? 0 : _simulation.ParticleCount;
            if (_cloudWorld.Length < count) _cloudWorld = new Vector3[Mathf.Max(count, 16)];
            Matrix4x4 toWorld = transform.localToWorldMatrix;
            _cloudMatrix = toWorld;
            for (int i = 0; i < count; i++) _cloudWorld[i] = toWorld.MultiplyPoint(_simulation.GetPosition(i));
            return _cloudWorld;
        }

        /// <summary>本体的碰撞集合，互碰组会往里挂别人的球云。</summary>
        public CollisionSet ParticleCollisions { get { return _simulation == null ? null : _simulation.Collisions; } }

        /// <summary>Inspector 上那个字段与 <c>group.Add(this)</c> 的回写共用同一个入口。</summary>
        ParticleInteractionGroup IParticleBody.CloudGroup
        {
            get { return interactionGroup; }
            set { interactionGroup = value; }
        }

        /// <summary>
        /// 把组件的变换与 Collider 列表同步给求解器。
        /// 既没开 Collider 桥接、又不在互碰组里时一律不注入变换矩阵——保证默认路径与 v1.2.0 逐位一致。
        /// 但**互碰组也必须注入**：球云存的是世界坐标、登记的也是 World 空间，
        /// 这里偷懒留单位矩阵就会把局部坐标当成世界坐标用，互碰当场错位。
        /// </summary>
        void SyncColliders()
        {
            if (_simulation == null) return;

            // 惰性入组：Inspector 里拖了组但没人调 Add 是最常见的用法，
            // 放在这里而不是 OnEnable，是因为 EditMode 测试里 OnEnable 不一定按预期时机跑。
            if (interactionGroup != null && !interactionGroup.Contains(this)) interactionGroup.Add(this);

            if (!collideWithSceneColliders && interactionGroup == null)
            {
                _simulation.SetSimulationToWorld(Matrix4x4.identity);
                return;
            }
            _simulation.SetSimulationToWorld(transform.localToWorldMatrix);
            if (updateCollidersEveryStep || !_collidersSynced) ColliderProxies.RefreshInto(sceneColliders, _simulation.Collisions);
            _collidersSynced = true;
        }

        /// <summary>回到构建时的初始布局。</summary>
        public void ResetToInitialLayout()
        {
            if (!IsBuilt) return;
            _simulation.ResetToInitial();
            WriteBack();
        }

        /// <summary>把当前姿态设为"初始布局"（摆好姿势后再复位就回到这里）。</summary>
        public void CaptureCurrentAsInitial()
        {
            if (!IsBuilt) return;
            _simulation.CaptureInitialLayout();
        }

        /// <summary>收集弹簧线段（局部空间），Gizmos / 调试用。</summary>
        public void CollectEdges(List<ValueTuple<Vector3, Vector3>> into, bool includeBend)
        {
            if (!IsBuilt) return;
            _simulation.CollectEdges(into, includeBend);
        }

        void OnDrawGizmos()
        {
            if (!drawGizmoWireframe || !IsBuilt) return;

            var segments = new List<ValueTuple<Vector3, Vector3>>();
            CollectEdges(segments, false);

            Gizmos.color = new Color(0.35f, 0.9f, 1f, 0.55f);
            for (int i = 0; i < segments.Count; i++)
                Gizmos.DrawLine(transform.TransformPoint(segments[i].Item1), transform.TransformPoint(segments[i].Item2));

            Gizmos.color = new Color(1f, 0.85f, 0.3f, 0.9f);
            for (int i = 0; i < _simulation.ParticleCount; i++)
            {
                float size = _simulation.IsPinned(i) ? 0.06f : 0.03f;
                Gizmos.DrawSphere(transform.TransformPoint(_simulation.GetPosition(i)), size);
            }
        }
    }
}
