// Copyright (c) 2026 PhysicsSimulation. MIT License.
using System;
using UnityEngine;

namespace PhysicsSimulation
{
    /// <summary>
    /// 位置基流体（Position Based Fluids, Macklin &amp; Müller 2013）。
    ///
    /// 一个子步的次序（次序本身就是算法的一部分，写错一步就会"看着像水但守恒律全破"）：
    /// <code>
    ///   1. v += Δt·g                        外力
    ///   2. x̂ = x + Δt·v                     预测位置
    ///   3. 用 x̂ 建邻居表                    （邻居必须按预测位置找，不是当前位置）
    ///   4. 碰撞投影 x̂                       先投影一次，密度才不会被穿进墙里的粒子算歪
    ///   5. 反复：算 ρ、Δλ，把 x̂ 投影到 ρ=ρ0  位置投影，不是力 —— 所以无条件稳定
    ///   6. 再碰一次 x̂                       硬保证"没有粒子埋在碰撞体里"
    ///   7. v = (x̂ − x)/Δt                   速度从位置差回算，投影带出来的法向响应就自然有了
    ///   8. 涡度约束、XSPH 粘度              都只改 v，不改 x
    ///   9. x = x̂
    /// </code>
    /// 与布料/软体同一条契约：**模拟空间是本组件的局部空间**，Transform 只是摆位；
    /// 世界空间的碰撞代理会先换算到局部再投影（见 <see cref="SetSimulationToWorld"/>）。
    /// </summary>
    public sealed class FluidSimulation
    {
        readonly FluidParameters _parameters;
        readonly Vector3[] _position;
        readonly Vector3[] _velocity;
        readonly Vector3[] _predicted;
        readonly float[] _density;
        /// <summary>
        /// 单轮迭代里一个粒子最多被推走 <c>kernelRadius × 本系数</c>。
        ///
        /// 为什么必须有：密度约束在稀疏区是**不可满足**的（两个孤立粒子的密度永远到不了 ρ0），
        /// 而投影会把不可满足的约束推到极限 —— 实测两点 5 cm 时单轮修正约 1.6 m，粒子直接
        /// 互相穿过去，整坨水一帧甩没。限幅削掉的就是这一类极端值；正常水体里单轮修正只有
        /// 毫米级，碰不到这条线。参考实现同样靠"默认钳掉拉力 + 修正限幅"两条一起用。
        /// </summary>
        // 可调是为了能复现调参扫描；实测 0.02 ~ 0.25 的稳定段很宽（4 秒后 v_max 0.83~1.02 m/s），
        // 所以它只是"别把水甩上天"的保险丝，不是流体的主参数。置 0 或负数 = 关掉护栏。
        public static float MaxCorrectionPerIterationFactor = 0.25f;

        /// <summary>本子步里每个粒子被碰撞投影推出去的方向累加（模拟空间），用于抹掉伪弹跳。</summary>
        readonly Vector3[] _contactNormal;

        readonly float[] _lambda;
        readonly Vector3[] _deltaLambda;        // 求解期暂存：s·Σ Δλ_k ∇W
        readonly Vector3[] _vorticity;
        readonly Vector3[] _velocityDelta;

        readonly FluidNeighborSearch _search;

        /// <summary>按当前 h 预算好系数的核集合（热循环用它，别每次求值都去算归一化常数）。</summary>
        FluidKernelSet _k;

        void RefreshKernels()
        {
            float h = _parameters.kernelRadius;
            if (_k.H != h) _k = new FluidKernelSet(h);
        }

        readonly CollisionSet _collisions = new CollisionSet();

        Matrix4x4 _localToWorld = Matrix4x4.identity;
        Matrix4x4 _worldToLocal = Matrix4x4.identity;
        bool _spaceIsIdentity = true;

        public FluidParameters Parameters { get { return _parameters; } }
        public int ParticleCount { get { return _count; } }
        public float ParticleMass { get { return _parameters.ParticleMass; } }
        public CollisionSet Collisions { get { return _collisions; } }

        /// <summary>当前粒子位置（渲染直接读，别改）。</summary>
        public Vector3[] Positions { get { return _position; } }

        public int MaxNeighborDegree { get { return _search.MaxDegree; } }
        public float AverageNeighborDegree { get { return _search.AverageDegree; } }

        readonly int _count;

