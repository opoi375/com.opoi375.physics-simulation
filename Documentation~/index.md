---
layout: home

hero:
  name: Physics Simulation
  text: Unity 质点弹簧与布料物理模拟工具包
  tagline: 确定性求解 · 质点弹簧 + PBD 布料 · 半隐式欧拉与距离约束 · 纯逻辑层可单测 —— 从一根会摆的链子和一面会飘的旗开始学物理模拟
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
  - icon: 🚩
    title: 布料模拟（v1.1.0）
    details: 结构 / 剪切 / 弯曲三类距离约束，PBD 投影求解，硬度与步长解耦，stiffness=1 也不炸；风、球体障碍碰撞、自动网格
    link: /cloth/
  - icon: ⚡
    title: 实测性能
    details: 托管求解器 32×32（5826 约束）4.65 ms/步、64×64（23938 约束）20.2 ms/步，基准用例本身就是回归门槛
    link: /cloth/
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
    details: 无 Random / 无 Time / 无并行，同参数同步数跑两次逐位一致；66 个 EditMode 测试直接断言离散闭式解与逐位一致
    link: /reference/mass-spring-parameters
  - icon: 🧿
    title: Gizmos 可视化
    details: 质点画线框球（固定点另一种颜色、半径按 log 质量），弹簧按应变着色：压缩偏蓝、拉伸偏红
    link: /tools/
  - icon: 🛠️
    title: 编辑器工具三件套
    details: Tools > Physics Simulation > Create Demo Scene / Build In Current Scene / Dump State，静默存盘不弹模态框
    link: /tools/

  - icon: 🗺️
    title: Roadmap
    details: v1.1.0 布料（XPBD 距离约束）· v1.2.0 软体（四面体体积约束）· v1.3.0 Jobs + Burst 并行求解
    link: /guide/overview
---
