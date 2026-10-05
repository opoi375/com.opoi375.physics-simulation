// Copyright (c) 2026 PhysicsSimulation. MIT License.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace PhysicsSimulation
{
    /// <summary>一条软体弹簧的两端质点下标与静止长度（拓扑视图，诊断与 Gizmos 用）。</summary>
    public struct SoftBodyEdge
    {
        public readonly int a;
        public readonly int b;
        public readonly float restLength;

        public SoftBodyEdge(int a, int b, float restLength)
        {
            this.a = a;
            this.b = b;
            this.restLength = restLength;
        }
    }

    /// <summary>
    /// 软体模拟：把**任意三角形网格**变成会变形、会被重力拽、会自己鼓回原体积的软体。
    ///
    /// 拓扑：
    ///  * 网格顶点按位置焊接（<see cref="SoftBodyParameters.weldTolerance"/>）→ 质点。
    ///    Unity 的网格是按面拆顶点的（一个长方体 8 个角会给出 24 个顶点），不焊接就会散架。
    ///  * 每条无向棱 → 一条结构弹簧；共边两侧的"对面顶点"配对 → 弯曲弹簧（去重、且不与结构重复）。
    ///  * 每条棱都恰好被两个三角形共享 ⇒ 判定为闭合网格 ⇒ 才有可靠的体积。
    ///
    /// 力学：直接复用 v1.0.0 的 <see cref="MassSpringSystem"/>（重力 + 弹簧内力 + 隐式阻尼），
    /// 体积则作为**梯度恢复力**加进力累积器：F_i = -(k·(V-V0) + c·dV/dt)·∇_i V，
    /// 其中 ∇_i V = (1/3)·Σ_{含 i 的三角形} 面积向量。这样"不漏气"这件事不需要新求解器。
    ///
    /// 确定性：固定遍历顺序、无随机、无 Time、无并行；同参数同步数逐位一致。
    /// </summary>
    public sealed class SoftBodySimulation
    {
        const int StretchClampPasses = 8;

        readonly SoftBodyParameters _parameters;
        MassSpringSystem _system;

        int[] _weldMap;                 // 网格顶点下标 → 质点下标
        int[] _triangles;               // 焊接后的三角形索引（每 3 个一组）
        SoftBodyEdge[] _structural;
        SoftBodyEdge[] _bend;
        Vector3[] _gradients;           // 体积梯度暂存（每质点一个）

        bool _isClosed;
        float _restVolume;
        bool _isBuilt;

        public SoftBodySimulation(SoftBodyParameters parameters)
        {
            if (parameters == null) throw new ArgumentNullException("parameters", "软体参数不能为 null");
            parameters.Validate();
            _parameters = parameters;
        }

        public SoftBodyParameters Parameters { get { return _parameters; } }

        /// <summary>底层质点弹簧系统（诊断用；体积力由本类在每子步注入）。</summary>
        public MassSpringSystem System { get { return _system; } }

        public bool IsBuilt { get { return _isBuilt; } }
        public int ParticleCount { get { return _system == null ? 0 : _system.Particles.Count; } }
        public int MeshVertexCount { get { return _weldMap == null ? 0 : _weldMap.Length; } }
        public int StructuralSpringCount { get { return _structural == null ? 0 : _structural.Length; } }
        public int BendSpringCount { get { return _bend == null ? 0 : _bend.Length; } }
        public int TriangleCount { get { return _triangles == null ? 0 : _triangles.Length / 3; } }

        /// <summary>每条棱都被两个三角形共享 ⇒ 闭合 ⇒ 体积可靠。</summary>
        public bool IsClosed { get { return _isClosed; } }

        // ==================================================================
        // 构建
        // ==================================================================

        /// <summary>
        /// 用一份网格数据重建软体。所有校验都在改动自身状态**之前**完成：
        /// 抛异常时，之前已经建好的软体保持逐位不变。
        /// </summary>
        public void Build(SoftBodyMeshData mesh)
        {
            if (mesh == null) throw new ArgumentNullException("mesh", "网格数据不能为 null");

            Vector3[] vertices = mesh.Vertices;
            int[] triangles = mesh.Triangles;

            if (vertices == null || vertices.Length == 0)
                throw new ArgumentException("网格顶点数组为空，无法构建软体", "mesh");
            if (triangles == null || triangles.Length == 0)
                throw new ArgumentException("网格三角形索引为空，无法构建软体", "mesh");
            if (triangles.Length % 3 != 0)
                throw new ArgumentException("三角形索引长度必须是 3 的倍数，当前 " + triangles.Length, "mesh");

            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 v = vertices[i];
                if (float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z)
                    || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z))
                {
                    throw new ArgumentException("顶点 " + i + " 坐标非有限：" + v, "mesh");
                }
            }
            for (int i = 0; i < triangles.Length; i++)
            {
                if (triangles[i] < 0 || triangles[i] >= vertices.Length)
                {
                    throw new ArgumentOutOfRangeException("mesh",
                        "三角形索引 " + i + " = " + triangles[i] + " 越界（顶点数 " + vertices.Length + "）");
                }
            }
            for (int t = 0; t < triangles.Length; t += 3)
            {
                int i0 = triangles[t], i1 = triangles[t + 1], i2 = triangles[t + 2];
                if (i0 == i1 || i1 == i2 || i0 == i2)
                    throw new ArgumentException("三角形 " + (t / 3) + " 退化（索引重复：" + i0 + "," + i1 + "," + i2 + "）", "mesh");
                if (Vector3.Cross(vertices[i1] - vertices[i0], vertices[i2] - vertices[i0]).sqrMagnitude <= 0f)
                    throw new ArgumentException("三角形 " + (t / 3) + " 面积为 0（三点共线），体积梯度会失效", "mesh");
            }

            // ---- 焊接顶点 ------------------------------------------------
            var welded = new List<Vector3>(vertices.Length);
            var weldMap = new int[vertices.Length];
            var buckets = new Dictionary<long, List<int>>();
            float tolerance = _parameters.weldTolerance;

            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 p = vertices[i];
                int found = FindWelded(buckets, welded, p, tolerance);
                if (found < 0)
                {
                    found = welded.Count;
                    welded.Add(p);
                    InsertIntoBucket(buckets, p, tolerance, found);
                }
                weldMap[i] = found;
            }

            // ---- 重映射三角形（焊接后可能退化）---------------------------
            var remapped = new int[triangles.Length];
            for (int t = 0; t < triangles.Length; t += 3)
            {
                int a = weldMap[triangles[t]], b = weldMap[triangles[t + 1]], c = weldMap[triangles[t + 2]];
                if (a == b || b == c || a == c)
                {
                    throw new ArgumentException(
                        "三角形 " + (t / 3) + " 在焊接容差 " + tolerance + " 下退化成一条线，请缩小 weldTolerance 或简化网格", "mesh");
                }
                remapped[t] = a; remapped[t + 1] = b; remapped[t + 2] = c;
            }

            // ---- 抽棱 + 找对面顶点 ---------------------------------------
            var edgeTriangles = new Dictionary<long, int>();     // 棱 → 已见三角形数
            var edgeOpposite = new Dictionary<long, int>();      // 棱 → 第一个对面顶点
            var structuralList = new List<SoftBodyEdge>();
            var structuralKeys = new List<long>();

            for (int t = 0; t < remapped.Length; t += 3)
            {
                int a = remapped[t], b = remapped[t + 1], c = remapped[t + 2];
                RegisterEdge(a, b, c, edgeTriangles, edgeOpposite);
                RegisterEdge(b, c, a, edgeTriangles, edgeOpposite);
                RegisterEdge(c, a, b, edgeTriangles, edgeOpposite);
            }

            foreach (var pair in edgeTriangles)
            {
                int a = UnpackA(pair.Key), b = UnpackB(pair.Key);
                structuralKeys.Add(pair.Key);
                structuralList.Add(new SoftBodyEdge(a, b, Vector3.Distance(welded[a], welded[b])));
            }
            structuralList.Sort(EdgeComparer);

            // 弯曲弹簧：每条被两个面共享的棱，连接两侧"对面顶点"
            var bendKeys = new HashSet<long>(structuralKeys);
            var bendList = new List<SoftBodyEdge>();
            foreach (var pair in edgeTriangles)
            {
                if (pair.Value != 2) continue;
                int first = edgeOpposite[pair.Key];
                int second = SecondOpposite(remapped, pair.Key);
                if (second < 0 || first == second) continue;
                long key = Pack(first, second);
                if (!bendKeys.Add(key)) continue;
                bendList.Add(new SoftBodyEdge(UnpackA(key), UnpackB(key),
                    Vector3.Distance(welded[UnpackA(key)], welded[UnpackB(key)])));
            }
            bendList.Sort(EdgeComparer);

            // ---- 体积 ----------------------------------------------------
            bool closed = true;
            foreach (var pair in edgeTriangles)
                if (pair.Value != 2) { closed = false; break; }

            float restVolume = 0f;
            if (closed) restVolume = SignedVolume(remapped, welded);

            // ---- 全部校验通过，才开始改动自身状态 -------------------------
            var system = new MassSpringSystem();
            system.Parameters.gravity = _parameters.gravity;
            system.Parameters.globalDamping = _parameters.damping;
            system.Parameters.substeps = 1;                       // 子步由本类驱动，才能在中间注入体积力
            system.Parameters.maxDeltaTime = float.MaxValue;

            for (int i = 0; i < welded.Count; i++) system.AddParticle(welded[i], _parameters.mass, false, 0f);
            for (int i = 0; i < structuralList.Count; i++)
            {
                SoftBodyEdge e = structuralList[i];
                system.AddSpring(e.a, e.b, e.restLength, _parameters.springStiffness, _parameters.springDamping);
            }
            if (_parameters.bendStiffness > 0f)
            {
                for (int i = 0; i < bendList.Count; i++)
                {
                    SoftBodyEdge e = bendList[i];
                    system.AddSpring(e.a, e.b, e.restLength, _parameters.bendStiffness, _parameters.bendDamping);
                }
            }

            _system = system;
            _weldMap = weldMap;
            _triangles = remapped;
            _structural = structuralList.ToArray();
            _bend = bendList.ToArray();
            _gradients = new Vector3[welded.Count];
            _isClosed = closed;
            _restVolume = restVolume;
            _isBuilt = true;
        }

        static void RegisterEdge(int a, int b, int opposite, Dictionary<long, int> edgeTriangles, Dictionary<long, int> edgeOpposite)
        {
            long key = Pack(a, b);
            int seen;
            if (edgeTriangles.TryGetValue(key, out seen)) edgeTriangles[key] = seen + 1;
            else
            {
                edgeTriangles[key] = 1;
                edgeOpposite[key] = opposite;
            }
        }

        /// <summary>找一条棱在"第二个三角形"里的对面顶点（第一个已在 edgeOpposite 里）。</summary>
        static int SecondOpposite(int[] remapped, long edgeKey)
        {
            int a = UnpackA(edgeKey), b = UnpackB(edgeKey);
            int seen = 0;
            for (int t = 0; t < remapped.Length; t += 3)
            {
                int i0 = remapped[t], i1 = remapped[t + 1], i2 = remapped[t + 2];
                // 三角形含这条棱 ⇔ 它的三个顶点里恰好两个落在 {a, b} 上（第三个就是对面顶点）
                int onEdge = ((i0 == a || i0 == b) ? 1 : 0)
                           + ((i1 == a || i1 == b) ? 1 : 0)
                           + ((i2 == a || i2 == b) ? 1 : 0);
                if (onEdge != 2) continue;
                seen++;
                if (seen == 2)
                {
                    if (i0 != a && i0 != b) return i0;
                    if (i1 != a && i1 != b) return i1;
                    return i2;
                }
            }
            return -1;
        }

        static long Pack(int a, int b)
        {
            return ((long)Mathf.Min(a, b) << 32) | (uint)Mathf.Max(a, b);
        }

        static int UnpackA(long key) { return (int)(key >> 32); }
        static int UnpackB(long key) { return (int)(uint)key; }

        static int EdgeComparer(SoftBodyEdge x, SoftBodyEdge y)
        {
            int byA = x.a.CompareTo(y.a);
            return byA != 0 ? byA : x.b.CompareTo(y.b);
        }

        static int FindWelded(Dictionary<long, List<int>> buckets, List<Vector3> welded, Vector3 p, float tolerance)
        {
            int cx = Mathf.FloorToInt(p.x / tolerance);
            int cy = Mathf.FloorToInt(p.y / tolerance);
            int cz = Mathf.FloorToInt(p.z / tolerance);
            float sqr = tolerance * tolerance;

            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        List<int> bucket;
                        if (!buckets.TryGetValue(CellKey(cx + dx, cy + dy, cz + dz), out bucket)) continue;
                        for (int i = 0; i < bucket.Count; i++)
                            if ((welded[bucket[i]] - p).sqrMagnitude <= sqr) return bucket[i];
                    }
            return -1;
        }

        static void InsertIntoBucket(Dictionary<long, List<int>> buckets, Vector3 p, float tolerance, int index)
        {
            long key = CellKey(
                Mathf.FloorToInt(p.x / tolerance),
                Mathf.FloorToInt(p.y / tolerance),
                Mathf.FloorToInt(p.z / tolerance));
            List<int> bucket;
            if (!buckets.TryGetValue(key, out bucket))
            {
                bucket = new List<int>();
                buckets[key] = bucket;
            }
            bucket.Add(index);
        }

        static long CellKey(int x, int y, int z)
        {
            unchecked
            {
                long h = (long)x * 73856093L ^ (long)y * 19349663L ^ (long)z * 83492791L;
                return h;
            }
        }

        // ==================================================================
        // 体积
        // ==================================================================

        /// <summary>由三角形用散度定理算有符号体积：V = Σ (1/6)·x0·(x1 × x2)。绕序反了会得到负值。</summary>
        static float SignedVolume(int[] triangles, List<Vector3> positions)
        {
            float volume = 0f;
            for (int t = 0; t < triangles.Length; t += 3)
            {
                Vector3 a = positions[triangles[t]];
                Vector3 b = positions[triangles[t + 1]];
                Vector3 c = positions[triangles[t + 2]];
                volume += Vector3.Dot(a, Vector3.Cross(b, c)) / 6f;
            }
            return volume;
        }

        /// <summary>
        /// 体积梯度：∇_i V = (1/3)·Σ_{含 i 的三角形} 面积向量（面积向量 = (1/2)·(x1-x0)×(x2-x0)）。
        /// 顺带把当前体积算出来，省一遍遍历。
        /// </summary>
        float ComputeVolumeAndGradients(Vector3[] gradients)
        {
            IReadOnlyList<Particle> particles = _system.Particles;
            for (int i = 0; i < gradients.Length; i++) gradients[i] = Vector3.zero;

            float volume = 0f;
            for (int t = 0; t < _triangles.Length; t += 3)
            {
                int i0 = _triangles[t], i1 = _triangles[t + 1], i2 = _triangles[t + 2];
                Vector3 a = particles[i0].position;
                Vector3 b = particles[i1].position;
                Vector3 c = particles[i2].position;

                volume += Vector3.Dot(a, Vector3.Cross(b, c)) / 6f;

                Vector3 areaVector = Vector3.Cross(b - a, c - a) * (1f / 6f);   // (1/3)·(1/2)·(...)
                gradients[i0] += areaVector;
                gradients[i1] += areaVector;
                gradients[i2] += areaVector;
            }
            return volume;
        }

        /// <summary>当前有符号体积（闭合网格才有意义；开放网格返回 0，不硬编一个假体积）。</summary>
        public float Volume()
        {
            if (!_isBuilt) throw new InvalidOperationException("软体还没构建，先调用 Build");
            if (!_isClosed) return 0f;
            return ComputeVolumeAndGradients(_gradients);
        }

        /// <summary>构建时的静止体积（体积恢复力的目标值）。开放网格为 0。</summary>
        public float RestVolume()
        {
            if (!_isBuilt) throw new InvalidOperationException("软体还没构建，先调用 Build");
            return _restVolume;
        }

        void ApplyVolumeForce()
        {
            if (!_isClosed || _parameters.volumeStiffness <= 0f) return;

            float volume = ComputeVolumeAndGradients(_gradients);
            IReadOnlyList<Particle> particles = _system.Particles;

            float dVolumePerTime = 0f;
            for (int i = 0; i < particles.Count; i++)
                dVolumePerTime += Vector3.Dot(_gradients[i], particles[i].velocity);

            float coefficient = -(_parameters.volumeStiffness * (volume - _restVolume)
                                  + _parameters.volumeDamping * dVolumePerTime);

            for (int i = 0; i < particles.Count; i++)
                particles[i].force += coefficient * _gradients[i];
        }

        // ==================================================================
        // 积分
        // ==================================================================

        // ==================================================================
        // 碰撞代理（v1.3.0）：软体不再只能穿地
        // ==================================================================

        private readonly CollisionSet _collisions = new CollisionSet();
        private Matrix4x4 _localToWorld = Matrix4x4.identity;
        private Matrix4x4 _worldToLocal = Matrix4x4.identity;
        private bool _spaceIsIdentity = true;

        /// <summary>碰撞代理列表。空列表时逐位等价于 v1.2.0，这一条有回归测试锁住。</summary>
        public CollisionSet Collisions { get { return _collisions; } }

        /// <summary>有没有碰撞体（Dump State 靠它把“为什么不落地”说清）。</summary>
        public bool HasColliders { get { return _collisions.Count > 0; } }

        /// <summary>模拟空间（通常是组件局部空间）到世界的变换。</summary>
        public Matrix4x4 SimulationToWorld { get { return _localToWorld; } }

        /// <summary>告知求解器相对世界怎么摆；只有世界空间的代理会用到，传单位矩阵就退回 v1.2.0 行为。</summary>
        public void SetSimulationToWorld(Matrix4x4 localToWorld)
        {
            if (localToWorld == Matrix4x4.identity)
            {
                _localToWorld = Matrix4x4.identity;
                _worldToLocal = Matrix4x4.identity;
                _spaceIsIdentity = true;
                return;
            }
            _localToWorld = localToWorld;
            _worldToLocal = localToWorld.inverse;
            _spaceIsIdentity = false;
        }

        /// <summary>推进一个时间步：dt 先钳到 maxDeltaTime，再均分成 substeps 个子步逐步积分。</summary>
        public void Step(float deltaTime)
        {
            if (!_isBuilt) throw new InvalidOperationException("软体还没构建，先调用 Build");
            if (!(deltaTime > 0f) || float.IsInfinity(deltaTime) || float.IsNaN(deltaTime))
                throw new ArgumentOutOfRangeException("deltaTime", "dt 必须是有限正数（秒），当前 " + deltaTime);

            float span = _parameters.ClampDeltaTime(deltaTime);
            int substeps = _parameters.EffectiveSubsteps;
            float h = span / substeps;
            float globalDamping = _parameters.damping;
            IReadOnlyList<Particle> particles = _system.Particles;

            for (int s = 0; s < substeps; s++)
            {
                _system.ApplyForces();
                ApplyVolumeForce();

                for (int i = 0; i < particles.Count; i++)
                {
                    MassSpringIntegrator.Integrate(particles[i], h, globalDamping);
                    LimitSpeed(particles[i]);
                }

                if (_parameters.enableStretchLimit) ClampStretch();

                // 碰撞放在子步最后：“不穿模”是硬保证，不能被后面的限幅拉回几何体里（与布料的约定一致）
                if (_collisions.Count > 0)
                {
                    CollisionPass.ResolveParticles(_collisions, particles, _parameters.collisionThickness,
                        _localToWorld, _worldToLocal, _spaceIsIdentity);
                }
            }
        }

        void LimitSpeed(Particle particle)
        {
            float max = _parameters.maxSpeed;
            if (max <= 0f) return;
            float speed = particle.velocity.magnitude;
            if (speed > max) particle.velocity *= max / speed;
        }

        /// <summary>把超过 maxStretchRatio 的结构弹簧按位置投影回来（钉住的一端不动）。多轮扫描收敛。</summary>
        void ClampStretch()
        {
            IReadOnlyList<Particle> particles = _system.Particles;
            float limit = _parameters.maxStretchRatio;

            for (int pass = 0; pass < StretchClampPasses; pass++)
            {
                bool clean = true;
                for (int i = 0; i < _structural.Length; i++)
                {
                    SoftBodyEdge edge = _structural[i];
                    Particle a = particles[edge.a];
                    Particle b = particles[edge.b];

                    Vector3 delta = b.position - a.position;
                    float length = delta.magnitude;
                    float max = edge.restLength * limit;
                    if (length <= max || length <= 0f) continue;

                    clean = false;
                    Vector3 correction = delta * ((length - max) / length);
                    if (a.pinned && b.pinned) continue;
                    if (a.pinned) b.position -= correction;
                    else if (b.pinned) a.position += correction;
                    else
                    {
                        a.position += correction * 0.5f;
                        b.position -= correction * 0.5f;
                    }
                }
                if (clean) return;
            }
        }

        // ==================================================================
        // 读写状态
        // ==================================================================

        void RequireParticle(int index)
        {
            if (!_isBuilt) throw new InvalidOperationException("软体还没构建，先调用 Build");
            if (index < 0 || index >= ParticleCount)
                throw new ArgumentOutOfRangeException("index",
                    "质点下标 " + index + " 越界（共 " + ParticleCount + " 个质点）");
        }

        public Vector3 GetPosition(int index)
        {
            RequireParticle(index);
            return _system.Particles[index].position;
        }

        public Vector3 GetVelocity(int index)
        {
            RequireParticle(index);
            return _system.Particles[index].velocity;
        }

        /// <summary>
        /// 给单个质点设速度（"推一把"用的就是这个）：只改速度，绝不碰位置。
        /// 钉住的质点也允许设 —— 求解器每步会把钉住质点的速度归零，所以没有副作用。
        /// </summary>
        public void SetVelocity(int index, Vector3 velocity)
        {
            RequireParticle(index);
            _system.Particles[index].velocity = velocity;
        }

        public Vector3[] CapturePositions()
        {
            if (!_isBuilt) throw new InvalidOperationException("软体还没构建，先调用 Build");
            var result = new Vector3[_system.Particles.Count];
            for (int i = 0; i < result.Length; i++) result[i] = _system.Particles[i].position;
            return result;
        }

        /// <summary>整体摆位（外部初始化 / 动画对齐用）：位置写入、速度清零。</summary>
        public void SetPositions(Vector3[] positions)
        {
            if (!_isBuilt) throw new InvalidOperationException("软体还没构建，先调用 Build");
            if (positions == null) throw new ArgumentNullException("positions", "位置数组不能为 null");
            if (positions.Length != ParticleCount)
                throw new ArgumentException("位置数组长度 " + positions.Length + " 与质点数量 " + ParticleCount + " 不匹配", "positions");
            for (int i = 0; i < positions.Length; i++)
            {
                Vector3 v = positions[i];
                if (float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z)
                    || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z))
                {
                    throw new ArgumentException("位置 " + i + " 非有限：" + v, "positions");
                }
            }

            // 校验全过之后才写
            for (int i = 0; i < positions.Length; i++)
            {
                Particle particle = _system.Particles[i];
                particle.position = positions[i];
                particle.velocity = Vector3.zero;
            }
        }

        public void SetPinned(int index, bool pinned)
        {
            RequireParticle(index);
            Particle particle = _system.Particles[index];
            particle.pinned = pinned;
            particle.inverseMass = pinned ? 0f : 1f / particle.mass;
        }

        public bool IsPinned(int index)
        {
            RequireParticle(index);
            return _system.Particles[index].pinned;
        }

        /// <summary>网格顶点下标 → 焊接后的质点下标。</summary>
        public int IndexOfVertex(int meshVertexIndex)
        {
            if (!_isBuilt) throw new InvalidOperationException("软体还没构建，先调用 Build");
            if (meshVertexIndex < 0 || meshVertexIndex >= _weldMap.Length)
                throw new ArgumentOutOfRangeException("meshVertexIndex",
                    "网格顶点下标 " + meshVertexIndex + " 越界（共 " + _weldMap.Length + " 个顶点）");
            return _weldMap[meshVertexIndex];
        }

        /// <summary>按网格顶点下标钉住（会连带钉住与它焊接在一起的同一个质点）。</summary>
        public void SetVertexPinned(int meshVertexIndex, bool pinned)
        {
            SetPinned(IndexOfVertex(meshVertexIndex), pinned);
        }

        /// <summary>把质点位置按焊接映射写回网格顶点数组（长度必须等于 MeshVertexCount）。</summary>
        public void WritePositionsTo(Vector3[] meshVertices)
        {
            if (!_isBuilt) throw new InvalidOperationException("软体还没构建，先调用 Build");
            if (meshVertices == null) throw new ArgumentNullException("meshVertices", "目标数组不能为 null");
            if (meshVertices.Length != _weldMap.Length)
                throw new ArgumentException("目标数组长度 " + meshVertices.Length + " 与网格顶点数 " + _weldMap.Length + " 不匹配", "meshVertices");

            for (int i = 0; i < _weldMap.Length; i++)
                meshVertices[i] = _system.Particles[_weldMap[i]].position;
        }

        /// <summary>拷一份"按原始网格顶点展开"的位置出来。</summary>
        public Vector3[] CaptureMeshVertices()
        {
            if (!_isBuilt) throw new InvalidOperationException("软体还没构建，先调用 Build");
            var result = new Vector3[_weldMap.Length];
            WritePositionsTo(result);
            return result;
        }

        public SoftBodyEdge GetStructuralSpring(int index)
        {
            if (!_isBuilt) throw new InvalidOperationException("软体还没构建，先调用 Build");
            if (index < 0 || index >= _structural.Length)
                throw new ArgumentOutOfRangeException("index",
                    "结构弹簧下标 " + index + " 越界（共 " + _structural.Length + " 条）");
            return _structural[index];
        }

        public SoftBodyEdge GetBendSpring(int index)
        {
            if (!_isBuilt) throw new InvalidOperationException("软体还没构建，先调用 Build");
            if (index < 0 || index >= _bend.Length)
                throw new ArgumentOutOfRangeException("index",
                    "弯曲弹簧下标 " + index + " 越界（共 " + _bend.Length + " 条）");
            return _bend[index];
        }

        /// <summary>回到建好时的初始布局（位置、速度、钉住状态全部复位）。</summary>
        public void ResetToInitial()
        {
            if (!_isBuilt) throw new InvalidOperationException("软体还没构建，先调用 Build");
            _system.ResetToInitial();
        }

        /// <summary>把当前布局捕获成"初始布局"，之后 ResetToInitial 会回到这里。</summary>
        public void CaptureInitialLayout()
        {
            if (!_isBuilt) throw new InvalidOperationException("软体还没构建，先调用 Build");
            for (int i = 0; i < _system.Particles.Count; i++) _system.Particles[i].CaptureInitialLayout();
        }

        /// <summary>结构弹簧里最大的 长度/静止长度，1.0 表示完全没被拉长。</summary>
        public float MaxStretchRatio()
        {
            if (!_isBuilt) throw new InvalidOperationException("软体还没构建，先调用 Build");
            float max = 0f;
            IReadOnlyList<Particle> particles = _system.Particles;
            for (int i = 0; i < _structural.Length; i++)
            {
                SoftBodyEdge edge = _structural[i];
                float length = Vector3.Distance(particles[edge.a].position, particles[edge.b].position);
                float ratio = length / edge.restLength;
                if (ratio > max) max = ratio;
            }
            return max;
        }

        /// <summary>是否出现 NaN / Infinity（位置或速度），冒烟断言用。</summary>
        public bool HasNonFiniteState()
        {
            if (!_isBuilt) return false;
            return _system.HasNonFiniteState();
        }

        /// <summary>收集弹簧两端的世界位置线段，Gizmos / 调试绘制用。</summary>
        public void CollectEdges(List<ValueTuple<Vector3, Vector3>> into, bool includeBend)
        {
            if (!_isBuilt) throw new InvalidOperationException("软体还没构建，先调用 Build");
            if (into == null) throw new ArgumentNullException("into", "目标列表不能为 null");

            IReadOnlyList<Particle> particles = _system.Particles;
            for (int i = 0; i < _structural.Length; i++)
            {
                SoftBodyEdge edge = _structural[i];
                into.Add(new ValueTuple<Vector3, Vector3>(particles[edge.a].position, particles[edge.b].position));
            }
            if (!includeBend) return;
            for (int i = 0; i < _bend.Length; i++)
            {
                SoftBodyEdge edge = _bend[i];
                into.Add(new ValueTuple<Vector3, Vector3>(particles[edge.a].position, particles[edge.b].position));
            }
        }
    }
}
