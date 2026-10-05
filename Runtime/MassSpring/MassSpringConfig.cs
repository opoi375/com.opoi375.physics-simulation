// Copyright (c) 2026 PhysicsSimulation. MIT License.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace PhysicsSimulation
{
    /// <summary>
    /// 可序列化的质点配置（Inspector 里填的那一行）。
    /// </summary>
    [Serializable]
    public sealed class MassSpringParticleData
    {
        [Tooltip("初始世界位置（米）——也是 Reset To Initial Layout 回到的位置")]
        public Vector3 position;

        [Tooltip("质量（千克），必须是有限正数")]
        public float mass = 1f;

        [Tooltip("是否固定点（固定 = inverseMass 归零，不受任何力影响）")]
        public bool pinned;

        [Tooltip("初速度（米/秒）")]
        public Vector3 initialVelocity;

        [Tooltip("粒子级线性阻尼 c_particle（1/秒），乘数会被钳到 [0,1]")]
        public float damping;
    }

    /// <summary>
    /// 可序列化的弹簧配置（Inspector 里填的那一行）。
    /// </summary>
    [Serializable]
    public sealed class MassSpringSpringData
    {
        [Tooltip("端点 A 的质点索引（链式建模里它是上面那一节）")]
        public int a;

        [Tooltip("端点 B 的质点索引")]
        public int b;

        [Tooltip("刚度 k（牛/米）。越大越硬，也越容易数值发散——必须配合子步")]
        public float stiffness = 200f;

        [Tooltip("轴向阻尼 c（牛·秒/米）")]
        public float damping = 1f;

        [Tooltip("原长（米）；填 0 或负数表示自动取两端初始距离")]
        public float restLength;
    }

    /// <summary>
    /// 把 Inspector 的配置翻译成 <see cref="MassSpringSystem"/>。纯静态、无场景依赖，可直接在编辑器测试里验证。
    /// </summary>
    public static class MassSpringBuilder
    {
        /// <summary>
        /// 构建系统。<paramref name="particles"/> / <paramref name="springs"/> 为 null 时按空集合处理；
        /// 索引越界、质量非正等非法配置会原样抛出 <see cref="ArgumentOutOfRangeException"/>。
        /// </summary>
        public static MassSpringSystem Build(
            IList<MassSpringParticleData> particles,
            IList<MassSpringSpringData> springs,
            Vector3 gravity,
            float globalDamping,
            int substeps,
            float maxDeltaTime)
        {
            var system = new MassSpringSystem();
            system.Parameters.gravity = gravity;
            system.Parameters.globalDamping = globalDamping;
            system.Parameters.substeps = substeps;
            system.Parameters.maxDeltaTime = maxDeltaTime;

            if (particles != null)
            {
                for (int i = 0; i < particles.Count; i++)
                {
                    MassSpringParticleData data = particles[i];
                    if (data == null)
                    {
                        throw new ArgumentOutOfRangeException("particles[" + i + "]", "质点配置不能为空引用");
                    }

                    int index = system.AddParticle(data.position, data.mass, data.pinned, data.damping);
                    Particle particle = system.Particles[index];
                    particle.velocity = data.initialVelocity;
                    // 让 ResetToInitial 回到"配置里写的"速度，而不是零速度
                    particle.CaptureInitialLayout();
                }
            }

            if (springs != null)
            {
                for (int i = 0; i < springs.Count; i++)
                {
                    MassSpringSpringData data = springs[i];
                    if (data == null)
                    {
                        throw new ArgumentOutOfRangeException("springs[" + i + "]", "弹簧配置不能为空引用");
                    }

                    // restLength <= 0 时，MassSpringSystem.AddSpring 会自动取两端当前（初始）距离
                    system.AddSpring(data.a, data.b, data.restLength, data.stiffness, data.damping);
                }
            }

            return system;
        }
    }
}
