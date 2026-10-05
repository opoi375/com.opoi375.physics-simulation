// Copyright (c) 2026 PhysicsSimulation. MIT License.
using System;
using System.Collections.Generic;
using NUnit.Framework;
using PhysicsSimulation.EditorTools;
using UnityEditor;
using UnityEngine;

namespace PhysicsSimulation.Editor.Tests
{
    /// <summary>
    /// 软体模型审计工具（SoftBodyModelAuditTools）行为规格。
    ///
    /// 设计约束：工具壳子必须能在**不依赖任何项目资产**的前提下被断言
    /// —— 包是被人 Git 引用的，测试不能假设使用者电脑上有某些 FBX。
    /// 所以"从一堆对象里抽网格"和"把结果拼成报告"都做成了纯函数。
    /// </summary>
    public class SoftBodyModelAuditToolsTests
    {
        [Test]
        public void CollectMeshDatas_FromGameObjects_ReturnsNamedMeshDatas()
        {
            // Given: 两个运行时生成的 GameObject，各挂一个 MeshFilter
            var box = new GameObject("AuditBox");
            box.AddComponent<MeshFilter>().sharedMesh = MakeMesh(BoxData(1f, 1f, 1f));
            var sheet = new GameObject("AuditSheet");
            sheet.AddComponent<MeshFilter>().sharedMesh = MakeMesh(GridData(4, 1f));

            // When:  抽取
            var collected = SoftBodyModelAuditTools.CollectMeshDatas(new UnityEngine.Object[] { box, sheet });

            // Then:  两条、带名字、顶点与三角形非空
            Assert.AreEqual(2, collected.Count, "应抽到两个网格");
            Assert.AreEqual("AuditBox", collected[0].Key, "名字应取 GameObject 名");
            Assert.AreEqual(24, collected[0].Value.VertexCount, "立方体是 24 个按面拆开的顶点");
            Assert.AreEqual(12, collected[0].Value.TriangleCount, "12 个三角形");
            Assert.Greater(collected[1].Value.VertexCount, 0, "网格片要有顶点");
            UnityEngine.Object.DestroyImmediate(box);
            UnityEngine.Object.DestroyImmediate(sheet);
        }

        [Test]
        public void CollectMeshDatas_SkipsObjectsWithoutMeshes()
        {
            // Given: 空 GameObject（没有 MeshFilter）+ null
            var empty = new GameObject("NoMesh");

            // When:  抽取
            var collected = SoftBodyModelAuditTools.CollectMeshDatas(
                new UnityEngine.Object[] { empty, null });

            // Then:  跳过而不是抛 —— 在 Project 里全选一堆东西是常态
            Assert.AreEqual(0, collected.Count, "没有网格就该什么都不返回");
            UnityEngine.Object.DestroyImmediate(empty);
        }

        [Test]
        public void BuildReport_HeaderRowsAndSummary_AreAllPresent()
        {
            // Given: 三行结果（健康 / 开放 / 构建失败）
            var p = new SoftBodyParameters { substeps = 2 };
            var rows = new List<KeyValuePair<string, SoftBodyAuditResult>>
            {
                new KeyValuePair<string, SoftBodyAuditResult>("healthy", SoftBodyMeshAudit.Audit(BoxData(1f, 1f, 1f), p, 5, true)),
                new KeyValuePair<string, SoftBodyAuditResult>("sheet", SoftBodyMeshAudit.Audit(GridData(4, 1f), p, 5, true)),
                new KeyValuePair<string, SoftBodyAuditResult>("broken", SoftBodyMeshAudit.Audit(null, p, 5)),
            };

            // When:  拼报告
            string report = SoftBodyModelAuditTools.BuildReport(rows);

            // Then:  表头、分隔行、每行、按判定分组的汇总、总数
            StringAssert.Contains(SoftBodyMeshAudit.MarkdownHeader, report, "报告要有表头");
            StringAssert.Contains(SoftBodyMeshAudit.MarkdownSeparator, report, "报告要有分隔行");
            StringAssert.Contains("healthy", report, "每一行都要出现");
            StringAssert.Contains("sheet", report, "每一行都要出现");
            StringAssert.Contains("broken", report, "每一行都要出现");
            StringAssert.Contains("Healthy", report, "汇总要按判定分类计数");
            StringAssert.Contains("OpenMesh", report, "汇总要按判定分类计数");
            StringAssert.Contains("BuildFailed", report, "汇总要按判定分类计数");
            StringAssert.Contains("合计 3", report, "汇总要有总数");
        }

