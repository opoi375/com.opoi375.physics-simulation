// Copyright (c) 2026 PhysicsSimulation. MIT License.
using System.IO;
using NUnit.Framework;
using PhysicsSimulation.EditorTools;
using UnityEditor;
using UnityEngine;

namespace PhysicsSimulation.Editor.Tests
{
    /// <summary>
    /// 演示工具的存盘口子。这条用例存在的原因是真实事故：
    /// EditorSceneManager.SaveOpenScenes() 遇到没有文件路径的场景会弹系统"保存场景"对话框，
    /// 从菜单项 / REST 自动化里调用时没人能点掉，编辑器主线程被堵死 7 分钟。
    /// </summary>
    public class DemoSceneSaveTests
    {
        [Test]
        public void CanSaveSilently_RejectsUntitledScenes()
        {
            // Then:  空路径 = untitled 场景，保存它必然弹系统对话框
            Assert.IsFalse(DemoSceneSave.CanSaveSilently(null), "null 路径不能静默保存");
            Assert.IsFalse(DemoSceneSave.CanSaveSilently(""), "空路径不能静默保存");
            Assert.IsFalse(DemoSceneSave.CanSaveSilently("   "), "空白路径也算没有路径");

            // 有路径（哪怕还没落盘）才能静默保存
            Assert.IsTrue(DemoSceneSave.CanSaveSilently("Assets/Scenes/FluidDemo.unity"),
                "有路径的场景应当可以静默保存");
        }

        [Test]
        public void EditorTools_NeverCallThePromptingSaveApis()
        {
            // Given:  包里全部 Editor 源码
            var root = Path.Combine(Directory.GetParent(Application.dataPath).FullName,
                "Packages", "com.opoi375.physics-simulation", "Editor");
            if (!Directory.Exists(root))
                Assert.Ignore("找不到 Editor 源码目录：" + root);

            var files = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories);
            Assert.Greater(files.Length, 5, "扫描到的文件太少，用例本身要复查");

            // Then:  一个调用点都不许留下。SaveOpenScenes / SaveCurrentModifiedScenesIfUserWantsTo
            //        在场景没有路径时会弹模态框，自动化流程里等于死锁。
            var offenders = new System.Text.StringBuilder();
            foreach (var file in files)
            {
                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i].TrimStart();
                    if (line.StartsWith("//")) continue;         // 注释里提到是允许的（教训要写下来）
                    int a = line.IndexOf("EditorSceneManager.SaveOpenScenes(");
                    int b = line.IndexOf("SaveCurrentModifiedScenesIfUserWantsTo(");
                    if (a >= 0 || b >= 0)
                    {
                        offenders.Append("  ").Append(Path.GetFileName(file))
                                 .Append(':').Append(i + 1).Append(' ')
                                 .Append(line).Append('\n');
                    }
                }
            }
            Assert.AreEqual(string.Empty, offenders.ToString().Trim(),
                "这些调用点会在没有路径的场景上弹系统保存对话框，把自动化流程堵死，" +
                "改用 DemoSceneSave.SaveOpenScenesWithoutPrompting()：\n" + offenders);
        }
    }
}
