// Copyright (c) 2026 PhysicsSimulation. MIT License.
using UnityEngine;

namespace PhysicsSimulation
{
    /// <summary>
    /// 半隐式（辛）欧拉积分 + 线性阻尼。
    ///   a     = force * inverseMass
    ///   v_new = (v + a * dt) / (1 + c_global * dt)      // 隐式除法：任意 c_global*dt 都不反号、不发散
    ///   v_new = v_new * clamp(1 - c_particle * dt, 0, 1) // 粒子级阻尼
    ///   x_new = x + v_new * dt
    /// 固定点（inverseMass == 0）自然不参与积分：加速度恒为零、速度恒保持原值。
    /// 顺序不可调换：先用新速度更新位置，才是"半隐式"；写成先位置后速度会退化成显式欧拉，等效阻尼变负。
    /// </summary>
    internal static class MassSpringIntegrator
    {
        internal static void Integrate(Particle particle, float dt, float globalDamping)
        {
            Vector3 acceleration = particle.force * particle.inverseMass;

            // 隐式阻尼：除以 (1 + c*dt)。c*dt -> 无穷大时速度趋近 0，永远不会越过 0 变成反向
            Vector3 velocity = (particle.velocity + acceleration * dt) / (1f + globalDamping * dt);

            // 粒子级阻尼：乘数必须钳到 [0,1]，否则 c_particle*dt > 1 会把速度反向、> 2 会发散
            float factor = 1f - particle.damping * dt;
            if (factor < 0f) factor = 0f;
            else if (factor > 1f) factor = 1f;
            velocity *= factor;

            particle.velocity = velocity;
            particle.position += velocity * dt;
        }
    }
}
