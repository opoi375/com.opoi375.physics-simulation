// Copyright (c) 2026 PhysicsSimulation. MIT License.
using System;
using UnityEngine;

namespace PhysicsSimulation
{
    /// <summary>
    /// 流体邻居表：均匀空间哈希 + 计数排序 + CSR 邻居数组。
    ///
    /// 和软体焊接用的是同一套思路（cell 边长 = 搜索半径、27 邻格探查、同一组质数混合哈希），
    /// 差别在于焊接要"合并成一个点"，这里要"保留每一对关系"，所以输出是 CSR 而不是字典。
    ///
    /// 为什么 cell 取 h 而不是 2h：邻居判据是 |xi − xj| ≤ h，cell = h 时 27 邻格恰好覆盖；
    /// 取 2h 会让单格体积变成 8 倍，候选数直接翻 8 倍，精度一点没多。
    ///
    /// 三条契约（都有测试钉着）：
    ///  * 结果与 O(n²) 暴力枚举**逐粒子等价**；
    ///  * 不含自身、关系对称、<see cref="TotalPairs"/> = 有向条目 / 2；
    ///  * 同一输入重复构建得到完全相同的邻居序列 —— 这里没有任何字典遍历：
    ///    桶内顺序由"粒子下标递增的计数排序"决定，跨桶顺序由固定的 27 格三重循环决定。
    /// </summary>
    public sealed class FluidNeighborSearch
    {
        /// <summary>与软体焊接同一组质数（换格坐标时混得开，且跨平台逐位一致）。</summary>
        const int PrimeX = 73856093;
        const int PrimeY = 19349663;
        const int PrimeZ = 83492791;

        int _capacity;
        int _tableSize = 16;                  // 2 的幂 ⇒ 取模退化成位与
        int _count;
        float _cellSize = 1f;

        int[] _bucketOf;                      // 每个粒子的桶号
        int[] _cellX, _cellY, _cellZ;         // 每个粒子的整数格坐标
        int[] _bucketStart;                   // 桶 → 排序数组起点（长度 tableSize + 1）
        int[] _bucketCursor;
        int[] _sorted;                        // 按桶排好序的粒子下标
        int[] _stamp;                         // 去重：不同格映射到同一桶时，同一个 j 只测一次

        int[] _degree;
        int[] _start;                         // CSR：i 的邻居在 _neighbors[_start[i] .. +_degree[i])
        int[] _neighbors;
        int[] _writeCursor;
        int _directedEntries;

        // 扫描期临时缓冲：按 i 主序记 (i, j) 有向对
        int[] _pairI, _pairJ;
        int _pairCapacity, _pairCount;

        public int Capacity { get { return _capacity; } }
        public int Count { get { return _count; } }
        public float CellSize { get { return _cellSize; } }
        public int MaxDegree { get; private set; }

        /// <summary>每对只计一次（有向条目 / 2）—— 密度循环的工作量分配就靠它。</summary>
        public int TotalPairs { get { return _directedEntries / 2; } }

        public float AverageDegree
        {
            get { return _count > 0 ? (float)_directedEntries / _count : 0f; }
        }

        public FluidNeighborSearch(int capacity)
        {
            Grow(Mathf.Max(1, capacity));
        }

        /// <summary>扩容。只在容量真的不够时重新分配，所以反复 Build 不会每帧产生垃圾。</summary>
        public void Grow(int newCapacity)
        {
            if (newCapacity <= _capacity) return;

            _capacity = Mathf.Max(newCapacity, 1);
            _bucketOf = new int[_capacity];
            _cellX = new int[_capacity];
            _cellY = new int[_capacity];
            _cellZ = new int[_capacity];
            _sorted = new int[_capacity];
            _stamp = new int[_capacity];
            _degree = new int[_capacity];
            _start = new int[_capacity + 1];
            _writeCursor = new int[_capacity];
            _neighbors = new int[Mathf.Max(64, _capacity * 40)];

            _tableSize = 16;
            while (_tableSize < _capacity * 2) _tableSize <<= 1;
            _bucketStart = new int[_tableSize + 1];
            _bucketCursor = new int[_tableSize + 1];

            _pairCapacity = Mathf.Max(64, _capacity * 40);
            _pairI = new int[_pairCapacity];
            _pairJ = new int[_pairCapacity];
        }

