// Copyright (c) 2026 PhysicsSimulation. MIT License.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace PhysicsSimulation
{
    /// <summary>流体初始形状。</summary>
    public enum FluidVolumeShape
    {
        /// <summary>长方体水体，从组件原点向 +xyz 展开。</summary>
        Box = 0,
        /// <summary>溃坝：水柱贴在 x=0 的"坝"上，撤坝后靠重力自己摊开。</summary>
        DamBreak = 1,
        /// <summary>球状水体。</summary>
        Sphere = 2
    }

    /// <summary>流体怎么被看见。</summary>
    public enum FluidRenderMode
    {
        /// <summary>逐粒子实例化小球（v1.5.0 的默认，也是唯一行为）。零额外成本，但看不出水面。</summary>
        Particles = 0,
        /// <summary>marching tetrahedra 等值面。看得出水面、浪、 splash，代价是每帧重建标量场。</summary>
        Surface = 1,
        /// <summary>两者都画：小球从水面里透出来，适合排查“水面是不是漏了”。成本 = 两者相加。</summary>
        Both = 2
    }

    /// <summary>
    /// 流体（PBF）的挂载组件。
    ///
    /// 与布料/软体同一条契约：
    ///  * 模拟发生在组件自身的**局部空间**，Transform 只是摆位，不参与力学；
    ///    注意 <see cref="FluidParameters.gravity"/> 因此也是**局部方向** ——
    ///    把组件旋转 90°，水就往"局部的下"落。要世界重力就让组件不旋转（演示与水箱都不旋转）。
    ///  * 构建失败不抛异常打断游戏，原因写进 <see cref="LastBuildError"/>。
    ///
    /// 渲染走 <see cref="Graphics.DrawMeshInstanced"/>：不生成水面网格、不改任何资源。
    /// 单批上限 1023 实例是引擎硬规定，超了必须分批（见 <see cref="RenderBatchCount"/>）。
    /// </summary>
    [AddComponentMenu("Physics Simulation/Fluid")]
    public sealed class FluidBehaviour : MonoBehaviour
    {
        [Tooltip("流体参数（静止密度、粒子间距、核半径、子步、粘度、涡度等）")]
        public FluidParameters parameters = new FluidParameters();

        [Tooltip("初始形状")]
        public FluidVolumeShape volumeShape = FluidVolumeShape.Box;

        [Tooltip("Box/DamBreak 的水体尺寸（米）；DamBreak 时分别是宽/高/深")]
        public Vector3 volumeSize = new Vector3(0.4f, 0.6f, 0.4f);

        [Tooltip("Sphere 的半径（米）")]
        public float volumeRadius = 0.3f;

        [Tooltip("让场景里的 Collider 参与碰撞（只解析球/盒/胶囊；MeshCollider 会被跳过）。" +
                 "默认关：开上来就改变现有场景的行为，比漏一个碰撞更难查")]
        public bool collideWithSceneColliders = false;

        [Tooltip("collideWithSceneColliders 为真时参与碰撞的 Collider 列表")]
        public List<Collider> sceneColliders = new List<Collider>();

        [Tooltip("把水关进一个长方体容器（内侧盒子：越界的每个轴各自钉回内壁）。坐标是世界空间，" +
                 "与本组件的缩放无关。演示水箱用它，而不是拿六块实体板当碰撞体——薄板代理会把水" +
                 "沿「穿透最浅的单轴」挤到板的另一面去")]
        public bool enableBoxContainer = false;

        [Tooltip("容器中心（世界空间）")]
        public Vector3 containerCenter = Vector3.zero;

        [Tooltip("容器半尺寸（世界空间，米）。三个分量必须为正")]
        public Vector3 containerHalfSize = Vector3.one;

        [Tooltip("每步重读一次 Collider 列表（会分配内存）；碰撞体会动、会开关时再开")]
        public bool updateCollidersEveryStep = false;

        [Tooltip("关掉后由外部调 Step(dt)，用于定步长、回放或网络同步")]
        public bool autoSimulate = true;

        [Tooltip("用 DrawMeshInstanced 把粒子画成小球")]
        public bool renderParticles = true;

        [Tooltip("粒子大小按核半径自动推（关掉则用下面的手动值）")]
        public bool autoParticleSize = true;

        [Tooltip("手动粒子半径（米）")]
        public float particleRenderScale = 0.03f;

        [Tooltip("粒子颜色")]
        public Color particleColor = new Color(0.25f, 0.55f, 0.95f, 1f);

        [Tooltip("自发光强度（0 = 纯靠场景光照）")]
        public float emissionStrength = 0f;

        [Tooltip("渲染模式：粒子小球 / 等值面水面 / 两者都画。默认粒子，v1.5.0 的观感不变")]
        public FluidRenderMode renderMode = FluidRenderMode.Particles;

        [Tooltip("水面体素边长（米）。0 = 自动取粒子间距。越小越精细，而成本按体素数三次方增长")]
        public float surfaceCellSize = 0f;

        [Tooltip("水面阈值。标量场按静止点阵归一化，1 = 水体内部，0.5 大致就是表面")]
        [Range(0.05f, 2f)]
        public float surfaceIsoLevel = FluidSurface.DefaultIsoLevel;

        [Tooltip("每隔几帧重建一次水面（1 = 每帧）。粒子那三次方的成本主要花在这里")]
        [Range(1, 10)]
        public int surfaceRefreshEveryNFrames = 2;

        [Tooltip("水面体素预算，超了自己放大格子。宁可糊一点，不要把内存吃掉")]
        public int surfaceMaxCells = FluidSurface.DefaultMaxCells;

        [Tooltip("水面颜色。alpha 就是水的通透度，渲染队列固定在透明队列")]
        public Color surfaceColor = new Color(0.16f, 0.45f, 0.78f, 0.72f);

        FluidSimulation _simulation;
        Vector3[] _initialPositions;
        Vector3[] _initialVelocities;

        Mesh _particleMesh;
        Material _particleMaterial;
        float _particleMeshRadius = -1f;
        Matrix4x4[] _matrices;

        GameObject _surfaceObject;
        MeshFilter _surfaceFilter;
        MeshRenderer _surfaceRenderer;
        Mesh _surfaceMesh;
        Material _surfaceMaterial;
        int _surfaceRefreshCounter;

        public FluidSimulation Simulation { get { return _simulation; } }
        public bool IsBuilt { get { return _simulation != null; } }
        public string LastBuildError { get; private set; }

        /// <summary>当前水面网格。没开水面模式时为 null（不是空网格）。</summary>
        public Mesh SurfaceMesh { get { return _surfaceMesh; } }

        /// <summary>水面材质（半透明）。没建过时为 null。</summary>
        public Material SurfaceMaterial { get { return _surfaceMaterial; } }

        /// <summary>水面重建过几次。测试用它证明“节流真的生效”，而不是靠看帧率猜。</summary>
        public int SurfaceRevision { get; private set; }

        /// <summary>上一帧实际提交了几批粒子。等于 0 说明这一帧根本没画粒子。</summary>
        public int ParticleBatchCount { get; private set; }

        /// <summary>当前水面的三角形数。</summary>
        public int SurfaceTriangleCount
        {
            get { return _surfaceMesh == null ? 0 : _surfaceMesh.triangles.Length / 3; }
        }

        /// <summary>渲染与诊断用的当前位置（未构建时是空数组，不是 null）。</summary>
        public Vector3[] ParticlePositions
        {
            get { return _simulation != null ? _simulation.Positions : EmptyPositions; }
        }
        static readonly Vector3[] EmptyPositions = new Vector3[0];

        /// <summary>实际用于绘制的粒子半径。</summary>
        public float EffectiveParticleRadius
        {
            get
            {
                if (!autoParticleSize) return particleRenderScale > 0f ? particleRenderScale : 0.001f;
                float h = parameters != null ? parameters.kernelRadius : 0.1f;
                // h/2：再大就会糊成一整块，看不出单个粒子；再小就看不见飞溅的水珠
                return Mathf.Max(1e-4f, h * 0.5f);
            }
        }

        void Awake()
        {
            if (_simulation == null) Rebuild();
        }

        void OnDestroy()
        {
            DisposeAsset(_particleMesh); _particleMesh = null;
            DisposeAsset(_particleMaterial); _particleMaterial = null;
            DisposeAsset(_surfaceMesh); _surfaceMesh = null;
        }

        /// <summary>回收运行时自建的资源。EditMode 里 <c>Destroy</c> 会打一条 Error，而 Unity 的
        /// 测试框架把任何未预期的 Error 直接算成测试失败；水面重建在 EditMode 一帧里就会被走好几次，
        /// 两个测试就是这样变红的。运行时用 Destroy、编辑器里用 DestroyImmediate。</summary>
        static void DisposeAsset(UnityEngine.Object asset)
        {
            if (asset == null) return;
            if (Application.isPlaying) Destroy(asset);
            else DestroyImmediate(asset);
        }

        /// <summary>按当前参数与初始形状重建粒子集（会丢掉当前模拟状态）。</summary>
        public void Rebuild()
        {
            try
            {
                parameters.Validate();
                var set = BuildVolume();
                if (set.Count == 0)
                    throw new InvalidOperationException("按当前尺寸与间距生成的粒子数为 0，请把水体尺寸调大或把间距调小");
                if (set.Count > parameters.maxParticles)
                    throw new InvalidOperationException("粒子数 " + set.Count + " 超过 maxParticles = "
                        + parameters.maxParticles + "，请加大 particleSpacing");

                _initialPositions = (Vector3[])set.Positions.Clone();
                _initialVelocities = (Vector3[])set.Velocities.Clone();

                var simulation = new FluidSimulation(parameters, set);
                simulation.SetSimulationToWorld(transform.localToWorldMatrix);
                if (collideWithSceneColliders && sceneColliders != null && sceneColliders.Count > 0)
                    ColliderProxies.RefreshInto(sceneColliders, simulation.Collisions);

                // 容器必须在 Rebuild 里加：Rebuild 会换一个全新的 FluidSimulation（连带全新的
                // CollisionSet），从外面往 Simulation.Collisions 塞的东西第二次 Rebuild（比如
                // 进 Play 模式）就没了 —— 实测：手工挂的容器代理在 Play 里变成"碰撞代理 0"，
                // 一箱水直落到 y = −92，动能 16904，
                if (enableBoxContainer)
                    simulation.Collisions.Add(new BoxContainerProxy(containerCenter, containerHalfSize,
                                                                    Quaternion.identity),
                                              CollisionProxySpace.World);

                _simulation = simulation;
                LastBuildError = null;
            }
            catch (Exception e)
            {
                _simulation = null;
                LastBuildError = e.Message;
                Debug.LogWarning("[PhysicsSimulation] 流体构建失败：" + e.Message, this);
            }
        }

        FluidParticleSet BuildVolume()
        {
            float d = parameters.particleSpacing;
            switch (volumeShape)
            {
                case FluidVolumeShape.DamBreak:
                    return FluidVolume.DamBreak(volumeSize.x, volumeSize.y, volumeSize.z, d);
                case FluidVolumeShape.Sphere:
                    return FluidVolume.Sphere(transform.localPosition, volumeRadius, d);
                default:
                    return FluidVolume.Box(volumeSize, d);
            }
        }

        /// <summary>推进一步（<paramref name="deltaTime"/> ≤ 0 时是空操作）。</summary>
        public void Step(float deltaTime)
        {
            if (_simulation == null) return;
            if (updateCollidersEveryStep && collideWithSceneColliders && sceneColliders != null)
                ColliderProxies.RefreshInto(sceneColliders, _simulation.Collisions);

            _simulation.SetSimulationToWorld(transform.localToWorldMatrix);
            _simulation.Step(deltaTime);
        }

        /// <summary>回到初始布局，速度也清零（演示里的"重来一次"）。</summary>
        public void ResetToInitialLayout()
        {
            if (_simulation == null || _initialPositions == null) return;
            var set = new FluidParticleSet
            {
                Positions = (Vector3[])_initialPositions.Clone(),
                Velocities = (Vector3[])_initialVelocities.Clone(),
                Count = _initialPositions.Length
            };
            _simulation = new FluidSimulation(parameters, set);
            _simulation.SetSimulationToWorld(transform.localToWorldMatrix);
            if (collideWithSceneColliders && sceneColliders != null && sceneColliders.Count > 0)
                ColliderProxies.RefreshInto(sceneColliders, _simulation.Collisions);
        }

        /// <summary>
        /// 分批数 = ceil(粒子数 / 单批上限)。<see cref="Graphics.DrawMeshInstanced"/> 的
        /// 单批上限是 1023，多一个就整批画不出来 —— 这个函数存在的全部理由就是别让它发生。
        /// </summary>
        public static int RenderBatchCount(int particleCount, int maxPerBatch)
        {
            if (particleCount <= 0 || maxPerBatch <= 0) return 0;
            return (particleCount + maxPerBatch - 1) / maxPerBatch;
        }

        public void Update()
        {
            if (autoSimulate) Step(Time.deltaTime);

            // 粒子与水面各自独立开关：renderParticles 是 v1.5.0 就有的开关，renderMode 决定画什么。
            // 两者取“与”，这样旧场景（只改 renderParticles）行为逐位不变。
            bool wantParticles = renderParticles && renderMode != FluidRenderMode.Surface;
            if (wantParticles) DrawParticles(); else ParticleBatchCount = 0;

            if (renderMode != FluidRenderMode.Particles)
            {
                int every = Mathf.Max(1, surfaceRefreshEveryNFrames);
                if (_surfaceRefreshCounter <= 0)
                {
                    RebuildSurface();
                    _surfaceRefreshCounter = every - 1;
                }
                else _surfaceRefreshCounter--;
            }
        }

        /// <summary>
        /// 用当前粒子位置重建水面，返回三角形数。粒子太少或阈值太高时是 0（不抛异常、不断渲染）。
        /// 网格在模拟空间（= 组件局部空间）生成，挂在子物体上，所以组件的 Transform 会自然带上它。
        /// </summary>
        public int RebuildSurface()
        {
            if (_simulation == null || _simulation.ParticleCount == 0) return 0;

            Vector3[] positions = _simulation.Positions;
            float spacing = parameters != null && parameters.particleSpacing > 0f ? parameters.particleSpacing : 0.05f;
            float kernel = parameters != null && parameters.kernelRadius > 0f ? parameters.kernelRadius : spacing * 2f;
            float cell = surfaceCellSize > 0f ? surfaceCellSize : spacing;
            float iso = surfaceIsoLevel > 0f ? surfaceIsoLevel : FluidSurface.DefaultIsoLevel;
            int budget = surfaceMaxCells > 0 ? surfaceMaxCells : FluidSurface.DefaultMaxCells;

            FluidSurfaceMesh surface = FluidSurface.Build(positions, positions.Length, cell, kernel,
                                                          spacing, iso, budget, kernel);
            DisposeAsset(_surfaceMesh);
            _surfaceMesh = FluidSurface.ToMesh(surface);
            _surfaceMesh.hideFlags = HideFlags.DontSave;                // 运行时资源，不进场景也不进包

            EnsureSurfaceObject();
            if (_surfaceFilter != null) _surfaceFilter.mesh = _surfaceMesh;
            if (_surfaceMaterial != null) _surfaceMaterial.color = surfaceColor;
            SurfaceRevision++;
            return surface.TriangleCount;
        }

        void EnsureSurfaceObject()
        {
            if (_surfaceObject == null)
            {
                _surfaceObject = new GameObject("FluidSurface");
                _surfaceObject.transform.SetParent(transform, false);
                _surfaceObject.hideFlags = HideFlags.DontSave;
                _surfaceFilter = _surfaceObject.AddComponent<MeshFilter>();
                _surfaceRenderer = _surfaceObject.AddComponent<MeshRenderer>();
                _surfaceRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _surfaceRenderer.receiveShadows = false;
            }
            if (_surfaceMaterial == null)
            {
                _surfaceMaterial = FluidSurfaceMaterial.Create(surfaceColor);
                if (_surfaceMaterial != null) _surfaceMaterial.hideFlags = HideFlags.DontSave;
                if (_surfaceRenderer != null) _surfaceRenderer.sharedMaterial = _surfaceMaterial;
            }
        }

        void DrawParticles()
        {
            if (_simulation == null || _simulation.ParticleCount == 0) { ParticleBatchCount = 0; return; }

            float radius = EffectiveParticleScale();
            EnsureParticleAssets(radius);
            if (_particleMesh == null || _particleMaterial == null) return;

            Vector3[] positions = _simulation.Positions;
            int count = positions.Length;
            if (_matrices == null || _matrices.Length < count) _matrices = new Matrix4x4[count];

            Matrix4x4 localToWorld = transform.localToWorldMatrix;
            var scale = Matrix4x4.Scale(new Vector3(radius, radius, radius));
            for (int i = 0; i < count; i++)
                _matrices[i] = localToWorld * Matrix4x4.Translate(positions[i]) * scale;

            const int maxPerBatch = 1023;      // DrawMeshInstanced 的引擎硬上限
            int batches = RenderBatchCount(count, maxPerBatch);
            for (int b = 0; b < batches; b++)
            {
                int offset = b * maxPerBatch;
                int n = Mathf.Min(maxPerBatch, count - offset);
                Graphics.DrawMeshInstanced(_particleMesh, 0, _particleMaterial, _matrices, n,
                    null, UnityEngine.Rendering.ShadowCastingMode.Off, false, gameObject.layer, null);
            }
            ParticleBatchCount = batches;
        }

        float EffectiveParticleScale()
        {
            // 实例矩阵里带的是组件的局部→世界变换，所以半径要按世界尺度换算，
            // 否则组件被放大 3 倍时粒子还是原大小，看起来像蚂蚁搬家。
            Vector3 lossy = transform.lossyScale;
            float s = Mathf.Max(0.0001f, Mathf.Max(Mathf.Abs(lossy.x), Mathf.Max(Mathf.Abs(lossy.y), Mathf.Abs(lossy.z))));
            return EffectiveParticleRadius * s;
        }

        void EnsureParticleAssets(float radius)
        {
            if (_particleMesh == null || _particleMeshRadius <= 0f || Mathf.Abs(_particleMeshRadius - radius) > radius * 0.25f)
            {
                DisposeAsset(_particleMesh);
                _particleMesh = FluidParticleMesh.Build(radius, 1);
                _particleMesh.hideFlags = HideFlags.DontSave;     // 运行时生成的东西，不进场景也不进包体资源
                _particleMeshRadius = radius;
            }
            if (_particleMaterial == null)
            {
                _particleMaterial = FluidParticleMaterial.Create(particleColor, emissionStrength);
                if (_particleMaterial != null) _particleMaterial.hideFlags = HideFlags.DontSave;
            }
        }
    }
}
