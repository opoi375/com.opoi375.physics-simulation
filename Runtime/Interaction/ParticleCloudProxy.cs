// Copyright (c) 2026 PhysicsSimulation. MIT License.
using System;
using UnityEngine;

namespace PhysicsSimulation
{
    /// <summary>
    /// 一朵会跟着体动的"质点球云"碰撞代理：把某个体的每个质点当成一个半径
    /// <see cref="IParticleBody.CloudRadius"/> 的球，点落进任意一个球就被顶到球面 + skin 之外。
    ///
    /// 三条设计约束，都是被实测逼出来的：
    ///
    /// 1. **读活体位置，不读快照**。代理持有 <see cref="IParticleBody"/> 引用，靠版本号自查：
    ///    源变了才重建索引。否则要么每帧白白重建（N 个体就是 O(N²)），要么拿到上一帧的旧云。
    ///    为什么不用 <c>Time.frameCount</c> 当守卫：三个 Behaviour 的步进时机本来就不一致
    ///    （软体与质点弹簧走 FixedUpdate，布料走 Update），而 EditMode 测试里 frameCount 根本不动，
    ///    用它会把第二次 Step 悄悄变成空操作，测出来一片假绿。
    ///
    /// 2. **顺序 = 哈希链表的插入顺序，且完全由输入决定**。多个球同时命中同一个点时逐个投影，
    ///    与 <see cref="CollisionSet"/> "迭代顺序即契约"的约定一致；同一份云、同一个点，
    ///    两个互不认识的代理必须给出逐位相同的结果。
    ///
    /// 3. **搜索半径要留一倍余量**。从 A 球里被顶出来，落点离原始点最远可达 need，
    ///    而那个位置仍可能落在另一颗球里，所以候选球要按 "need + 半径" 而不是 "need" 取。
    ///    少取这一份，就是"从缝隙里漏过去"。
    /// </summary>
    public sealed class ParticleCloudProxy : ICollisionProxy
    {
        const int MinTableSize = 64;
        const int MaxTableSize = 1 << 22;         // 4M 桶封顶：再大就该换 LOD 或者收规模了
        const float MinCellSize = 1e-4f;

        readonly IParticleBody _source;

        Vector3[] _world = new Vector3[0];        // 世界坐标拷贝（不能长期引用源给的缓冲区）
        int[] _head = new int[0];                 // 桶 -> 第一个质点下标，-1 表示空
        int[] _next = new int[0];                 // 质点 -> 同桶下一个，-1 表示尾
        int _tableMask;
        int _count;
        int _version = -1;
        float _radius;
        float _cellSize = MinCellSize;

        // 球团的“全局出路”方向：质心。是本索引自己的派生量，跟版本一起刷新，不引入新状态。
        Vector3 _centroid;

        public ParticleCloudProxy(IParticleBody source)
        {
            if (source == null)
            {
                throw new ArgumentNullException("source", "球云代理必须有一个活的质点源，null 会让每次查询都静默失效");
            }
            float radius = source.CloudRadius;
            if (!(radius > 0f) || float.IsInfinity(radius))
            {
                throw new ArgumentOutOfRangeException("source",
                    "球云半径必须是正有限值（米），当前 " + radius);
            }
            _source = source;
            Refresh();
        }

        /// <summary>这个代理对应的那个体。</summary>
        public IParticleBody Source { get { return _source; } }

        /// <summary>当前索引里的质点数。</summary>
        public int ParticleCount { get { return _count; } }

        /// <summary>索引所对应的源版本号；-1 表示还没建过。</summary>
        public int BuiltVersion { get { return _version; } }

        /// <summary>分桶格子的边长（米），诊断用。</summary>
        public float CellSize { get { return _cellSize; } }

