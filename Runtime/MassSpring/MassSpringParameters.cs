// Copyright (c) 2026 PhysicsSimulation. MIT License.
using UnityEngine;

namespace PhysicsSimulation
{
    /// <summary>
    /// 系统级参数：重力、全局阻尼、子步数、dt 钳制上限。纯数据载体，可整体替换。
    /// </summary>
    public sealed class MassSpringParameters
    {
        /// <summary>常规模拟的重力（米/秒²），默认 (0, -9.81, 0)。设为零向量可单测纯弹簧行为。</summary>
        public Vector3 gravity = new Vector3(0f, -9.81f, 0f);

        /// <summary>
        /// 全局线性阻尼 c_global（1/秒）。积分里写成除数 (1 + c*dt)，
        /// 因此任意大小的 c*dt 都只会衰减、绝不会把速度反号（显式写法 v -= c*v*dt 在 c*dt&gt;1 时会反号发散）。
        /// </summary>
        public float globalDamping = 0f;

        /// <summary>子步数：Step(dt) 把（钳制后的）dt 均分成这么多份逐步积分，是主要的稳定性手段。小于 1 时按 1 处理。</summary>
        public int substeps = 1;

        /// <summary>单步 dt 上限（秒），默认 1/15。超过它的 dt 会被钳制，避免掉帧/暂停后一次性灌进巨大时间步。</summary>
        public float maxDeltaTime = 1f / 15f;

        /// <summary>
        /// 碰撞皮肤厚度（米），默认 0.01。碰撞代理会把质点顶到几何体外至少这个距离，
        /// 免得质点恰好卡在表面上因浮点误差反复“穿入/顶出”。只在 <see cref="MassSpringSystem.Collisions"/> 非空时参与计算。
        /// </summary>
        public float collisionThickness = 0.01f;

        /// <summary>实际生效的子步数（至少 1）。</summary>
        public int EffectiveSubsteps
        {
            get { return substeps < 1 ? 1 : substeps; }
        }

        /// <summary>把请求的 dt 钳到 maxDeltaTime（maxDeltaTime &lt;= 0 时视为不钳制）。</summary>
        public float ClampDeltaTime(float dt)
        {
            if (maxDeltaTime > 0f && dt > maxDeltaTime) return maxDeltaTime;
            return dt;
        }
    }
}
