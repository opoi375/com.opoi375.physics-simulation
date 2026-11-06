// Copyright (c) 2026 PhysicsSimulation. MIT License.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace PhysicsSimulation
{
    /// <summary>布料哪几条边被钉住（可以组合）。</summary>
    [Flags]
    public enum ClothPinEdges
    {
        None = 0,
        Top = 1,
        Bottom = 2,
        Left = 4,
        Right = 8
    }

    /// <summary>
    /// 布料组件：把 <see cref="ClothSimulation"/> 挂到 GameObject 上，自动生成分区网格、每帧推进、更新顶点。
    ///
    /// 坐标约定：模拟在**本组件所在 Transform 的局部空间**里跑，网格铺在局部 XY 平面上
    /// （列 → +X，行 → -Y，法线朝 +Z）。移动/旋转这个 GameObject 就能整块布摆位。
    /// 障碍物 Transform 会被换算进同一个局部空间，所以父级缩放/旋转都不会让碰撞穿帮。
    ///
    /// 生命周期：<see cref="Awake"/> 自动 <see cref="Rebuild"/>；参数改完请手动调 <see cref="Rebuild"/>
    /// （不在 OnValidate 里重建，避免编辑器里误删用户手捏的钉住状态）。
    /// </summary>
    [AddComponentMenu("Physics Simulation/Cloth Simulation")]
    [DisallowMultipleComponent]
    public sealed class ClothBehaviour : MonoBehaviour, IParticleBody
    {
        [Tooltip("布料模拟参数（网格尺寸、重力、三类约束硬度、子步与迭代）")]
        public ClothParameters parameters = new ClothParameters();

        [Tooltip("钉住哪几条边（Top = 挂旗子，Top|Right = 风吹旗面，None = 自由落体）")]
        public ClothPinEdges pinEdges = ClothPinEdges.Top;

        [Tooltip("关闭后只按手动 Step() 推进，便于回放与测试")]
        public bool autoSimulate = true;

        [Tooltip("持续风加速度（米/秒²），每个时间步施加一次冲量")]
        public Vector3 windAcceleration;

        [Tooltip("自动把下列 Transform 变成球形障碍物（每步换算到局部空间）")]
        public bool obtainObstaclesFromTransforms = true;

        [Tooltip("障碍物列表：有 SphereCollider 用它的半径，没有就用下面的回退半径")]
        public List<Transform> obstacles = new List<Transform>();

        [Tooltip("障碍物没有 SphereCollider 时的回退半径（米）")]
        public float obstacleRadiusFallback = 0.25f;

        [Tooltip("让场景里的 Collider 参与碰撞（本版只解析球/盒/胶囊；MeshCollider 会被跳过）。默认关：旧场景的行为不该因为升级就变")]
        public bool collideWithSceneColliders = false;

        [Tooltip("collideWithSceneColliders 为真时参与碰撞的 Collider 列表（世界空间，不需要折算）")]
        public List<Collider> sceneColliders = new List<Collider>();

        [Tooltip("每步重读一次 Collider 列表（会分配内存）；碰撞体会动、会开关时再开")]
        public bool updateCollidersEveryStep = false;

        [Tooltip("自动生成/更新网格（关掉就只跑模拟，自己拿质点数据画东西）")]
        public bool generateMesh = true;

        [Tooltip("每帧重算法线。关掉更省，但布会显得“平”；快速摆动的布建议开着")]
        public bool recalculateNormals = true;

        [Tooltip("在 Editor 里选中本物体时画出结构边线框（只影响 Scene View）")]
        public bool drawGizmoWireframe = true;

        [Tooltip("加入这个互碰组后，同组的体（软体 / 布料 / 质点弹簧，类型不限）之间会互相挤开。\n"
                 + "留空 = 完全不参与互碰，行为与之前版本逐位一致。")]
        public ParticleInteractionGroup interactionGroup;

        ClothSimulation _system;
        bool _collidersSynced;        // Collider 列表同步一次标记，重建时重置
        MeshFilter _filter;
        Mesh _mesh;
        Vector3[] _vertices;
        int[] _triangles;
        Vector2[] _uvs;
        bool _ownsMesh;

        public ClothSimulation System { get { return _system; } }
        public bool IsBuilt { get { return _system != null; } }
        public string LastBuildError { get; private set; }
        public Mesh Mesh { get { return _mesh; } }

        void Awake()
        {
            if (!IsBuilt) Rebuild();
        }

        void Update()
        {
            // 子步在 ClothSimulation 内部，所以这里跟 Update 走就够了；dt 过大由 maxDeltaTime 兜住
            if (autoSimulate && IsBuilt) Step(Time.deltaTime);
        }

        void OnDestroy()
        {
            if (interactionGroup != null) interactionGroup.Remove(this);
            if (_ownsMesh && _mesh != null)
            {
                RuntimeAssets.Dispose(_mesh);
                _mesh = null;
            }
        }

        /// <summary>按当前 parameters / pinEdges 重建系统与网格。非法参数只记录错误，不抛异常打断播放。</summary>
        [ContextMenu("Rebuild")]
        public void Rebuild()
        {
            LastBuildError = null;

            try
            {
                _system = new ClothSimulation(parameters);
                _collidersSynced = false;
                ApplyPins(_system, pinEdges);
            }
            catch (Exception e)
            {
                _system = null;
                LastBuildError = e.Message;
                Debug.LogWarning("[ClothBehaviour] " + name + " 配置无效，未构建系统：" + e.Message, this);
                return;
            }

            if (generateMesh) BuildMesh();

            // Rebuild 会 new 一个 ClothSimulation，连带换掉整个碰撞集合：
            // 互碰代理必须重新挂回去，否则就是“编辑器里看着接上了，跑起来一点不碰”。
            if (interactionGroup != null) interactionGroup.Reattach(this);
        }

        /// <summary>手动推进一个时间步（不管 autoSimulate 是什么）。<paramref name="dt"/> 由内部钳制与分子步。</summary>
        public void Step(float dt)
        {
            if (!IsBuilt) return;

            RefreshObstacles();
            SyncSceneColliders();

            if (windAcceleration != Vector3.zero) _system.AddWindImpulse(windAcceleration, dt);

            _system.Step(dt);                       // 非法 dt 由系统报错，不吞异常（吞了就只能靠猜了）
            _cloudVersion++;                        // 质点动了：让别人的球云索引知道该重建
            WritePositions();
        }

        // ------------------------------------------------------------------
        // 互碰（IParticleBody）：布料也是一朵质点球云，与软体走同一套代理，没有任何类型分支。
        // 版本号而不是帧号当刷新守卫：布料走 Update、软体走 FixedUpdate，而 EditMode 测试里
        // Time.frameCount 不动，用帧号会把第二次 Step 变成空操作。
        // ------------------------------------------------------------------
        /// <summary>
        /// 互碰球半径 = 网格间距 × 这个系数，与软体同一个口径、同一个默认值。
        /// 实测标定（240 步零载荷，两个 0.6 m 软体，质点间距不同）：半径 0.114→漂移 2.94 m，
        /// 0.085→1.96 m，0.068→1.55 m，0.057→1.36 m——**漂移跟着半径走，半径减半漂移几乎减半**。
        /// 因为半径决定"两个体的表面至少隔多远才算接触"，硬摆得更近就必须把壳掰凹那么多，
        /// 凹进去的量存成弹簧能，回弹把整个体甩走。这是球云接触的固有代价，不是算法缺陷
        /// （质点弹簧的单个质点 peakOut = 0.000 已经排除了"修正量转成速度"这条路径）。
        /// 所以：0.5 = 半个间距 = "相邻两球刚好相切"，是两体表面**真的碰上**才算接触的那个点；
        /// 调大 → 隔着距离就互推、并且弹得更狠；调小 → 缝隙会漏（质点之间露出洞）。
        /// 想要两个体安静地堆在一起，唯一有效的办法是**加密网格**（间距变小，半径跟着变小）。
        /// </summary>
        [Tooltip("互碰球半径 = 网格间距 × 此系数。0.5 = 两体表面真碰上才算接触。与软体同口径。")]
        public float cloudRadiusFactor = 0.5f;

        int _cloudVersion = 1;
        Matrix4x4 _cloudMatrix = Matrix4x4.zero;
        Vector3[] _cloudWorld = new Vector3[0];

        /// <summary>参与互碰的质点数（含钉住的）。</summary>
        public int CloudParticleCount { get { return _system == null ? 0 : _system.ParticleCount; } }

        /// <summary>球半径 = 网格间距 × <see cref="cloudRadiusFactor"/>，下限 0（半径 0 表示几乎不互碰，仍然良定义）。</summary>
        public float CloudRadius
        {
            get
            {
                float spacing = parameters == null ? 0f : parameters.spacing;
                if (spacing <= 0f) return 0.05f;
                float factor = cloudRadiusFactor > 0f ? cloudRadiusFactor : 0f;
                return spacing * factor;
            }
        }

        /// <summary>质点或变换每变一次自增；读取幂等，但会顺手自检变换矩阵。</summary>
        public int CloudVersion
        {
            get
            {
                Matrix4x4 now = transform.localToWorldMatrix;
                if (now != _cloudMatrix) { _cloudMatrix = now; _cloudVersion++; }
                return _cloudVersion;
            }
        }

        /// <summary>当前世界坐标下的质点位置（前 <see cref="CloudParticleCount"/> 个有效）。</summary>
        public Vector3[] CloudWorldPositions()
        {
            int count = _system == null ? 0 : _system.ParticleCount;
            if (_cloudWorld.Length < count) _cloudWorld = new Vector3[Mathf.Max(count, 16)];
            Matrix4x4 toWorld = transform.localToWorldMatrix;
            _cloudMatrix = toWorld;
            for (int i = 0; i < count; i++) _cloudWorld[i] = toWorld.MultiplyPoint(_system.GetPosition(i));
            return _cloudWorld;
        }

        /// <summary>本体的碰撞集合，互碰组会往里挂别人的球云。</summary>
        public CollisionSet ParticleCollisions { get { return _system == null ? null : _system.Collisions; } }

        /// <summary>Inspector 上那个字段与 <c>group.Add(this)</c> 的回写共用同一个入口。</summary>
        ParticleInteractionGroup IParticleBody.CloudGroup
        {
            get { return interactionGroup; }
            set { interactionGroup = value; }
        }

        /// <summary>
        /// 把组件变换与 Collider 列表同步给求解器。
        /// 既没开 Collider 桥接、又不在互碰组里时注入单位矩阵，于是逐位等价于 v1.1.0/v1.2.0 的布料。
        /// 但**互碰组也必须注入**：球云存的是世界坐标、登记的也是 World 空间，
        /// 这里偷懒留单位矩阵就会把布料的局部坐标当成世界坐标用，互碰当场错位。
        /// 旧的 <c>obstacles</c>（模拟空间球）与这批世界空间代理可以同时生效，互不干扰。
        /// </summary>
        void SyncSceneColliders()
        {
            if (_system == null) return;

            // 惰性入组：Inspector 里拖了组但没人调 Add 是最常见的用法
            if (interactionGroup != null && !interactionGroup.Contains(this)) interactionGroup.Add(this);

            if (!collideWithSceneColliders && interactionGroup == null)
            {
                _system.SetSimulationToWorld(Matrix4x4.identity);
                return;
            }
            _system.SetSimulationToWorld(transform.localToWorldMatrix);
            if (updateCollidersEveryStep || !_collidersSynced)
            {
                ColliderProxies.RefreshInto(sceneColliders, _system.Collisions);
                _collidersSynced = true;
            }
        }

        /// <summary>回到初始网格布局、速度清零（钉住状态保留）。</summary>
        [ContextMenu("Reset To Initial Layout")]
        public void ResetToInitialLayout()
        {
            if (!IsBuilt) return;
            _system.ResetToInitial();
            WritePositions();
        }

        /// <summary>把当前位置当作新的初始布局（编辑模式里摆好姿势后固化下来）。</summary>
        [ContextMenu("Capture Current As Initial")]
        public void CaptureCurrentAsInitial()
        {
            if (!IsBuilt) return;
            _system.CaptureInitialLayout();
        }

        /// <summary>收集结构边端点（画线框用，不分配新列表则由调用方复用）。</summary>
        public void CollectStructuralEdges(List<ValueTuple<Vector3, Vector3>> into)
        {
            if (into == null) throw new ArgumentNullException("into");
            if (!IsBuilt) return;

            var constraints = _system.Constraints;
            for (int i = 0; i < constraints.Count; i++)
            {
                var c = constraints[i];
                if (c.type != ClothConstraintType.Structural) continue;
                into.Add(new ValueTuple<Vector3, Vector3>(_system.GetPosition(c.a), _system.GetPosition(c.b)));
            }
        }

        // ---------------------------------------------------------------- 内部

        static void ApplyPins(ClothSimulation cloth, ClothPinEdges edges)
        {
            if (edges == ClothPinEdges.None) return;

            if ((edges & ClothPinEdges.Top) != 0)
            {
                for (int col = 0; col < cloth.Columns; col++) cloth.SetPinned(cloth.IndexOf(col, 0), true);
            }
            if ((edges & ClothPinEdges.Bottom) != 0)
            {
                for (int col = 0; col < cloth.Columns; col++) cloth.SetPinned(cloth.IndexOf(col, cloth.Rows - 1), true);
            }
            if ((edges & ClothPinEdges.Left) != 0)
            {
                for (int row = 0; row < cloth.Rows; row++) cloth.SetPinned(cloth.IndexOf(0, row), true);
            }
            if ((edges & ClothPinEdges.Right) != 0)
            {
                for (int row = 0; row < cloth.Rows; row++) cloth.SetPinned(cloth.IndexOf(cloth.Columns - 1, row), true);
            }
        }

        void RefreshObstacles()
        {
            if (!obtainObstaclesFromTransforms || _system == null) return;

            // 只清自己这批模拟空间的球，别把桥接进来的世界空间碰撞体一起清掉
            _system.Collisions.ClearSimulationSpace();
            float invMyScale = 1f / Mathf.Max(1e-4f, transform.lossyScale.magnitude / 1.7320508f);

            for (int i = 0; i < obstacles.Count; i++)
            {
                Transform obstacle = obstacles[i];
                if (obstacle == null) continue;

                float worldRadius = obstacleRadiusFallback;
                var sphere = obstacle.GetComponent<SphereCollider>();
                if (sphere != null) worldRadius = sphere.radius;

                worldRadius *= obstacle.lossyScale.magnitude / 1.7320508f;
                float localRadius = worldRadius * invMyScale;
                if (localRadius <= 0f) continue;          // 退化缩放：跳过，而不是塞一个非法障碍物

                _system.AddSphereObstacle(transform.InverseTransformPoint(obstacle.position), localRadius);
            }
        }

        void BuildMesh()
        {
            int columns = _system.Columns;
            int rows = _system.Rows;

            _triangles = ClothMeshBuilder.BuildTriangles(columns, rows);
            _uvs = ClothMeshBuilder.BuildUvs(columns, rows);
            _vertices = new Vector3[_system.ParticleCount];
            ClothMeshBuilder.ApplyPositions(_system, _vertices);

            _filter = GetComponent<MeshFilter>();
            if (_filter == null) _filter = gameObject.AddComponent<MeshFilter>();

            if (_mesh == null || !_ownsMesh)
            {
                _mesh = new Mesh { name = "ClothMesh (" + name + ")" };
                _ownsMesh = true;
            }
            _mesh.Clear(true);

            ClothMeshBuilder.FillMesh(_mesh, columns, rows, _vertices, _triangles, _uvs, true, recalculateNormals);
            _filter.sharedMesh = _mesh;               // 忘了这行就是"网格生成成功但画面什么都没有"

            // MeshRenderer 交给使用者：包不依赖任何渲染管线，材质也不在这里猜
            if (GetComponent<MeshRenderer>() == null)
            {
                var renderer = gameObject.AddComponent<MeshRenderer>();
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                renderer.receiveShadows = true;
            }
        }

        void WritePositions()
        {
            if (!generateMesh || _mesh == null || _vertices == null) return;

            ClothMeshBuilder.ApplyPositions(_system, _vertices);
            _mesh.SetVertices(_vertices, 0, _vertices.Length);
            if (recalculateNormals) _mesh.RecalculateNormals();
            _mesh.RecalculateBounds();
        }

        // ---------------------------------------------------------------- Gizmos

        void OnDrawGizmosSelected()
        {
            if (!drawGizmoWireframe || !IsBuilt) return;

            var edges = new List<ValueTuple<Vector3, Vector3>>();
            CollectStructuralEdges(edges);

            Gizmos.color = new Color(0.35f, 0.8f, 1f, 0.45f);
            Transform root = transform;
            for (int i = 0; i < edges.Count; i++)
            {
                Gizmos.DrawLine(root.TransformPoint(edges[i].Item1), root.TransformPoint(edges[i].Item2));
            }

            Gizmos.color = new Color(1f, 0.45f, 0.2f, 0.9f);
            for (int i = 0; i < _system.ParticleCount; i++)
            {
                Gizmos.DrawSphere(root.TransformPoint(_system.GetPosition(i)), parameters.spacing * 0.12f);
            }
        }
    }
}
