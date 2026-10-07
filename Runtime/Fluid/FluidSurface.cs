// Copyright (c) 2026 PhysicsSimulation. MIT License.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace PhysicsSimulation
{
    /// <summary>
    /// 表面采样的体素网格。<b>节点</b>比<b>格子</b>每维多一个：X 个格子有 X+1 个节点。
    /// </summary>
    public struct FluidSurfaceGrid
    {
        public Vector3 Min;
        public Vector3 Cell;
        public int X;
        public int Y;
        public int Z;

        /// <summary>格子数。用 long：1e12 这个量级不是假设 —— 预算前曾把 int 撑成负数，
        /// 于是“超预算”判为“没超”，一个百万格的网格就这么静悄悄分下去了。</summary>
        public long CellCount { get { return (long)X * Y * Z; } }
        public int NodesX { get { return X + 1; } }
        public int NodesY { get { return Y + 1; } }
        public int NodesZ { get { return Z + 1; } }
        public long NodeCount { get { return (long)NodesX * NodesY * NodesZ; } }

        public int NodeIndex(int i, int j, int k)
        {
            return i + NodesX * j + NodesX * NodesY * k;
        }

        public Vector3 NodePosition(float i, float j, float k)
        {
            return Min + new Vector3(i * Cell.x, j * Cell.y, k * Cell.z);
        }

        public Vector3 Size { get { return new Vector3(X * Cell.x, Y * Cell.y, Z * Cell.z); } }

        /// <summary>把任意位置夹回网格范围（用于梯度取样，边界处退化成一阶差分但不产生 NaN）。</summary>
        public void ClampNode(ref int i, ref int j, ref int k)
        {
            i = Mathf.Clamp(i, 0, X); j = Mathf.Clamp(j, 0, Y); k = Mathf.Clamp(k, 0, Z);
        }
    }

    /// <summary>一张表面：顶点 + 外法线 + 右手绕序三角形。</summary>
    public struct FluidSurfaceMesh
    {
        public Vector3[] Vertices;
        public Vector3[] Normals;
        public int[] Triangles;

        public int TriangleCount { get { return Triangles == null ? 0 : Triangles.Length / 3; } }
        public bool IsEmpty { get { return Triangles == null || Triangles.Length == 0; } }

        public Bounds Bounds
        {
            get
            {
                if (Vertices == null || Vertices.Length == 0) return new Bounds(Vector3.zero, Vector3.zero);
                var b = new Bounds(Vertices[0], Vector3.zero);
                for (int i = 1; i < Vertices.Length; i++) b.Encapsulate(Vertices[i]);
                return b;
            }
        }

        public static FluidSurfaceMesh Empty
        {
            get { return new FluidSurfaceMesh { Vertices = new Vector3[0], Normals = new Vector3[0], Triangles = new int[0] }; }
        }
    }

    /// <summary>
    /// 粒子 → 水面：先按核权重把质点溅射（splat）成均匀体素标量场 α，再在 α = isoLevel 处取等值面。
    ///
    /// 为什么是 marching <b>tetrahedra</b> 而不是教科书那张 256 case 表：
    ///  * 那张表要手抄四千多个整数，抄错一处就得到一个"看着对、偶尔破面"的网格，测试还难发现；
    ///  * 每个立方体按 Kuhn 剖分成 6 个四面体，剖分对**平移不变**，相邻格子共享的那张面用的是同一条
    ///    对角线 —— 不产生 T 形接缝，等值面天生闭合；
    ///  * 二义性（马鞍面）由四面体自己消化，不需要额外规则。
    ///
    /// 确定性：一条晶边上的交点永远按"小编号节点在前"的规范顺序插值，共用这条边的两个四面体算式
    /// 逐字相同 ⇒ 顶点逐位一致；绕序最后统一按"外法线 = α 减小方向"翻正，与四面体枚举顺序无关。
    /// </summary>
    public static class FluidSurface
    {
        /// <summary>默认格子预算（64³ = 262144）。超了自动放大格子，而不是把内存吃掉。</summary>
        public const int DefaultMaxCells = 262144;

        /// <summary>默认阈值。场按"静止点阵的邻居权重和"归一化，1 ≈ 水体内部，0.5 大致就是表面。</summary>
        public const float DefaultIsoLevel = 0.5f;

        /// <summary>单张 Unity 网格的顶点上限（ushort 索引）。留余量，超了宁可明确报错。</summary>
        public const int MaxMeshVertices = 65000;

        // ==================================================================
        // 标量场

        /// <summary>
        /// 单个质点的核权重：Poly6 同族形状 (1 − q²)³，q = r/h。纯函数，
        /// 好直接断言"中心最大、r ≥ h 时恰好为零、单调不增"。
        /// </summary>
        public static float KernelWeight(float distance, float kernelRadius)
        {
            if (!(kernelRadius > 0f)) return 0f;
            if (distance >= kernelRadius) return 0f;
            float q = distance / kernelRadius;
            float s = 1f - q * q;
            return s * s * s;
        }

        /// <summary>
        /// 静止点阵（间距 spacing）中一个节点的邻居权重和。用它归一化后，水体内部的 α ≈ 1，
        /// 阈值 0.5 才是"半个密度"这种有物理含义的数，而不是一个要现场调的手感参数。
        /// </summary>
        public static float LatticeWeightSum(float kernelRadius, float spacing)
        {
            if (!(kernelRadius > 0f) || !(spacing > 0f)) return 1f;
            float sum = 0f;
            int n = Mathf.CeilToInt(kernelRadius / spacing);
            for (int i = -n; i <= n; i++)
                for (int j = -n; j <= n; j++)
                    for (int k = -n; k <= n; k++)
                        sum += KernelWeight(new Vector3(i * spacing, j * spacing, k * spacing).magnitude, kernelRadius);
            return sum > 1e-6f ? sum : 1f;
        }

        /// <summary>
        /// 把包围盒按 cellSize 切格，并保证格子数不超过 maxCells：超了就整体**放大**格子
        /// （只放大不缩小 ⇒ 一定收敛）。Min 不动，覆盖范围只多不少。
        /// </summary>
        public static FluidSurfaceGrid PlanGrid(Vector3 min, Vector3 max, float cellSize, int maxCells)
        {
            if (maxCells <= 0) maxCells = DefaultMaxCells;
            var extent = Vector3.Max(max - min, new Vector3(1e-4f, 1e-4f, 1e-4f));   // 退化盒子也得能算
            if (!(cellSize > 0f))
                cellSize = Mathf.Max(1e-4f, Mathf.Max(extent.x, Mathf.Max(extent.y, extent.z)) / 16f);

            var grid = new FluidSurfaceGrid { Min = min, Cell = new Vector3(cellSize, cellSize, cellSize) };
            for (int guard = 0; guard < 64; guard++)                                // 兜住取整，绝不死循环
            {
                grid.X = CellsAlong(extent.x, grid.Cell.x);
                grid.Y = CellsAlong(extent.y, grid.Cell.y);
                grid.Z = CellsAlong(extent.z, grid.Cell.z);
                if (grid.CellCount <= maxCells) break;
                float grow = Mathf.Max(1.02f, Mathf.Pow(grid.CellCount / (float)maxCells, 1f / 3f));
                grid.Cell *= grow;
            }
            return grid;
        }

        static int CellsAlong(float extent, float cellSize)
        {
            int n = Mathf.CeilToInt(extent / cellSize);
            return n > 1 ? n : 1;                                                   // 0 格的网格无从三角化
        }

        /// <summary>
        /// 溅射：把每个质点的核权重累加到周围节点，再除以静止点阵的权重和。
        /// 外层遍历固定为质点下标升序、不进任何哈希容器 ⇒ 同输入逐位可复现。
        /// </summary>
        public static float[] BuildField(IList<Vector3> positions, int count, FluidSurfaceGrid grid,
                                        float kernelRadius, float normalizeTo)
        {
            if (positions == null) throw new ArgumentNullException("positions", "质点数组不能为 null");
            if (!(normalizeTo > 0f)) normalizeTo = 1f;

            var field = new float[NodesOf(grid)];
            if (count <= 0 || !(kernelRadius > 0f)) return field;

            int ri = Mathf.CeilToInt(kernelRadius / grid.Cell.x);
            int rj = Mathf.CeilToInt(kernelRadius / grid.Cell.y);
            int rk = Mathf.CeilToInt(kernelRadius / grid.Cell.z);
            int planeStride = grid.NodesX * grid.NodesY;

            for (int p = 0; p < count; p++)
            {
                Vector3 pos = positions[p];
                if (float.IsNaN(pos.x) || float.IsNaN(pos.y) || float.IsNaN(pos.z)
                    || float.IsInfinity(pos.x) || float.IsInfinity(pos.y) || float.IsInfinity(pos.z))
                {
                    throw new ArgumentException("质点 " + p + " 的坐标不是有限值：" + pos, "positions");
                }

                Vector3 local = pos - grid.Min;
                int ci = Mathf.RoundToInt(local.x / grid.Cell.x);
                int cj = Mathf.RoundToInt(local.y / grid.Cell.y);
                int ck = Mathf.RoundToInt(local.z / grid.Cell.z);

                int i0 = Mathf.Max(0, ci - ri), i1 = Mathf.Min(grid.X, ci + ri);
                int j0 = Mathf.Max(0, cj - rj), j1 = Mathf.Min(grid.Y, cj + rj);
                int k0 = Mathf.Max(0, ck - rk), k1 = Mathf.Min(grid.Z, ck + rk);

                for (int k = k0; k <= k1; k++)
                {
                    float dz = k * grid.Cell.z - local.z;
                    int plane = k * planeStride;
                    for (int j = j0; j <= j1; j++)
                    {
                        float dy = j * grid.Cell.y - local.y;
                        int row = plane + j * grid.NodesX;
                        for (int i = i0; i <= i1; i++)
                        {
                            float dx = i * grid.Cell.x - local.x;
                            float w = KernelWeight((float)Math.Sqrt(dx * dx + dy * dy + dz * dz), kernelRadius);
                            if (w != 0f) field[row + i] += w;
                        }
                    }
                }
            }

            float invNorm = 1f / normalizeTo;
            for (int i = 0; i < field.Length; i++) field[i] *= invNorm;
            return field;
        }

        /// <summary>预算拦一道，这里再标一道：节点数超过 int 上限就明确报错，不拿 OOM 当错误信息。</summary>
        static int NodesOf(FluidSurfaceGrid grid)
        {
            long nodes = grid.NodeCount;
            if (nodes > 16000000L)
                throw new InvalidOperationException("采样节点数 " + nodes + " 超过上限 16000000："
                    + "请加大体素边长或收紧 maxCells（一格节点一个 float，再大就不是卡、是内存呷完了）");
            return (int)nodes;
        }

        // ==================================================================
        // 等值面（marching tetrahedra）

        // Kuhn 剖分：按三根轴的 6 种排列把立方体切成 6 个四面体。角点位码 bit0=+x, bit1=+y, bit2=+z。
        static readonly int[][] TetCorners = BuildTetCorners();

        static int[][] BuildTetCorners()
        {
            int[][] axisBits = { new[] { 1, 2, 4 }, new[] { 1, 4, 2 }, new[] { 2, 1, 4 },
                                 new[] { 2, 4, 1 }, new[] { 4, 1, 2 }, new[] { 4, 2, 1 } };
            var tets = new int[6][];
            for (int t = 0; t < 6; t++)
                tets[t] = new[] { 0, axisBits[t][0], axisBits[t][0] | axisBits[t][1], 7 };
            return tets;
        }

        // 四面体的 6 条边（角标编号）：0=(0,1) 1=(0,2) 2=(0,3) 3=(1,2) 4=(1,3) 5=(2,3)
        static readonly int[] EdgeA = { 0, 0, 0, 1, 1, 2 };
        static readonly int[] EdgeB = { 1, 2, 3, 2, 3, 3 };

        /// <summary>
        /// 取等值面。外法线指向 α 减小的方向（= 离开水体），绕序右手。
        /// </summary>
        public static FluidSurfaceMesh Triangleize(float[] field, FluidSurfaceGrid grid, float isoLevel)
        {
            if (field == null) throw new ArgumentNullException("field", "标量场不能为 null");
            if (field.Length != grid.NodeCount)
                throw new ArgumentException("标量场长度 " + field.Length + " 与网格节点数 " + grid.NodeCount
                                            + " 不符（网格改过就必须重新溅射）", "field");
            if (float.IsNaN(isoLevel) || float.IsInfinity(isoLevel))
                throw new ArgumentOutOfRangeException("isoLevel", "阈值必须是有限值，当前 " + isoLevel);

            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var triangles = new List<int>();
            var edgeVertex = new Dictionary<long, int>();        // 一条晶边只算一个交点

            var cornerNode = new int[8];
            var cornerValue = new float[8];
            var insideCorner = new bool[4];
            var crossing = new int[6];
            var ring = new int[4];

            for (int k = 0; k < grid.Z; k++)
                for (int j = 0; j < grid.Y; j++)
                    for (int i = 0; i < grid.X; i++)
                    {
                        for (int b = 0; b < 8; b++)
                        {
                            int node = grid.NodeIndex(i + (b & 1), j + ((b >> 1) & 1), k + ((b >> 2) & 1));
                            cornerNode[b] = node;
                            cornerValue[b] = field[node];
                        }

                        for (int t = 0; t < 6; t++)
                        {
                            int[] tc = TetCorners[t];
                            int nInside = 0;
                            for (int v = 0; v < 4; v++)
                            {
                                insideCorner[v] = cornerValue[tc[v]] > isoLevel;
                                if (insideCorner[v]) nInside++;
                            }
                            if (nInside == 0 || nInside == 4) continue;      // 整个四面体在一侧

                            for (int e = 0; e < 6; e++)
                            {
                                int a = tc[EdgeA[e]], b2 = tc[EdgeB[e]];
                                crossing[e] = (insideCorner[EdgeA[e]] == insideCorner[EdgeB[e]])
                                    ? -1
                                    : AddEdgeVertex(edgeVertex, vertices, normals, field, grid,
                                                    cornerNode[a], cornerNode[b2], isoLevel);
                            }

                            if (nInside == 1 || nInside == 3)
                                EmitTriangle(triangles, crossing, insideCorner, nInside);
                            else
                                EmitQuad(triangles, crossing, insideCorner, ring);
                        }
                    }

            if (vertices.Count == 0 || triangles.Count == 0) return FluidSurfaceMesh.Empty;

            var mesh = new FluidSurfaceMesh
            {
                Vertices = vertices.ToArray(),
                Normals = normals.ToArray(),
                Triangles = triangles.ToArray()
            };
            OrientTriangles(mesh);
            return mesh;
        }

        /// <summary>
        /// 一个角与其它三个不同号 ⇒ 切面是三角形，由与该角相连的三条边构成。
        /// 三条边的取出顺序无所谓：绕序在 OrientTriangles 里统一翻正。
        /// </summary>
        static void EmitTriangle(List<int> triangles, int[] crossing, bool[] insideCorner, int nInside)
        {
            int odd = -1;
            bool oddSign = nInside == 1;                        // 只有一个在内 ⇒ 异类就是那个内侧角
            for (int v = 0; v < 4; v++) if (insideCorner[v] == oddSign) { odd = v; break; }

            int a = -1, b = -1, c = -1;
            for (int e = 0; e < 6; e++)
            {
                if (EdgeA[e] != odd && EdgeB[e] != odd) continue;
                if (a < 0) a = crossing[e];
                else if (b < 0) b = crossing[e];
                else c = crossing[e];
            }
            AddTriangle(triangles, a, b, c);
        }

        /// <summary>
        /// 两个角在一侧 ⇒ 切面是四边形。按四面体的四个三角面推一下就知道四条穿越边怎么连成环：
        /// 同一张面上的两条穿越边必须相邻。于是三种配对各有唯一环序（见 RingAB/AC/BC）。
        /// 取 {0,mate} 就能定分区（2 内 2 外时，与角 0 同号的只有一个），环序与“谁在内侧”无关。
        /// </summary>
        static void EmitQuad(List<int> triangles, int[] crossing, bool[] insideCorner, int[] ring)
        {
            int mate = -1;
            for (int v = 1; v < 4; v++)
            {
                if (insideCorner[v] == insideCorner[0]) { mate = v; break; }
            }
            // 边编号：0=(0,1) 1=(0,2) 2=(0,3) 3=(1,2) 4=(1,3) 5=(2,3)
            //   分区 {01|23} → 穿越边 {1,2,3,4}，相邻关系 (1,3)(3,4)(4,2)(2,1) → 环 1,3,4,2
            //   分区 {02|13} → 穿越边 {0,2,3,5}，相邻关系 (0,3)(3,5)(5,2)(2,0) → 环 0,3,5,2
            //   分区 {03|12} → 穿越边 {0,1,4,5}，相邻关系 (0,1)(1,5)(5,4)(4,0) → 环 0,1,5,4
            int[] ringEdges = mate == 1 ? RingAB : (mate == 2 ? RingAC : RingBC);
            for (int v = 0; v < 4; v++) ring[v] = crossing[ringEdges[v]];
            AddTriangle(triangles, ring[0], ring[1], ring[2]);
            AddTriangle(triangles, ring[0], ring[2], ring[3]);
        }

        static readonly int[] RingAB = { 1, 3, 4, 2 };   // 同侧角 {0,1}（或 {2,3}）
        static readonly int[] RingAC = { 0, 3, 5, 2 };   // 同侧角 {0,2}（或 {1,3}）
        static readonly int[] RingBC = { 0, 1, 5, 4 };   // 同侧角 {0,3}（或 {1,2}）

        static void AddTriangle(List<int> triangles, int a, int b, int c)
        {
            if (a < 0 || b < 0 || c < 0) return;
            if (a == b || b == c || a == c) return;                     // 退化三角形丢掉，别让它进渲染
            triangles.Add(a); triangles.Add(b); triangles.Add(c);
        }

        static int AddEdgeVertex(Dictionary<long, int> cache, List<Vector3> vertices, List<Vector3> normals,
                                 float[] field, FluidSurfaceGrid grid, int nodeA, int nodeB, float isoLevel)
        {
            // 规范化：小编号在前 ⇒ 共用这条边的两个四面体写出的算式逐字相同
            int lo = nodeA < nodeB ? nodeA : nodeB;
            int hi = nodeA < nodeB ? nodeB : nodeA;
            long key = ((long)lo << 32) | (uint)hi;

            int existing;
            if (cache.TryGetValue(key, out existing)) return existing;

            float fLo = field[lo], fHi = field[hi];
            float denominator = fHi - fLo;
            float t = Mathf.Abs(denominator) < 1e-20f ? 0.5f : (isoLevel - fLo) / denominator;
            if (t < 0f) t = 0f; else if (t > 1f) t = 1f;                 // 数值兜底：顶点绝不允许跑到边外

            int nodesXY = grid.NodesX * grid.NodesY;
            float ai = lo % grid.NodesX, aj = (lo / grid.NodesX) % grid.NodesY, ak = lo / nodesXY;
            float bi = hi % grid.NodesX, bj = (hi / grid.NodesX) % grid.NodesY, bk = hi / nodesXY;
            Vector3 pos = grid.NodePosition(ai + (bi - ai) * t, aj + (bj - aj) * t, ak + (bk - ak) * t);

            Vector3 gradient = GridGradient(field, grid, pos);
            Vector3 outward = -gradient;                                  // 外法线 = α 减小的方向
            if (outward.sqrMagnitude < 1e-12f) outward = Vector3.up;      // 场平坦时给固定备用向，绝不 NaN

            int index = vertices.Count;
            vertices.Add(pos);
            normals.Add(outward.normalized);
            cache[key] = index;
            return index;
        }

        /// <summary>中心差分求 ∇α；越界夹到边界节点，边界处梯度变小但永远是有限值。</summary>
        public static Vector3 GridGradient(float[] field, FluidSurfaceGrid grid, Vector3 world)
        {
            Vector3 local = world - grid.Min;
            int i0 = Mathf.RoundToInt(local.x / grid.Cell.x);
            int j0 = Mathf.RoundToInt(local.y / grid.Cell.y);
            int k0 = Mathf.RoundToInt(local.z / grid.Cell.z);
            return new Vector3(
                Delta(field, grid, i0 + 1, j0, k0, i0 - 1, j0, k0, grid.Cell.x),
                Delta(field, grid, i0, j0 + 1, k0, i0, j0 - 1, k0, grid.Cell.y),
                Delta(field, grid, i0, j0, k0 + 1, i0, j0, k0 - 1, grid.Cell.z));
        }

        static float Delta(float[] field, FluidSurfaceGrid grid, int ip, int jp, int kp,
                           int im, int jm, int km, float cell)
        {
            grid.ClampNode(ref ip, ref jp, ref kp);
            grid.ClampNode(ref im, ref jm, ref km);
            float span = Mathf.Abs(ip - im) * cell + Mathf.Abs(jp - jm) * cell + Mathf.Abs(kp - km) * cell;
            if (span <= 1e-6f) return 0f;
            return (field[grid.NodeIndex(ip, jp, kp)] - field[grid.NodeIndex(im, jm, km)]) / span;
        }

        /// <summary>
        /// 统一绕序：让每个三角形的几何法线与顶点外法线同号。有了这一步，绕序与四面体枚举顺序
        /// 彻底无关 —— 换机器、换编译器、换循环顺序都不会翻面。
        /// </summary>
        static void OrientTriangles(FluidSurfaceMesh mesh)
        {
            Vector3[] v = mesh.Vertices, n = mesh.Normals;
            int[] tris = mesh.Triangles;
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                Vector3 face = Vector3.Cross(v[b] - v[a], v[c] - v[a]);
                Vector3 avg = n[a] + n[b] + n[c];
                if (Vector3.Dot(face, avg) < 0f) { tris[t + 1] = c; tris[t + 2] = b; }
            }
        }

        // ==================================================================
        // 一把梭 + 交付给 Unity

        /// <summary>
        /// 从质点位置直接出表面。<paramref name="margin"/> 一般取核半径：包围盒至少外扩这么多，
        /// 否则贴着边界的水会被截成一堵直墙。
        /// </summary>
        public static FluidSurfaceMesh Build(IList<Vector3> positions, int count, float cellSize,
                                             float kernelRadius, float particleSpacing, float isoLevel,
                                             int maxCells, float margin)
        {
            if (positions == null) throw new ArgumentNullException("positions", "质点数组不能为 null");
            if (count <= 0) return FluidSurfaceMesh.Empty;

            var min = positions[0];
            var max = min;
            for (int i = 1; i < count; i++)
            {
                min = Vector3.Min(min, positions[i]);
                max = Vector3.Max(max, positions[i]);
            }
            var pad = new Vector3(margin, margin, margin);
            FluidSurfaceGrid grid = PlanGrid(min - pad, max + pad, cellSize, maxCells);
            float[] field = BuildField(positions, count, grid, kernelRadius,
                                       LatticeWeightSum(kernelRadius, particleSpacing));
            return Triangleize(field, grid, isoLevel);
        }

        /// <summary>
        /// 交给 MeshFilter。<paramref name="surface"/> 为空时返回一张空网格（不是 null），
        /// 这样"水太少还画不出面"不会让渲染路径突然断掉。调用方负责销毁。
        /// </summary>
        public static Mesh ToMesh(FluidSurfaceMesh surface)
        {
            var mesh = new Mesh { name = "FluidSurface" };
            if (surface.IsEmpty)
            {
                mesh.vertices = new Vector3[0];
                mesh.triangles = new int[0];
                return mesh;
            }
            if (surface.Vertices.Length > MaxMeshVertices)
                throw new InvalidOperationException("表面顶点数 " + surface.Vertices.Length + " 超过单张网格上限 "
                    + MaxMeshVertices + "：请加大 surfaceCellSize 或收紧 maxCells（引擎的 ushort 索引只有 65535）");
            mesh.vertices = surface.Vertices;
            mesh.normals = surface.Normals;
            mesh.triangles = surface.Triangles;
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>调试用：数一下表面里有多少条边是"只被一个三角形用到"的边界边（闭合表面应为 0）。</summary>
        public static int CountBoundaryEdges(FluidSurfaceMesh surface)
        {
            if (surface.Triangles == null) return 0;
            var counts = new Dictionary<long, int>();
            for (int t = 0; t + 2 < surface.Triangles.Length; t += 3)
            {
                int a = surface.Triangles[t], b = surface.Triangles[t + 1], c = surface.Triangles[t + 2];
                Bump(counts, a, b); Bump(counts, b, c); Bump(counts, c, a);
            }
            int boundary = 0;
            foreach (var kv in counts) if (kv.Value != 2) boundary++;
            return boundary;
        }

        static void Bump(Dictionary<long, int> counts, int a, int b)
        {
            long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            int old;
            counts.TryGetValue(key, out old);
            counts[key] = old + 1;
        }
    }
}
