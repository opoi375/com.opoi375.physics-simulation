---
layout: home

hero:
  name: Physics Simulation
  text: Mass-spring, cloth, soft body and fluid physics for Unity
  tagline: Deterministic solver · mass-spring chains, PBD cloth, volume-preserving soft bodies, scene collision and PBF fluids · substepping & dt clamping · a pure logic layer you can unit-test
  image:
    src: /logo.png
    alt: Physics Simulation
  actions:
    - theme: brand
      text: Quick Start
      link: /en/guide/quickstart
    - theme: alt
      text: GitHub
      link: https://github.com/opoi375/com.opoi375.physics-simulation

features:
  - icon: 💧
    title: Fluid simulation (v1.5.0)
    details: "PBF (Position Based Fluids): uniform hash-grid + CSR neighbour table, poly6/spiky kernels, density-constraint projection, tensile clamping and a displacement rail; a 1,456-particle dam break on the managed solver (measured: 13~21 ms/step at 1,000 particles, 65~97 ms at 4,096), drawn with DrawMeshInstanced so no 100k-triangle mesh is ever built"
    link: /en/fluid/
  - icon: 🧱
    title: Scene collision (v1.3.0)
    details: The solvers only know injected collision proxies (sphere / oriented box / capsule / half-space); the Unity layer samples Colliders for them — the logic layer never queries the scene, so closed-form assertions and bit-identical replay survive intact
    link: /en/collision/
  - icon: 🫧
    title: Soft body simulation (v1.2.0)
    details: Any mesh grows its own topology — welded vertices become particles, triangle edges become structural springs, opposite vertices across shared edges become bend springs, plus a divergence-theorem volume constraint that pops back after being squashed. 642 particles at 2.745 ms/step
    link: /en/soft-body/
  - icon: 🚩
    title: Cloth simulation (v1.1.0)
    details: Structural, shear and bend distance constraints solved with PBD projection — stiffness decoupled from step size and stable at stiffness = 1; wind, sphere obstacles, generated mesh
    link: /en/cloth/
  - icon: ⚡
    title: Measured performance (managed, no Burst)
    details: Soft body 642 particles 2.745 ms/step, cloth 32×32 (5,826 constraints) 3.299 ms/step and 64×64 (23,938 constraints) 19.447 ms/step; the benchmarks are the regression gates
    link: /en/soft-body/
  - icon: 🪢
    title: Mass-spring system
    details: Particle / Spring / MassSpringSystem — Hooke's law plus axial relative-velocity damping, forces applied strictly equal and opposite so internal forces never change total momentum
    link: /en/mass-spring/
  - icon: 📉
    title: Semi-implicit Euler, implicit damping
    details: "Damping written as a divisor (1 + c·dt): any magnitude only decays the velocity, it can never flip its sign; per-particle damping factor is clamped to [0,1]"
    link: /en/mass-spring/
  - icon: 🎛️
    title: Substepping & dt clamping
    details: One FixedUpdate split into N sub-steps, dt clamped to 1/15 s by default — frame drops and long pauses cannot blow the system up
    link: /en/mass-spring/
  - icon: 🧪
    title: Deterministic & reproducible
    details: No Random, no Time, no parallelism. Same parameters and step count produce bit-identical results, so tests assert closed-form solutions — 270 EditMode tests
    link: /en/reference/mass-spring-parameters
  - icon: 🧿
    title: Gizmo visualization
    details: "Wire-sphere particles (pinned ones in another colour, radius scaled by log mass) and springs coloured by strain: blue when compressed, red when stretched"
    link: /en/tools/
  - icon: 🛠️
    title: Editor tools
    details: "Tools > Physics Simulation > (mass-spring 100~103), Cloth (110~112), Soft Body (120~124), Fluid (130~132) — each with Create Demo Scene / Build In Current Scene / Dump State. Saving always goes through DemoSceneSave, which never opens a system dialog"
    link: /en/tools/
  - icon: 🗺️
    title: Roadmap
    details: "v1.1.0 cloth · v1.2.0 soft bodies · v1.3.0 collision proxies + Collider bridging · v1.4.0 model audit · v1.5.0 PBF fluids, all shipped · v1.6.0 plans the Jobs + Burst parallel solver as an optional assembly"
    link: /en/guide/overview
---