        /// <summary>重建邻居表，返回 <see cref="TotalPairs"/>。空输入安全返回 0。</summary>
        public int Build(Vector3[] positions, int count, float radius)
        {
            _count = 0;
            _directedEntries = 0;
            _pairCount = 0;
            MaxDegree = 0;
            if (positions == null || count <= 0 || !(radius > 0f)) return 0;

            if (count > _capacity) Grow(count);
            _count = count;
            _cellSize = radius;

            int mask = _tableSize - 1;
            float inv = 1f / radius;

            // ---- 1. 格坐标 + 桶号 + 计数排序（下标递增 ⇒ 桶内顺序确定）
            Array.Clear(_bucketStart, 0, _tableSize + 1);
            for (int i = 0; i < count; i++)
            {
                var p = positions[i];
                int cx = Mathf.FloorToInt(p.x * inv);
                int cy = Mathf.FloorToInt(p.y * inv);
                int cz = Mathf.FloorToInt(p.z * inv);
                _cellX[i] = cx; _cellY[i] = cy; _cellZ[i] = cz;
                int bucket = BucketOf(cx, cy, cz) & mask;
                _bucketOf[i] = bucket;
                _bucketStart[bucket + 1]++;
            }
            for (int b = 0; b < _tableSize; b++) _bucketStart[b + 1] += _bucketStart[b];
            Array.Copy(_bucketStart, _bucketCursor, _tableSize + 1);
            for (int i = 0; i < count; i++) _sorted[_bucketCursor[_bucketOf[i]]++] = i;

            // ---- 2. 逐粒子探查 27 邻格，把有向对按 i 主序记进临时缓冲
            float r2 = radius * radius;
            Array.Clear(_degree, 0, count);
            // 去重戳记必须每次清零：它存的是"这一轮里 j 被哪个 i 测过"，
            // 不清的话上一次 Build 留下的值会和这次的 i+1 撞上，合法邻居被当成已测过**静默丢掉**。
            // 流体每步都要重建邻居表，所以这个 bug 只在第二次 Build 之后才现形。
            Array.Clear(_stamp, 0, count);
            for (int i = 0; i < count; i++)
            {
                var pi = positions[i];
                int cx = _cellX[i], cy = _cellY[i], cz = _cellZ[i];
                int stamp = i + 1;

                for (int dz = -1; dz <= 1; dz++)
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int bucket = BucketOf(cx + dx, cy + dy, cz + dz) & mask;
                            int end = _bucketStart[bucket + 1];
                            for (int s = _bucketStart[bucket]; s < end; s++)
                            {
                                int j = _sorted[s];
                                if (j == i) continue;
                                if (_stamp[j] == stamp) continue;     // 同哈希桶里已经测过
                                _stamp[j] = stamp;

                                var d = pi - positions[j];
                                if (d.sqrMagnitude > r2) continue;

                                if (_pairCount == _pairCapacity) GrowPairs();
                                _pairI[_pairCount] = i;
                                _pairJ[_pairCount] = j;
                                _pairCount++;
                                _degree[i]++;
                            }
                        }
            }

            // ---- 3. 前缀和 + 一次拷贝成 CSR（拷贝顺序 = 记录顺序 ⇒ 邻居序列可复现）
            _start[0] = 0;
            for (int i = 0; i < count; i++)
            {
                _start[i + 1] = _start[i] + _degree[i];
                _writeCursor[i] = _start[i];
                if (_degree[i] > MaxDegree) MaxDegree = _degree[i];
            }
            _directedEntries = _start[count];
            EnsureNeighborCapacity(_directedEntries);
            for (int p = 0; p < _pairCount; p++)
                _neighbors[_writeCursor[_pairI[p]]++] = _pairJ[p];

            return TotalPairs;
        }

        void GrowPairs()
        {
            int size = _pairCapacity * 2;
            Array.Resize(ref _pairI, size);
            Array.Resize(ref _pairJ, size);
            _pairCapacity = size;
        }

        void EnsureNeighborCapacity(int needed)
        {
            if (_neighbors.Length >= needed) return;
            int size = _neighbors.Length;
            while (size < needed) size *= 2;
            Array.Resize(ref _neighbors, size);
        }

        static int BucketOf(int cx, int cy, int cz)
        {
            return unchecked(cx * PrimeX ^ cy * PrimeY ^ cz * PrimeZ);
        }

        // ---- 查询

        /// <summary>
        /// CSR 行偏移（长度 count+1）。热循环里用 <c>for (int s = Starts[i]; s &lt; Starts[i+1]; s++)</c>
        /// 直接扫，别每个邻居都走一遍 <see cref="NeighborAt"/> 的五六个分支 —— 邻居数 ~25 时
        /// 那点分支成本在整个求解里是能看见的。公开 API 保持带越界保护的版本给诊断用。
        /// </summary>
        internal int[] Starts { get { return _start; } }

        /// <summary>CSR 的列（邻居下标），按行主序排在 <see cref="Starts"/> 指出的区间里。</summary>
        internal int[] Indices { get { return _neighbors; } }

        public int Degree(int i)
        {
            if (_degree == null || i < 0 || i >= _count) return 0;
            return _degree[i];
        }

        /// <summary>第 i 个粒子的第 slot 个邻居；越界返回 -1（不抛，诊断路径也敢直接调）。</summary>
        public int NeighborAt(int i, int slot)
        {
            if (_neighbors == null || i < 0 || i >= _count) return -1;
            if (slot < 0 || slot >= _degree[i]) return -1;
            return _neighbors[_start[i] + slot];
        }
    }
}
