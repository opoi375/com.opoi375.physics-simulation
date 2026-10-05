// Copyright (c) 2026 PhysicsSimulation. MIT License.
using System;
using UnityEngine;

namespace PhysicsSimulation
{
    /// <summary>布料网格约束的类型。</summary>
    public enum ClothConstraintType
    {
        /// <summary>结构约束：上下左右相邻（决定布的面积与形状）。</summary>
        Structural = 0,
        /// <summary>剪切约束：格子的两条对角线（抵抗" parallelogram 变形"，布不斜塌）。</summary>
        Shear = 1,
        /// <summary>弯曲约束：隔一个的邻居（抵抗折叠，布不折成一条线）。</summary>
        Bend = 2
    }

    /// <summary>
    /// 一条距离约束：把两个质点的距离保持在 restLength 附近。
    /// 求解是位置投影（PBD/XPBD 思路），硬度与时间步解耦，因此比"把 k 调到很大的弹簧"稳得多。
    /// </summary>
    public sealed class DistanceConstraint
    {
        public readonly int a;
        public readonly int b;
        public readonly float restLength;
        public readonly ClothConstraintType type;

        public DistanceConstraint(int a, int b, float restLength, ClothConstraintType type)
        {
            this.a = a;
            this.b = b;
            this.restLength = restLength;
            this.type = type;
        }
    }

    /// <summary>
    /// 布料参数：网格尺寸、重力、质量、三类约束的硬度、子步与投影迭代、拉伸上限、碰撞厚度。
    /// 可直接贴进 <see cref="ClothBehaviour"/> 在 Inspector 里调。
    /// </summary>
    [Serializable]
    public sealed class ClothParameters
    {
        [Tooltip("网格列数（X 方向的质点数），至少 2")]
        public int columns = 16;

        [Tooltip("网格行数（Y 方向的质点数），至少 2")]
        public int rows = 16;

        [Tooltip("相邻质点的初始间距（米），必须为正")]
        public float spacing = 0.1f;

        [Tooltip("每个质点的质量（千克）")]
        public float mass = 1f;

        [Tooltip("模拟重力（米/秒²）")]
        public Vector3 gravity = new Vector3(0f, -9.81f, 0f);

        [Tooltip("速度衰减（1/秒），写成除数 (1 + d*dt)，永远不会把速度反号")]
        public float damping = 0.05f;

        [Tooltip("结构约束硬度 [0,1]")]
        public float structuralStiffness = 1f;

        [Tooltip("剪切约束硬度 [0,1]")]
        public float shearStiffness = 0.6f;

        [Tooltip("弯曲约束硬度 [0,1]")]
        public float bendStiffness = 0.2f;

        [Tooltip("是否启用剪切约束")]
        public bool enableShear = true;

        [Tooltip("是否启用弯曲约束")]
        public bool enableBend = true;

        [Tooltip("一个时间步内均分成多少子步（每子步都做一次完整投影）")]
        [Min(1)]
        public int substeps = 4;

        [Tooltip("每个子步内的约束投影迭代次数")]
        [Min(1)]
        public int iterations = 2;

        [Tooltip("单步 dt 上限（秒）")]
        public float maxDeltaTime = 1f / 15f;

        [Tooltip("拉伸上限比：任何约束的长度不得超过 restLength * 该值（兜底，防炸）")]
        public float maxStretchRatio = 2f;

        [Tooltip("碰撞厚度（米）：质点被推到障碍物表面外多远，相当于给布一层“皮毛”，避免穿模与抖动")]
        public float collisionThickness = 0.01f;

        /// <summary>把请求的 dt 钳到 maxDeltaTime（maxDeltaTime &lt;= 0 视为不钳制）。</summary>
        public float ClampDeltaTime(float dt)
        {
            if (maxDeltaTime > 0f && dt > maxDeltaTime) return maxDeltaTime;
            return dt;
        }

        /// <summary>校验参数，非法时抛 <see cref="ArgumentOutOfRangeException"/>。</summary>
        public void Validate()
        {
            if (columns < 2) throw new ArgumentOutOfRangeException("columns", "列数至少为 2（当前 " + columns + "）");
            if (rows < 2) throw new ArgumentOutOfRangeException("rows", "行数至少为 2（当前 " + rows + "）");
            if (!(spacing > 0f) || float.IsInfinity(spacing))
            {
                throw new ArgumentOutOfRangeException("spacing", "间距必须是有限正数（米）");
            }
            if (!(mass > 0f) || float.IsInfinity(mass))
            {
                throw new ArgumentOutOfRangeException("mass", "质量必须是有限正数（千克）");
            }
            ValidateStiffness(structuralStiffness, "structuralStiffness");
            ValidateStiffness(shearStiffness, "shearStiffness");
            ValidateStiffness(bendStiffness, "bendStiffness");
            if (!(damping >= 0f) || float.IsInfinity(damping))
            {
                throw new ArgumentOutOfRangeException("damping", "阻尼必须是非负有限值（1/秒）");
            }
            if (!(maxStretchRatio > 1f) || float.IsInfinity(maxStretchRatio))
            {
                throw new ArgumentOutOfRangeException("maxStretchRatio", "拉伸上限比必须大于 1");
            }
            if (!(collisionThickness >= 0f) || float.IsInfinity(collisionThickness))
            {
                throw new ArgumentOutOfRangeException("collisionThickness", "碰撞厚度必须是非负有限值（米）");
            }
        }

        static void ValidateStiffness(float value, string name)
        {
            if (float.IsNaN(value) || value < 0f || value > 1f)
            {
                throw new ArgumentOutOfRangeException(name, "硬度必须在 [0,1] 区间内（当前 " + value + "）");
            }
        }
    }
}
