// Copyright (c) 2026 PhysicsSimulation. MIT License.
using System;
using System.Diagnostics;
using System.Text;
using UnityEngine;

namespace PhysicsSimulation
{
    /// <summary>
    /// 软体审计判定。顺序即优先级，见 <see cref="SoftBodyMeshAudit.Classify"/>。
    /// </summary>
    public enum SoftBodyAuditVerdict
    {
        /// <summary>闭合、焊接正常、落体后体积与拉伸都在带宽内。</summary>
        Healthy,
        /// <summary>能构建，但不是流形闭合网格 ⇒ 体积约束按设计跳过（它会像一袋空气而不是果冻）。</summary>
        OpenMesh,
        /// <summary>焊接把网格塌成了极少数质点（通常是 <c>weldTolerance</c> 太大）。</summary>
        DegenerateWeld,
        /// <summary>跑起来之后出现非有限值，或体积/拉伸出了带宽。</summary>
        Unstable,
        /// <summary>闭合但<b>有向体积为负</b> —— 三角形绕序整体朝内（导入或建模时翻了法线）。它会当果冻跑，但每一面都是里朝外。</summary>
        InvertedWinding,
        /// <summary>闭合但围住的体积小到可以忽略（&lt; 1 mm³）—— 零厚度的"压扁气球"。体积约束没有可回弹的余量。</summary>
        DegenerateVolume,
        /// <summary><c>Build</c> 直接拒了（空网格、越界索引、非有限顶点、退化三角形……）。错误原文在 <c>BuildError</c>。</summary>
        BuildFailed
    }

    /// <summary>
    /// 一次网格审计的结果。<b>结构体、无引用</b>（<c>BuildError</c> 除外），方便在编辑器里成批收集与打印。
    ///
    /// 除 <see cref="BuildMs"/> 与 <see cref="MsPerStep"/> 之外，同一个输入跑两次必须逐位相同 ——
    /// 计时是唯一允许抖动的字段。
    /// </summary>
    public struct SoftBodyAuditResult
    {
        public int MeshVertexCount;
        public int MeshTriangleCount;
        public int ParticleCount;
        public int StructuralSpringCount;
        public int BendSpringCount;
        public bool IsClosed;
        public Vector3 BoundsSize;
        public float WeldRatio;
        public float RestVolume;
        public int StepsCompleted;
        public double BuildMs;
        public double MsPerStep;
        public float VolumeRetention;
        public float MaxStretchRatio;
        public float LowestWorldY;
        public bool HasNonFinite;
        public string BuildError;
        public SoftBodyAuditVerdict Verdict;

        public override string ToString()
        {
            return SoftBodyMeshAudit.DescribeVerdict(this)
                 + " | 顶点 " + MeshVertexCount + " → 质点 " + ParticleCount
                 + " | 结构 " + StructuralSpringCount + " | 弯曲 " + BendSpringCount
                 + " | 三角形 " + MeshTriangleCount + " | 闭合 " + IsClosed
                 + " | 静止体积 " + RestVolume.ToString("0.0000")
                 + " | 体积保持 " + VolumeRetention.ToString("0.000")
                 + " | 最大拉伸 " + MaxStretchRatio.ToString("0.0000")
                 + " | ms/步 " + MsPerStep.ToString("0.000")
                 + (string.IsNullOrEmpty(BuildError) ? "" : " | 错误 " + BuildError);
        }
    }

