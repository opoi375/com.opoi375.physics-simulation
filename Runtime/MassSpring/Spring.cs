// Copyright (c) 2026 PhysicsSimulation. MIT License.
using System;
using UnityEngine;

namespace PhysicsSimulation
{
    /// <summary>
    /// 弹簧：连接两个质点索引，按胡克定律 + 沿轴向的相对速度阻尼产生内力。
    /// 力总是等大反向，因此不会改变系统总动量。
    /// </summary>
    public sealed class Spring
    {
        /// <summary>端点 A 的质点索引（力方向 A→B 为正向）。</summary>
        public readonly int a;

        /// <summary>端点 B 的质点索引。</summary>
        public readonly int b;

        /// <summary>原长（米），必须为非负有限值。</summary>
        public readonly float restLength;

        /// <summary>刚度 k（牛/米），必须为非负有限值。越大越硬、越容易数值发散，需配合子步。</summary>
        public readonly float stiffness;

        /// <summary>弹簧轴向阻尼 c（牛·秒/米），必须为非负有限值。</summary>
        public readonly float damping;

        public Spring(int a, int b, float restLength, float stiffness, float damping)
        {
            if (!(restLength >= 0f) || float.IsInfinity(restLength))
            {
                throw new ArgumentOutOfRangeException(nameof(restLength), "原长必须是非负有限值（米）");
            }
            if (!(stiffness >= 0f) || float.IsInfinity(stiffness))
            {
                throw new ArgumentOutOfRangeException(nameof(stiffness), "刚度 k 必须是非负有限值（牛/米）");
            }
            if (!(damping >= 0f) || float.IsInfinity(damping))
            {
                throw new ArgumentOutOfRangeException(nameof(damping), "弹簧阻尼 c 必须是非负有限值（牛·秒/米）");
            }

            this.a = a;
            this.b = b;
            this.restLength = restLength;
            this.stiffness = stiffness;
            this.damping = damping;
        }

        /// <summary>应变 (l - restLength) / restLength；restLength 为 0 时返回 0。用于 Gizmos 着色。</summary>
        public float Strain(Vector3 positionA, Vector3 positionB)
        {
            if (restLength <= 1e-6f) return 0f;
            return ((positionB - positionA).magnitude - restLength) / restLength;
        }
    }
}
