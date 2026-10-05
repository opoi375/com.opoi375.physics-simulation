---
layout: home

hero:
  name: Physics Simulation
  text: Mass-spring, cloth and soft body physics for Unity
  tagline: Deterministic solver · mass-spring chains, PBD cloth and volume-preserving soft bodies · substepping & dt clamping · a pure logic layer you can unit-test
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
    details: No Random, no Time, no parallelism. Same parameters and step count produce bit-identical results, so tests assert closed-form solutions — 97 EditMode tests
    link: /en/reference/mass-spring-parameters
  - icon: 🧿
    title: Gizmo visualization
    details: "Wire-sphere particles (pinned ones in another colour, radius scaled by log mass) and springs coloured by strain: blue when compressed, red when stretched"
    link: /en/tools/
  - icon: 🛠️
    title: Editor tools
    details: Tools > Physics Simulation > Create Demo Scene / Build In Current Scene / Dump State — silent scene saving, no modal dialogs
    link: /en/tools/
  - icon: 🗺️
    title: Roadmap
    details: "v1.1.0 cloth shipped · v1.2.0 soft bodies shipped (any mesh → welded particles, triangle-edge springs, divergence-theorem volume constraint) · v1.3.0 Jobs + Burst parallel solver as an optional assembly"
    link: /en/guide/overview
---
