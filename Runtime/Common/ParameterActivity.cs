// Copyright (c) 2026 PhysicsSimulation. MIT License.
using System;

namespace PhysicsSimulation
{
    /// <summary>
    /// 标记一个参数字段**在播放中改了就立刻生效**，不需要重建。
    ///
    /// 为什么要有这套标记：v1.2.0 到 v1.6.0 之间，同一个缺陷在四个模块里各长了一次 ——
    /// 求解器在构造时把某个字段**拷贝**进内部结构（软体的 gravity/damping、质点弹簧的四个积分参数、
    /// 布料的 mass），此后 Inspector 上怎么改都毫无反应，也不报错。每一次都是靠人肉踩到才发现。
    ///
    /// 现在把这件事变成**可断言**的：每个参数字段必须恰好带一个标记
    /// （<see cref="RuntimeTunableAttribute"/> 或 <see cref="RequiresRebuildAttribute"/>），
    /// 有一条反射测试守着"新增字段必须表态"。漏标记 = 测试直接红，而不是等用户发现改了没用。
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class RuntimeTunableAttribute : Attribute
    {
    }

    /// <summary>
    /// 标记一个参数字段**只在构建时读一次**，运行期改它必须重新 <c>Rebuild()</c>。
    /// 必须写明理由（哪个阶段读的），否则下一个人没法判断该不该绕过。
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class RequiresRebuildAttribute : Attribute
    {
        public RequiresRebuildAttribute(string reason)
        {
            Reason = reason;
        }

        /// <summary>为什么必须重建：在哪一步被读走、被拷进了什么结构。</summary>
        public string Reason { get; private set; }
    }

    /// <summary>
    /// 参数字段的"生效方式"查询。给编辑器工具与回归测试共用，避免两处各写一份判断。
    /// </summary>
    public static class ParameterActivity
    {
        public enum Kind
        {
            /// <summary>既没标 <see cref="RuntimeTunableAttribute"/> 也没标 <see cref="RequiresRebuildAttribute"/>。</summary>
            Unclassified = 0,
            /// <summary>运行期改立即生效。</summary>
            RuntimeTunable = 1,
            /// <summary>需要 Rebuild。</summary>
            RequiresRebuild = 2,
        }

        public static Kind Classify(System.Reflection.FieldInfo field)
        {
            if (field == null) throw new ArgumentNullException("field");

            bool live = field.IsDefined(typeof(RuntimeTunableAttribute), false);
            bool rebuild = field.IsDefined(typeof(RequiresRebuildAttribute), false);

            // 两个都标等于没说：调用方无法判断该不该重建。当作未分类，让测试抓住。
            if (live == rebuild) return Kind.Unclassified;
            return live ? Kind.RuntimeTunable : Kind.RequiresRebuild;
        }

        public static string RebuildReason(System.Reflection.FieldInfo field)
        {
            var attribute = (RequiresRebuildAttribute)Attribute.GetCustomAttribute(
                field, typeof(RequiresRebuildAttribute));
            return attribute == null ? null : attribute.Reason;
        }
    }
}
