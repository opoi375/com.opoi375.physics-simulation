// Copyright (c) 2026 PhysicsSimulation. MIT License.
using UnityEngine;

namespace PhysicsSimulation
{
    /// <summary>
    /// 水面材质。与 <see cref="FluidParticleMaterial"/> 同一套shader 选择逻辑
    /// （URP 活动走 URP/Lit，否则退回内置 Standard），差别只在这里必须配成<b>半透明</b>。
    ///
    /// 不硬编 URP：包的真实依赖只有 test-framework，把 URP 的 C# 类型写进源码就会让包在无 URP
    /// 的工程里编译失败。所有管线差异都用 <c>Material</c> 的属性/关键字在运行时设置，
    /// 找不到属性就静默跳过 —— 那是“水的通透度差一点”，不是“一片品红”。
    /// </summary>
    public static class FluidSurfaceMaterial
    {
        /// <summary>透明队列起点（Unity 约定 3000）。水面必须排在 opaque 之后，否则会被自己前面的水挡掉。</summary>
        public const int TransparentQueue = 3000;

        /// <summary>纯函数：该用哪个着色器名。与粒子材质共用同一个判断，避免两套逻辑漂移。</summary>
        public static string SelectShaderName(bool urpActive)
        {
            return FluidParticleMaterial.SelectShaderName(urpActive);
        }

        /// <summary>
        /// 创建水面材质（调用方负责销毁）。两条着色器路径都找不到时返回 null，
        /// 由调用方降级成“不画水面”，物理照跑。
        /// </summary>
        public static Material Create(Color color)
        {
            string shaderName = SelectShaderName(!FluidParticleMaterial.IsBuiltInPipeline());
            var shader = Shader.Find(shaderName);
            if (shader == null) shader = Shader.Find(SelectShaderName(false));
            if (shader == null) return null;

            var material = new Material(shader);
            material.name = "FluidSurface";
            SetupTransparent(material);
            material.color = color;
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            return material;
        }

        /// <summary>
        /// 把材质配成半透明混合。属性名在两条管线上不一样，全部按"有就设、没有就跳"处理。
        /// </summary>
        public static void SetupTransparent(Material material)
        {
            if (material == null) return;

            material.renderQueue = TransparentQueue;
            material.SetFloat("_Surface", 1f);                       // 0=Opaque 1=Transparent（两条管线同名）
            material.SetFloat("_Blend", 0f);                         // URP：Alpha
            material.SetFloat("_Mode", 1f);                          // 内置 Standard：Transparent
            material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            material.SetFloat("_ZWrite", 0f);                        // 透明面不许写深度，否则后层水被丢掉
            material.SetFloat("_AlphaClip", 0f);

            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");     // URP
            material.EnableKeyword("_ALPHABLEND_ON");                // 内置 Standard
            material.DisableKeyword("_ALPHATEST_ON");
            material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            material.DisableKeyword("_ALPHAMODULATE_ON");

            // 背面也画：水体是闭合面，只画前壁的话从上看会透出内壁，看着像漏了一层皮。
            // 内置 Standard 用 Cull [_Cull]（属性直接生效），URP Lit 用关键字，两条都上。
            material.SetFloat("_Cull", 0f);
            material.SetFloat("_CullingMode", 0f);
            material.EnableKeyword("_CULL_OFF");
            material.DisableKeyword("_CULL_BACK");
            material.DisableKeyword("_CULL_FRONT");
        }

        /// <summary>给测试用的可观察判据：这团材质是不是按半透明在画。</summary>
        public static bool IsTranslucent(Material material)
        {
            return material != null && material.renderQueue >= TransparentQueue && material.color.a < 1f;
        }
    }
}
