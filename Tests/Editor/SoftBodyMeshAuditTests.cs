// Copyright (c) 2026 PhysicsSimulation. MIT License.
using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace PhysicsSimulation.Editor.Tests
{
    /// <summary>
    /// 软体网格审计（SoftBodyMeshAudit）行为规格。
    ///
    /// 这一层的唯一职责：把"一个真实美术网格丢进软体求解器会发生什么"变成**可打印的数字**，
    /// 而不是靠肉眼看演示场景猜。它必须能吃下任何脏输入并**只报告、不抛异常**，
    /// 否则扫描 93 个 prop 时第一个坏模型就把整轮扫描打断。
    /// </summary>
    public class SoftBodyMeshAuditTests
    {
        // ==================================================================
        // 测试用网格构造工具
        //
        // 立方体的面表与 SoftBodySimulationTests.MakeBox 同源（绕序已被 v1.2.0 的绿色测试锁死：
        // 24 个按面拆开的顶点 → 焊接成 8 个质点、18 条边每条恰好 2 个三角形、散度定理体积等于解析值）。
        // 自己另起一套绕序是危险的：本项目已经因为"4 朝外 2 朝内"的立方体踩过一次体积只剩 1/3 的坑。
        // ==================================================================

        static readonly int[][] BoxFaces =
        {
            new[] { 0, 3, 2, 1 },   // -z
            new[] { 4, 5, 6, 7 },   // +z
            new[] { 0, 4, 7, 3 },   // -x
            new[] { 1, 2, 6, 5 },   // +x
            new[] { 0, 1, 5, 4 },   // -y
            new[] { 3, 7, 6, 2 }    // +y
        };

        /// <summary>按面拆开的闭合长方体：24 个网格顶点，焊接后 8 个质点；解析体积 = w·h·d。</summary>
        static SoftBodyMeshData BoxMesh(float width, float height, float depth)
        {
            float hx = width * 0.5f, hy = height * 0.5f, hz = depth * 0.5f;
            var c = new[]
            {
                new Vector3(-hx, -hy, -hz), new Vector3(hx, -hy, -hz),
                new Vector3(hx, hy, -hz), new Vector3(-hx, hy, -hz),
                new Vector3(-hx, -hy, hz), new Vector3(hx, -hy, hz),
                new Vector3(hx, hy, hz), new Vector3(-hx, hy, hz)
            };
            var verts = new List<Vector3>();
            var tris = new List<int>();
            foreach (var face in BoxFaces)
            {
                int start = verts.Count;
                for (int i = 0; i < 4; i++) verts.Add(c[face[i]]);
                tris.Add(start); tris.Add(start + 1); tris.Add(start + 2);
                tris.Add(start); tris.Add(start + 2); tris.Add(start + 3);
            }
            return new SoftBodyMeshData(verts.ToArray(), tris.ToArray());
        }

        /// <summary>n×n 开放网格片：边界边只有 1 个三角形 ⇒ 非闭合。</summary>
        static SoftBodyMeshData OpenGridPlane(int n, float size)
        {
            var v = new Vector3[n * n];
            for (int r = 0; r < n; r++)
                for (int q = 0; q < n; q++)
                    v[r * n + q] = new Vector3(q * size / (n - 1) - size * 0.5f, 0f, r * size / (n - 1) - size * 0.5f);
            var t = new List<int>();
            for (int r = 0; r < n - 1; r++)
                for (int q = 0; q < n - 1; q++)
                {
                    int a = r * n + q, b = a + 1, c = a + n, d = c + 1;
                    t.Add(a); t.Add(c); t.Add(b);
                    t.Add(b); t.Add(c); t.Add(d);
                }
            return new SoftBodyMeshData(v, t.ToArray());
        }

        /// <summary>审计专用参数：子步小一点，扫描 93 个模型时不至于跑到天荒地老。</summary>
        static SoftBodyParameters Standard()
        {
            return new SoftBodyParameters { substeps = 2 };
        }

        static SoftBodyAuditResult Healthy(SoftBodyMeshData data, int steps, bool ground = false)
        {
            return SoftBodyMeshAudit.Audit(data, Standard(), steps, ground);
        }

        // ==================================================================
        // Feature: 审计一个闭合网格
        // ==================================================================

        [Test]
        public void Audit_ClosedBoxMesh_ReportsWeldedParticleCountAndPositiveVolume()
        {
            // Given/When: 24 顶点、绕序朝外的闭合立方体（1×1×1），审计 1 步
            var r = Healthy(BoxMesh(1f, 1f, 1f), 1);

            // Then: 网格顶点 24 → 焊接成 8 个质点；闭合；体积等于解析值；判定 Healthy
            Assert.AreEqual(24, r.MeshVertexCount, "网格顶点应是 24（每个面 4 个独立顶点）");
            Assert.AreEqual(12, r.MeshTriangleCount, "12 个三角形");
            Assert.AreEqual(8, r.ParticleCount, "焊接后应只剩 8 个角点质点");
            Assert.AreEqual(3f, r.WeldRatio, 1e-4f, "焊接比应是 24/8 = 3 倍（UV 接缝那类重复顶点被合掉的直接度量）");
            Assert.AreEqual(18, r.StructuralSpringCount, "立方体 12 条棱 + 6 条面对角线 = 18 条结构边");
            Assert.IsTrue(r.IsClosed, "立方体应是闭合的（每条边恰好 2 个三角形）");
            Assert.AreEqual(1f, r.RestVolume, 1e-4f, "散度定理体积应等于解析体积 1.0");
            Assert.IsNull(r.BuildError, "健康网格不该有构建错误");
            Assert.AreEqual(SoftBodyAuditVerdict.Healthy, r.Verdict, "判定应是 Healthy");
        }

        [Test]
        public void Audit_RunsTheRequestedNumberOfStepsAndReportsPerStepCost()
        {
            // Given/When: 要求跑 120 步
            var r = Healthy(BoxMesh(1f, 1f, 1f), 120);

            // Then: 步数不多不少，计时字段非负且有限
            Assert.AreEqual(120, r.StepsCompleted, "审计必须如实报告跑了多少步");
            Assert.GreaterOrEqual(r.MsPerStep, 0.0, "ms/步 不能是负数");
            Assert.IsTrue(double.IsFinite(r.MsPerStep), "ms/步 必须是有限值，否则基准数字没意义");
            Assert.Greater(r.BuildMs, 0.0, "构建耗时应为正");
        }

        [Test]
        public void Audit_WithGroundPlane_ReportsVolumeRetentionAfterLanding()
        {
            // Given: 1×1.6×1 的立方体，中心在原点 ⇒ 最低点 y = -0.8，在 y=0 的地面之上
            // When:  带地面自由落体 90 步（1/60 步长 = 1.5 秒）
            var r = Healthy(BoxMesh(1f, 1.6f, 1f), 90, true);

            // Then: 落住了 —— 体积没瘪穿、拉伸没超限、最低点没钻进地面以下
            Assert.AreEqual(SoftBodyAuditVerdict.Healthy, r.Verdict, "落在地面上应该是 Healthy，实际：" + r);
            Assert.GreaterOrEqual(r.VolumeRetention, 0.90f, "落地压扁 10% 以内是正常，实际 " + r.VolumeRetention);
            Assert.LessOrEqual(r.VolumeRetention, 1.05f, "体积不该被撑大到超过静止值 5%，实际 " + r.VolumeRetention);
            Assert.Less(r.MaxStretchRatio, Standard().maxStretchRatio, "最大拉伸比应低于限幅上限");
            Assert.GreaterOrEqual(r.LowestWorldY, SoftBodyMeshAudit.GroundY - 0.01f - 1e-3f,
                "最低质点不许钻进地面（半空间 + skin 至少要停在面上 + 0.01）");
        }

        [Test]
        public void Audit_WithoutGroundPlane_KeepsFiniteState()
        {
            // Given/When: 同样的立方体，但没有碰撞代理 ⇒ 整块自由下落
            var r = Healthy(BoxMesh(1f, 1.6f, 1f), 60, false);

            // Then: 没有地面不等于会炸；弹簧是内力，自由落体里体积应基本守恒
            Assert.IsFalse(r.HasNonFinite, "无碰撞的自由落体不许产生非有限值");
            Assert.Greater(r.VolumeRetention, 0.95f, "整块自由下落时内部弹簧几乎不受力，体积应保持住，实际 " + r.VolumeRetention);
            Assert.Less(r.VolumeRetention, 1.05f, "同上，实际 " + r.VolumeRetention);
        }

        // ==================================================================
        // Feature: 脏输入只报告、不抛
        // ==================================================================

        [Test]
        public void Audit_OpenMesh_ReportsOpenMeshVerdictAndZeroVolume()
        {
            // Given/When: 4×4 开放网格片
            var r = Healthy(OpenGridPlane(4, 1f), 1);

            // Then: 能构建（不是错误），但闭合性为假 ⇒ 体积约束按设计跳过，判定 OpenMesh
            Assert.IsNull(r.BuildError, "开放网格是可以正常构建的，只是没有体积约束");
            Assert.IsFalse(r.IsClosed, "边界边只有 1 个三角形，应判为不闭合");
            Assert.AreEqual(0f, r.RestVolume, "不闭合时静止体积记为 0（体积约束被跳过）");
            Assert.AreEqual(SoftBodyAuditVerdict.OpenMesh, r.Verdict, "判定应是 OpenMesh 而不是 Healthy");
            Assert.AreEqual(0f, r.VolumeRetention, "开放网格没有体积保持率可言");
        }

        [Test]
        public void Audit_EmptyMeshData_ReportsBuildFailedWithoutThrowing()
        {
            // Given: 零顶点零三角形
            // When/Then: 不抛异常，且报成 BuildFailed + 有错误原文
            SoftBodyAuditResult[] box = new SoftBodyAuditResult[1];
            Assert.DoesNotThrow(() => box[0] = Healthy(new SoftBodyMeshData(new Vector3[0], new int[0]), 1),
                "空网格必须只报告、不许抛异常");
            var r = box[0];
            Assert.AreEqual(SoftBodyAuditVerdict.BuildFailed, r.Verdict, "空网格判定应是 BuildFailed");
            Assert.IsFalse(string.IsNullOrEmpty(r.BuildError), "必须带上 Build 拒绝的原因，否则扫描报告没法查");
        }

        [Test]
        public void Audit_TriangleIndicesOutOfRange_ReportsBuildFailedWithIndex()
        {
            // Given: 三角形索引 999 越界
            var data = BoxMesh(1f, 1f, 1f);
            var bad = new SoftBodyMeshData(data.Vertices, new[] { 0, 1, 999 });

            // When/Then: 不抛，错误原文里要能定位到越界下标
            SoftBodyAuditResult[] box = new SoftBodyAuditResult[1];
            Assert.DoesNotThrow(() => box[0] = Healthy(bad, 1), "越界索引必须只报告、不许抛异常");
            var r = box[0];
            Assert.AreEqual(SoftBodyAuditVerdict.BuildFailed, r.Verdict, "越界索引判定应是 BuildFailed");
            StringAssert.Contains("999", r.BuildError, "错误原文要含越界下标，不然扫描报告等于没信息");
        }

        [Test]
        public void Audit_NonFiniteVertex_ReportsBuildFailedWithoutThrowing()
        {
            // Given: 某个顶点是 NaN
            var data = BoxMesh(1f, 1f, 1f);
            var v = (Vector3[])data.Vertices.Clone();
            v[2] = new Vector3(float.NaN, 0f, 0f);
            var bad = new SoftBodyMeshData(v, data.Triangles);

            // When/Then
            SoftBodyAuditResult[] box = new SoftBodyAuditResult[1];
            Assert.DoesNotThrow(() => box[0] = Healthy(bad, 1), "非有限顶点必须只报告、不许抛异常");
            var r = box[0];
            Assert.AreEqual(SoftBodyAuditVerdict.BuildFailed, r.Verdict, "NaN 顶点判定应是 BuildFailed");
            StringAssert.Contains("非有限", r.BuildError, "错误原文应指出是非有限坐标");
        }

        [Test]
        public void Audit_OversizedWeldTolerance_ReportsDegenerateWeldOrBuildFailed()
        {
            // Given: weldTolerance 5 米 > 盒子尺寸 ⇒ 所有角点焊成一坨
            var p = Standard();
            p.weldTolerance = 5f;

            // When: 审计
            var r = SoftBodyMeshAudit.Audit(BoxMesh(1f, 1f, 1f), p, 1);

            // Then: 要么被 Build 拒（三角形退化），要么判成焊接塌缩；总之绝不判 Healthy
            Assert.AreNotEqual(SoftBodyAuditVerdict.Healthy, r.Verdict, "焊成一坨的东西不能报 Healthy");
            Assert.IsTrue(r.Verdict == SoftBodyAuditVerdict.DegenerateWeld || r.Verdict == SoftBodyAuditVerdict.BuildFailed,
                "过大容差应报 DegenerateWeld 或 BuildFailed，实际：" + r.Verdict);
            Assert.Less(r.ParticleCount, 8, "容差 5 米之下 8 个角点必然焊到一起");
        }

        [Test]
        public void Audit_CoincidentVertices_AlwaysWeldEvenAtTinyTolerance()
        {
            // Given: 完全重合的重复顶点（UV 接缝的典型形态：同一角点被拆成几份但坐标逐位相同），
            //        容差小到了 1e-9
            var p = Standard();
            p.weldTolerance = 1e-9f;

            // When: 审计同一个 24 顶点的立方体
            var r = SoftBodyMeshAudit.Audit(BoxMesh(1f, 1f, 1f), p, 1);

            // Then: 依旧焊回 8 个 —— tolerance 是**半径**，重合点距离为 0，任何正容差都盖得住。
            //        这条把“小容差 = 不焊接”这个直觉直接推掉：不成立。
            Assert.AreEqual(8, r.ParticleCount, "重合顶点在任何正容差下都应该被焊到一起");
            Assert.AreEqual(SoftBodyAuditVerdict.Healthy, r.Verdict, "实际：" + r);
        }

        [Test]
        public void Audit_CrackedSeamsWiderThanTolerance_ReportOpenMesh()
        {
            // Given: 每个面沿自己的外法线拉开 2 mm ⇒ 接缝不再是重合顶点，而是 4 mm 宽的缝
            //        （导入资产里很常见：建模时没焊死、或导出时拆了壳）
            var cracked = CrackFaces(BoxMesh(1f, 1f, 1f), 0.002f);
            var p = Standard();
            p.weldTolerance = 1e-4f;   // 默认的焊接容差

            // When: 审计
            var r = SoftBodyMeshAudit.Audit(cracked, p, 1);

            // Then: 缝宽 4 mm > 容差 0.1 mm ⇒ 谁也焊不上 ⇒ 24 个独立质点、每条边只属 1 个三角形
            //        ⇒ 网格被判不闭合，体积约束失效。这就是 weldTolerance 真实存在的理由与它的代价。
            Assert.AreEqual(24, r.ParticleCount, "缝宽超过容差时焊不上，质点数就等于网格顶点数");
            Assert.IsFalse(r.IsClosed, "没焊上的立方体是一堆独立面片，必须判不闭合");
            Assert.AreEqual(SoftBodyAuditVerdict.OpenMesh, r.Verdict, "实际：" + r);
            Assert.AreEqual(0f, r.RestVolume, "不闭合 ⇒ 体积约束按设计跳过");
        }

        /// <summary>把每个面沿其外法线拉开一段距离，模拟“接缝没焊死”的导入网格。</summary>
        static SoftBodyMeshData CrackFaces(SoftBodyMeshData box, float gap)
        {
            // 面表顺序与 BoxFaces 一致：-z, +z, -x, +x, -y, +y
            var normals = new[]
            {
                new Vector3(0f, 0f, -1f), new Vector3(0f, 0f, 1f),
                new Vector3(-1f, 0f, 0f), new Vector3(1f, 0f, 0f),
                new Vector3(0f, -1f, 0f), new Vector3(0f, 1f, 0f)
            };
            var v = new Vector3[box.VertexCount];
            Array.Copy(box.Vertices, v, v.Length);
            for (int f = 0; f < 6; f++)
                for (int k = 0; k < 4; k++)
                    v[f * 4 + k] += normals[f] * gap;
            return new SoftBodyMeshData(v, box.Triangles);
        }

        [Test]
        public void Audit_NullData_ReportsBuildFailed()
        {
            // Given: 数据为 null
            // When/Then: 不抛异常，报 BuildFailed
            SoftBodyAuditResult[] box = new SoftBodyAuditResult[1];
            Assert.DoesNotThrow(() => box[0] = SoftBodyMeshAudit.Audit(null, Standard(), 1),
                "null 输入必须只报告、不许抛异常");
            Assert.AreEqual(SoftBodyAuditVerdict.BuildFailed, box[0].Verdict, "null 判定应是 BuildFailed");
            Assert.IsFalse(string.IsNullOrEmpty(box[0].BuildError), "null 输入也要给出错误原文");
        }

        // ==================================================================
        // Feature: 确定性 —— 计时之外的一切必须逐位一致
        // ==================================================================

        [Test]
        public void Audit_IsDeterministic_SameInputTwiceYieldsSameReportExceptTimings()
        {
            // Given: 同一份网格与参数，各审计一次（带地面、40 步）
            var a = Healthy(BoxMesh(0.8f, 1.4f, 1f), 40, true);
            var b = Healthy(BoxMesh(0.8f, 1.4f, 1f), 40, true);

            // Then: 除计时外每个字段逐位相等
            Assert.AreEqual(a.MeshVertexCount, b.MeshVertexCount, "网格顶点数应一致");
            Assert.AreEqual(a.ParticleCount, b.ParticleCount, "质点数应一致");
            Assert.AreEqual(a.StructuralSpringCount, b.StructuralSpringCount, "结构弹簧数应一致");
            Assert.AreEqual(a.BendSpringCount, b.BendSpringCount, "弯曲弹簧数应一致");
            Assert.AreEqual(a.IsClosed, b.IsClosed, "闭合性应一致");
            Assert.AreEqual(a.RestVolume, b.RestVolume, "静止体积应逐位一致");
            Assert.AreEqual(a.VolumeRetention, b.VolumeRetention, "体积保持率应逐位一致");
            Assert.AreEqual(a.MaxStretchRatio, b.MaxStretchRatio, "最大拉伸比应逐位一致");
            Assert.AreEqual(a.LowestWorldY, b.LowestWorldY, "最低质点 y 应逐位一致");
            Assert.AreEqual(a.Verdict, b.Verdict, "判定应一致");
        }

        // ==================================================================
        // Feature: 真实扫描暴露出来的三类漏报（这一节是用扫描结果反推出来的，不是设想出来的）
        // ==================================================================

        [Test]
        public void Audit_ReversedWindingBox_IsNotHealthy()
        {
            // Given: 同一个立方体，但每个面的绕序整体反过来 ⇒ 有向体积为负
            var box = BoxMesh(1f, 1f, 1f);
            var flipped = FlipWindings(box);

            // When:  审计
            var r = Healthy(flipped, 5, true);

            // Then:  闭合性为真、静止体积为负、判定必须是 InvertedWinding
            //        旧分类在这里会报 Healthy —— 因为保持率 V/V₀ 两个负数一除变成正的 1，
            //        「一块里朝外的果冻」就这么蒙混过关了
            Assert.IsTrue(r.IsClosed, "反过来的绕序仍然是每条边 2 个三角形");
            Assert.Less(r.RestVolume, 0f, "有向体积应为负，实际 " + r.RestVolume);
            Assert.AreEqual(SoftBodyAuditVerdict.InvertedWinding, r.Verdict, "实际：" + r);
            StringAssert.Contains("绕序朝内", SoftBodyMeshAudit.Note(r), "note 要说明为什么不算健康");
        }

        [Test]
        public void Audit_ZeroThicknessShell_ReportsDegenerateVolume()
        {
            // Given: 边长 4 毫米的闭合小方块 ⇒ 体积 6.4e-8 m³，围不住任何可回弹的余量
            var r = Healthy(BoxMesh(0.004f, 0.004f, 0.004f), 5, true);

            // Then:  判定 DegenerateVolume，而不是被"保持率 0"和"真的塌成 0"混成一谈
            Assert.IsTrue(r.IsClosed, "它是闭合的");
            Assert.GreaterOrEqual(r.RestVolume, 0f, "体积为正，不是绕序问题");
            Assert.Less(r.RestVolume, SoftBodyMeshAudit.NegligibleVolume, "体积应低于可忽略线");
            Assert.AreEqual(SoftBodyAuditVerdict.DegenerateVolume, r.Verdict, "实际：" + r);
            StringAssert.Contains("围住体积", SoftBodyMeshAudit.Note(r), "note 要给出体积数字");
        }

        [Test]
        public void Audit_BuildFailed_RowCarriesTheReasonInMarkdown()
        {
            // Given: 一个必然构建失败的输入
            var r = Healthy(new SoftBodyMeshData(new Vector3[0], new int[0]), 1);

            // When:  压成报告行
            string row = SoftBodyMeshAudit.ToMarkdownRow("broken", r);

            // Then:  行里必须有错误原文 —— 只有"BuildFailed"三个字的报告等于让人去猜
            StringAssert.Contains("BuildFailed", row, "判定要在行里");
            // 不断具体措辞，只断不变量：note 必须原样出现在表格里（扫描报告只有表格这一份输出）
            string note = SoftBodyMeshAudit.Note(r);
            Assert.IsNotEmpty(note, "BuildFailed 必须带原因，实际 BuildError=" + r.BuildError);
            StringAssert.Contains("顶点", note, "原因要说清是哪里建不起来，实际：" + note);
            StringAssert.Contains(note, row, "note 必须落在表格里");
            Assert.AreEqual(SoftBodyMeshAudit.MarkdownHeader.Split('|').Length, row.Split('|').Length,
                "note 列也必须保持列数一致：" + row);
        }

        [Test]
        public void Audit_RowsWithPipesOrNewlinesInError_StillKeepColumnCount()
        {
            // Given: 错误原文里带换行与竖线（真实异常消息经常这样）
            var r = Healthy(new SoftBodyMeshData(new Vector3[0], new int[0]), 1);
            r.BuildError = "第一行\n含有 | 竖线 | 的异常消息";

            // When:  压成行
            string row = SoftBodyMeshAudit.ToMarkdownRow("x", r);

            // Then:  不撕坏表格
            Assert.AreEqual(1, row.Split('\n').Length, "行里不许有换行：" + row);
            Assert.AreEqual(SoftBodyMeshAudit.MarkdownHeader.Split('|').Length, row.Split('|').Length,
                "列数必须与表头一致：" + row);
        }

        [Test]
        public void Audit_HealthyRow_NoteCellIsPlaceholder_NotUnnamed()
        {
            // Given: 一个健康的立方体（没有任何需要额外说明的事）
            var r = Healthy(BoxMesh(1f, 1f, 1f), 5, true);

            // When:  压成报告行
            string row = SoftBodyMeshAudit.ToMarkdownRow("box", r);

            // Then:  note 格给一个明确的空占位，而不是 "(unnamed)"
            //        —— Sanitize 对空串的兜底是给“名字”用的，串到 note 列上会让人以为
            //        这一行有个叫 (unnamed) 的东西要解释（真实扫描里每一行 Healthy 都中了这招）
            Assert.AreEqual(SoftBodyAuditVerdict.Healthy, r.Verdict, "实际：" + r);
            Assert.AreEqual("", SoftBodyMeshAudit.Note(r), "健康行不该有 note");
            string last = row.TrimEnd('|').Split('|')[row.Split('|').Length - 2].Trim();
            Assert.AreEqual("-", last, "note 格应该是“-”这类明确占位，实际：" + last);
        }

        [Test]
        public void Audit_ReversedWinding_NoteCellCarriesIt()
        {
            // Given/When: 绕序朝内的盒子
            var r = Healthy(FlipWindings(BoxMesh(1f, 1f, 1f)), 5, true);
            string row = SoftBodyMeshAudit.ToMarkdownRow("flip", r);

            // Then:  这一格的 note 不是占位符，而是真实原因
            Assert.AreNotEqual("-", row.TrimEnd('|').Split('|')[row.Split('|').Length - 2].Trim(),
                "InvertedWinding 必须带原因");
        }

        /// <summary>把每个三角形的后两个索引互换 ⇒ 有向体积取反（绕序整体翻面）。</summary>
        static SoftBodyMeshData FlipWindings(SoftBodyMeshData box)
        {
            var t = (int[])box.Triangles.Clone();
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                int tmp = t[i + 1]; t[i + 1] = t[i + 2]; t[i + 2] = tmp;
            }
            return new SoftBodyMeshData(box.Vertices, t);
        }

        // ==================================================================
        // Feature: 报告可读性 —— 扫描结果要能直接贴进文档
        // ==================================================================

        [Test]
        public void Audit_DescribeVerdict_UsesStableEnglishTokens()
        {
            // When: 拿一个健康结果
            var ok = Healthy(BoxMesh(1f, 1f, 1f), 1);

            // Then: 标记就是枚举名本身（文档表格与 grep 都依赖这个稳定性）
            Assert.AreEqual("Healthy", SoftBodyMeshAudit.DescribeVerdict(ok), "DescribeVerdict 应与枚举名逐字一致");
            Assert.AreEqual("OpenMesh", SoftBodyMeshAudit.DescribeVerdict(Healthy(OpenGridPlane(4, 1f), 1)), "开放网格标记");
            Assert.AreEqual("BuildFailed", SoftBodyMeshAudit.DescribeVerdict(Healthy(null, 1)), "null 输入标记");
        }

        [Test]
        public void Audit_ToMarkdownRow_FormatsOneLinePerMesh()
        {
            // Given: 一个健康结果
            var r = Healthy(BoxMesh(1f, 1f, 1f), 10);

            // When: 压成一行 Markdown
            string row = SoftBodyMeshAudit.ToMarkdownRow("prop_crate", r);
            string header = SoftBodyMeshAudit.MarkdownHeader;
            string sep = SoftBodyMeshAudit.MarkdownSeparator;

            // Then: 单行、竖线开头结尾、列数与表头/分隔行一致、含关键字段
            Assert.IsNotNull(row);
            Assert.AreEqual(1, row.Split('\n').Length, "表格行必须是单行：" + row);
            Assert.IsTrue(row.StartsWith("| "), "应以竖线开始：" + row);
            Assert.IsTrue(row.EndsWith("|"), "应以竖线结束：" + row);
            Assert.AreEqual(header.Split('|').Length, sep.Split('|').Length, "表头与分隔行列数必须一致");
            Assert.AreEqual(header.Split('|').Length, row.Split('|').Length, "行与表头列数必须一致：" + row);
            StringAssert.Contains("prop_crate", row, "行里要有模型名");
            StringAssert.Contains("24", row, "行里要有网格顶点数");
            StringAssert.Contains("Healthy", row, "行里要有判定");
        }

        [Test]
        public void Audit_ToMarkdownRow_SanitizesPipesInName()
        {
            // Given: 名字里带竖线（真实资产路径里可能出现）
            var r = Healthy(BoxMesh(1f, 1f, 1f), 1);

            // When: 压成一行
            string row = SoftBodyMeshAudit.ToMarkdownRow("a|b\nc", r);

            // Then: 竖线与换行被替掉，列数不被撕坏
            Assert.IsFalse(row.Contains("\n"), "行里不许有换行");
            Assert.AreEqual(SoftBodyMeshAudit.MarkdownHeader.Split('|').Length, row.Split('|').Length,
                "名字里的竖线不能改变列数：" + row);
        }
    }
}
