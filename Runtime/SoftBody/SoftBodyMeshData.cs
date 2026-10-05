// Copyright (c) 2026 PhysicsSimulation. MIT License.
using System;
using UnityEngine;

namespace PhysicsSimulation
{
    /// <summary>
    /// 软体的输入数据：一份"顶点数组 + 三角形索引"的纯拷贝。
    ///
    /// 之所以不直接吃 <see cref="Mesh"/>：软体逻辑层要能在 EditMode 里、在没有 GameObject 的情况下被断言，
    /// 而且网格可能来自程序化生成、来自别的资源管线。要喂 Unity 网格时用 <see cref="FromMesh"/> 拷一份出来即可。
    ///
    /// 构造时不做校验（空数组、越界索引都要能构造出来，好在测试里验证"Build 会拒"），
    /// 所有合法性检查集中在 <see cref="SoftBodySimulation.Build"/>，那里保证"抛异常前不改任何已有状态"。
    /// </summary>
    public sealed class SoftBodyMeshData
    {
        /// <summary>顶点位置数组（可以是按面拆开的重复顶点，构建时会按位置焊接去重）。</summary>
        public readonly Vector3[] Vertices;

        /// <summary>三角形索引数组，长度必须是 3 的倍数；绕序决定外法线，进而决定体积正负。</summary>
        public readonly int[] Triangles;

        public SoftBodyMeshData(Vector3[] vertices, int[] triangles)
        {
            Vertices = vertices;
            Triangles = triangles;
        }

        /// <summary>顶点数组长度（未焊接的原始网格顶点数）。</summary>
        public int VertexCount { get { return Vertices == null ? 0 : Vertices.Length; } }

        /// <summary>三角形个数（索引长度 / 3，不整除时向下取整，合法性由 Build 判定）。</summary>
        public int TriangleCount { get { return Triangles == null ? 0 : Triangles.Length / 3; } }

        /// <summary>从 Unity 网格拷一份出来。mesh 为 null 时抛 <see cref="ArgumentNullException"/>。</summary>
        public static SoftBodyMeshData FromMesh(Mesh mesh)
        {
            if (mesh == null) throw new ArgumentNullException("mesh", "网格不能为 null");
            return new SoftBodyMeshData(mesh.vertices, mesh.triangles);
        }
    }
}