        public FluidSimulation(FluidParameters parameters, FluidParticleSet particles)
        {
            if (parameters == null) throw new ArgumentNullException("parameters");
            parameters.Validate();
            if (particles.Positions == null)
                throw new ArgumentException("粒子的位置数组不能为空", "particles");

            int count = particles.Count;
            if (count < 0) throw new ArgumentOutOfRangeException("particles", "粒子数不能为负");
            if (count > parameters.maxParticles)
                throw new ArgumentOutOfRangeException("particles",
                    "粒子数 " + count + " 超过 maxParticles = " + parameters.maxParticles
                    + "；请调大 maxParticles 或加大 particleSpacing（粒子数随间距三次方增长）");

            _parameters = parameters;
            _count = count;
            _position = new Vector3[count];
            _velocity = new Vector3[count];
            _predicted = new Vector3[count];
            _density = new float[count];
            _lambda = new float[count];
            _contactNormal = new Vector3[count];
            _deltaLambda = new Vector3[count];
            _vorticity = new Vector3[count];
            _velocityDelta = new Vector3[count];

            for (int i = 0; i < count; i++)
            {
                _position[i] = particles.Positions[i];
                _velocity[i] = particles.Velocities != null && i < particles.Velocities.Length
                    ? particles.Velocities[i] : Vector3.zero;
                RequireFinite(_position[i], i);
            }

            RefreshKernels();
            _search = new FluidNeighborSearch(Mathf.Max(1, count));
            _search.Build(_position, count, parameters.kernelRadius);
            ComputeDensities(_position);
        }

