---
layout: home

hero:
  name: Physics Simulation
  text: Unity 质点弹簧、布料、软体与流体物理模拟工具包
  tagline: 确定性求解 · 质点弹簧 + PBD 布料 + 体积保持软体 + 场景碰撞 + PBF 流体 · 半隐式欧拉与距离/密度约束 · 纯逻辑层可单测 —— 从一根会摆的链子、一面会飘的旗、一块会鼓回来的果冻，到一坨会塌开的水
  image:
    src: /logo.png
    alt: Physics Simulation
  actions:
    - theme: brand
      text: 快速上手
      link: /guide/quickstart
    - theme: alt
      text: GitHub
      link: https://github.com/opoi375/com.opoi375.physics-simulation

features:
  - icon: 💧
    title: 流体模拟（v1.5.0）
    details: PBF（Position Based Fluids）—— 均匀网格哈希 + CSR 邻居表、poly6/spiky 核、密度约束投影、拉力钳制与位移护栏；1456 粒的溃坝演示跑在托管求解器上（基准实测：1000 粒 13~21 ms、4096 粒 65~97 ms 每步），`DrawMeshInstanced` 渲染不生成十万面网格
    link: /fluid/
  - icon: 🧱
    title: 场景碰撞（v1.3.0）
    details: 求解器只认注入的碰撞代理（球 / OBB 盒 / 胶囊 / 半空间），Unity 层负责从 Collider 采样 —— 纯逻辑层不查场景，闭式解断言与逐位复现一条都不丢
    link: /collision/
  - icon: 🫧
    title: 软体模拟（v1.2.0）
    details: 任意网格自动长出拓扑：焊接顶点成质点、三角形边成结构弹簧、共边对顶点成弯曲弹簧，再加散度定理体积约束 —— 压扁了会自己鼓回来，642 质点 2.745 ms/步
    link: /soft-body/
  - icon: 🚩
    title: 布料模拟（v1.1.0）
    details: 结构 / 剪切 / 弯曲三类距离约束，PBD 投影求解，硬度与步长解耦，stiffness=1 也不炸；风、球体障碍碰撞、自动网格
    link: /cloth/
  - icon: ⚡
    title: 实测性能（托管，无 Burst）
    details: 软体 642 质点 2.745 ms/步、布料 32×32（5826 约束）3.299 ms/步、64×64（23938 约束）19.447 ms/步；基准用例本身就是回归门槛
    link: /soft-body/
  - icon: 🪢
    title: 质点弹簧系统
    details: Particle / Spring / MassSpringSystem 三件套，胡克定律 + 轴向相对速度阻尼，力严格等大反向，内力不改总动量
    link: /mass-spring/
  - icon: 📉
    title: 半隐式欧拉 + 隐式阻尼
    details: 阻尼写成除数 (1 + c·dt)，任意 c·dt 都只会衰减、绝不会把速度反号；粒子级阻尼乘数钳到 [0,1]
    link: /mass-spring/
  - icon: 🎛️
    title: 子步与 dt 钳制
    details: 一个 FixedUpdate 均分成 N 个子步，dt 默认钳到 1/15 秒；掉帧、暂停回来都不会把系统炸掉
    link: /mass-spring/
  - icon: 🧪
    title: 确定性、可复现
    details: 无 Random / 无 Time / 无并行，同参数同步数跑两次逐位一致；270 个 EditMode 测试直接断言离散闭式解、核函数解析值、等值面闭合与逐位一致
    link: /reference/mass-spring-parameters
  - icon: 🧿
    title: Gizmos 可视化
    details: 质点画线框球（固定点另一种颜色、半径按 log 质量），弹簧按应变着色：压缩偏蓝、拉伸偏红
    link: /tools/
  - icon: 🛠️
    title: 编辑器工具三件套 × 三套
    details: Tools > Physics Simulation >（质点弹簧 100~103）、Cloth（110~112）、Soft Body（120~124）、Fluid（130~132）各自的 Create Demo Scene / Build In Current Scene / Dump State；存盘一律走 DemoSceneSave，绝不弹系统对话框
    link: /tools/

  - icon: 🗺️
    title: Roadmap
    details: v1.1.0 布料 · v1.2.0 软体 · v1.3.0 碰撞代理 · v1.4.0 模型审计 · v1.5.0 PBF 流体均已发布 · v1.6.0 计划 Jobs + Burst 并行求解（可选程序集，托管实现留作回退）
    link: /guide/overview
---
