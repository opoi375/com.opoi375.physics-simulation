// Copyright (c) 2026 PhysicsSimulation. MIT License.
using UnityEngine;

namespace PhysicsSimulation
{
    /// <summary>
    /// 把一个 Transform 绑到 <see cref="MassSpringBehaviour"/> 的某个质点上，在 LateUpdate 同步位置。
    /// 纯可视化用：求解器不依赖它，删掉它模拟结果完全不变。
    /// </summary>
    [DefaultExecutionOrder(200)]
    [AddComponentMenu("Physics Simulation/Mass Spring Particle Link")]
    public sealed class MassSpringParticleLink : MonoBehaviour
    {
        [Tooltip("提供模拟的组件；留空则自动取父对象上的 MassSpringBehaviour")]
        public MassSpringBehaviour target;

        [Tooltip("绑定的质点索引")]
        public int particleIndex;

        [Tooltip("相对质点位置的偏移（米）")]
        public Vector3 offset;

        private void OnEnable()
        {
            if (target == null)
            {
                target = GetComponentInParent<MassSpringBehaviour>();
            }
        }

        private void LateUpdate()
        {
            if (target == null) return;
            MassSpringSystem system = target.System;
            if (system == null) return;
            if (particleIndex < 0 || particleIndex >= system.Particles.Count) return;

            transform.position = system.Particles[particleIndex].position + offset;
        }
    }
}
