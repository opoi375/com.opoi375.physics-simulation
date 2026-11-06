// Copyright (c) 2026 PhysicsSimulation. MIT License.
using UnityEngine;

namespace PhysicsSimulation
{
    /// <summary>
    /// 一个"既能把自己当成一朵质点球云供别人碰撞、又能接受别人推挤"的体。
    ///
    /// 三个求解器（软体 / 布料 / 质点弹簧）都实现它 —— 代理层只认"会动的球"，
    /// 不认体是什么类型，所以跨求解器互碰不需要任何额外分支。
    ///
    /// 为什么是"球云"而不是三角面：三角面贴合在深穿透时"最近面"方向会翻，
    /// 那是流体水箱踩过的角部传送带坑（见文档「流体 / 漏水」）；球云没有方向歧义，
    /// 代价是接触面有颗粒感，所以半径必须至少是粒子间距的一半，否则会从两球缝隙里漏过去。
    /// </summary>
    public interface IParticleBody
    {
        /// <summary>参与互碰的质点个数（含钉住的：钉住的质点是静态障碍，它该推开别人，只是自己不动）。</summary>
        int CloudParticleCount { get; }

        /// <summary>每个质点当成多大的球（米）。必须是正有限值。</summary>
        float CloudRadius { get; }

        /// <summary>
        /// 质点或变换每变一次就自增的版本号。<see cref="ParticleCloudProxy"/> 靠它决定要不要重建索引。
        /// 读取必须是幂等的（同一个步进周期内重复读要给出同一个值），否则代理会每帧白白重建。
        /// </summary>
        int CloudVersion { get; }

        /// <summary>
        /// 当前**世界坐标**下的质点位置，写进一个内部缓冲区并返回（前 <see cref="CloudParticleCount"/> 个有效）。
        /// 返回的数组允许被实现方复用，所以代理必须自己拷一份 —— 不能长期持有它。
        /// </summary>
        Vector3[] CloudWorldPositions();

        /// <summary>这个体自己的碰撞代理集合，互碰组会往里挂别人的球云。</summary>
        CollisionSet ParticleCollisions { get; }

        /// <summary>
        /// 所在的互碰组。<c>ParticleInteractionGroup.Add</c> 会把它回写到体上，
        /// Inspector 里填的那个字段也走同一个入口 —— 两条路必须汇到一处，
        /// 否则会出现“组里记了名、但体自己不知道”，连带把变换矩阵漏掉（球云就错位）。
        /// </summary>
        ParticleInteractionGroup CloudGroup { get; set; }
    }
}