    /// <summary>
    /// 把"任意网格丢进软体求解器会发生什么"变成可打印数字的纯逻辑审计器。
    ///
    /// 为什么要有它：演示场景里的长方体与二十面体是自己造的、性质已知。真实美术网格不是——
    /// 它有 UV 接缝带来的重复顶点、有开放曲面（灌木、旗面、地砖）、有非流形边、有空子网格，
    /// 尺寸还能从 5 厘米到 20 米随便跨度。这些问题光看演示画面看不出来，必须有一台能扫一遍的机器。
    ///
    /// <b>契约：任何输入都只报告、不抛异常。</b>扫描 93 个模型时，第一个坏模型不许把整轮扫描打断。
    ///
    /// 求解路径与运行时完全一致（同一个 <see cref="SoftBodySimulation.Build"/> 与 <see cref="SoftBodySimulation.Step"/>），
    /// 所以这里报出来的数字就是真实游戏里会看到的数字，不是一套"只在测试里成立"的近似。
    /// </summary>
    public static class SoftBodyMeshAudit
    {
        /// <summary>审计用的固定步长（秒）。定长步才可比，用 <c>Time.deltaTime</c> 会把帧率抖动写进结果。</summary>
        public const float AuditDeltaTime = 1f / 60f;

        /// <summary>默认地面高度（审计世界的 y=0）。</summary>
        public const float GroundY = 0f;

        // 体积保持带宽：闭合软体落在地上压扁一点是正常，超过这个带宽就是求解器真的出了问题
        const float RetentionLow = 0.60f;
        const float RetentionHigh = 1.40f;

        /// <summary>
        /// "围住的体积可以忽略"的绝对线（m³）。1 mm³ 折算下来是 1e-9，这里取 1e-6（= 边长 1 厘米的立方体）。
        /// 为什么用绝对值而不是相对包围盒的比例：压扁的壳其包围盒本身也跟着变扁，比例会假装它很健康。
        /// </summary>
        public const float NegligibleVolume = 1e-6f;

        /// <summary>不加地面（整块自由下落，只测数值稳定性）。</summary>
        public static SoftBodyAuditResult Audit(SoftBodyMeshData data, SoftBodyParameters parameters, int steps)
        {
            return Audit(data, parameters, steps, false);
        }

        /// <summary>
        /// 审计一份网格数据。
        /// </summary>
        /// <param name="data">顶点 + 三角形；null 也接受，会报成 BuildFailed。</param>
        /// <param name="parameters">为 null 时用默认参数。</param>
        /// <param name="steps">跑多少个 <c>Step(AuditDeltaTime)</c>；负数按 0 处理。</param>
        /// <param name="addGround">是否在 y=<see cref="GroundY"/> 加一条世界空间半空间地面，让这块软体真的落地。</param>
        public static SoftBodyAuditResult Audit(SoftBodyMeshData data, SoftBodyParameters parameters, int steps, bool addGround)
        {
            var r = new SoftBodyAuditResult();
            var p = parameters ?? new SoftBodyParameters();
            if (steps < 0) steps = 0;

            try
            {
                if (data == null)
                {
                    r.BuildError = "网格数据为 null";
                    r.Verdict = SoftBodyAuditVerdict.BuildFailed;
                    return r;
                }

                r.MeshVertexCount = data.VertexCount;
                r.MeshTriangleCount = data.TriangleCount;

                var buildWatch = Stopwatch.StartNew();
                var sim = new SoftBodySimulation(p);
                sim.Build(data);
                buildWatch.Stop();
                r.BuildMs = buildWatch.Elapsed.TotalMilliseconds;

                r.ParticleCount = sim.ParticleCount;
                r.StructuralSpringCount = sim.StructuralSpringCount;
                r.BendSpringCount = sim.BendSpringCount;
                r.IsClosed = sim.IsClosed;
                r.RestVolume = sim.RestVolume();
                r.WeldRatio = r.ParticleCount > 0 ? (float)r.MeshVertexCount / r.ParticleCount : 0f;
                r.BoundsSize = MeasureBounds(sim);

                if (addGround)
                    sim.Collisions.Add(new PlaneCollisionProxy(new Vector3(0f, GroundY, 0f), Vector3.up),
                                        CollisionProxySpace.World);

                var stepWatch = Stopwatch.StartNew();
                for (int i = 0; i < steps; i++) sim.Step(AuditDeltaTime);
                stepWatch.Stop();
                r.StepsCompleted = steps;
                r.MsPerStep = steps > 0 ? stepWatch.Elapsed.TotalMilliseconds / steps : 0.0;

                float rest = r.RestVolume;
                r.VolumeRetention = (r.IsClosed && Mathf.Abs(rest) > 1e-7f) ? sim.Volume() / rest : 0f;
                r.MaxStretchRatio = sim.MaxStretchRatio();
                r.LowestWorldY = MeasureLowestY(sim);
                r.HasNonFinite = sim.HasNonFiniteState();

                r.Verdict = Classify(r, p);
                return r;
            }
            catch (Exception e)
            {
                // 审计的立身之本：Build/Step 抛什么都在字段里说话，绝不让调用方接住异常
                r.BuildError = e.Message;
                r.Verdict = SoftBodyAuditVerdict.BuildFailed;
                return r;
            }
        }

