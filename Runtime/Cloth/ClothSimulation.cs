// Copyright (c) 2026 PhysicsSimulation. MIT License.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace PhysicsSimulation
{
    /// <summary>
    /// 布料模拟：C×R 质点网格 + 结构 / 剪切 / 弯曲三类距离约束，用位置投影（PBD / XPBD 思路）求解。
    ///
    /// 与 <see cref="MassSpringSystem"/> 的区别是本质性的：
    ///   - 质点弹簧把"硬度"写进力（F = -k·Δx），k 一大就要求极小步长，否则数值爆炸；
    ///   - 这里把"硬度"写进**位置投影的比例**（约束直接改位置），硬度与时间步解耦，
    ///     所以 k = 1（近乎不可伸长）也能在 1/60 秒的步长下稳定。
    ///
    /// 一个子步的流程（顺序固定 ⇒ 确定性）：
    ///   1) 预测：v += g·h，v /= (1 + damping·h)，prev = x，x += v·h
    ///   2) 投影：按 结构 → 剪切 → 弯曲 的固定顺序迭代 iterations 次
    ///   3) 拉伸上限兜底：任何约束超过 restLength·maxStretchRatio 就硬拉回来
    ///   4) 回算速度：v = (x - prev) / h
    ///
    /// 性能：求解器只走**扁平数组**（SoA），三类约束在同一个数组里按连续区间存放，
    /// 内层循环没有对象引用追逐、没有 List 枚举器、每约束只做 1 次除法 + 1 次开方。
    /// 对外的 <see cref="Constraints"/> 只作为诊断视图（构造时建一次，不参与内层循环）。
    ///
    /// 确定性：固定顺序遍历、无 Random / 无 Time / 无并行；同参数同步数逐位一致。
    /// </summary>
    public sealed class ClothSimulation
    {
        private readonly int _columns;
        private readonly int _rows;
        private readonly Vector3[] _initialPositions;
        private readonly Vector3[] _positions;
        private readonly Vector3[] _previous;
        private readonly Vector3[] _velocities;
        private readonly float[] _inverseMass;
        private readonly bool[] _pinned;

        // ---- 约束的 SoA 存储：[0, structuralEnd) 结构、[structuralEnd, shearEnd) 剪切、[shearEnd, bendEnd) 弯曲
        private readonly int[] _cA;
        private readonly int[] _cB;
        private readonly float[] _cRest;
        private readonly List<DistanceConstraint> _constraintView = new List<DistanceConstraint>();

        private int _structuralEnd;
        private int _shearEnd;
        private int _cursor;            // 约束写入游标（== 总约束数）

        // 碰撞代理（v1.3.0）：模拟空间的代理与旧的球障碍算术逐位一致；世界空间的代理经过变换矩阵生效。
        private readonly CollisionSet _collisions = new CollisionSet();
        private Matrix4x4 _localToWorld = Matrix4x4.identity;
        private Matrix4x4 _worldToLocal = Matrix4x4.identity;
        private bool _spaceIsIdentity = true;

        public ClothParameters Parameters { get; }
        public int ParticleCount { get { return _positions.Length; } }
        public int Columns { get { return _columns; } }
        public int Rows { get { return _rows; } }
        public int ConstraintCount { get { return _cursor; } }

        /// <summary>诊断视图：按 结构 → 剪切 → 弯曲 顺序排列，与内层循环同序。</summary>
        public IReadOnlyList<DistanceConstraint> Constraints { get { return _constraintView; } }

        public ClothSimulation(ClothParameters parameters)
        {
            if (parameters == null) throw new ArgumentNullException("parameters");
            parameters.Validate();

            Parameters = parameters;
            _columns = parameters.columns;
            _rows = parameters.rows;

            int count = _columns * _rows;
            _positions = new Vector3[count];
            _previous = new Vector3[count];
            _velocities = new Vector3[count];
            _initialPositions = new Vector3[count];
            _inverseMass = new float[count];
            _pinned = new bool[count];

            for (int row = 0; row < _rows; row++)
            {
                for (int col = 0; col < _columns; col++)
                {
                    int index = IndexOfInternal(col, row);
                    // 网格铺在 XY 平面上，行号向下增长：顶行（row = 0）就是常见的钉住边
                    Vector3 position = new Vector3(col * parameters.spacing, -row * parameters.spacing, 0f);
                    _positions[index] = position;
                    _initialPositions[index] = position;
                    _previous[index] = position;
                    _inverseMass[index] = 1f / parameters.mass;
                }
            }

            _cA = new int[CountConstraints()];
            _cB = new int[_cA.Length];
            _cRest = new float[_cA.Length];
            BuildConstraints();
        }

        /// <summary>(列, 行) → 质点索引。越界抛 <see cref="ArgumentOutOfRangeException"/>。</summary>
        public int IndexOf(int col, int row)
        {
            if (col < 0 || col >= _columns)
            {
                throw new ArgumentOutOfRangeException("col", "列号 " + col + " 越界（0.." + (_columns - 1) + "）");
            }
            if (row < 0 || row >= _rows)
            {
                throw new ArgumentOutOfRangeException("row", "行号 " + row + " 越界（0.." + (_rows - 1) + "）");
            }
            return IndexOfInternal(col, row);
        }

        int IndexOfInternal(int col, int row)
        {
            // 行主序：同一行的质点连续存放，内层循环的访存最友好
            return row * _columns + col;
        }

        /// <summary>钉住 / 解开某个质点。固定点的 inverseMass 为 0，投影不会移动它。</summary>
        public void SetPinned(int index, bool pinned)
        {
            ValidateIndex(index);
            _pinned[index] = pinned;
            _inverseMass[index] = pinned ? 0f : 1f / Parameters.mass;
        }

        public bool IsPinned(int index)
        {
            ValidateIndex(index);
            return _pinned[index];
        }

        public Vector3 GetPosition(int index)
        {
            ValidateIndex(index);
            return _positions[index];
        }

        public Vector3 GetVelocity(int index)
        {
            ValidateIndex(index);
            return _velocities[index];
        }

        /// <summary>拷贝一份当前位置（测试与诊断用）。</summary>
        public Vector3[] CapturePositions()
        {
            var copy = new Vector3[_positions.Length];
            Array.Copy(_positions, copy, _positions.Length);
            return copy;
        }

        /// <summary>给所有自由质点加一次速度冲量（风 / 爆炸之类的外部扰动，确定性）。</summary>
        public void AddWindImpulse(Vector3 acceleration, float dt)
        {
            if (float.IsNaN(dt) || float.IsInfinity(dt) || dt < 0f)
            {
                throw new ArgumentOutOfRangeException("dt", "冲量时长必须是非负有限值");
            }
            for (int i = 0; i < _velocities.Length; i++)
            {
                if (_inverseMass[i] > 0f) _velocities[i] += acceleration * dt;
            }
        }

        /// <summary>当前碰撞代理个数（含旧的球形障碍物）。</summary>
        public int ObstacleCount { get { return _collisions.Count; } }

        /// <summary>碰撞代理列表。往里 Add 就生效，不需要重建。</summary>
        public CollisionSet Collisions { get { return _collisions; } }

        /// <summary>有没有碰撞体（Dump State 用它说清状态）。</summary>
        public bool HasColliders { get { return _collisions.Count > 0; } }

        /// <summary>质点所在空间到世界空间的变换。不设置就是单位矩阵。</summary>
        public Matrix4x4 SimulationToWorld { get { return _localToWorld; } }

        /// <summary>
        /// 告知求解器它的坐标相对世界怎么摆（组件传 <c>transform.localToWorldMatrix</c>）。
        /// 只有登记成 <see cref="CollisionProxySpace.World"/> 的代理会用到它；
        /// 传单位矩阵就完全退回 v1.2.0 的行为。逆矩阵这里一次算好，不每个质点算一遍。
        /// </summary>
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

        /// <summary>
        /// 加一个球形障碍物（坐标与质点同一空间）。半径必须是非负有限值，球心必须有限。
        /// 非法参数在写入之前就报错，所以失败调用不会改变已有障碍物列表。
        /// </summary>
        public void AddSphereObstacle(Vector3 center, float radius)
        {
            if (!(radius > 0f) || float.IsNaN(radius) || float.IsInfinity(radius))
            {
                throw new ArgumentOutOfRangeException("radius", "障碍物半径必须是有限正数（当前 " + radius + "）");
            }
            if (float.IsNaN(center.x) || float.IsNaN(center.y) || float.IsNaN(center.z)
                || float.IsInfinity(center.x) || float.IsInfinity(center.y) || float.IsInfinity(center.z))
            {
                throw new ArgumentOutOfRangeException("center", "障碍物球心必须是有限值");
            }
            // 走统一的代理列表，空间标成 Simulation ⇒ 与 v1.1.0 那条算术逐位相同
            _collisions.Add(new SphereCollisionProxy(center, radius), CollisionProxySpace.Simulation);
        }

        /// <summary>清空所有碰撞代理（模拟空间与世界空间一起清）。</summary>
        public void ClearObstacles()
        {
            _collisions.Clear();
        }

        /// <summary>推进一个时间步：钳制 dt → 分子步 → 每子步（预测 + 投影 + 碰撞 + 拉伸上限 + 回算速度）。</summary>
        public void Step(float deltaTime)
        {
            if (!(deltaTime > 0f) || float.IsInfinity(deltaTime))
            {
                throw new ArgumentOutOfRangeException("deltaTime", "dt 必须是有限正数（秒）");
            }

            float span = Parameters.ClampDeltaTime(deltaTime);
            int substeps = Parameters.substeps < 1 ? 1 : Parameters.substeps;
            int iterations = Parameters.iterations < 1 ? 1 : Parameters.iterations;
            float h = span / substeps;
            float invH = 1f / h;

            // 刚度 → 每迭代等效投影比例（Macklin 的"小刚度修正"），整步只算一次
            float alphaStructural = EffectiveAlpha(Parameters.structuralStiffness, iterations);
            float alphaShear = EffectiveAlpha(Parameters.shearStiffness, iterations);
            float alphaBend = EffectiveAlpha(Parameters.bendStiffness, iterations);

            Vector3 gravity = Parameters.gravity;
            float dampingDivisor = 1f / (1f + Parameters.damping * h);
            float stretchLimit = Parameters.maxStretchRatio;
            bool solveShear = Parameters.enableShear;
            bool solveBend = Parameters.enableBend;

            for (int s = 0; s < substeps; s++)
            {
                Predict(gravity, dampingDivisor, h);

                for (int it = 0; it < iterations; it++)
                {
                    Project(0, _structuralEnd, alphaStructural);
                    if (solveShear) Project(_structuralEnd, _shearEnd, alphaShear);
                    if (solveBend) Project(_shearEnd, _cursor, alphaBend);
                }

                ClampStretch(stretchLimit);
                ResolveCollisions();      // 放在子步最后：“不穿模”是硬保证，不能被后续钳拉伸拉回球里
                Commit(invH);
            }
        }

        /// <summary>把当前位置当作新的初始布局（Z 零位 → 摆好姿势后固化，供复位用）。速度不变。</summary>
        public void CaptureInitialLayout()
        {
            Array.Copy(_positions, _initialPositions, _positions.Length);
        }

        /// <summary>回到建好时的网格布局，速度清零（钉住状态保留）。</summary>
        public void ResetToInitial()
        {
            Array.Copy(_initialPositions, _positions, _positions.Length);
            Array.Copy(_initialPositions, _previous, _previous.Length);
            for (int i = 0; i < _velocities.Length; i++)
            {
                _velocities[i] = Vector3.zero;
            }
        }

        /// <summary>最大长度比 max(len / restLength)，1 表示毫无形变。诊断与断言用。</summary>
        public float MaxStretchRatio()
        {
            float max = 0f;
            for (int i = 0; i < _cursor; i++)
            {
                Vector3 delta = _positions[_cA[i]] - _positions[_cB[i]];
                float length = (float)Math.Sqrt(delta.sqrMagnitude);
                float ratio = length / _cRest[i];
                if (ratio > max) max = ratio;
            }
            return max;
        }

        /// <summary>是否出现 NaN / Infinity。</summary>
        public bool HasNonFiniteState()
        {
            for (int i = 0; i < _positions.Length; i++)
            {
                Vector3 p = _positions[i];
                Vector3 v = _velocities[i];
                if (float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z)
                    || float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z)
                    || float.IsInfinity(p.x) || float.IsInfinity(p.y) || float.IsInfinity(p.z)
                    || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z))
                {
                    return true;
                }
            }
            return false;
        }

        // ---------------------------------------------------------------- 求解内核

        void Predict(Vector3 gravity, float dampingDivisor, float h)
        {
            Vector3[] positions = _positions;
            Vector3[] previous = _previous;
            Vector3[] velocities = _velocities;
            float[] inverseMass = _inverseMass;

            for (int i = 0; i < positions.Length; i++)
            {
                Vector3 before = positions[i];
                previous[i] = before;

                if (inverseMass[i] <= 0f) continue;         // 固定点：不动、不积分

                Vector3 velocity = (velocities[i] + gravity * h) * dampingDivisor;   // 隐式阻尼：任意 d·h 只衰减、不反号
                velocities[i] = velocity;
                positions[i] = before + velocity * h;
            }
        }

        /// <summary>把 [from, to) 区间内的距离约束投影回静息长度。</summary>
        void Project(int from, int to, float alpha)
        {
            if (alpha <= 0f) return;

            Vector3[] positions = _positions;
            float[] inverseMass = _inverseMass;
            int[] a = _cA;
            int[] b = _cB;
            float[] rest = _cRest;

            for (int i = from; i < to; i++)
            {
                int ia = a[i];
                int ib = b[i];
                float wA = inverseMass[ia];
                float wB = inverseMass[ib];
                float wSum = wA + wB;
                if (wSum <= 0f) continue;                    // 两端都钉住：没有可解

                Vector3 delta = positions[ib] - positions[ia];
                float lengthSqr = delta.sqrMagnitude;
                if (lengthSqr <= 1e-16f) continue;           // 重合：方向未定义，跳过（绝不引入 NaN）

                float length = (float)Math.Sqrt(lengthSqr);
                // 一次除法拿到"每单位权重的位移比例"，剩下的都是乘法
                float scale = alpha * (length - rest[i]) / (length * wSum);
                positions[ia] += delta * (scale * wA);
                positions[ib] -= delta * (scale * wB);
            }
        }

        /// <summary>
        /// 拉伸上限兜底：任何约束长度超过 restLength·limit 就硬拉回来。
        /// 拉一个约束可能反过来撑大另一个，所以逐遍扫描直到干净（正常参数下一遍就过，代价≈多一次遍历）。
        /// </summary>
        void ClampStretch(float limit)
        {
            for (int pass = 0; pass < 16; pass++)
            {
                if (ClampStretchPass(limit)) break;
            }
        }

        bool ClampStretchPass(float limit)
        {
            Vector3[] positions = _positions;
            float[] inverseMass = _inverseMass;
            int[] a = _cA;
            int[] b = _cB;
            float[] rest = _cRest;
            bool clean = true;

            for (int i = 0; i < _cursor; i++)
            {
                int ia = a[i];
                int ib = b[i];
                float wA = inverseMass[ia];
                float wB = inverseMass[ib];
                float wSum = wA + wB;
                if (wSum <= 0f) continue;

                Vector3 delta = positions[ib] - positions[ia];
                float lengthSqr = delta.sqrMagnitude;
                float maxLength = rest[i] * limit;
                if (lengthSqr <= maxLength * maxLength || lengthSqr <= 1e-16f) continue;

                clean = false;
                float length = (float)Math.Sqrt(lengthSqr);
                float scale = (length - maxLength) / (length * wSum);
                positions[ia] += delta * (scale * wA);
                positions[ib] -= delta * (scale * wB);
            }

            return clean;
        }

        /// <summary>
        /// 球体障碍物碰撞：把穿进球壳的质点沿径向顶回“表面 + 碰撞厚度”。
        /// 它跑在每个子步的**最后一次位置写入**，所以“没有质点埋进球里”是硬保证；
        /// 代价是碰撞可能把邻接约束撑得超过拉伸上限，但那会在下一个子步的 ClampStretch 里收敛回去。
        /// 只改位置不改速度，速度由 Commit 从位置差回算（所以会沿球面自然滑开，不会粘住）。
        /// </summary>
        void ResolveCollisions()
        {
            if (_collisions.Count == 0) return;
            CollisionPass.ResolvePositions(_collisions, _positions, Parameters.collisionThickness,
                _localToWorld, _worldToLocal, _spaceIsIdentity);
        }

        void Commit(float invH)
        {
            Vector3[] positions = _positions;
            Vector3[] previous = _previous;
            Vector3[] velocities = _velocities;
            float[] inverseMass = _inverseMass;

            for (int i = 0; i < positions.Length; i++)
            {
                if (inverseMass[i] > 0f) velocities[i] = (positions[i] - previous[i]) * invH;
            }
        }

        /// <summary>
        /// 把"每步硬度"换算成"每次迭代的投影比例"：
        /// α = 1 - (1 - k)^(1/iterations)。这样 iterations 只影响收敛速度，不影响最终硬度（改迭代数不会顺手改手感）。
        /// </summary>
        static float EffectiveAlpha(float stiffness, int iterations)
        {
            if (stiffness <= 0f) return 0f;
            if (stiffness >= 1f || iterations <= 1) return 1f;
            return 1f - (float)Math.Pow(1.0 - stiffness, 1.0 / iterations);
        }

        // ---------------------------------------------------------------- 构建

        int CountConstraints()
        {
            int structural = _rows * (_columns - 1) + _columns * (_rows - 1);
            int shear = Parameters.enableShear ? 2 * (_columns - 1) * (_rows - 1) : 0;
            int bend = Parameters.enableBend ? _rows * (_columns - 2) + _columns * (_rows - 2) : 0;
            return structural + shear + bend;
        }

        void BuildConstraints()
        {
            float spacing = Parameters.spacing;
            float diagonal = spacing * 1.41421356f;   // sqrt(2)
            float doubleSpacing = spacing * 2f;

            // 结构：每个质点向右、向下各一条
            for (int row = 0; row < _rows; row++)
            {
                for (int col = 0; col < _columns; col++)
                {
                    int self = IndexOfInternal(col, row);
                    if (col + 1 < _columns) Push(self, IndexOfInternal(col + 1, row), spacing, ClothConstraintType.Structural);
                    if (row + 1 < _rows) Push(self, IndexOfInternal(col, row + 1), spacing, ClothConstraintType.Structural);
                }
            }
            _structuralEnd = _cursor;

            // 剪切：每个单元格两条对角线
            if (Parameters.enableShear)
            {
                for (int row = 0; row < _rows - 1; row++)
                {
                    for (int col = 0; col < _columns; col++)
                    {
                        int self = IndexOfInternal(col, row);
                        if (col + 1 < _columns) Push(self, IndexOfInternal(col + 1, row + 1), diagonal, ClothConstraintType.Shear);
                        if (col - 1 >= 0) Push(self, IndexOfInternal(col - 1, row + 1), diagonal, ClothConstraintType.Shear);
                    }
                }
            }
            _shearEnd = _cursor;

            // 弯曲：隔一个邻居（跨两个间距）
            if (Parameters.enableBend)
            {
                for (int row = 0; row < _rows; row++)
                {
                    for (int col = 0; col < _columns; col++)
                    {
                        int self = IndexOfInternal(col, row);
                        if (col + 2 < _columns) Push(self, IndexOfInternal(col + 2, row), doubleSpacing, ClothConstraintType.Bend);
                        if (row + 2 < _rows) Push(self, IndexOfInternal(col, row + 2), doubleSpacing, ClothConstraintType.Bend);
                    }
                }
            }

            if (_cursor != _cA.Length)
            {
                throw new InvalidOperationException("约束数量与预计算不符：实际 " + _cursor + "，预计算 " + _cA.Length + "（拓扑计数回归）");
            }
        }

        void Push(int a, int b, float restLength, ClothConstraintType type)
        {
            int i = _cursor;
            if (i >= _cA.Length)
            {
                throw new InvalidOperationException("约束写入越界：拓扑计数公式与实际生成不一致");
            }
            _cA[i] = a;
            _cB[i] = b;
            _cRest[i] = restLength;
            _cursor = i + 1;
            _constraintView.Add(new DistanceConstraint(a, b, restLength, type));
        }

        void ValidateIndex(int index)
        {
            if (index < 0 || index >= _positions.Length)
            {
                throw new ArgumentOutOfRangeException("index", "质点索引 " + index + " 越界（0.." + (_positions.Length - 1) + "）");
            }
        }
    }
}
