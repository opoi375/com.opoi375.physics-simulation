// Copyright (c) 2026 PhysicsSimulation. MIT License.
using System;
using UnityEngine;

namespace PhysicsSimulation
{
    /// <summary>
    /// 流体（PBF）参数。
    ///
    /// 三个量互相牵制，改一个就要看另外两个：
    ///  * <see cref="particleSpacing"/> d —— 初始点阵间距，决定粒子数与粒子质量 m = ρ0·d³；
    ///  * <see cref="kernelRadius"/> h —— 核支撑半径，决定每个粒子有多少邻居（h = 2d 时三维点阵约 26~32 个）；
    ///  * <see cref="restDensity"/> ρ0 —— 静止密度，约束的目标值。
    /// h ≤ d 时核的支撑范围里可能一个邻居都没有，密度恒等于自身项，约束退化成"什么都不做"，
    /// 所以 <see cref="Validate"/> 直接拒绝，而不是让人对着一堆散开的粒子调半天参数。
    /// </summary>
    [Serializable]
    public class FluidParameters
    {
        [Tooltip("静止密度 ρ0（千克/立方米）。水就是 1000")]
        public float restDensity = 1000f;

        [Tooltip("初始粒子间距 d（米）。粒子质量按 m = ρ0·d³ 推出来；d 减半 ⇒ 粒子数 ×8、成本 ×8")]
        public float particleSpacing = 0.05f;

        [Tooltip("核支撑半径 h（米）。必须大于 d，常用 h = 2d")]
        public float kernelRadius = 0.1f;

        [Tooltip("每帧子步数。流体漏碰撞主要就发生在这里：1 子步时一步能跨过整块薄墙")]
        public int substeps = 2;

        [Tooltip("密度约束的投影迭代次数。2 次是画质/成本的平衡点，1 次会明显漏体积（粒子一坨坨分开）")]
        public int solverIterations = 2;

        [Tooltip("松弛系数 α：每轮迭代只修正所需位移的 1/(1+α)。0 = 严格投影（不可压），" +
                 "越大水越软、越容易看出'密度没顶住'。它不改变收敛点，只改变收敛速度")]
        [Range(0f, 4f)]
        public float complianceAlpha = 0f;

        [Tooltip("钳掉拉力（正 λ）。关掉之后自由表面会把整坨水吸成一团点 —— 名字不叫 clampNegative，" +
                 "因为该钳的是**正** λ：压缩时 λ 为负（推开），稀疏时 λ 为正（吸引）")]
        public bool clampTensileLambda = true;

        [Tooltip("XSPH 速度插值粘度 [0,1]。0 = 无粘（飞溅多），0.05~0.5 = 常见水/油")]
        public float xsphViscosity = 0.05f;

        [Tooltip("涡度约束强度。0 = 关（默认，省一遍邻居表）；0.2~1.0 能补回被数值耗散抹掉的漩涡")]
        public float vorticityEpsilon = 0f;

        [Tooltip("重力（模拟空间，米/秒²）")]
        public Vector3 gravity = new Vector3(0f, -9.81f, 0f);

        [Tooltip("碰撞厚度（皮肤）：粒子中心离碰撞面至少留这么远")]
        public float collisionThickness = 0.005f;

        [Tooltip("粒子数上限。超了直接拒绝构建，而不是让帧率崩了再去查")]
        public int maxParticles = 8192;

        /// <summary>
        /// 单步 dt 上限（秒）。进 Play 的第一帧、卡帧、切后台再回来，<c>Time.deltaTime</c>
        /// 都可能给出秒级的值；不钳住的话一个子步就能把整池水甩出去 —— 实测演示场景刚进
        /// Play 头几帧质心就掉到 y = −1394 m。与布料/质弹簧/软体同一套语义：
        /// <c>&lt;= 0</c> 视为不钳制。
        /// </summary>
        [Tooltip("单步 dt 上限（秒），防第一帧/卡帧的秒级 deltaTime 把水甩飞；<= 0 表示不钳制")]
        [Range(0f, 1f)]
        public float maxDeltaTime = 1f / 30f;

        /// <summary>把请求的 dt 钳到 maxDeltaTime（maxDeltaTime &lt;= 0 视为不钳制）。</summary>
        public float ClampDeltaTime(float dt)
        {
            if (maxDeltaTime > 0f && dt > maxDeltaTime) return maxDeltaTime;
            return dt;
        }

        /// <summary>
        /// 速度上限（米/秒）。0 或负表示不限制。
        ///
        /// 它存在的理由不是“让画面看起来更稳”，而是**薄碰撞体会漏水**：盒子代理把埋进体内的
        /// 质点沿“穿透最浅的那根轴”弹到体外，而单子步的平流位移正好是 |v|·dt_sub ——
        /// 实测演示水箱里水体拍在地板上能冲出 16~18 m/s，配 1/60 s 子步就是一步 0.27 m，
        /// 比 0.2 m 厚的地板还厚：粒子直接越过板的中线，被从**地板下表面**挤出去。
        /// 钳住 |v| 之后，“单步最大位移 小于 板厚的一半”才变成一条可验的不变式（见
        /// <c>FluidTankSealTests</c>）；而不钳时它只是一个“今天恰好没漏”的巧合。
        /// </summary>
        [Tooltip("速度上限（米/秒），0 表示不限制。水箱这类薄碰撞体必须设，否则水会穿板漏走")]
        public float maxSpeed = 0f;

