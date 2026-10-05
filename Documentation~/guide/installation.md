# 安装

## 通过 Git URL（推荐）

1. 打开 **Window → Package Manager**
2. 点左上角 **＋ → Add package from git URL**
3. 填入：

```
https://github.com/opoi375/com.opoi375.physics-simulation.git
```

包只有 `com.unity.test-framework` 一个依赖，Package Manager 会自动把它解析好；**不会**拖进 URP、`Unity.Mathematics` 或 Burst。

## 通过本地路径

**Add package from disk**，选择本仓库里的 `package.json`。

工程内嵌（embedded）用法：把整个目录放到 `Packages/com.opoi375.physics-simulation/`，Unity 会自动识别为内嵌包。注意内嵌包若自带 git 仓库，应在宿主工程根 `.gitignore` 里忽略它，否则宿主仓库会把它当普通目录吞进去：

```gitignore
# --- Embedded packages with their own repos -----------------------------
/Packages/com.opoi375.physics-simulation/
```

## 要求

| 项 | 要求 |
| --- | --- |
| Unity | **6000.5 或更高**（`package.json` 里声明 `"unity": "6000.5"`、`"unityRelease": "6f1"`） |
| 渲染管线 | 无要求（不依赖任何管线包） |
| 测试 | Unity Test Framework 1.7.0+（作为依赖自动安装；不跑测试时可以不管） |

## 引用到自定义程序集

包里的程序集不会被"隐式引用"绕过——只要你的 `asmdef` 加上：

```json
{
  "references": [
    "PhysicsSimulation.Runtime"
  ]
}
```

编辑器脚本再额外引用 `PhysicsSimulation.Editor`。

| 程序集 | 内容 | 平台 |
| --- | --- | --- |
| `PhysicsSimulation.Runtime` | 纯逻辑层 + `MassSpringBehaviour` / `MassSpringParticleLink` | 全平台 |
| `PhysicsSimulation.Editor` | `Tools > Physics Simulation` 菜单 | Editor |
| `PhysicsSimulation.Editor.Tests` | EditMode 测试（`autoReferenced: false`、`UNITY_INCLUDE_TESTS`） | Editor |

## 验证装好了

1. 菜单栏出现 **Tools → Physics Simulation → Create Demo Scene**
2. 点它 → 自动新建并保存 `Assets/Scenes/PhysicsDemo.unity`
3. 按 **Play**：一条侧偏 38° 释放的 5 节链会摆起来，Console 里跑 `Tools → Physics Simulation → Dump State` 能看到粒子数 / 弹簧数 / 最大速度 / 是否 NaN

::: warning 演示场景会覆盖同名文件
`Create Demo Scene` 是**静默存盘**（`EditorSceneManager.SaveScene`），目标路径固定为 `Assets/Scenes/PhysicsDemo.unity`。如果你在那个路径上已经改过东西，它会被覆盖——先另存再点。
:::
