// Copyright (c) 2026 PhysicsSimulation. MIT License.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace PhysicsSimulation
{
    /// <summary>
    /// 质点弹簧系统：持有质点与弹簧，负责受力（重力 + 弹簧内力）与积分调度。
    /// 纯逻辑、确定性（无 Random / Time / 并行），因此可以直接在编辑器测试里断言闭式解。
    ///
    /// 扩展点（v1 只声明不使用，避免过度设计）：
    ///   - TODO(M2) IConstraint：刚性距离约束（PBD/XPBD 投影），让"不会拉长的绳子""不会塌的布"成为可能
    ///   - TODO(M2) IForceGenerator：风力 / 介质阻力一类的额外力源，与弹簧内力一起在 ApplyForces 里累积
    ///   - TODO(M3) 碰撞（球-球 / 球-平面）与粒子级睡眠
    /// </summary>
    public sealed class MassSpringSystem
    {
        // 判"长度是否足够大到能算单位向量"的阈值，避免零长度弹簧除零
        private const float LengthEpsilon = 1e-8f;

        private readonly List<Particle> _particles = new List<Particle>();
        private readonly List<Spring> _springs = new List<Spring>();

        /// <summary>系统参数（重力 / 全局阻尼 / 子步数 / dt 上限）。可直接改字段。</summary>
        public MassSpringParameters Parameters { get; } = new MassSpringParameters();

        /// <summary>质点只读视图（要改位置/速度请通过 Pin/Unpin 与 Step，或按索引拿引用改）。</summary>
        public IReadOnlyList<Particle> Particles { get { return _particles; } }

        /// <summary>弹簧只读视图。弹簧本身不可变，要改参数请重建。</summary>
        public IReadOnlyList<Spring> Springs { get { return _springs; } }

        /// <summary>
        /// 加一个质点，返回它的索引。质量非有限正数、位置含 NaN/Inf、阻尼为负都会抛
        /// <see cref="ArgumentOutOfRangeException"/>，且抛出前不会改动系统。
        /// </summary>
        public int AddParticle(Vector3 position, float mass, bool pinned = false, float damping = 0f)
        {
            // 构造即校验：不通过的粒子不会进入列表
            var particle = new Particle(position, mass, pinned, damping);
            _particles.Add(particle);
            return _particles.Count - 1;
        }

        /// <summary>
        /// 加一根弹簧，返回它的索引。<paramref name="restLength"/> 传 0 时自动取两端当前距离。
        /// 索引越界、两端相同、参数非法都会抛 <see cref="ArgumentOutOfRangeException"/>，且抛出前不会改动系统。
        /// </summary>
        public int AddSpring(int a, int b, float restLength, float stiffness, float damping)
        {
            ValidateParticleIndex(a, "a");
            ValidateParticleIndex(b, "b");
            if (a == b)
            {
                throw new ArgumentOutOfRangeException(nameof(b), "弹簧两端不能是同一个质点");
            }

            float length = restLength;
            if (length <= 0f)
            {
                length = (_particles[b].position - _particles[a].position).magnitude;
            }

            // 构造即校验，通过后才入列表
            var spring = new Spring(a, b, length, stiffness, damping);
            _springs.Add(spring);
            return _springs.Count - 1;
        }

        /// <summary>把质点钉死（inverseMass = 0）。</summary>
        public void Pin(int index)
        {
            ValidateParticleIndex(index, "index");
            Particle particle = _particles[index];
            particle.pinned = true;
            particle.inverseMass = 0f;
        }

        /// <summary>解开质点（inverseMass 恢复为 1/mass）。</summary>
        public void Unpin(int index)
        {
            ValidateParticleIndex(index, "index");
            Particle particle = _particles[index];
            particle.pinned = false;
            particle.inverseMass = 1f / particle.mass;
        }

        /// <summary>
        /// 重算所有质点的受力累积器：清零 -&gt; 重力 -&gt; 弹簧内力。
        /// 弹簧力严格按"等大反向 + 沿轴向"施加，因此内力不改变系统总动量。
        /// </summary>
        public void ApplyForces()
        {
            for (int i = 0; i < _particles.Count; i++)
            {
                _particles[i].force = Vector3.zero;
            }

            Vector3 gravity = Parameters.gravity;
            for (int i = 0; i < _particles.Count; i++)
            {
                Particle particle = _particles[i];
                particle.force += gravity * particle.mass;
            }

            for (int s = 0; s < _springs.Count; s++)
            {
                ApplySpringForce(_springs[s]);
            }
        }

        /// <summary>
        /// 推进一个时间步：dt 先钳到 <see cref="MassSpringParameters.maxDeltaTime"/>，再均分成子步逐步积分。
        /// dt 非有限正数会抛 <see cref="ArgumentOutOfRangeException"/>，且抛出前不改动系统。
        /// </summary>
        public void Step(float deltaTime)
        {
            if (!(deltaTime > 0f) || float.IsInfinity(deltaTime))
            {
                throw new ArgumentOutOfRangeException("deltaTime", "dt 必须是有限正数（秒）");
            }

            float span = Parameters.ClampDeltaTime(deltaTime);
            int substeps = Parameters.EffectiveSubsteps;
            float subStepDeltaTime = span / substeps;

            for (int n = 0; n < substeps; n++)
            {
                ApplyForces();
                for (int i = 0; i < _particles.Count; i++)
                {
                    MassSpringIntegrator.Integrate(_particles[i], subStepDeltaTime, Parameters.globalDamping);
                }
            }
        }

        /// <summary>回到建好时的初始布局：位置、速度、固定状态、粒子阻尼都复位，力累积器清零。</summary>
        public void ResetToInitial()
        {
            for (int i = 0; i < _particles.Count; i++)
            {
                Particle particle = _particles[i];
                particle.position = particle.initialPosition;
                particle.velocity = particle.initialVelocity;
                particle.pinned = particle.initialPinned;
                particle.damping = particle.initialDamping;
                particle.inverseMass = particle.initialPinned ? 0f : 1f / particle.mass;
                particle.force = Vector3.zero;
            }
        }

        /// <summary>当前所有质点里最大的速度大小，诊断用（编辑器 Dump State 会打）。</summary>
        public float MaxSpeed()
        {
            float max = 0f;
            for (int i = 0; i < _particles.Count; i++)
            {
                float speed = _particles[i].velocity.magnitude;
                if (speed > max) max = speed;
            }
            return max;
        }

        /// <summary>是否出现 NaN / Infinity，诊断用。</summary>
        public bool HasNonFiniteState()
        {
            for (int i = 0; i < _particles.Count; i++)
            {
                Vector3 p = _particles[i].position;
                Vector3 v = _particles[i].velocity;
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

        // F_b = (-k * (l - restLength) - c * ((v_b - v_a) · dir)) * dir ; F_a = -F_b
        private void ApplySpringForce(Spring spring)
        {
            Particle a = _particles[spring.a];
            Particle b = _particles[spring.b];

            Vector3 delta = b.position - a.position;
            float length = delta.magnitude;
            Vector3 direction = length > LengthEpsilon ? delta / length : Vector3.zero;

            float stretch = length - spring.restLength;
            float axialRelativeSpeed = Vector3.Dot(b.velocity - a.velocity, direction);
            float axialForce = -(spring.stiffness * stretch + spring.damping * axialRelativeSpeed);

            Vector3 force = axialForce * direction;
            b.force += force;
            a.force -= force;   // 等大反向，保证内力不改变总动量
        }

        private void ValidateParticleIndex(int index, string parameterName)
        {
            if (index < 0 || index >= _particles.Count)
            {
                throw new ArgumentOutOfRangeException(parameterName,
                    "质点索引 " + index + " 越界（当前质点数 " + _particles.Count + "）");
            }
        }
    }
}
