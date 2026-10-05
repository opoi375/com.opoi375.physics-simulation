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
    public sealed class MassSpringBehaviour : MonoBehaviour
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
            if (_system == null && !Rebuild()) return;
            _system.Step(Time.fixedDeltaTime);
        }

        /// <summary>
        /// 按当前 Inspector 配置重建系统。配置非法时返回 false 并记录原因（不抛异常刷错误）。
        /// </summary>
        public bool Rebuild()
        {
            try
            {
                _system = MassSpringBuilder.Build(particles, springs, gravity, globalDamping, substeps, maxDeltaTime);
                _lastBuildError = null;
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