        /// <summary>直接从 Unity 网格审计（读 vertices / triangles，不碰你的资源）。</summary>
        public static SoftBodyAuditResult AuditMesh(Mesh mesh, SoftBodyParameters parameters, int steps, bool addGround)
        {
            if (mesh == null)
                return new SoftBodyAuditResult { BuildError = "网格为 null", Verdict = SoftBodyAuditVerdict.BuildFailed };
            return Audit(SoftBodyMeshData.FromMesh(mesh), parameters, steps, addGround);
        }

        /// <summary>
        /// 这一行需要额外说明的事：BuildFailed 的错误原文（截断到 120 字），
        /// 以及"体积围不住"这类数字本身说不清的情况。扫描报告里没有它就只能看到"失败了"三个字。
        /// </summary>
        public static string Note(SoftBodyAuditResult r)
        {
            string raw = NoteRaw(r);
            // 空就是空，不能走 Sanitize 对空串的“(unnamed)”兜底 —— 那是给模型名字用的，
            // 挤到 note 列上会让每一个健康行看上去都像“有个叫 (unnamed) 的东西需要解释”
            return string.IsNullOrEmpty(raw) ? "" : Sanitize(raw);
        }

        static string NoteRaw(SoftBodyAuditResult r)
        {
            if (!string.IsNullOrEmpty(r.BuildError)) return r.BuildError;
            if (r.Verdict == SoftBodyAuditVerdict.InvertedWinding)
                return "绕序朝内，有向体积 " + r.RestVolume.ToString("0.0000");
            if (r.Verdict == SoftBodyAuditVerdict.DegenerateVolume)
                return "围住体积 " + r.RestVolume.ToString("0.000000") + " m³，可忽略";
            if (r.VolumeRetention < 0f && r.IsClosed && r.RestVolume > 0f)
                return "跑完之后体积符号反转（翻了面）";
            return "";
        }

        /// <summary>判定标记的短字符串，与枚举名一致 —— 文档表格和 grep 都靠它。</summary>
        public static string DescribeVerdict(SoftBodyAuditResult r)
        {
            return r.Verdict.ToString();
        }

        /// <summary>表格表头；与 <see cref="ToMarkdownRow"/> 的列顺序一一对应。</summary>
        public static string MarkdownHeader
        {
            get
            {
                return "| model | mesh verts | particles | weld x | structural | bend | triangles | closed "
                     + "| bounds (x,y,z) | rest volume | volume retention | max stretch | ms/step | lowest y | verdict | note |";
            }
        }

        /// <summary>表头下方的分隔线。</summary>
        public static string MarkdownSeparator
        {
            get
            {
                int columns = 0;
                foreach (char c in MarkdownHeader) if (c == '|') columns++;
                columns -= 1;                       // 竖线数 = 列数 + 1（首尾各一条）
                var sb = new StringBuilder("|");
                for (int i = 0; i < columns; i++) sb.Append(" --- |");
                return sb.ToString();
            }
        }