        [Test]
        public void BuildReport_EmptyRows_StillHasHeader()
        {
            // Given: 空列表（扫了个目录，一个模型都没有）
            // When:  拼报告
            string report = SoftBodyModelAuditTools.BuildReport(
                new List<KeyValuePair<string, SoftBodyAuditResult>>());

            // Then:  表头照给、合计 0，不抛
            StringAssert.Contains(SoftBodyMeshAudit.MarkdownHeader, report, "空扫描也要有表头");
            StringAssert.Contains("合计 0", report, "空扫描的汇总要写 0");
        }

        [Test]
        public void MenuPaths_AreUnderSoftBodySubmenuAndDoNotCollideWithExistingOnes()
        {
            // Then: 路径前缀对、优先级后缀对，且不与已有的 120~122 撞号
            StringAssert.StartsWith("Tools/Physics Simulation/Soft Body/",
                SoftBodyModelAuditTools.MenuPathAuditSelection, "菜单要在软体子菜单下");
            StringAssert.StartsWith("Tools/Physics Simulation/Soft Body/",
                SoftBodyModelAuditTools.MenuPathAuditFolder, "菜单要在软体子菜单下");
            StringAssert.EndsWith("(123)", SoftBodyModelAuditTools.MenuPathAuditSelection, "审计选中项优先级 123");
            StringAssert.EndsWith("(124)", SoftBodyModelAuditTools.MenuPathAuditFolder, "审计目录优先级 124");
            Assert.AreNotEqual(SoftBodyModelAuditTools.MenuPathAuditSelection,
                SoftBodyModelAuditTools.MenuPathAuditFolder, "两个菜单不能同名");
        }

        [Test]
        public void AuditObjects_ProducesOneRowPerMesh()
        {
            // Given: 一个带网格的 GameObject
            var go = new GameObject("AuditOne");
            go.AddComponent<MeshFilter>().sharedMesh = MakeMesh(BoxData(1f, 1f, 1f));

            // When:  走工具那套「抽取 → 逐个审计」
            var rows = SoftBodyModelAuditTools.AuditObjects(new UnityEngine.Object[] { go }, 3, true);

            // Then:  一行，判定是真的
            Assert.AreEqual(1, rows.Count, "一个网格就该有一行");
            Assert.AreEqual("AuditOne", rows[0].Key, "行名字要对");
            Assert.AreEqual(SoftBodyAuditVerdict.Healthy, rows[0].Value.Verdict, "实际：" + rows[0].Value);
            UnityEngine.Object.DestroyImmediate(go);
        }

        [Test]
        public void DefaultAuditParameters_AreTheSameOnesTheRuntimeUses()
        {
            // Then:  审计参数用的是 SoftBodyParameters 默认值（不能为了测试好看而偷偷改硬）
            var p = SoftBodyModelAuditTools.DefaultParameters;
            Assert.AreEqual(new SoftBodyParameters().substeps, p.substeps, "子步要和默认一致");
            Assert.AreEqual(new SoftBodyParameters().volumeStiffness, p.volumeStiffness, "体积刚度要和默认一致");
            Assert.AreEqual(new SoftBodyParameters().collisionThickness, p.collisionThickness, "皮肤厚度要和默认一致");
        }

