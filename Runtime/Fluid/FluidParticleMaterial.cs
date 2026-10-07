// Copyright (c) 2026 PhysicsSimulation. MIT License.
using System;
using UnityEngine;

namespace PhysicsSimulation
{
    /// <summary>
    /// 粒子材质。<see cref="SelectShaderName"/> 是纯函数：URP 活动时选 URP/Lit，
    /// 否则退回内置 Standard —— 反过来会是一片品红（v1.2.0 演示场景就栽在这里）。
    /// </summary>
    public static class FluidParticleMaterial
    {
        public const string UrpShaderName = "Universal Render Pipeline/Lit";
        public const string BuiltInShaderName = "Standard";

        /// <summary>纯函数：只回答"该用哪个着色器名"，不碰图形设备。</summary>
        public static string SelectShaderName(bool urpActive)
        {
            return urpActive ? UrpShaderName : BuiltInShaderName;
        }

        /// <summary>
        /// 当前是否为无管线的内置管线。GraphicsSettings 在 UnityEngine.Rendering 命名空间下
        /// （写成 UnityEngine.GraphicsSettings 会 CS0234 —— Unity 6 里它早就不在 UnityEngine 根下了）。
        /// </summary>
        public static bool IsBuiltInPipeline()
        {
            return UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline == null;
        }

        /// <summary>
        /// 创建粒子材质（调用方负责销毁）。两条着色器路径都找不到时返回 null，
        /// 由调用方降级成"不画粒子"—— 物理照跑，总比一片品红好排查。
        /// </summary>
        public static Material Create(Color color, float emissionStrength)
        {
            string shaderName = SelectShaderName(!IsBuiltInPipeline());
            var shader = Shader.Find(shaderName);
            if (shader == null)
            {
                // 工程换了管线但包没重启之类的情况，退到另一条路径再试一次
                shader = Shader.Find(SelectShaderName(false));
            }
            if (shader == null) return null;

            var material = new Material(shader);
            material.enableInstancing = true;             // DrawMeshInstanced 必须开，否则每批都吃一次 setpass
            material.SetColor("_BaseColor", color);       // URP Lit
            material.SetColor("_Color", color);           // 内置 Standard
            if (emissionStrength > 0f)
            {
                material.SetColor("_EmissionColor", color * emissionStrength);
                material.EnableKeyword("_EMISSION");
            }
            return material;
        }
    }
}
