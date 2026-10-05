// Copyright (c) 2026 PhysicsSimulation. MIT License.
using System;
using UnityEngine;

namespace PhysicsSimulation
{
    /// <summary>
    /// 软体参数。力学骨架复用 v1.0.0 的质点弹簧系统（重力 + 结构/弯曲弹簧 + 隐式阻尼 + 子步），
    /// 在其之上叠一条**体积恢复力**：F_i = -(k·(V - V0) + c·dV/dt)·∇_i V，
    /// 其中 ∇_i V = (1/3)·Σ_{含 i 的三角形} 面积向量。
    ///
    /// 所有字段都在 <see cref="Validate"/> 里被检查；非法值在构造 <see cref="SoftBodySimulation"/> 时立刻抛，
    /// 不会悄悄跑出一个错的模拟。
    /// </summary>
    public sealed class SoftBodyParameters
    {
        // ---------------------------------------------------------------- 物体

        /// <summary>单个质点质量（千克）。体积力与弹簧力都是真力，所以质量会实实在在影响加速度。</summary>
        public float mass = 1f;

        /// <summary>常重力加速度。</summary>
        public Vector3 gravity = new Vector3(0f, -9.81f, 0f);

        /// <summary>全局速度阻尼，积分时除以 (1 + damping·dt)，只衰减不反号。</summary>
        public float damping = 0.5f;

        /// <summary>
        /// 速度上限（米/秒），0 表示不限。显式弹簧在极端刚度下会先把速度打飞，
        /// 这个限幅是"绝不出现 Infinity"的最后一道保险，同时让拉伸限幅能真正把形状拉回来。
        /// </summary>
        public float maxSpeed = 40f;

        // ---------------------------------------------------------------- 弹簧

        /// <summary>结构弹簧刚度 k（沿网格棱边）。</summary>
        public float springStiffness = 1200f;

        /// <summary>结构弹簧轴向阻尼 c，抑制"弹一下一直抖"。</summary>
        public float springDamping = 6f;

        /// <summary>弯曲弹簧刚度 k（共边两侧的对面顶点之间）。为 0 时不生成弯曲弹簧。</summary>
        public float bendStiffness = 150f;

        /// <summary>弯曲弹簧轴向阻尼 c。</summary>
        public float bendDamping = 2f;

        // ---------------------------------------------------------------- 体积

        /// <summary>体积恢复刚度 k（力 / 体积误差）。0 表示关掉体积约束，退化成一张"会抖的壳"。</summary>
        public float volumeStiffness = 4000f;

        /// <summary>体积阻尼 c（力 / 体积变化率），吃掉恢复过程中的振荡。</summary>
        public float volumeDamping = 20f;

        // ---------------------------------------------------------------- 积分

        /// <summary>一次 Step 内切几个子步。显式积分的稳定性几乎全押在这里。</summary>
        public int substeps = 4;

        /// <summary>单帧 dt 的上限，防止卡顿一帧把软体炸飞。</summary>
        public float maxDeltaTime = 1f / 15f;

        // ---------------------------------------------------------------- 构建与保险

        /// <summary>顶点焊接容差（米）：距离小于它的网格顶点视为同一个质点。</summary>
        public float weldTolerance = 1e-4f;

        /// <summary>是否启用结构弹簧的拉伸限幅（位置级投影，每子步多轮扫描）。</summary>
        public bool enableStretchLimit = true;

        /// <summary>结构弹簧最大 长度/静止长度。必须 >= 1。</summary>
        public float maxStretchRatio = 2f;

        /// <summary>
        /// 碰撞皮肤厚度（米），默认 0.01。只有 <see cref="SoftBodySimulation.Collisions"/> 里有代理时才会用到。
        /// 0 合法（质点贴表面），但容易因浮点误差在地面上反复“穿入/顶出”，所以默认留一层薄皮肤。
        /// </summary>
        public float collisionThickness = 0.01f;

        /// <summary>把外部 dt 钳到 [0, maxDeltaTime]。</summary>
        public float ClampDeltaTime(float dt)
        {
            if (dt <= 0f) return 0f;
            return dt < maxDeltaTime ? dt : maxDeltaTime;
        }

        /// <summary>实际生效的子步数（至少 1）。</summary>
        public int EffectiveSubsteps { get { return substeps < 1 ? 1 : substeps; } }

        /// <summary>
        /// 校验参数。任何一项不合法都抛 <see cref="ArgumentException"/>，消息里写清楚是哪一项、当前值是多少。
        /// </summary>
        public void Validate()
        {
            if (!(mass > 0f) || float.IsInfinity(mass))
                throw new ArgumentException("mass 必须是有限正数，当前 " + mass, "mass");
            if (!IsFinite(gravity))
                throw new ArgumentException("gravity 必须是有限向量，当前 " + gravity, "gravity");
            if (!(damping >= 0f) || float.IsInfinity(damping))
                throw new ArgumentException("damping 必须是有限非负数，当前 " + damping, "damping");
            if (!(maxSpeed >= 0f) || float.IsInfinity(maxSpeed))
                throw new ArgumentException("maxSpeed 必须是有限非负数（0 表示不限），当前 " + maxSpeed, "maxSpeed");

            if (!(springStiffness >= 0f) || float.IsInfinity(springStiffness))
                throw new ArgumentException("springStiffness 必须是有限非负数，当前 " + springStiffness, "springStiffness");
            if (!(springDamping >= 0f) || float.IsInfinity(springDamping))
                throw new ArgumentException("springDamping 必须是有限非负数，当前 " + springDamping, "springDamping");
            if (!(bendStiffness >= 0f) || float.IsInfinity(bendStiffness))
                throw new ArgumentException("bendStiffness 必须是有限非负数，当前 " + bendStiffness, "bendStiffness");
            if (!(bendDamping >= 0f) || float.IsInfinity(bendDamping))
                throw new ArgumentException("bendDamping 必须是有限非负数，当前 " + bendDamping, "bendDamping");

            if (!(volumeStiffness >= 0f) || float.IsInfinity(volumeStiffness))
                throw new ArgumentException("volumeStiffness 必须是有限非负数，当前 " + volumeStiffness, "volumeStiffness");
            if (!(volumeDamping >= 0f) || float.IsInfinity(volumeDamping))
                throw new ArgumentException("volumeDamping 必须是有限非负数，当前 " + volumeDamping, "volumeDamping");

            if (substeps < 1)
                throw new ArgumentException("substeps 至少为 1，当前 " + substeps, "substeps");
            if (!(maxDeltaTime > 0f) || float.IsInfinity(maxDeltaTime))
                throw new ArgumentException("maxDeltaTime 必须是有限正数，当前 " + maxDeltaTime, "maxDeltaTime");
            if (!(weldTolerance > 0f) || float.IsInfinity(weldTolerance))
                throw new ArgumentException("weldTolerance 必须是有限正数，当前 " + weldTolerance, "weldTolerance");
            if (!(maxStretchRatio >= 1f) || float.IsInfinity(maxStretchRatio))
                throw new ArgumentException("maxStretchRatio 必须是不小于 1 的有限数，当前 " + maxStretchRatio, "maxStretchRatio");
            if (!(collisionThickness >= 0f) || float.IsInfinity(collisionThickness))
                throw new ArgumentException("collisionThickness 必须是非负有限数（米），当前 " + collisionThickness, "collisionThickness");
        }

        static bool IsFinite(Vector3 v)
        {
            return !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z)
                && !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);
        }
    }
}