        [Test]
        public void SelectWithinBudget_FiltersOversizedMeshesAndCapsCount()
        {
            // Given: 5 个网格，顶点数 10 / 20 / 5000 / 30 / 40（第三个远超预算线）
            var all = new List<KeyValuePair<string, SoftBodyMeshData>>
            {
                Kv("a", GridData(4, 1f)),        // 16 顶点
                Kv("b", GridData(5, 1f)),        // 25 顶点
                Kv("big", GridData(80, 1f)),     // 6400 顶点
                Kv("c", GridData(6, 1f)),        // 36 顶点
                Kv("d", GridData(7, 1f)),        // 49 顶点
            };

            // When:  上限 3 个、顶点预算 4000
            int tooBig, overCap;
            var picked = SoftBodyModelAuditTools.SelectWithinBudget(all, 3, 4000, out tooBig, out overCap);

            // Then:  大的被剔掉、剩下的按原顺序取前 3、超上限的计入 overCap
            Assert.AreEqual(3, picked.Count, "只该取到上限个数量");
            Assert.AreEqual("a", picked[0].Key, "顺序必须与输入一致（不排序、不并行）");
            Assert.AreEqual("b", picked[1].Key, "同上");
            Assert.AreEqual("c", picked[2].Key, "big 被顶点线拦下，所以第三个是 c");
            Assert.AreEqual(1, tooBig, "顶点数超限的个数要报出来");
            Assert.AreEqual(1, overCap, "超过单次上限的个数也要报出来");
        }

        [Test]
        public void SelectWithinBudget_EmptyOrNull_ReturnsEmpty()
        {
            // Given/When/Then: 空与 null 都不能报错（扫描工具最怕中途抽风）
            int tooBig, overCap;
            Assert.AreEqual(0, SoftBodyModelAuditTools.SelectWithinBudget(
                new List<KeyValuePair<string, SoftBodyMeshData>>(), 10, 4000, out tooBig, out overCap).Count,
                "空输入应该返空");
            Assert.AreEqual(0, SoftBodyModelAuditTools.SelectWithinBudget(null, 10, 4000, out tooBig, out overCap).Count,
                "null 输入应该返空而不是抛");
            Assert.AreEqual(0, tooBig);
            Assert.AreEqual(0, overCap);
        }

        [Test]
        public void AuditDatas_OneRowPerMesh_InInputOrder()
        {
            // Given: 三个网格数据（健康 / 开放 / 健康）
            var all = new List<KeyValuePair<string, SoftBodyMeshData>>
            {
                Kv("box", BoxData(1f, 1f, 1f)),
                Kv("sheet", GridData(4, 1f)),
                Kv("chest", BoxData(1f, 0.6f, 0.8f)),
            };

            // When:  逐行审计
            var rows = SoftBodyModelAuditTools.AuditDatas(all, 5, true);

            // Then:  一行一个、名字与顺序对得上、判定按真实拓扑给
            Assert.AreEqual(3, rows.Count, "一个网格一行");
            Assert.AreEqual("box", rows[0].Key);
            Assert.AreEqual("sheet", rows[1].Key);
            Assert.AreEqual(SoftBodyAuditVerdict.Healthy, rows[0].Value.Verdict, "实际：" + rows[0].Value);
            Assert.AreEqual(SoftBodyAuditVerdict.OpenMesh, rows[1].Value.Verdict, "实际：" + rows[1].Value);
            Assert.AreEqual(SoftBodyAuditVerdict.Healthy, rows[2].Value.Verdict, "实际：" + rows[2].Value);
        }

        [Test]
        public void RecommendedParameters_ScaleWithSizeAndStayAtDefaultsForSmallMeshes()
        {
            // Then: 1 米以内（含）就是运行时默认值 —— 不能为了“看着稳”把小模型也调硬
            var small = SoftBodyModelAuditTools.RecommendedParameters(0.3f);
            var d = new SoftBodyParameters();
            Assert.AreEqual(d.springStiffness, small.springStiffness, 1e-4f, "小模型应保持默认刚度");
            Assert.AreEqual(d.substeps, small.substeps, "小模型应保持默认子步");

            // And: 尺寸越大越硬、子步越多，且有上限（不无限放大）
            var mid = SoftBodyModelAuditTools.RecommendedParameters(3f);
            var big = SoftBodyModelAuditTools.RecommendedParameters(50f);
            Assert.Greater(mid.springStiffness, d.springStiffness, "3 米级要更硬");
            Assert.Greater(mid.substeps, d.substeps, "3 米级要多子步");
            Assert.AreEqual(SoftBodyModelAuditTools.RecommendedParameters(8f).springStiffness,
                            big.springStiffness, 1e-3f, "超过 8 米封在上限，不能无限放大");
        }

