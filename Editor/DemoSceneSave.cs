// Copyright (c) 2026 PhysicsSimulation. MIT License.
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace PhysicsSimulation.EditorTools
{
    /// <summary>
    /// 演示工具的场景存盘口子。存在的唯一理由：<c>EditorSceneManager.SaveOpenScenes()</c>
    /// 遇到**没有文件路径的场景**（untitled / 刚 NewScene 出来还没存过）时，会弹系统的
    /// "保存场景"文件对话框。从菜单项或自动化（ExecuteMenuItem / REST / CI）里调用时没人
    /// 能点掉那个框，编辑器主线程就永久堵死 —— 实测卡了 7 分钟，所有需要主线程的接口全无响应。
    /// 所以这里只保存"有路径"的场景，其余跳过并给出可操作的提示，全程不弹任何模态框。
    /// </summary>
    public static class DemoSceneSave
    {
        /// <summary>
        /// 纯函数：这条场景路径能不能静默保存。空路径（untitled 场景）必须返回 false ——
        /// 对它调用任何 SaveScene/SaveOpenScenes 都会触发系统保存对话框。
        /// </summary>
        public static bool CanSaveSilently(string scenePath)
        {
            return !string.IsNullOrEmpty(scenePath) && scenePath.Trim().Length > 0;
        }

        /// <summary>当前打开的场景是否全部有路径（即全部可静默保存）。</summary>
        public static bool AllOpenScenesHavePaths()
        {
            for (int i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                if (!CanSaveSilently(EditorSceneManager.GetSceneAt(i).path)) return false;
            }
            return true;
        }

        /// <summary>
        /// 静默保存所有"脏 + 有路径"的场景，返回实际保存的个数。
        /// 没路径的脏场景会被跳过（并打一条 Warning 说明原因），绝不弹对话框。
        /// </summary>
        public static int SaveDirtyScenesWithPaths()
        {
            var pathless = new List<string>();
            int saved = 0;
            for (int i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                var scene = EditorSceneManager.GetSceneAt(i);
                if (!scene.isDirty) continue;
                if (!CanSaveSilently(scene.path))
                {
                    pathless.Add(scene.name);
                    continue;
                }
                if (EditorSceneManager.SaveScene(scene)) saved++;
            }
            if (pathless.Count > 0)
            {
                Debug.LogWarning("[PhysicsSimulation] 跳过 " + pathless.Count +
                                 " 个没有文件路径的场景（" + string.Join(", ", pathless.ToArray()) +
                                 "）：保存它们会弹系统对话框，自动化流程里没人能点。" +
                                 "请先手动 Save 一次再运行本菜单。");
            }
            return saved;
        }

        /// <summary>
        /// 给"必须先存干净才能换场景"的工具用：全部场景都有路径就静默保存并返回 true；
        /// 只要有 untitled 场景就返回 false（调用方据此中止并提示用户），同样不弹任何框。
        /// </summary>
        public static bool SaveOpenScenesWithoutPrompting()
        {
            if (!AllOpenScenesHavePaths()) return false;
            SaveDirtyScenesWithPaths();
            return true;
        }
    }
}
