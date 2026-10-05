// Copyright (c) 2026 PhysicsSimulation. MIT License.
using System;
using UnityEngine;

namespace PhysicsSimulation
{
    /// <summary>
    /// 质点：位置 / 速度 / 质量 / 受力累积器。<see cref="inverseMass"/> 为 0 表示固定点（不参与积分）。
    /// 纯逻辑类型，不依赖 MonoBehaviour、Time、场景。
    /// </summary>
    public sealed class Particle
    {
        /// <summary>世界位置（米）。</summary>
        public Vector3 position;

        /// <summary>速度（米/秒）。</summary>
        public Vector3 velocity;

        /// <summary>当前这一步累积到的合力（牛）。由 <see cref="MassSpringSystem.ApplyForces"/> 每步重算。</summary>
        public Vector3 force;

        /// <summary>质量（千克），必须为有限正数。</summary>
        public float mass;

        /// <summary>质量倒数 1/m；0 表示固定点。</summary>
        public float inverseMass;

        /// <summary>粒子级线性阻尼 c_particle（1/秒）。积分时乘数 (1 - c*dt) 会被钳到 [0,1]。</summary>
        public float damping;

        /// <summary>是否被固定（等价于 inverseMass == 0）。</summary>
        public bool pinned;

        // 初始快照，供 ResetToInitial 使用
        internal Vector3 initialPosition;
        internal Vector3 initialVelocity;
        internal bool initialPinned;
        internal float initialDamping;

        /// <summary>
        /// 建一个质点。<paramref name="mass"/> 为 0 / 负数 / NaN / 无穷时抛 <see cref="ArgumentOutOfRangeException"/>。
        /// </summary>
        public Particle(Vector3 position, float mass, bool pinned = false, float damping = 0f)
        {
            if (float.IsNaN(position.x) || float.IsNaN(position.y) || float.IsNaN(position.z)
                || float.IsInfinity(position.x) || float.IsInfinity(position.y) || float.IsInfinity(position.z))
            {
                throw new ArgumentOutOfRangeException(nameof(position), "质点初始位置必须是有限值");
            }
            if (!(mass > 0f) || float.IsInfinity(mass))
            {
                throw new ArgumentOutOfRangeException(nameof(mass), "质量必须是有限正数（千克）");
            }
            if (!(damping >= 0f) || float.IsInfinity(damping))
            {
                throw new ArgumentOutOfRangeException(nameof(damping), "粒子级阻尼必须是非负有限值（1/秒）");
            }

            this.position = position;
            velocity = Vector3.zero;
            force = Vector3.zero;
            this.mass = mass;
            this.pinned = pinned;
            this.damping = damping;
            inverseMass = pinned ? 0f : 1f / mass;

            initialPosition = position;
            initialVelocity = Vector3.zero;
            initialPinned = pinned;
            initialDamping = damping;
        }

        /// <summary>由外部改动初始位置后调用，让 ResetToInitial 回到最新布局。</summary>
        public void CaptureInitialLayout()
        {
            initialPosition = position;
            initialVelocity = velocity;
            initialPinned = pinned;
            initialDamping = damping;
        }
    }
}
