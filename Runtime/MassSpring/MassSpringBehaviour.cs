// Copyright (c) 2026 PhysicsSimulation. MIT License.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace PhysicsSimulation
{
    /// <summary>
    /// 质点弹簧的 Unity 层驱动：只负责"Inspector 配置 -&gt; 系统构建 -&gt; FixedUpdate 推进 -&gt; Gizmos 可视化"。
    /// 数值逻辑全在 <see cref="MassSpringSystem"/> 里，这个类不碰积分公式，因此可以被整体替换成 Burst 版求解器。
    /// </summary>
    [DefaultExecutionOrder(-100)]
    [AddComponentMenu("Physics Simulation/Mass Spring Behaviour")]
    public sealed class MassSpringBehaviour : MonoBehaviour, IParticleBody
    {
        [Header("积分参数")]
        [Tooltip("常规模拟重力（米/秒²）。设成零可以用来单独验证弹簧内力。")]
        public Vector3 gravity = new Vector3(0f, -9.81f, 0f);

        [Tooltip("全局线性阻尼 c_global（1/秒）。积分里写成除数 (1 + c*dt)，因此永远不会把速度反号。")]
        public float globalDamping = 0.5f;

        [Tooltip("子步数：把一个 FixedUpdate 的时间均分成这么多份逐步积分，是唯一的稳定性旋钮。")]
        [Min(1)]
        public int substeps = 8;

        [Tooltip("单步 dt 上限（秒）。掉帧时不会一次灌进巨大的时间步。")]
        [Min(0.001f)]
        public float maxDeltaTime = 1f / 15f;

        [Header("质点（位置 / 质量 / 是否固定）")]
        public List<MassSpringParticleData> particles = new List<MassSpringParticleData>();

        [Header("弹簧（索引对 / k / c；原长填 0 表示自动取初始距离）")]
        public List<MassSpringSpringData> springs = new List<MassSpringSpringData>();

        [Header("Gizmos")]
        [Tooltip("地面参考线的半边长（米），0 表示不画。演示场景用它对齐链条摆动范围。")]
        [Min(0f)]
        public float groundReferenceSize = 0f;

        [Tooltip("画出质点半径的基准（米）；实际半径按 log(质量) 缩放。")]
        [Min(0.01f)]
        public float particleRadiusBase = 0.06f;

        [Tooltip("关掉则不自动推进，改成由脚本调 <c>Step()</c>。默认开：不该因为升级就改变现有场景。")]
        public bool autoSimulate = true;

        [Tooltip("加入这个互碰组后，同组的体（软体 / 布料 / 质点弹簧，类型不限）之间会互相挤开。\n"
                 + "留空 = 完全不参与互碰，行为与之前版本逐位一致。")]
        public ParticleInteractionGroup interactionGroup;

        private static readonly Color FreeParticleColor = new Color(0.35f, 0.9f, 1f, 1f);
        private static readonly Color PinnedParticleColor = new Color(1f, 0.82f, 0.2f, 1f);
        private static readonly Color SpringBaseColor = new Color(0.55f, 0.85f, 0.6f, 1f);
        private static readonly Color CompressedColor = new Color(0.25f, 0.45f, 1f, 1f);
        private static readonly Color StretchedColor = new Color(1f, 0.3f, 0.25f, 1f);
        private static readonly Color GroundColor = new Color(0.6f, 0.6f, 0.6f, 0.75f);

        private MassSpringSystem _system;
        private string _lastBuildError;

        /// <summary>当前构建出来的系统；配置非法时为 null。</summary>
        public MassSpringSystem System { get { return _system; } }

        /// <summary>最近一次构建失败的原因（Inspector 诊断用）。</summary>
        public string LastBuildError { get { return _lastBuildError; } }

        /// <summary>是否已经成功构建。</summary>
        public bool IsBuilt { get { return _system != null; } }

        private void Awake()
        {
            Rebuild();
        }

        private void OnEnable()
        {
            // 脚本重载 / 退出播放模式后 _system 会丢，这里补建
            if (_system == null)
            {
                Rebuild();
            }
        }

        private void OnValidate()
        {
            // 编辑期改参数后立刻重建，这样 Gizmos 反映的就是最新配置；
            // 播放模式下绝不重建（会把正在跑的系统瞬间复位）。
            if (Application.isPlaying) return;
            Rebuild();
        }

        private void FixedUpdate()
        {
            if (!autoSimulate) return;
            Step(Time.fixedDeltaTime);
        }

        /// <summary>
        /// 手动推进一个时间步（不管 autoSimulate 是什么）。
        /// 与另外两个 Behaviour 对齐：以前只有 FixedUpdate 一条路，而 EditMode 测试里 FixedUpdate
        /// 根本不触发，质点弹簧以没有任何办法被测试驱动 —— 这是已发布 API 的一个真空缺。
        /// </summary>
        public void Step(float deltaTime)
        {
            if (_system == null && !Rebuild()) return;
            SyncRuntimeParameters();
            SyncSceneColliders();
            _system.Step(deltaTime);
            _cloudVersion++;                        // 质点动了：让别人的球云索引知道该重建
        }

        /// <summary>
        /// 把四个**非拓扑**参数同步进内层系统，每步一次。
        ///
        /// 这四项（重力 / 全局阻尼 / 子步 / dt 上限）只影响"怎么积分"，跟质点与弹簧的拓扑无关，
        /// 所以完全没有理由冻在 Rebuild 里。旧实现恰恰把它们拷进 <see cref="MassSpringSystem"/>
        /// 就再也不管了，而 <c>OnValidate</c> 在播放模式又故意跳过重建 ——
        /// 于是播放中拖 Inspector 的这四项**完全没有反应，也不报错**。
        /// 软体在 v1.6.0 修过同一类问题（<c>SoftBodySimulation.Step</c>），这里是同一个成因。
        /// </summary>
        void SyncRuntimeParameters()
        {
            MassSpringParameters p = _system.Parameters;
            p.gravity = gravity;
            p.globalDamping = globalDamping;
            p.substeps = substeps;
            p.maxDeltaTime = maxDeltaTime;
        }

        // ------------------------------------------------------------------
        // 互碰（IParticleBody）：质点弹簧与软体/布料走同一套球云代理。
        // 半径直接用 particleRadiusBase（与 Gizmos 画的同一个量），不另发明一套尺寸。
        // ------------------------------------------------------------------
        int _cloudVersion = 1;
        Matrix4x4 _cloudMatrix = Matrix4x4.zero;
        Vector3[] _cloudWorld = new Vector3[0];

        /// <summary>参与互碰的质点数（含钉住的）。</summary>
        public int CloudParticleCount { get { return _system == null ? 0 : _system.Particles.Count; } }

        /// <summary>
        /// 互碰球半径 = <see cref="particleRadiusBase"/> × 这个系数。
        /// 这里默认 **1.0** 而不是软体/布料的 0.5：particleRadiusBase 本来就是**绝对半径**（画出来多大就是多大），
        /// 而那两个是"间距 × 系数"。口径不同 ⇒ 系数含义也不同，强行统一默认值反而会把已发布的
        /// 质点弹簧外观与互碰距离一起改掉。想更贴身就把这个系数调小。
        /// </summary>
        [Tooltip("互碰球半径 = 质点基础半径 × 此系数。默认 1.0：质点弹簧的半径基数是绝对值，不是间距折算。")]
        public float cloudRadiusFactor = 1f;

        /// <summary>球半径：取 <see cref="particleRadiusBase"/> × <see cref="cloudRadiusFactor"/>，下限 0.01 m。</summary>
        public float CloudRadius
        {
            get
            {
                float factor = cloudRadiusFactor > 0f ? cloudRadiusFactor : 0f;
                float r = (particleRadiusBase > 0.01f ? particleRadiusBase : 0.01f) * factor;
                return r > 0.001f ? r : 0.001f;
            }
        }

        /// <summary>质点或变换每变一次自增；读取幂等，但会顺手自检变换矩阵。</summary>
        public int CloudVersion
        {
            get
            {
                Matrix4x4 now = transform.localToWorldMatrix;
                if (now != _cloudMatrix) { _cloudMatrix = now; _cloudVersion++; }
                return _cloudVersion;
            }
        }

        /// <summary>当前世界坐标下的质点位置（前 <see cref="CloudParticleCount"/> 个有效）。</summary>
        public Vector3[] CloudWorldPositions()
        {
            var particles = _system == null ? null : _system.Particles;
            int count = particles == null ? 0 : particles.Count;
            if (_cloudWorld.Length < count) _cloudWorld = new Vector3[Mathf.Max(count, 16)];
            Matrix4x4 toWorld = transform.localToWorldMatrix;
            _cloudMatrix = toWorld;
            for (int i = 0; i < count; i++) _cloudWorld[i] = toWorld.MultiplyPoint(particles[i].position);
            return _cloudWorld;
        }

        /// <summary>本体的碰撞集合，互碰组会往里挂别人的球云。</summary>
        public CollisionSet ParticleCollisions { get { return _system == null ? null : _system.Collisions; } }

        /// <summary>Inspector 上那个字段与 <c>group.Add(this)</c> 的回写共用同一个入口。</summary>
        ParticleInteractionGroup IParticleBody.CloudGroup
        {
            get { return interactionGroup; }
            set { interactionGroup = value; }
        }

        void OnDestroy()
        {
            if (interactionGroup != null) interactionGroup.Remove(this);
        }

        [Tooltip("让场景里的 Collider 参与碰撞（本版只解析球/盒/胶囊；MeshCollider 会被跳过）。默认关：不该因为升级就改变现有场景")]
        public bool collideWithSceneColliders = false;

        [Tooltip("collideWithSceneColliders 为真时参与碰撞的 Collider 列表（世界空间）")]
        public System.Collections.Generic.List<Collider> sceneColliders = new System.Collections.Generic.List<Collider>();

        [Tooltip("每步重读一次 Collider 列表（会分配内存）；碰撞体会动、会开关时再开")]
        public bool updateCollidersEveryStep = false;

        bool _collidersSynced;

        /// <summary>
        /// 把组件变换与 Collider 列表同步给系统。既没开开关、又不在互碰组里时注入单位矩阵，
        /// 于是默认路径逐位等价于 v1.0.0（一条回归测试专门锁这件事）。
        /// 但**互碰组也必须注入**：球云存的是世界坐标、登记的也是 World 空间。
        /// </summary>
        void SyncSceneColliders()
        {
            if (_system == null) return;

            // 惰性入组：Inspector 里拖了组但没人调 Add 是最常见的用法
            if (interactionGroup != null && !interactionGroup.Contains(this)) interactionGroup.Add(this);

            if (!collideWithSceneColliders && interactionGroup == null)
            {
                _system.SetSimulationToWorld(Matrix4x4.identity);
                return;
            }
            _system.SetSimulationToWorld(transform.localToWorldMatrix);
            if (updateCollidersEveryStep || !_collidersSynced)
            {
                ColliderProxies.RefreshInto(sceneColliders, _system.Collisions);
                _collidersSynced = true;
            }
        }

        /// <summary>
        /// 按当前 Inspector 配置重建系统。配置非法时返回 false 并记录原因（不抛异常刷错误）。
        /// </summary>
        public bool Rebuild()
        {
            try
            {
                _system = MassSpringBuilder.Build(particles, springs, gravity, globalDamping, substeps, maxDeltaTime);
                _collidersSynced = false;
                _lastBuildError = null;

                // Rebuild 会换掉整个碰撞集合，互碰代理必须重新挂回去
                if (interactionGroup != null) interactionGroup.Reattach(this);
                return true;
            }
            catch (ArgumentOutOfRangeException exception)
            {
                _system = null;
                _lastBuildError = exception.Message;
                Debug.LogWarning("[MassSpringBehaviour] 配置无效，未构建系统：" + exception.Message, this);
                return false;
            }
        }

        /// <summary>右键菜单：把当前模拟出来的位置当作新的初始布局与原长（"摆到好看的位置再定格"）。</summary>
        [ContextMenu("Capture Current As Rest")]
        public void CaptureCurrentAsRest()
        {
            if (_system == null && !Rebuild()) return;

            for (int i = 0; i < particles.Count && i < _system.Particles.Count; i++)
            {
                Particle particle = _system.Particles[i];
                particles[i].position = particle.position;
                particles[i].initialVelocity = particle.velocity;
            }

            for (int i = 0; i < springs.Count && i < _system.Springs.Count; i++)
            {
                Spring spring = _system.Springs[i];
                springs[i].restLength = Vector3.Distance(
                    _system.Particles[spring.a].position,
                    _system.Particles[spring.b].position);
            }

            Rebuild();
        }

        /// <summary>右键菜单：回到配置里的初始布局（位置、速度、固定状态、力累积器全部复位）。</summary>
        [ContextMenu("Reset To Initial Layout")]
        public void ResetToInitialLayout()
        {
            _system?.ResetToInitial();
        }

        private void OnDrawGizmos()
        {
            MassSpringSystem view = _system;
            if (view == null && (particles == null || particles.Count == 0)) return;
            if (view == null && !Rebuild()) return;
            view = _system;
            if (view == null) return;

            if (groundReferenceSize > 0f)
            {
                Vector3 ground = new Vector3(transform.position.x, 0f, transform.position.z);
                Gizmos.color = GroundColor;
                Gizmos.DrawLine(ground + Vector3.left * groundReferenceSize, ground + Vector3.right * groundReferenceSize);
                Gizmos.DrawLine(ground + Vector3.back * groundReferenceSize, ground + Vector3.forward * groundReferenceSize);
            }

            for (int s = 0; s < view.Springs.Count; s++)
            {
                Spring spring = view.Springs[s];
                Vector3 positionA = view.Particles[spring.a].position;
                Vector3 positionB = view.Particles[spring.b].position;
                float strain = spring.Strain(positionA, positionB);
                float amount = Mathf.Clamp01(Mathf.Abs(strain) * 2f);
                Gizmos.color = strain >= 0f
                    ? Color.Lerp(SpringBaseColor, StretchedColor, amount)      // 拉伸偏红
                    : Color.Lerp(SpringBaseColor, CompressedColor, amount);    // 压缩偏蓝
                Gizmos.DrawLine(positionA, positionB);
            }

            for (int i = 0; i < view.Particles.Count; i++)
            {
                Particle particle = view.Particles[i];
                Gizmos.color = particle.pinned ? PinnedParticleColor : FreeParticleColor;
                Gizmos.DrawWireSphere(particle.position, ParticleRadius(particle.mass));
            }
        }

        /// <summary>质点 gizmo 半径：按 log(质量) 缩放，让 0.1kg 与 10kg 在同屏下都看得见。</summary>
        private float ParticleRadius(float mass)
        {
            float scale = 1f + Mathf.Log(1f + Mathf.Max(0.001f, mass) * 4f);
            return Mathf.Clamp(particleRadiusBase * scale, 0.01f, 2f);
        }
    }
}