        /// <summary>
        /// 源变了就重读世界坐标并重建分桶。返回 1 表示这次真的重建了，0 表示版本没动、直接复用。
        /// </summary>
        public int Refresh()
        {
            int version = _source.CloudVersion;
            if (version == _version) return 0;

            int count = _source.CloudParticleCount;
            float radius = _source.CloudRadius;
            if (!(radius > 0f) || float.IsInfinity(radius))
            {
                throw new ArgumentOutOfRangeException("source",
                    "球云半径中途变成非法值，索引不可信，当前 " + radius);
            }

            if (_world.Length < count)
            {
                var grown = new Vector3[Mathf.Max(count, 16)];
                Array.Copy(_world, grown, _world.Length);
                _world = grown;
            }
            if (count > 0)
            {
                Vector3[] source = _source.CloudWorldPositions();
                Array.Copy(source, _world, count);
            }

            _count = count;
            _radius = radius;
            float cell = radius * 2f;
            _cellSize = cell > MinCellSize ? cell : MinCellSize;

            // 质心与最远球心距：给“理不出局部方向”埋没点当兼底方向用。
            // 用 double 累加再除：几千个质点拿 float 求和会漂出可见误差。
            if (count > 0)
            {
                double sx = 0.0, sy = 0.0, sz = 0.0;
                for (int i = 0; i < count; i++) { sx += _world[i].x; sy += _world[i].y; sz += _world[i].z; }
                float inv = 1f / count;
                _centroid = new Vector3((float)(sx * inv), (float)(sy * inv), (float)(sz * inv));
            }
            else
            {
                _centroid = Vector3.zero;
            }
            BuildGrid();
            _version = version;
            return 1;
        }

        void BuildGrid()
        {
            int table = MinTableSize;
            while (table < _count * 2 && table < MaxTableSize) table <<= 1;
            _tableMask = table - 1;

            if (_head.Length != table) _head = new int[table];
            for (int i = 0; i < table; i++) _head[i] = -1;

            if (_next.Length < _count) _next = new int[Mathf.Max(_count, 16)];
            // 倒着插：链表内顺序最终等于质点下标升序，读起来直观，也保证与遍历方式无关的确定性
            for (int p = _count - 1; p >= 0; p--)
            {
                int h = HashOf(_world[p]);
                _next[p] = _head[h];
                _head[h] = p;
            }
        }

        int HashOf(Vector3 world)
        {
            int ix = Mathf.FloorToInt(world.x / _cellSize);
            int iy = Mathf.FloorToInt(world.y / _cellSize);
            int iz = Mathf.FloorToInt(world.z / _cellSize);
            return HashCells(ix, iy, iz) & _tableMask;
        }

        static int HashCells(int ix, int iy, int iz)
        {
            unchecked
            {
                int h = ix * 73856093;
                h ^= iy * 19349663;
                h ^= iz * 83492791;
                return h;
            }
        }

        /// <summary>
        /// 把点顶到所有球的皮肤之外。<paramref name="point"/> 必须是**世界坐标**
        /// （球云存的就是世界坐标，所以这个代理要按 <see cref="CollisionProxySpace.World"/> 登记）。
        /// </summary>
        public Vector3 PushOut(Vector3 point, float skin)
        {
            Refresh();
            if (_count == 0) return point;

            float need = _radius + CollisionMath.SafeSkin(skin);
            int reach = Mathf.Max(1, Mathf.CeilToInt(need / _cellSize));

            // 每一趟 = 局部顺序投影 + （还埋着的话）一次有界的方向性退出。
            // 为什么不能只靠顺序投影：球云是**互相重叠的一堆球**，把点从第 3 颗顶出去
            // 正好落进第 4 颗，实测会在两球之间来回弹（0.30 → 0.34 → 0.26 → 0.34 …），
            // 跑多少趟都出不去。而整团的禁区是连成一片的，想出去必须朝一个**全局**方向走，
            // 任何两颗球都给不出这个方向。场景 Collider 是单个凸形，所以这缺陷在 v1.3.0–v1.5.0
            // 一直没暴露。
            // 方向取“离本体质心更远”：球云来自闭合表面，这个方向就是天然的外侧。
            for (int pass = 0; pass < MaxPushPasses; pass++)
            {
                point = Sweep(point, need, reach);
                if (!IsInsideAnySphere(point, need, reach)) break;           // 干净了
                point = EscapeOutward(point, need, reach);
            }

            return point;
        }

        /// <summary>
        /// 对当前点周围的候选球做一趟顺序投影。每一趟都按**当前**位置重算所在格：
        /// 点已经跑出去了，按老格子扫会盖不住落点。
        /// </summary>
        Vector3 Sweep(Vector3 point, float need, int reach)
        {
            float needSqr = need * need;
            int cx = Mathf.FloorToInt(point.x / _cellSize);
            int cy = Mathf.FloorToInt(point.y / _cellSize);
            int cz = Mathf.FloorToInt(point.z / _cellSize);

            for (int dz = -reach; dz <= reach; dz++)
            {
                for (int dy = -reach; dy <= reach; dy++)
                {
                    for (int dx = -reach; dx <= reach; dx++)
                    {
                        int head = _head[HashCells(cx + dx, cy + dy, cz + dz) & _tableMask];
                        for (int p = head; p >= 0; p = _next[p])
                        {
                            Vector3 center = _world[p];
                            Vector3 radial = point - center;
                            float distanceSqr = radial.sqrMagnitude;
                            if (distanceSqr >= needSqr) continue;          // 体外：这颗球不参与

                            if (distanceSqr <= CollisionMath.DegenerateSqr)
                            {
                                // 与球心重合：方向未定义，用固定备用轴，绝不 NaN（与球代理同一约定）
                                point = center + CollisionMath.FallbackNormal * need;
                                continue;
                            }

                            float distance = (float)Math.Sqrt(distanceSqr);
                            point = center + radial * (need / distance);
                        }
                    }
                }
            }
            return point;
        }

