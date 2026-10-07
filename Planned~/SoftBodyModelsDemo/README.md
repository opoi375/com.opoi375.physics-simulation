# 停放：真实模型软体演示（原计划 v1.5.0）

这两个文件是**已完成并通过测试**的（7 条，整包当时 177/177），但**不能发布**，所以按仓库惯例
停放在 `Planned~`（目录名以 `~` 结尾，Unity 的 AssetDatabase 不导入 ⇒ 不进编译、不出菜单）。

- `SoftBodyModelsDemoTools.cs` → 恢复位置 `Editor/SoftBody/`
- `SoftBodyModelsDemoToolsTests.cs` → 恢复位置 `Tests/Editor/`

## 它做什么

把审计判定 Healthy 的 8 个真实道具（桶/凳/灌木/花盆/营火/弯道铁轨/桥/棕榈）摆成一排，
从 1.5→7.8 米逐个升高自由落体砸地面；落点高度、间距、相机位姿都是**纯函数**，可断言，不手调。
构建核心 `BuildModels(Transform, IList<Mesh>, Collider)` 吃 Mesh 列表而不是资产路径，
所以测试不依赖本工程的任何美术资产（包发布到别人工程里照样绿）。

## 为什么不能发布：编辑器能读顶点，运行时读不到

Play 实测（截图与 `gameobject_find` 坐标）：8 块几何**原封不动浮在摆位高度上**，一块都没落。

根因有硬证据，不是猜：

```
Assets/ArtRes/Showcase/Models/prop_barrel.fbx.meta:    isReadable: 0
```

- `SoftBodyMeshData.FromMesh(mesh)` 走的是 `mesh.vertices` / `mesh.triangles`；
- Unity 的 Read/Write 关闭只保证**运行时**拿不到 CPU 端顶点数据，**编辑器里照样读得出来**；
- 于是 `Tools/Physics Simulation/Soft Body/Audit Mesh Assets In Folder` 把 104 个网格全审完了、
  数字都正常，而同一批网格一进 Play 就构建失败 —— `SoftBodyBehaviour` 按契约不抛异常，
  只把原因写进 `LastBuildError`，画面就成了"静态模型摆拍"。

**这正是 v1.4.0 审计文档"§9 审计的边界"里该补而没补的一条**：审计跑在编辑器里，
它验的是几何，验不出 `isReadable`。所以"审计 Healthy"目前**不等于**"运行时能跑"。

## 下一版要做的事（按顺序）

1. `SoftBodyModelsDemoTools` 里加一步：把源网格**拷成演示专用的可读 Mesh 资产**
   （`new Mesh()` + 赋顶点/三角形 + `AssetDatabase.CreateAsset` 到 `Assets/PhysicsSimulationDemo/`），
   场景引用副本。**不要**去改用户美术资产的导入设置（`isReadable = true` 会让整包重新导入、
   显存/CPU 内存翻倍，那是替用户做他没同意的决定）。
2. 审计侧补一条判定/列：`isReadable == false` 时明说"编辑器可审、运行时不可用"，
   并给一句修复指引（这条要先进 `SoftBodyMeshAudit` 的 BDD 骨架）。
3. `SoftBodyBehaviour` 增加一条测试：源网格不可读时 `LastBuildError` 必须点名
   "Read/Write 未开启"，而不是含糊的"顶点数组为空"。
4. 恢复菜单优先级 125/126 与文档、changelog、roadmap 行。

## 已经踩过的两个坑（别再踩）

- **绝不能用 `editor_execute_menu File/Save` 去清 test_run 的"脏场景"闸门**：
  那次 `File/Save` 把已发布的 `Assets/Scenes/SoftBodyDemo.unity` 里**场景内程序化网格**
  （718 行顶点数据）删掉，并把 `sourceMesh` 改指到 `building-sample-house-b.fbx` 的一个子网格
  （`guid: d2fc5c2db7e4f344d85675ab4dc5348f, fileID: -6264755719532673971`）。
  已用 `git checkout HEAD -- Assets/Scenes/SoftBodyDemo.unity` 还原。
  正确做法：闸门挡住时**还原**场景（`git checkout` / `EditorSceneManager` 重新打开），不是存盘。
- Play 模式退出后场景会脏（运行时实例网格被重新序列化）；这时任何"顺手存一下盘"都会造成上面那种静默改指。