        [Test]
        public void Diagonal_MeasuresBoundingBoxDiagonal()
        {
            // Given/Then: 1×1×1 立方体的对角线 = √3（推荐参数就靠它缩放）
            Assert.AreEqual(Mathf.Sqrt(3f), SoftBodyModelAuditTools.Diagonal(BoxData(1f, 1f, 1f)), 1e-3f,
                "对角线是尺寸缩放依据，必须算包围盒对角线");
            Assert.AreEqual(0f, SoftBodyModelAuditTools.Diagonal(null), 1e-6f, "null 要返回 0 而不是抛");
        }

        [Test]
        public void AuditDatas_RecommendedModeIsNeverWorseThanDefault()
        {
            // Given: 一个 6 米见方的大盒子（扫描里这个量级在默认参数下会塌）
            var datas = new List<KeyValuePair<string, SoftBodyMeshData>> { Kv("bigbox", BoxData(6f, 6f, 6f)) };

            // When: 默认与推荐各跑一轮
            var def = SoftBodyModelAuditTools.AuditDatas(datas, 90, true, false);
            var rec = SoftBodyModelAuditTools.AuditDatas(datas, 90, true, true);

            // Then: 推荐参数不该比默认更差
            Assert.AreEqual(1, rec.Count, "一个网格一行");
            Assert.LessOrEqual(Rank(rec[0].Value.Verdict), Rank(def[0].Value.Verdict),
                "推荐参数不该比默认更差：默认 " + def[0].Value.Verdict + "，推荐 " + rec[0].Value.Verdict);
        }

        [Test]
        public void BuildComparison_CountsAndNamesStillBad()
        {
            // Given: 两轮结果，一个健康、一个两轮都构建失败
            var baseRows = new List<KeyValuePair<string, SoftBodyAuditResult>>
            {
                Kr("good", SoftBodyMeshAudit.Audit(BoxData(1f, 1f, 1f), new SoftBodyParameters(), 3, true)),
                Kr("bad", SoftBodyMeshAudit.Audit(null, new SoftBodyParameters(), 3)),
            };
            var tunedRows = new List<KeyValuePair<string, SoftBodyAuditResult>>
            {
                Kr("good", baseRows[0].Value),
                Kr("bad", baseRows[1].Value),
            };

            // When: 对照
            string cmp = SoftBodyModelAuditTools.BuildComparison(baseRows, tunedRows);

            // Then: 有计数、有“仍然不健康”的名单
            StringAssert.Contains("对照", cmp, "要有对照汇总");
            StringAssert.Contains("不变 2", cmp, "两轮判定相同就该计成不变，实际：" + cmp);
            StringAssert.Contains("bad", cmp, "仍然不健康的要列名字");
        }

        [Test]
        public void BuildReport_CountsEachVerdictIntoItsOwnBucket()
        {
            // Given: 四种判定各来一个（其中两个是扫描新加的：绕序朝内、体积可忽略）
            var rows = new List<KeyValuePair<string, SoftBodyAuditResult>>
            {
                Kr("healthy", SoftBodyMeshAudit.Audit(BoxData(1f, 1f, 1f), new SoftBodyParameters(), 3, true)),
                Kr("open", SoftBodyMeshAudit.Audit(GridData(4, 1f), new SoftBodyParameters(), 3, true)),
                Kr("flipped", SoftBodyMeshAudit.Audit(ReversedBox(), new SoftBodyParameters(), 3, true)),
                Kr("thin", SoftBodyMeshAudit.Audit(BoxData(0.004f, 0.004f, 0.004f), new SoftBodyParameters(), 3, true)),
                Kr("broken", SoftBodyMeshAudit.Audit(null, new SoftBodyParameters(), 3)),
            };

            // When:  生成报告
            string report = SoftBodyModelAuditTools.BuildReport(rows);

            // Then:  InvertedWinding / DegenerateVolume 单独成档，绝不能被塞进 BuildFailed 计数
            StringAssert.Contains("合计 5 个网格", report, "总数要对：" + report);
            StringAssert.Contains("BuildFailed 1", report,
                "只有真正构建失败的那一个该进 BuildFailed 档，实际：\n" + report);
            StringAssert.Contains("InvertedWinding 1", report, "绕序朝内要单独报");
            StringAssert.Contains("DegenerateVolume 1", report, "体积可忽略要单独报");
            StringAssert.Contains("OpenMesh 1", report, "开放网格要单独报");
        }