        /// <summary>
        /// 还埋在球里时的退出：沿"离本体质心更远"的方向，一次只清掉**正压着点的那几颗球**，
        /// 反复直到干净。方向在整次查询里固定，所以每跳都严格朝外走，必然单调收敛；
        /// 不会像顺序径向投影那样在两球之间来回弹（实测 0.26 ↔ 0.34 振荡，永远出不去）。
        ///
        /// 这里的"一次只清压着的那几颗"是命门。曾经写成"沿射线扫窗口里所有球，走到最远那颗之外"，
        /// 听起来更彻底，实际是把对方**整个 0.6 m 身体里沿射线的每颗球**都当成挡路目标，
        /// 于是只差 0.02 m 就该分开的两个体，每个粒子被推 ~0.3 m，直接飞到 2.9 m 外。
        /// 需要走多远，只由"和点真正重叠的那片球"决定，不由射线扫到谁决定。
        /// </summary>
        Vector3 EscapeOutward(Vector3 point, float need, int reach)
        {
            Vector3 outward = point - _centroid;
            if (outward.sqrMagnitude <= CollisionMath.DegenerateSqr) outward = CollisionMath.FallbackNormal;
            outward = outward.normalized;

            float needSqr = need * need;
            for (int hop = 0; hop < MaxPushPasses; hop++)
            {
                int cx = Mathf.FloorToInt(point.x / _cellSize);
                int cy = Mathf.FloorToInt(point.y / _cellSize);
                int cz = Mathf.FloorToInt(point.z / _cellSize);
                float alongPoint = Vector3.Dot(point, outward);

                float best = float.NegativeInfinity;
                bool found = false;
                for (int dz = -reach; dz <= reach; dz++)
                {
                    for (int dy = -reach; dy <= reach; dy++)
                    {
                        for (int dx = -reach; dx <= reach; dx++)
                        {
                            int head = _head[HashCells(cx + dx, cy + dy, cz + dz) & _tableMask];
                            for (int p = head; p >= 0; p = _next[p])
                            {
                                Vector3 center = _world[p];
                                if ((center - point).sqrMagnitude >= needSqr) continue;   // 没压着点：跳过
                                float along = Vector3.Dot(center, outward);
                                if (!found || along > best) { best = along; found = true; }
                            }
                        }
                    }
                }
                if (!found) return point;                          // 干净了
                point += outward * (best + need - alongPoint);     // 刚好跨过压着点的最外侧那颗
            }
            return point;
        }

        /// <summary>
        /// 点是否还在**任何**一颗球的禁区里。只扫当前点周围的 reach 窗口，不遍历全云：
        /// 能装下这个点的球，球心必在 need 以内，它的格子就一定在窗口里——所以下限是精确的，
        /// 不是近似。写成全量扫描会把空间哈希的意义抹掉（每次查询退化回 O(n)，整个互碰就变成 O(n²)/步）。
        /// </summary>
        bool IsInsideAnySphere(Vector3 point, float need, int reach)
        {
            float needSqr = need * need;
            int cx = Mathf.FloorToInt(point.x / _cellSize);
            int cy = Mathf.FloorToInt(point.y / _cellSize);
            int cz = Mathf.FloorToInt(point.z / _cellSize);
            for (int dz = -reach; dz <= reach; dz++)
            {
                for (int dy = -reach; dy <= reach; dy++)
                {
                    for (int dx = -reach; dx <= reach; dx++)
                    {
                        int head = _head[HashCells(cx + dx, cy + dy, cz + dz) & _tableMask];
                        for (int p = head; p >= 0; p = _next[p])
                        {
                            if ((_world[p] - point).sqrMagnitude < needSqr) return true;
                        }
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// 单点投影的最大扫描趟数。取 8 是量出来的：零载荷下两个深度重叠的软体
        /// 从“只分开一半”变成“球面基本分离”，再多就只是白烧 CPU。
        /// </summary>
        const int MaxPushPasses = 8;


    }
}
