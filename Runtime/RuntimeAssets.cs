// Copyright (c) 2026 PhysicsSimulation. MIT License.
using UnityEngine;

namespace PhysicsSimulation
{
    /// <summary>
    /// 运行时自建资源（Mesh / Material）的统一释放口。
    ///
    /// 为什么不能直接 <c>Destroy</c>：EditMode 里 <c>Destroy</c> 会打一条 Error，而 Unity 测试框架
    /// 把任何未预期的 Error 直接算成测试失败 —— 流体当初就是这么红了两条测试的。
    /// 布料后来还在无条件 <c>Destroy</c>，同一个包两套写法必有一套是错的，所以收敛到这里。
    /// </summary>
    internal static class RuntimeAssets
    {
        internal static void Dispose(Object asset)
        {
            if (asset == null) return;
            if (Application.isPlaying) Object.Destroy(asset);
            else Object.DestroyImmediate(asset);
        }
    }
}