        /// <summary>把一次审计压成一行 Markdown（单行，竖线开头竖线结尾，列顺序固定）。</summary>
        public static string ToMarkdownRow(string name, SoftBodyAuditResult r)
        {
            return "| " + Sanitize(name)
                 + " | " + r.MeshVertexCount
                 + " | " + r.ParticleCount
                 + " | " + r.WeldRatio.ToString("0.00")
                 + " | " + r.StructuralSpringCount
                 + " | " + r.BendSpringCount
                 + " | " + r.MeshTriangleCount
                 + " | " + (r.IsClosed ? "yes" : "no")
                 + " | " + r.BoundsSize.x.ToString("0.00") + "," + r.BoundsSize.y.ToString("0.00") + "," + r.BoundsSize.z.ToString("0.00")
                 + " | " + r.RestVolume.ToString("0.0000")
                 + " | " + r.VolumeRetention.ToString("0.000")
                 + " | " + r.MaxStretchRatio.ToString("0.000")
                 + " | " + r.MsPerStep.ToString("0.000")
                 + " | " + r.LowestWorldY.ToString("0.000")
                 + " | " + DescribeVerdict(r)
                 + " | " + (Note(r).Length == 0 ? "-" : Note(r)) + " |";
        }

        /// <summary>
        /// 判定优先级：BuildFailed &gt; DegenerateWeld &gt; OpenMesh &gt; Unstable &gt; Healthy。
        ///
        /// 前两条排在 OpenMesh 之前是有意的：焊接塌掉或压根没构建成功时，谈闭不闭合没有意义。
        /// </summary>
        public static SoftBodyAuditVerdict Classify(SoftBodyAuditResult r, SoftBodyParameters p)
        {
            if (!string.IsNullOrEmpty(r.BuildError)) return SoftBodyAuditVerdict.BuildFailed;
            // 少于 4 个质点连个体积都围不出来，一定是焊过头了
            if (r.ParticleCount < 4) return SoftBodyAuditVerdict.DegenerateWeld;
            if (!r.IsClosed) return SoftBodyAuditVerdict.OpenMesh;
            // 绕序整体朝内：V₀ &lt; 0。求解器对称地照样跑，但视觉上是里朝外，必须单独说
            if (r.RestVolume < 0f) return SoftBodyAuditVerdict.InvertedWinding;
            // 闭合却围不住体积：零厚度壳，体积约束没有余量
            if (Mathf.Abs(r.RestVolume) < NegligibleVolume) return SoftBodyAuditVerdict.DegenerateVolume;
            if (r.HasNonFinite) return SoftBodyAuditVerdict.Unstable;
            if (r.VolumeRetention < RetentionLow || r.VolumeRetention > RetentionHigh) return SoftBodyAuditVerdict.Unstable;
            if (p != null && p.enableStretchLimit && r.StepsCompleted > 0 && r.MaxStretchRatio > p.maxStretchRatio + 1e-3f)
                return SoftBodyAuditVerdict.Unstable;
            return SoftBodyAuditVerdict.Healthy;
        }

        /// <summary>名字里带竖线会撕坏 Markdown 表格，这里统一替掉（顺带压掉换行）。</summary>
        static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "(unnamed)";
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (c == '|' || c == '\n' || c == '\r') sb.Append(' ');
                else sb.Append(c);
            }
            return sb.ToString();
        }

        static Vector3 MeasureBounds(SoftBodySimulation sim)
        {
            if (sim.ParticleCount == 0) return Vector3.zero;
            Vector3 lo = sim.GetPosition(0), hi = lo;
            for (int i = 1; i < sim.ParticleCount; i++)
            {
                Vector3 p = sim.GetPosition(i);
                lo = Vector3.Min(lo, p);
                hi = Vector3.Max(hi, p);
            }
            return hi - lo;
        }

        static float MeasureLowestY(SoftBodySimulation sim)
        {
            float minY = float.MaxValue;
            for (int i = 0; i < sim.ParticleCount; i++)
            {
                float y = sim.GetPosition(i).y;
                if (y < minY) minY = y;
            }
            return sim.ParticleCount == 0 ? 0f : minY;
        }
    }
}
