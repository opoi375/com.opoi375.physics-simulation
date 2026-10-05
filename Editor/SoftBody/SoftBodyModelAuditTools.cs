// Copyright (c) 2026 PhysicsSimulation. MIT License.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace PhysicsSimulation.EditorTools
{
    /// <summary>
    /// 模型扫描工具：把"这套软体放到我的真实资产上会是什么样"变成一张表。
    ///
    /// 两个入口：
    ///  - **Audit Selected Meshes (123)**：审计 Project/Hierarchy 里选中的东西（GameObject / FBX / Mesh 都吃）。
    ///  - **Audit Mesh Assets In Folder (124)**：审计 Project 里选中的**文件夹**（没选文件夹就用 <c>Assets</c>），
    ///    带数量上限，避免一上来扫几千个网格把编辑器挂住。
    ///
    /// 输出是一份 Markdown 表（表头/分隔行/数据行/按判定分组的汇总），直接贴进文档或 issue 就能看。
    /// 若项目根存在 <c>Logs/</c> 目录，同时写一份 <c>Logs/SoftBodyMeshAudit.md</c>；没有就只打 Console。
    ///
    /// 只读工具：不创建、不修改、不保存任何资产或场景。
    /// </summary>
    public static class SoftBodyModelAuditTools
    {
        public const string MenuPathAuditSelection = "Tools/Physics Simulation/Soft Body/Audit Selected Meshes (123)";
        public const string MenuPathAuditFolder = "Tools/Physics Simulation/Soft Body/Audit Mesh Assets In Folder (124)";

        /// <summary>每个模型跑多少步。90 步 @ 1/60 = 1.5 秒，够落到底并压扁一次。</summary>
        public const int AuditSteps = 90;

        /// <summary>单次扫描最多审计多少个网格（超出的会写明被跳过多少个）。</summary>
        public const int MaxMeshesPerScan = 120;

        /// <summary>
        /// 超过这个顶点数的网格<b>不参与逐步模拟</b>，只在报告里列名。
        ///
        /// 这是一条刻意的预算线：扫描工具不该把编辑器挂住几分钟。
        /// 一条 5000 顶点的 prop 在这里是 90 步 × 4 子步 × 上万约束，单它一个就能吃掉一整分钟。
        /// </summary>
        public const int MaxVertexCountForAudit = 4000;

        /// <summary>
        /// 按预算挑选待审计网格：先按顶点数过滤，再按输入顺序取前 <paramref name="maxCount"/> 个。
        /// 顺序稳定（不排序、不并行），所以同一批资产两次扫描得到同一份表。
        /// </summary>
        public static List<KeyValuePair<string, SoftBodyMeshData>> SelectWithinBudget(
            IList<KeyValuePair<string, SoftBodyMeshData>> all, int maxCount, int maxVertexCount,
            out int skippedTooBig, out int skippedOverCap)
        {
            var picked = new List<KeyValuePair<string, SoftBodyMeshData>>();
            skippedTooBig = 0;
            skippedOverCap = 0;
            if (all == null) return picked;

            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].Value == null || all[i].Value.VertexCount > maxVertexCount)
                {
                    skippedTooBig++;
                    continue;
                }
                if (picked.Count >= maxCount)
                {
                    skippedOverCap++;
                    continue;
                }
                picked.Add(all[i]);
            }
            return picked;
        }

        /// <summary>
        /// 审计参数。<b>刻意保持与运行时默认值逐字段相同</b> —— 扫描要暴露的就是默认配置下的真实表现，
        /// 调出一组"只在扫描里好看"的参数是自欺欺人。
        /// </summary>
        public static SoftBodyParameters DefaultParameters
        {
            get { return new SoftBodyParameters(); }
        }

        // ======================================================================
        // 纯函数部分（可单测，不碰 AssetDatabase）
        // ======================================================================

        /// <summary>
        /// 从一组对象里抽出网格数据。名字取对象名；FBX/模型取它所有子资产里的网格；GameObject 取 MeshFilter。
        /// 没有网格的对象、null、非 UnityEngine.Object 一律跳过 —— 绝不抛异常。
        /// </summary>
        public static List<KeyValuePair<string, SoftBodyMeshData>> CollectMeshDatas(IList<UnityEngine.Object> assets)
        {
            var list = new List<KeyValuePair<string, SoftBodyMeshData>>();
            if (assets == null) return list;

            for (int i = 0; i < assets.Count; i++)
            {
                var obj = assets[i];
                if (obj == null) continue;

                var mesh = obj as Mesh;
                if (mesh != null)
                {
                    AddIfUsable(list, obj.name, mesh);
                    continue;
                }

                var go = obj as GameObject;
                if (go != null)
                {
                    var filters = go.GetComponentsInChildren<MeshFilter>(true);
                    for (int f = 0; f < filters.Length; f++)
                        AddIfUsable(list, go.name + (filters.Length > 1 ? "[" + f + "]" : ""), filters[f].sharedMesh);
                    continue;
                }

                // 模型/FBX 之类的资产：把子资产里的网格都捞出来
                string path = AssetDatabase.GetAssetPath(obj);
                if (!string.IsNullOrEmpty(path))
                {
                    var sub = AssetDatabase.LoadAllAssetsAtPath(path);
                    for (int s = 0; s < sub.Length; s++)
                    {
                        var m = sub[s] as Mesh;
                        if (m != null) AddIfUsable(list, obj.name + "/" + m.name, m);
                    }
                }
            }
            return list;
        }

        static void AddIfUsable(List<KeyValuePair<string, SoftBodyMeshData>> into, string name, Mesh mesh)
        {
            if (mesh == null) return;
            // 只读拷贝，不碰资产本体
            into.Add(new KeyValuePair<string, SoftBodyMeshData>(name, SoftBodyMeshData.FromMesh(mesh)));
        }

        /// <summary>
        /// 按尺寸给的推荐参数。<b>诊断建议，不是包的默认值</b> —— 默认值保持保守，扫描报告负责告诉你该改哪里。
        ///
        /// 依据（都是扫描里量出来的）：质点质量是<b>每点 1 kg、与尺寸无关</b>，
        /// 所以一个 7 米高的钟楼和一个 10 厘米的花有完全一样的"绝对重量/刚度比"错觉
        /// —— 前者会被自身重量压塌甚至翻面，后者根本不会。刚度与子步必须跟着包围盒对角线走。
        /// </summary>
        public static SoftBodyParameters RecommendedParameters(float diagonalMeters)
        {
            var p = new SoftBodyParameters();
            // 1 米及以下保持默认；超过之后线性放大，最多 8 倍（对角线 8 米以上再大也先按 8 算）
            float scale = Mathf.Clamp(diagonalMeters, 1f, 8f);
            p.springStiffness = 1200f * scale;
            p.bendStiffness = 150f * scale;
            p.volumeStiffness = 4000f * scale;
            p.substeps = 4 + Mathf.RoundToInt(scale - 1f); // 1 米时正好是默认的 4，最大 11
            return p;
        }

        /// <summary>网格数据自身的包围盒对角线（米）。null 或空返回 0。</summary>
        public static float Diagonal(SoftBodyMeshData data)
        {
            if (data == null || data.Vertices == null || data.Vertices.Length == 0) return 0f;
            Vector3 lo = data.Vertices[0], hi = lo;
            for (int i = 1; i < data.Vertices.Length; i++)
            {
                lo = Vector3.Min(lo, data.Vertices[i]);
                hi = Vector3.Max(hi, data.Vertices[i]);
            }
            return (hi - lo).magnitude;
        }

        /// <summary>抽取 + 逐个审计；返回与输入同序的结果（顺序本身就是可复现的一部分）。</summary>
        public static List<KeyValuePair<string, SoftBodyAuditResult>> AuditObjects(IList<UnityEngine.Object> objects, int steps, bool addGround)
        {
            return AuditDatas(CollectMeshDatas(objects), steps, addGround);
        }

        /// <summary>审计一组已抽好的网格数据（默认参数）。</summary>
        public static List<KeyValuePair<string, SoftBodyAuditResult>> AuditDatas(
            IList<KeyValuePair<string, SoftBodyMeshData>> datas, int steps, bool addGround)
        {
            return AuditDatas(datas, steps, addGround, false);
        }

        /// <summary>
        /// 审计一组网格数据。<paramref name="recommended"/> 为真时改用 <see cref="RecommendedParameters"/>
        /// （按每个网格自己的尺寸给），用来回答"这些不健康的到底该怎么调"。
        /// </summary>
        public static List<KeyValuePair<string, SoftBodyAuditResult>> AuditDatas(
            IList<KeyValuePair<string, SoftBodyMeshData>> datas, int steps, bool addGround, bool recommended)
        {
            var rows = new List<KeyValuePair<string, SoftBodyAuditResult>>();
            if (datas == null) return rows;
            for (int i = 0; i < datas.Count; i++)
            {
                var p = recommended ? RecommendedParameters(Diagonal(datas[i].Value)) : DefaultParameters;
                rows.Add(new KeyValuePair<string, SoftBodyAuditResult>(
                    datas[i].Key, SoftBodyMeshAudit.Audit(datas[i].Value, p, steps, addGround)));
            }
            return rows;
        }

        /// <summary>
        /// 两轮扫描的对照：好转 / 变差 / 不变，以及"换了参数还是不行"的名单。
        /// 只统计判定等级，不统计数字，因为数字本来就该逐位一致（不一致就是不确定性 bug）。
        /// </summary>
        public static string BuildComparison(
            IList<KeyValuePair<string, SoftBodyAuditResult>> baseline,
            IList<KeyValuePair<string, SoftBodyAuditResult>> tuned)
        {
            var sb = new StringBuilder();
            int improved = 0, worse = 0, same = 0, stillBad = 0;
            var badNames = new List<string>();
            int n = baseline == null || tuned == null ? 0 : Math.Min(baseline.Count, tuned.Count);
            for (int i = 0; i < n; i++)
            {
                int a = Rank(baseline[i].Value.Verdict);
                int b = Rank(tuned[i].Value.Verdict);
                if (b < a) improved++;
                else if (b > a) { worse++; stillBad++; if (badNames.Count < 20) badNames.Add(baseline[i].Key + " " + baseline[i].Value.Verdict + "→" + tuned[i].Value.Verdict); }
                else
                {
                    same++;
                    if (b >= 3 && badNames.Count < 20) { stillBad++; badNames.Add(baseline[i].Key + " 仍然 " + baseline[i].Value.Verdict); }
                }
            }
            sb.Append("对照：好转 ").Append(improved).Append(" | 不变 ").Append(same)
              .Append(" | 变差 ").Append(worse).Append(" | 换了推荐参数仍然不健康 ").Append(stillBad).Append('\n');
            for (int i = 0; i < badNames.Count; i++) sb.Append("  · ").Append(badNames[i]).Append('\n');
            return sb.ToString();
        }

        /// <summary>判定好坏排序：数字越小越好。</summary>
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
                default: return 4;                       // BuildFailed
            }
        }

        /// <summary>
        /// 把结果拼成 Markdown 报告：表头 + 分隔行 + 每模型一行 + 按判定分组的汇总。
        /// </summary>
        public static string BuildReport(IList<KeyValuePair<string, SoftBodyAuditResult>> rows)
        {
            var sb = new StringBuilder();
            sb.Append("软体模型扫描（步骤 ").Append(AuditSteps).Append(" × 1/60 s，参数为运行时默认值）").Append('\n');
            sb.Append('\n');
            sb.Append(SoftBodyMeshAudit.MarkdownHeader).Append('\n');
            sb.Append(SoftBodyMeshAudit.MarkdownSeparator).Append('\n');

            int healthy = 0, open = 0, degenerate = 0, unstable = 0, failed = 0;
            int inverted = 0, thinShell = 0, other = 0;
            double slowest = 0.0;
            string slowestName = "(none)";

            if (rows != null)
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    var name = rows[i].Key;
                    var r = rows[i].Value;
                    sb.Append(SoftBodyMeshAudit.ToMarkdownRow(name, r)).Append('\n');
                    // 每个判定单独一格；新加枚举值时会走 default → other，
                    // 不会再像以前那样被 default 吞进 BuildFailed 里把合计说错
                    switch (r.Verdict)
                    {
                        case SoftBodyAuditVerdict.Healthy: healthy++; break;
                        case SoftBodyAuditVerdict.OpenMesh: open++; break;
                        case SoftBodyAuditVerdict.DegenerateWeld: degenerate++; break;
                        case SoftBodyAuditVerdict.Unstable: unstable++; break;
                        case SoftBodyAuditVerdict.InvertedWinding: inverted++; break;
                        case SoftBodyAuditVerdict.DegenerateVolume: thinShell++; break;
                        case SoftBodyAuditVerdict.BuildFailed: failed++; break;
                        default: other++; break;
                    }
                    if (r.MsPerStep > slowest) { slowest = r.MsPerStep; slowestName = name; }
                }
            }

            int total = rows == null ? 0 : rows.Count;
            sb.Append('\n');
            sb.Append("合计 ").Append(total)
              .Append(" 个网格：Healthy ").Append(healthy)
              .Append(" | OpenMesh ").Append(open)
              .Append(" | DegenerateWeld ").Append(degenerate)
              .Append(" | Unstable ").Append(unstable)
              .Append(" | InvertedWinding ").Append(inverted)
              .Append(" | DegenerateVolume ").Append(thinShell)
              .Append(" | BuildFailed ").Append(failed);
            if (other > 0) sb.Append(" | 未知判定 ").Append(other);
            sb.Append('\n');
            AssertBucketSum(total, healthy, open, degenerate, unstable, inverted, thinShell, failed, other);
            sb.Append("最慢：").Append(slowestName).Append(" ").Append(slowest.ToString("0.000")).Append(" ms/步").Append('\n');
            return sb.ToString();
        }

        /// <summary>
        /// 兵不走样的检查：各档之和必须等于总数。对账不上时宁可报错也不能输出一张数字错的表
        /// （扫描报告唯一的用处就是那些数字）。
        /// </summary>
        static void AssertBucketSum(int total, params int[] buckets)
        {
            int sum = 0;
            for (int i = 0; i < buckets.Length; i++) sum += buckets[i];
            if (sum != total)
                throw new InvalidOperationException(
                    "报告分档对不上账：各档之和 " + sum + " ≠ 总数 " + total + "，有判定没被计入");
        }

        // ======================================================================
        // 菜单入口
        // ======================================================================

        [MenuItem(MenuPathAuditSelection)]
        public static void AuditSelection()
        {
            var selected = Selection.objects;
            var rows = AuditObjects(selected, AuditSteps, true);
            if (rows.Count == 0)
            {
                Debug.Log("[PhysicsSimulation] 模型扫描：选中的东西里没有网格（试试选 FBX、GameObject 或 Mesh 资产）");
                return;
            }
            Emit(BuildReport(rows), rows.Count);
        }

        [MenuItem(MenuPathAuditFolder)]
        public static void AuditFolder()
        {
            string folder = ResolveFolder();
            var paths = new List<string>();
            foreach (var guid in AssetDatabase.FindAssets("t:Model t:Mesh", new[] { folder }))
                paths.Add(AssetDatabase.GUIDToAssetPath(guid));
            paths.Sort(StringComparer.Ordinal);   // 同一批资产两次扫描得到同一张表

            var all = new List<KeyValuePair<string, SoftBodyMeshData>>();
            for (int i = 0; i < paths.Count; i++)
            {
                var a = AssetDatabase.LoadMainAssetAtPath(paths[i]);
                if (a == null) continue;
                all.AddRange(CollectMeshDatas(new UnityEngine.Object[] { a }));
            }

            int tooBig, overCap;
            var picked = SelectWithinBudget(all, MaxMeshesPerScan, MaxVertexCountForAudit, out tooBig, out overCap);

            var rows = AuditDatas(picked, AuditSteps, true, false);
            var tuned = AuditDatas(picked, AuditSteps, true, true);
            var report = BuildReport(rows)
                       + "\n\n# 同一批网格，改用按包围盒对角线放大的推荐参数\n\n"
                       + BuildReport(tuned)
                       + "\n" + BuildComparison(rows, tuned);
            if (tooBig > 0 || overCap > 0)
                report += "\n预算线跳过：顶点数 > " + MaxVertexCountForAudit + " 的 " + tooBig
                        + " 个；超出单次上限 " + MaxMeshesPerScan + " 的 " + overCap + " 个。\n";
            Emit(report + "\n扫描目录：" + folder + "（共发现 " + all.Count + " 个网格）", rows.Count);
        }

        static string ResolveFolder()
        {
            var obj = Selection.activeObject;
            string path = obj == null ? null : AssetDatabase.GetAssetPath(obj);
            if (!string.IsNullOrEmpty(path) && AssetDatabase.IsValidFolder(path)) return path;
            return "Assets";
        }

        static void Emit(string report, int count)
        {
            Debug.Log("[PhysicsSimulation] 模型扫描 " + count + " 个网格\n" + report);

            // Logs/ 存在才写文件：那是主工程/CI 收集输出的地方，不该由包凭空创建
            try
            {
                string logs = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Logs");
                if (Directory.Exists(logs))
                {
                    string file = Path.Combine(logs, "SoftBodyMeshAudit.md");
                    File.WriteAllText(file, report);
                    Debug.Log("[PhysicsSimulation] 扫描报告已写出：" + file);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PhysicsSimulation] 扫描报告写文件失败（不影响 Console 输出）：" + e.Message);
            }
        }
    }
}