        /// <summary>绕序整体朝内的立方体（与审计测试里同一手：把每个三角形后两个索引互换）。</summary>
        static SoftBodyMeshData ReversedBox()
        {
            var box = BoxData(1f, 1f, 1f);
            var t = (int[])box.Triangles.Clone();
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                int tmp = t[i + 1]; t[i + 1] = t[i + 2]; t[i + 2] = tmp;
            }
            return new SoftBodyMeshData(box.Vertices, t);
        }

        /// <summary>与工具内部一致的好坏序（Healthy 最好）；测试里独立写一份，避免和被测代码共用同一张可能写错的表。</summary>
        static int Rank(SoftBodyAuditVerdict v)
        {
            switch (v)
            {
                case SoftBodyAuditVerdict.Healthy: return 0;
                case SoftBodyAuditVerdict.OpenMesh: return 1;
                case SoftBodyAuditVerdict.InvertedWinding: return 2;
                case SoftBodyAuditVerdict.DegenerateVolume: return 2;
                case SoftBodyAuditVerdict.DegenerateWeld: return 3;
                case SoftBodyAuditVerdict.Unstable: return 3;
                default: return 4;
            }
        }

        // ==================================================================
        // 测试辅助
        // ==================================================================

        static KeyValuePair<string, SoftBodyMeshData> Kv(string name, SoftBodyMeshData data)
        {
            return new KeyValuePair<string, SoftBodyMeshData>(name, data);
        }

        static KeyValuePair<string, SoftBodyAuditResult> Kr(string name, SoftBodyAuditResult result)
        {
            return new KeyValuePair<string, SoftBodyAuditResult>(name, result);
        }

        static readonly int[][] Faces =
        {
            new[] { 0, 3, 2, 1 }, new[] { 4, 5, 6, 7 }, new[] { 0, 4, 7, 3 },
            new[] { 1, 2, 6, 5 }, new[] { 0, 1, 5, 4 }, new[] { 3, 7, 6, 2 }
        };

        /// <summary>24 顶点（按面拆开）、绕序朝外的闭合立方体；面表与 v1.2.0 已验证的 MakeBox 同源。</summary>
        static SoftBodyMeshData BoxData(float w, float h, float d)
        {
            float hx = w * 0.5f, hy = h * 0.5f, hz = d * 0.5f;
            var c = new[]
            {
                new Vector3(-hx, -hy, -hz), new Vector3(hx, -hy, -hz), new Vector3(hx, hy, -hz), new Vector3(-hx, hy, -hz),
                new Vector3(-hx, -hy, hz), new Vector3(hx, -hy, hz), new Vector3(hx, hy, hz), new Vector3(-hx, hy, hz)
            };
            var v = new List<Vector3>();
            var t = new List<int>();
            foreach (var face in Faces)
            {
                int s = v.Count;
                for (int i = 0; i < 4; i++) v.Add(c[face[i]]);
                t.Add(s); t.Add(s + 1); t.Add(s + 2);
                t.Add(s); t.Add(s + 2); t.Add(s + 3);
            }
            return new SoftBodyMeshData(v.ToArray(), t.ToArray());
        }

        static SoftBodyMeshData GridData(int n, float size)
        {
            var v = new Vector3[n * n];
            for (int r = 0; r < n; r++)
                for (int q = 0; q < n; q++)
                    v[r * n + q] = new Vector3(q * size / (n - 1) - size * 0.5f, 0f, r * size / (n - 1) - size * 0.5f);
            var t = new List<int>();
            for (int r = 0; r < n - 1; r++)
                for (int q = 0; q < n - 1; q++)
                {
                    int a = r * n + q;
                    t.Add(a); t.Add(a + n); t.Add(a + 1);
                    t.Add(a + 1); t.Add(a + n); t.Add(a + n + 1);
                }
            return new SoftBodyMeshData(v, t.ToArray());
        }

        static Mesh MakeMesh(SoftBodyMeshData data)
        {
            var m = new Mesh { name = "AuditTestMesh" };
            m.vertices = data.Vertices;      // 先 vertices 再 triangles（v1.2.0 踩过：反了会被 Unity 拒索引）
            m.triangles = data.Triangles;
            m.RecalculateBounds();
            return m;
        }
    }
}
