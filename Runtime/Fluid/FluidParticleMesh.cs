// Copyright (c) 2026 PhysicsSimulation. MIT License.
using System.Collections.Generic;
using UnityEngine;

namespace PhysicsSimulation
{
    /// <summary>
    /// 流体粒子的渲染网格 —— **程序生成**，包不引任何外部美术资源。
    /// 默认按"单位球 + 实例矩阵缩放"用，改粒子大小不需要重建网格。
    /// </summary>
    public static class FluidParticleMesh
    {
        /// <summary>生成一个绕自身中心、半径为 radius 的 UV 球。subdivisions 控制环/段密度。</summary>
        public static Mesh Build(float radius, int subdivisions)
        {
            float r = radius > 0f ? radius : 0.05f;
            int rings = Mathf.Clamp(4 + subdivisions * 2, 4, 24);
            int segments = Mathf.Clamp(6 + subdivisions * 3, 6, 32);

            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var triangles = new List<int>();

            for (int ring = 0; ring <= rings; ring++)
            {
                float v = (float)ring / rings;
                float theta = v * Mathf.PI;
                float sinT = Mathf.Sin(theta), cosT = Mathf.Cos(theta);

                for (int seg = 0; seg <= segments; seg++)
                {
                    float u = (float)seg / segments;
                    float phi = u * Mathf.PI * 2f;
                    var n = new Vector3(sinT * Mathf.Cos(phi), cosT, sinT * Mathf.Sin(phi));
                    normals.Add(n);
                    vertices.Add(n * r);
                }
            }

            int stride = segments + 1;
            for (int ring = 0; ring < rings; ring++)
            {
                for (int seg = 0; seg < segments; seg++)
                {
                    int a = ring * stride + seg;
                    int b = a + stride;
                    triangles.Add(a); triangles.Add(b); triangles.Add(a + 1);
                    triangles.Add(a + 1); triangles.Add(b); triangles.Add(b + 1);
                }
            }

            var mesh = new Mesh();
            mesh.name = "FluidParticleSphere";
            mesh.vertices = vertices.ToArray();       // 必须先 vertices 后 triangles
            mesh.triangles = triangles.ToArray();
            mesh.normals = normals.ToArray();
            return mesh;
        }
    }
}
