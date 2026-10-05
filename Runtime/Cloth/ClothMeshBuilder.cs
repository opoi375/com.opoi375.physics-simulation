// Copyright (c) 2026 PhysicsSimulation. MIT License.
using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace PhysicsSimulation
{
    /// <summary>
    /// 布料网格生成：把 C×R 的质点网格翻译成一个可以直接给 MeshFilter 用的三角网格。
    ///
    /// 只做纯数据搬运，不碰场景、不建 GameObject，因此可以在 EditMode 里直接断言。
    /// 绕序固定为"法线朝 +Z"，同一次生成的所有三角形方向一致（否则布会一半正面一半背面，开背面剔除就穿帮）。
    /// </summary>
    public static class ClothMeshBuilder
    {
        /// <summary>
        /// 每个格子切成 2 个三角形，索引顺序固定：先按行、再按列，保证确定性。
        /// 返回长度 = 6 * (columns-1) * (rows-1)。
        /// </summary>
        public static int[] BuildTriangles(int columns, int rows)
        {
            ValidateGrid(columns, rows);

            int cells = (columns - 1) * (rows - 1);
            var triangles = new int[cells * 6];
            int w = 0;

            for (int row = 0; row < rows - 1; row++)
            {
                for (int col = 0; col < columns - 1; col++)
                {
                    int a = row * columns + col;              // 左上
                    int b = a + 1;                            // 右上
                    int c = a + columns;                       // 左下
                    int d = c + 1;                             // 右下

                    // (a,c,b) 与 (b,c,d)：绕序一致，法线朝 +Z
                    triangles[w++] = a; triangles[w++] = c; triangles[w++] = b;
                    triangles[w++] = b; triangles[w++] = c; triangles[w++] = d;
                }
            }

            return triangles;
        }

        /// <summary>
        /// 铺满 [0,1]² 的 UV：U 沿列、V 沿行（顶行 V=1，底行 V=0），与贴图习惯一致。
        /// </summary>
        public static Vector2[] BuildUvs(int columns, int rows)
        {
            ValidateGrid(columns, rows);

            var uvs = new Vector2[columns * rows];
            float width = columns - 1;
            float height = rows - 1;

            for (int row = 0; row < rows; row++)
            {
                for (int col = 0; col < columns; col++)
                {
                    // 用除法而不是乘倒数：后者会因为 5 * (1f/5) = 1.0000000149 把 uv 顶出 [0,1]
                    uvs[row * columns + col] = new Vector2(col / width, 1f - row / height);
                }
            }

            return uvs;
        }

        /// <summary>把当前模拟位置写进顶点缓冲区（就地复用，不分配）。</summary>
        public static void ApplyPositions(ClothSimulation cloth, Vector3[] into)
        {
            if (cloth == null) throw new ArgumentNullException("cloth");
            if (into == null) throw new ArgumentNullException("into");
            if (into.Length != cloth.ParticleCount)
            {
                throw new ArgumentException("缓冲区长度 " + into.Length + " 与质点数 " + cloth.ParticleCount + " 不符", "into");
            }

            for (int i = 0; i < into.Length; i++)
            {
                into[i] = cloth.GetPosition(i);
            }
        }

        /// <summary>
        /// 把拓扑与顶点灌进一个 Mesh。只在重建时调用 <paramref name="uploadTopology"/> = true，
        /// 每帧更新只需灌顶点（可选重算法线）。
        /// </summary>
        public static void FillMesh(Mesh mesh, int columns, int rows, Vector3[] vertices, int[] triangles,
                                    Vector2[] uvs, bool uploadTopology, bool recalculateNormals)
        {
            if (mesh == null) throw new ArgumentNullException("mesh");

            if (columns * rows > 65000)
            {
                mesh.indexFormat = IndexFormat.UInt32;        // 否则超 65535 顶点的布会被 Unity 静默截断
            }

            mesh.SetVertices(vertices, 0, vertices.Length);
            if (uvs != null && uvs.Length == vertices.Length) mesh.SetUVs(0, uvs, 0, uvs.Length);

            if (uploadTopology)
            {
                mesh.subMeshCount = 1;
                mesh.SetTriangles(triangles, 0);
            }

            if (recalculateNormals) mesh.RecalculateNormals();
            mesh.RecalculateBounds();          // 顶点每帧都在变，不重算包围盒会被相机/剔除错杀
        }

        static void ValidateGrid(int columns, int rows)
        {
            if (columns < 2) throw new ArgumentOutOfRangeException("columns", "列数至少为 2（当前 " + columns + "）");
            if (rows < 2) throw new ArgumentOutOfRangeException("rows", "行数至少为 2（当前 " + rows + "）");
        }
    }
}