        /// <summary>把速度钳到 maxSpeed（maxSpeed &lt;= 0 视为不限制）并返回钳后的平方速度，省一次开方。</summary>
        public float ClampSpeedSqr(Vector3 v)
        {
            float sqr = v.sqrMagnitude;
            if (maxSpeed <= 0f) return sqr;
            return sqr > maxSpeed * maxSpeed ? maxSpeed * maxSpeed : sqr;
        }

        /// <summary>粒子质量 m = ρ0 · d³（设计密度下的单粒子质量）。</summary>
        public float ParticleMass
        {
            get { return restDensity * particleSpacing * particleSpacing * particleSpacing; }
        }

        /// <summary>分母的防除零下限（不是软度来源 —— 软度走 1/(1+α) 的松弛）。
        /// α=0 时它是一个远小于典型 Σ|∇C|²（1e5 量级）的值，只会挡住"孤立粒子分母为零"。</summary>
        public float ComplianceEpsilon
        {
            get
            {
                float a = complianceAlpha * kernelRadius;
                float e = a * a * a * restDensity;
                return e > 1e-12f ? e : 1e-12f;
            }
        }

        public FluidParameters Clone()
        {
            var p = new FluidParameters();
            p.restDensity = restDensity;
            p.particleSpacing = particleSpacing;
            p.kernelRadius = kernelRadius;
            p.substeps = substeps;
            p.solverIterations = solverIterations;
            p.complianceAlpha = complianceAlpha;
            p.clampTensileLambda = clampTensileLambda;
            p.xsphViscosity = xsphViscosity;
            p.vorticityEpsilon = vorticityEpsilon;
            p.gravity = gravity;
            p.collisionThickness = collisionThickness;
            p.maxParticles = maxParticles;
            p.maxDeltaTime = maxDeltaTime;
            p.maxSpeed = maxSpeed;
            return p;
        }

        public void Validate()
        {
            if (!(restDensity > 0f) || float.IsInfinity(restDensity) || float.IsNaN(restDensity))
                throw new ArgumentOutOfRangeException("restDensity", "静止密度 restDensity 必须是正的有限值");

            if (!(particleSpacing > 0f) || float.IsInfinity(particleSpacing))
                throw new ArgumentOutOfRangeException("particleSpacing", "粒子间距 particleSpacing 必须是正的有限值");

            if (!(kernelRadius > particleSpacing) || float.IsInfinity(kernelRadius))
                throw new ArgumentOutOfRangeException("kernelRadius",
                    "核支撑半径 kernelRadius（h）必须大于粒子间距 particleSpacing（d），"
                    + "否则核的支撑范围里可能一个邻居都没有，密度恒等于自身项，水会散成一团点");

            if (substeps < 1)
                throw new ArgumentOutOfRangeException("substeps", "子步数 substeps 至少为 1");

            if (maxDeltaTime < 0f)
                throw new ArgumentOutOfRangeException("maxDeltaTime",
                    "dt 上限 maxDeltaTime 不能为负（<= 0 表示不钳制）");
            if (float.IsNaN(maxSpeed) || float.IsInfinity(maxSpeed) || maxSpeed < 0f)
                throw new ArgumentOutOfRangeException("maxSpeed",
                    "速度上限 maxSpeed 必须是有限非负值（0 表示不限制），当前 " + maxSpeed);

            if (solverIterations < 1)
                throw new ArgumentOutOfRangeException("solverIterations",
                    "投影迭代次数 solverIterations 至少为 1（0 次等于不解密度约束）");

            if (!(xsphViscosity >= 0f) || xsphViscosity > 1f || float.IsNaN(xsphViscosity))
                throw new ArgumentOutOfRangeException("xsphViscosity",
                    "XSPH 粘度系数必须在 [0,1]：它是速度插值权重，大于 1 会反向过冲");

            if (!(vorticityEpsilon >= 0f) || float.IsInfinity(vorticityEpsilon))
                throw new ArgumentOutOfRangeException("vorticityEpsilon", "涡度约束强度必须是非负有限值");

            if (!(collisionThickness >= 0f) || float.IsInfinity(collisionThickness))
                throw new ArgumentOutOfRangeException("collisionThickness", "碰撞厚度必须是非负有限值（米）");

            if (!(complianceAlpha >= 0f) || float.IsInfinity(complianceAlpha))
                throw new ArgumentOutOfRangeException("complianceAlpha", "顺应度 α 必须是非负有限值");

            if (maxParticles < 1)
                throw new ArgumentOutOfRangeException("maxParticles", "粒子数上限至少为 1");

            if (float.IsNaN(gravity.x) || float.IsInfinity(gravity.x)
                || float.IsNaN(gravity.y) || float.IsInfinity(gravity.y)
                || float.IsNaN(gravity.z) || float.IsInfinity(gravity.z))
                throw new ArgumentOutOfRangeException("gravity", "重力必须是有限向量");
        }
    }
}
