// Copyright (c) 2026 PhysicsSimulation. MIT License.
using UnityEditor;
using UnityEngine;

namespace PhysicsSimulation.EditorTools
{
    /// <summary>
    /// 演示材质工具。
    ///
    /// 为什么单独拎出来：软体和布料的演示场景都要"给个能正常渲染的材质"，
    /// 而这件事在 v1.1.0 踩过两个坑（详见 ClothDemoTools 的注释）：
    ///  1. 猜着色器名字（Shader.Find("Universal Render Pipeline/Lit")）在新建材质上会画成品红；
    ///  2. 把材质标成 HideFlags.DontSave，存盘后进 Play 模式引用丢失，同样画成品红。
    /// 正确做法是拿**当前管线的 defaultMaterial** 当模板复制一份，并让它随场景序列化。
    /// 两处各自实现的话，第二个坑一定会被再踩一遍。
    /// </summary>
    internal static class DemoMaterialHelper
    {
        /// <summary>
        /// 给 renderer 套一份带颜色的演示材质。
        /// 返回 true 表示确实换上了材质；usedPath 说明材质是从哪来的（诊断用）。
        /// reference 是"可以抄材质"的参照物（可为 null）。
        /// </summary>
        internal static bool Apply(Renderer renderer, Color color, Renderer reference, out string usedPath)
        {
            usedPath = "无（保持无材质，走管线默认）";
            if (renderer == null) return false;

            Material template = PipelineDefaultMaterial()
                                ?? (reference != null ? reference.sharedMaterial : null);

            if (template != null)
            {
                var material = new Material(template) { name = "PhysicsSimulation Demo" };
                SetColor(material, color);
                renderer.sharedMaterial = material;
                usedPath = "管线默认材质模板: " + template.name + " / shader: " + template.shader.name;
                return true;
            }

            string[] candidates = { "Universal Render Pipeline/Lit", "HDRP/Lit", "Standard", "Diffuse" };
            Shader shader = null;
            foreach (string candidate in candidates)
            {
                shader = Shader.Find(candidate);
                if (shader != null) break;
            }
            if (shader == null)
            {
                Debug.LogWarning("[PhysicsSimulation] 既拿不到管线默认材质也没找到可用着色器，演示物体将不带材质。");
                return false;
            }

            var fallback = new Material(shader) { name = "PhysicsSimulation Demo" };
            SetColor(fallback, color);
            renderer.sharedMaterial = fallback;
            usedPath = "猜名字兜底: " + shader.name;
            return true;
        }

        /// <summary>
        /// 用反射拿当前渲染管线的 defaultMaterial：包本身不依赖 URP/HDRP 程序集，
        /// 但在装了管线的工程里能拿到"画得对"的那个材质（内置管线时返回 null）。
        /// </summary>
        internal static Material PipelineDefaultMaterial()
        {
            var asset = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            if (asset == null) return null;

            // Instance 标志位不能省：只给 Public|NonPublic 时反射根本不匹配实例属性，
            // 于是 defaultMaterial 永远查不到、材质悄悄退化成"猜名字"，画面就是品红。
            var property = asset.GetType().GetProperty("defaultMaterial",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
            if (property == null) return null;

            try
            {
                return property.GetValue(asset, null) as Material;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[PhysicsSimulation] 读取管线 defaultMaterial 失败：" + e.Message);
                return null;
            }
        }

        /// <summary>按各管线的属性名依次尝试设色：URP 用 _BaseColor，内置用 _Color，另外也设 material.color。</summary>
        internal static void SetColor(Material material, Color color)
        {
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            else if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            material.color = color;
        }
    }
}
