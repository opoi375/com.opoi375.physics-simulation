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
    public sealed class SoftBodyBehaviour : MonoBehaviour
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

        SoftBodySimulation _simulation;
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
            if (_mesh != null)
            {
                if (Application.isPlaying) Destroy(_mesh);
                else DestroyImmediate(_mesh);
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
                ApplyPinning();
                ApplyInitialVelocity();
                EnsureInstanceMesh();
                WriteBack();
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
            _simulation.Step(deltaTime);
            WriteBack();
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
