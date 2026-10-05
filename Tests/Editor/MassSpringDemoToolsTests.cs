// Copyright (c) 2026 PhysicsSimulation. MIT License.
//
// 正式测试：编辑器演示工具 Tools > Physics Simulation > Build Chain Only 的构建结果。
// 只验证"搭出来的系统结构对不对"，不验证渲染效果；场景存盘由 Create Demo Scene 负责，
// 不在编辑器测试里写 Assets/Scenes 文件（避免污染用户工程）。

using NUnit.Framework;
using PhysicsSimulation.EditorTools;
using UnityEngine;

namespace PhysicsSimulation.Editor.Tests
{
    [TestFixture]
    public class MassSpringDemoToolsTests
    {
        private MassSpringBehaviour _behaviour;

        [SetUp]
        public void SetUp()
        {
            _behaviour = MassSpringDemoTools.BuildChain();
            if (_behaviour != null) _behaviour.hideFlags = HideFlags.DontSave;
            if (_behaviour != null) _behaviour.gameObject.hideFlags = HideFlags.DontSave;
        }

        [TearDown]
        public void TearDown()
        {
            if (_behaviour != null) UnityEngine.Object.DestroyImmediate(_behaviour.gameObject);
        }

        // Given 调用编辑器工具建出的演示链条
        // When 检查系统结构
        // Then 吊点 + 5 节链（6 个质点、5 根弹簧），且只有吊点被钉死
        [Test]
        public void BuildChain_CreatesAnchorPlusFiveLinks()
        {
            Assert.That(_behaviour, Is.Not.Null, "BuildChain 应返回一个 MassSpringBehaviour");
            Assert.That(_behaviour.IsBuilt, Is.True, "链条应构建成功，原因：" + _behaviour.LastBuildError);

            MassSpringSystem system = _behaviour.System;
            Assert.That(system.Particles.Count, Is.EqualTo(6), "1 个吊点 + 5 节 = 6 个质点");
            Assert.That(system.Springs.Count, Is.EqualTo(5), "5 节链 = 5 根弹簧");

            Assert.That(system.Particles[0].pinned, Is.True, "0 号必须是固定吊点");
            Assert.That(system.Particles[0].inverseMass, Is.EqualTo(0f), "固定吊点的 inverseMass 必须为 0");
            for (int i = 1; i < system.Particles.Count; i++)
            {
                Assert.That(system.Particles[i].pinned, Is.False, "第 " + i + " 个链节不应被钉死");
            }
        }

        // Given 演示链条的 5 根弹簧
        // When 逐节比较刚度与索引
        // Then k 严格递减（上硬下软），且索引首尾相接 0→1→2→3→4→5
        [Test]
        public void BuildChain_StiffnessDecreasesAndLinksAreChained()
        {
            MassSpringSystem system = _behaviour.System;

            for (int s = 0; s < system.Springs.Count; s++)
            {
                Spring spring = system.Springs[s];
                Assert.That(spring.a, Is.EqualTo(s), "第 " + s + " 根弹簧的 A 端应是链节 " + s);
                Assert.That(spring.b, Is.EqualTo(s + 1), "第 " + s + " 根弹簧的 B 端应是链节 " + (s + 1));
                Assert.That(spring.stiffness, Is.GreaterThan(0f), "第 " + s + " 根弹簧的 k 必须为正");
                if (s > 0)
                {
                    Assert.That(spring.stiffness, Is.LessThan(system.Springs[s - 1].stiffness),
                        "第 " + s + " 根弹簧应比上一根软（上硬下软的摆动层次）");
                }
            }
        }

        // Given 演示链条里弹簧原长都填 0
        // When 构建完成
        // Then 自动取到的原长等于相邻链节的初始间距
        [Test]
        public void BuildChain_RestLengthsAutoMatchInitialSpacing()
        {
            MassSpringSystem system = _behaviour.System;

            for (int s = 0; s < system.Springs.Count; s++)
            {
                Spring spring = system.Springs[s];
                float initialDistance = Vector3.Distance(
                    system.Particles[spring.a].InitialPosition,
                    system.Particles[spring.b].InitialPosition);
                Assert.That(spring.restLength, Is.EqualTo(initialDistance).Within(1e-5f),
                    "第 " + s + " 根弹簧的原长应等于其两端初始距离");
                Assert.That(spring.restLength, Is.GreaterThan(0f), "原长必须为正");
            }
        }

        // Given 演示链条配置好的积分参数
        // When 检查参数
        // Then 子步数足够稳住最硬那一节（k*dt^2/m 远小于稳定阈值 4）
        [Test]
        public void BuildChain_SubstepsKeepHardestLinkStable()
        {
            MassSpringSystem system = _behaviour.System;
            Assert.That(system.Parameters.substeps, Is.GreaterThan(1), "演示链条必须开子步");

            const float fixedDeltaTime = 1f / 60f;
            float subStep = system.Parameters.ClampDeltaTime(fixedDeltaTime) / system.Parameters.EffectiveSubsteps;

            for (int s = 0; s < system.Springs.Count; s++)
            {
                Spring spring = system.Springs[s];
                float massA = system.Particles[spring.a].mass;
                float massB = system.Particles[spring.b].mass;
                float worstMass = Mathf.Min(massA, massB);
                float stiffnessPerStep = spring.stiffness * subStep * subStep / worstMass;
                Assert.That(stiffnessPerStep, Is.LessThan(1f),
                    "第 " + s + " 根弹簧在子步长下的 k*h^2/m 必须远小于稳定阈值 4（实测 " + stiffnessPerStep.ToString("F4") + "）");
            }
        }

        // Given 演示链条里每个活动链节
        // When 检查可视化绑定
        // Then 都挂了一个 MassSpringParticleLink 且索引指向自己那个质点
        [Test]
        public void BuildChain_LinksEveryFreeParticle()
        {
            MassSpringParticleLink[] links = _behaviour.GetComponentsInChildren<MassSpringParticleLink>(true);
            Assert.That(links.Length, Is.EqualTo(_behaviour.System.Particles.Count - 1),
                "除吊点外每个链节都应有一个可见球");

            var seen = new System.Collections.Generic.HashSet<int>();
            foreach (MassSpringParticleLink link in links)
            {
                Assert.That(link.target, Is.SameAs(_behaviour), "绑定的目标应是链条自身");
                Assert.That(link.particleIndex, Is.GreaterThan(0), "吊点不需要可见球（锚点用方块表示）");
                Assert.That(link.particleIndex, Is.LessThan(_behaviour.System.Particles.Count), "索引必须在系统范围内");
                Assert.That(seen.Add(link.particleIndex), Is.True, "索引 " + link.particleIndex + " 不应被重复绑定");
            }
        }
    }
}