        static void RequireFinite(Vector3 v, int index)
        {
            if (float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z)
                || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z))
                throw new ArgumentException("第 " + index + " 个粒子的初始位置不是有限值", "particles");
        }

        /// <summary>设置"模拟空间 → 世界空间"的变换；世界空间的碰撞代理会按它的逆变换投影。</summary>
        public void SetSimulationToWorld(Matrix4x4 localToWorld)
        {
            _localToWorld = localToWorld;
            _worldToLocal = localToWorld.inverse;
            _spaceIsIdentity = localToWorld == Matrix4x4.identity;
        }

        public Vector3 GetPosition(int i) { return _position[i]; }
        public Vector3 GetVelocity(int i) { return _velocity[i]; }
        public float GetDensity(int i) { return _density[i]; }
        public float GetLambda(int i) { return _lambda[i]; }
        public int NeighborDegree(int i) { return _search.Degree(i); }
        public int NeighborAt(int i, int slot) { return _search.NeighborAt(i, slot); }

        /// <summary>用当前邻居表即时算第 i 个粒子的密度（含自身项）。诊断与测试用。</summary>
        public float ComputeDensity(int i)
        {
            if (i < 0 || i >= _count) return 0f;
            return DensityAt(_position, i);
        }

        /// <summary>重建邻居表到当前位置（Step 结束时自动调；外部改了位置后要自己调）。</summary>
        public void RebuildNeighborTable()
        {
            _search.Build(_position, _count, _parameters.kernelRadius);
            ComputeDensities(_position);
        }

        float DensityAt(Vector3[] source, int i)
        {
            float m = _parameters.ParticleMass;
            float sum = _k.Poly6(0f);                                   // 自身项
            Vector3 pi = source[i];
            int[] starts = _search.Starts, indices = _search.Indices;
            for (int slot = starts[i], end = starts[i + 1]; slot < end; slot++)
            {
                var d = pi - source[indices[slot]];
                sum += _k.Poly6(d.sqrMagnitude);
            }
            return m * sum;
        }

        void ComputeDensities(Vector3[] source)
        {
            for (int i = 0; i < _count; i++) _density[i] = DensityAt(source, i);
        }

        // ================================================================ 主循环

        public void Step(float deltaTime)
        {
            if (_count == 0) return;
            if (!(deltaTime > 0f)) return;            // dt = 0 或负数是空操作：回放/定步长不许偷偷动位置

            var p = _parameters;
            RefreshKernels();                       // 有人中途改了 kernelRadius 也要跟得上
            // 秒级 dt（进 Play 第一帧 / 卡帧）必须钳住：子步长直接进重力积分与密度投影，
            // 不钳的话一帧就能把水甩到几百米外，之后再也回不来。
            float subStep = p.ClampDeltaTime(deltaTime) / p.substeps;
            for (int s = 0; s < p.substeps; s++) Substep(subStep, p);

            // 位置定下来了，把邻居表与密度对齐到当前位置，外部读到的诊断量才不会是"求解中途的"
            _search.Build(_position, _count, p.kernelRadius);
            ComputeDensities(_position);
        }

        void Substep(float dt, FluidParameters p)
        {
            float m = p.ParticleMass;
            float kr = p.kernelRadius;
            float rho0 = p.restDensity;
            float invRho0 = 1f / rho0;
            // 量纲这条链只能这么走：
            //   ∇_{p_k} C_i = (m/ρ0)·∇W_ik        → 单位 1/m
            //   λ_i = −C_i / Σ_k|∇_k C_i|²        → 单位 m²
            //   Δx_i = (m/ρ0)·Σ_j (λ_i+λ_j)∇W_ij  → 单位 m
            // 早先这里抄成了论文的 s = h³/m 记号（λ 乘 1/h⁶、Δx 乘 h³/m），量纲不对，
            // 净效果是每轮迭代只推几十微米：0.9 m 高的一柱水落在地板上直接压成
            // 一张单层饼（实测密度 7.11 倍静止值）。见 ColumnOnFloor_SpreadsSideways。
            float gradScale = m * invRho0;                       // ∇C 的系数，也用在 Δx 上
            float relax = 1f / (1f + Mathf.Max(0f, p.complianceAlpha));

            // 接触法向是"本子步"的量，先清干净再累加
            if (_collisions.Count > 0) Array.Clear(_contactNormal, 0, _count);

            // 1 + 2. 外力与预测位置
            // 速度先钳再预测：单子步平流位移 = |v|·dt，不钳的话它就能大于薄碰撞体的半厚，
            // 粒子越过板中线后被从**另一面**弹出（实测穿地板）。见 FluidParameters.maxSpeed。
            float speedCap = p.maxSpeed;
            float speedCapSqr = speedCap * speedCap;
            for (int i = 0; i < _count; i++)
            {
                Vector3 v = _velocity[i] + p.gravity * dt;
                if (speedCap > 0f)
                {
                    float sqr = v.sqrMagnitude;
                    if (sqr > speedCapSqr) v *= speedCap / Mathf.Sqrt(sqr);
                }
                _velocity[i] = v;
                _predicted[i] = _position[i] + v * dt;
            }

            // 3. 邻居按预测位置建
            _search.Build(_predicted, _count, kr);

            // 4. 先投影一次碰撞
            ResolveCollisions(p);

            // 5. 密度约束投影
            int[] starts = _search.Starts, indices = _search.Indices;
            Vector3[] pos = _predicted;                       // 局部别名：少一层字段寻址，也少重复的边界检查
            float[] density = _density, lambda = _lambda;
            Vector3[] deltaLambda = _deltaLambda;
            FluidKernelSet k = _k;                            // struct 拷进局部变量，循环里就是纯值访问

            for (int iteration = 0; iteration < p.solverIterations; iteration++)
            {
                // 5a. 密度 → C → Δλ（密度与梯度共用同一趟邻居遍历，别再走第二遍）
                for (int i = 0; i < _count; i++)
                {
                    float d0 = k.Poly6(0f);
                    Vector3 pi = pos[i];
                    Vector3 gradientSelf = Vector3.zero;
                    float sumSquared = 0f;
                    int end = starts[i + 1];

                    for (int slot = starts[i]; slot < end; slot++)
                    {
                        int j = indices[slot];
                        var delta = pi - pos[j];
                        d0 += k.Poly6(delta.sqrMagnitude);
                        var gradient = k.SpikyGradient(delta, delta.magnitude) * gradScale;
                        // ∇_{p_j} C_i = −(m/ρ0)∇W
                        sumSquared += gradient.sqrMagnitude;
                        gradientSelf += gradient;
                    }
                    density[i] = m * d0;
                    float constraint = density[i] * invRho0 - 1f;
                    // ∇_{p_i} C_i = (m/ρ0) Σ_j ∇W，它的平方要和邻居那一项一起进分母
                    sumSquared += gradientSelf.sqrMagnitude;

                    float denominator = sumSquared + p.ComplianceEpsilon;
                    float lam = denominator > 1e-20f ? -constraint * relax / denominator : 0f;
                    // 钳的是**正** λ（稀疏 → 吸引）；压缩侧 λ<0 全部保留。
                    if (p.clampTensileLambda && lam > 0f) lam = 0f;
                    lambda[i] = lam;
                }

                // 5b. 位置修正：Δx_i = (m/ρ0) Σ_j (λ_i + λ_j) ∇W_ij
                // 用「成对相加」而不是「只累邻居 λ」：两个对称地互相压缩的粒子如果
                // 写成 λ_j − λ_i 这种差分形式，λ 相等时修正恒等于 0，谁也推不开谁。
                for (int i = 0; i < _count; i++)
                {
                    Vector3 correction = Vector3.zero;
                    Vector3 pi = pos[i];
                    float li = lambda[i];
                    int end = starts[i + 1];
                    for (int slot = starts[i]; slot < end; slot++)
                    {
                        int j = indices[slot];
                        var delta = pi - pos[j];
                        correction += k.SpikyGradient(delta, delta.magnitude) * (li + lambda[j]);
                    }
                    Vector3 move = correction * gradScale;
                    float len = move.magnitude;
                    float rail = kr * MaxCorrectionPerIterationFactor;
                    deltaLambda[i] = len > rail ? move * (rail / len) : move;      // 见常量注释
                }
                for (int i = 0; i < _count; i++) pos[i] += deltaLambda[i];
            }

            // 6. 再投影一次：这一步之后"没有粒子埋在碰撞体里"是硬保证
            ResolveCollisions(p);

            // 7. 速度从位置差回算
            float invDt = 1f / dt;
            for (int i = 0; i < _count; i++)
                _velocity[i] = (_predicted[i] - _position[i]) * invDt;

            // 7b. 水与刚体之间不许弹跳：把接触法向那一份速度抹掉（切向保留 ⇒ 照样摊开）。
            RemoveBounce();

            // 8a. 涡度约束：把被数值耗散抹掉的旋转补回来
            if (p.vorticityEpsilon > 0f) ApplyVorticity(dt, p, m, invRho0, kr);

            // 8b. XSPH 粘度：速度按核加权插值
            if (p.xsphViscosity > 0f) ApplyXSPH(p, m, kr);

            // 9. 推进
            for (int i = 0; i < _count; i++) _position[i] = _predicted[i];
        }

        void ApplyVorticity(float dt, FluidParameters p, float m, float invRho0, float kr)
        {
            int[] starts = _search.Starts, indices = _search.Indices;
            FluidKernelSet k = _k;

            // ω_i = Σ_j (m/ρ_j) (v_j − v_i) × ∇W(x̂_i − x̂_j)
            for (int i = 0; i < _count; i++)
            {
                Vector3 omega = Vector3.zero;
                Vector3 pi = _predicted[i];
                Vector3 vi = _velocity[i];
                int end = starts[i + 1];
                for (int slot = starts[i]; slot < end; slot++)
                {
                    int j = indices[slot];
                    var delta = pi - _predicted[j];
                    var gradient = k.SpikyGradient(delta, delta.magnitude);
                    float weight = m / Mathf.Max(1e-8f, _density[j]);
                    omega += Vector3.Cross(_velocity[j] - vi, gradient) * weight;
                }
                _vorticity[i] = omega;
            }

            // η_i = ∇|ω| 归一化；v += Δt·ε·(η × ω)
            for (int i = 0; i < _count; i++)
            {
                float magnitudeI = _vorticity[i].magnitude;
                Vector3 eta = Vector3.zero;
                Vector3 pi = _predicted[i];
                int end = starts[i + 1];
                for (int slot = starts[i]; slot < end; slot++)
                {
                    int j = indices[slot];
                    var delta = pi - _predicted[j];
                    var gradient = k.SpikyGradient(delta, delta.magnitude);
                    float weight = m / Mathf.Max(1e-8f, _density[j]);
                    eta += gradient * ((_vorticity[j].magnitude - magnitudeI) * weight);
                }
                if (eta.sqrMagnitude < 1e-16f) continue;
                eta.Normalize();
                _velocity[i] += Vector3.Cross(eta, _vorticity[i]) * (dt * p.vorticityEpsilon);
            }
        }

        void ApplyXSPH(FluidParameters p, float m, float kr)
        {
            for (int i = 0; i < _count; i++)
            {
                Vector3 sum = Vector3.zero;
                Vector3 pi = _predicted[i];
                Vector3 vi = _velocity[i];
                int[] starts = _search.Starts, indices = _search.Indices;
                int end = starts[i + 1];
                for (int slot = starts[i]; slot < end; slot++)
                {
                    int j = indices[slot];
                    var d = pi - _predicted[j];
                    float weight = m / Mathf.Max(1e-8f, _density[j]) * _k.Poly6(d.sqrMagnitude);
                    sum += (_velocity[j] - vi) * weight;
                }
                _velocityDelta[i] = sum * p.xsphViscosity;
            }
            for (int i = 0; i < _count; i++) _velocity[i] += _velocityDelta[i];
        }

        void ResolveCollisions(FluidParameters p)
        {
            if (_collisions.Count == 0) return;
            CollisionPass.ResolvePositions(_collisions, _predicted, p.collisionThickness,
                _localToWorld, _worldToLocal, _spaceIsIdentity, _contactNormal);
        }

        /// <summary>
        /// 抹掉接触法向的速度分量。
        ///
        /// 为什么必须有：密度修正是**位置投影**，物理上它是约束不是冲量；但第 7 步用
        /// (x̂ − x)/dt 反算速度，于是"粒子被推进地板 1.25 cm 再被投出来"被读成了
        /// 1.5 m/s 的离地速度。每子步一次，水就一帧一帧把自己弹射上天 ——
        /// 实测只给一块地板、水柱静止放上，4 秒后 v_max = 35 m/s、包围盒 22×64×22 m、
        /// 平均密度比掉到 0.28（整坨水变成一朵散开的云）。加了这条之后同一用例
        /// 稳定在一层浅水池里。见 FluidSimulationTests 的 TankOfBoxes_...。
        /// </summary>
        void RemoveBounce()
        {
            if (_collisions.Count == 0) return;
            for (int i = 0; i < _count; i++)
            {
                Vector3 n = _contactNormal[i];
                if (n.sqrMagnitude < 1e-12f) continue;
                n = n.normalized;
                float vn = Vector3.Dot(_velocity[i], n);
                if (vn > 0f) _velocity[i] -= n * vn;          // 只削离壁分量，压向壁的那一份由投影处理
            }
        }

        // ================================================================ 诊断量

        public float TotalMass() { return _count * _parameters.ParticleMass; }

        /// <summary>
        /// 平均涡量 |ω|。注意 _vorticity 只在涡度约束**开着**时才被填：
        /// 关掉时这里读到的是上一帧/零，别拿它当"当前速度场的涡量"用
        /// （要看当前场的涡量，把 vorticityEpsilon 暂时调成极小正数跑一步即可）。
        /// </summary>
        public float AverageVorticity()
        {
            if (_count == 0) return 0f;
            float sum = 0f;
            for (int i = 0; i < _count; i++) sum += _vorticity[i].magnitude;
            return sum / _count;
        }

        public float MaxVorticity()
        {
            float max = 0f;
            for (int i = 0; i < _count; i++) max = Mathf.Max(max, _vorticity[i].magnitude);
            return max;
        }

        public float TotalKineticEnergy()
        {
            float m = _parameters.ParticleMass;
            float sum = 0f;
            for (int i = 0; i < _count; i++) sum += 0.5f * m * _velocity[i].sqrMagnitude;
            return sum;
        }

        public Vector3 CenterOfMass()
        {
            if (_count == 0) return Vector3.zero;
            Vector3 sum = Vector3.zero;
            for (int i = 0; i < _count; i++) sum += _position[i];
            return sum / _count;
        }

        /// <summary>
        /// 位置/速度/密度里只要出现 NaN 或 Inf 就返回 true。托管求解器跑长龙（几千步）
        /// 之后最容易在这里暴露问题，基准与长跑用例都断言它。
        /// </summary>
        public bool HasNonFiniteState()
        {
            for (int i = 0; i < _count; i++)
            {
                if (!IsFinite(_position[i]) || !IsFinite(_velocity[i]) || !IsFiniteNumber(_density[i]))
                    return true;
            }
            return false;
        }

        static bool IsFinite(Vector3 v)
        {
            return IsFiniteNumber(v.x) && IsFiniteNumber(v.y) && IsFiniteNumber(v.z);
        }

        static bool IsFiniteNumber(float f)
        {
            return !float.IsNaN(f) && !float.IsInfinity(f);
        }

        public Bounds Bounds()
        {
            if (_count == 0) return new Bounds(Vector3.zero, Vector3.zero);
            var bounds = new Bounds(_position[0], Vector3.zero);
            for (int i = 1; i < _count; i++) bounds.Encapsulate(_position[i]);
            return bounds;
        }
    }
}
