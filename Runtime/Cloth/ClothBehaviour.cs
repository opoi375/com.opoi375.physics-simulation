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
    public sealed class ClothBehaviour : MonoBehaviour
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
            if (_ownsMesh && _mesh != null)
            {
                Destroy(_mesh);
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
        }

        /// <summary>手动推进一个时间步（不管 autoSimulate 是什么）。<paramref name="dt"/> 由内部钳制与分子步。</summary>
        public void Step(float dt)
        {
            if (!IsBuilt) return;

            RefreshObstacles();
            SyncSceneColliders();

            if (windAcceleration != Vector3.zero) _system.AddWindImpulse(windAcceleration, dt);

            _system.Step(dt);                       // 非法 dt 由系统报错，不吞异常（吞了就只能靠猜了）
            WritePositions();
        }

        /// <summary>
        /// 把组件变换与 Collider 列表同步给求解器。
        /// 没开 collideWithSceneColliders 时注入单位矩阵，于是逐位等价于 v1.1.0/v1.2.0 的布料。
        /// 旧的 <c>obstacles</c>（模拟空间球）与这批世界空间代理可以同时生效，互不干扰。
        /// </summary>
        void SyncSceneColliders()
        {
            if (_system == null) return;
            if (!collideWithSceneColliders)
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
