// Copyright (c) 2026 PhysicsSimulation. MIT License.
//
// 正式测试：Unity 层（配置 -> 系统 的翻译，以及演示场景构建器）。
// 约定：
//   - MassSpringBuilder 是纯静态翻译层，不碰场景，可直接断言
//   - MassSpringBehaviour 用 hideFlags = DontSave 的临时对象，TearDown 里 DestroyImmediate
//   - 断言消息用中文

using System;
using NUnit.Framework;
using UnityEngine;

namespace PhysicsSimulation.Editor.Tests
{
    [TestFixture]
    public class MassSpringBuilderTests
    {
        private static MassSpringParticleData P(float x, float y, float z, float mass, bool pinned = false,
                                                Vector3 velocity = default(Vector3), float damping = 0f)
        {
            return new MassSpringParticleData
            {
                position = new Vector3(x, y, z),
                mass = mass,
                pinned = pinned,
                initialVelocity = velocity,
                damping = damping
            };
        }

        private static MassSpringSpringData S(int a, int b, float stiffness = 200f, float damping = 1f, float restLength = 0f)
        {
            return new MassSpringSpringData { a = a, b = b, stiffness = stiffness, damping = damping, restLength = restLength };
        }

        private static MassSpringSystem BuildTwoLinkChain()
        {
            return MassSpringBuilder.Build(
                new System.Collections.Generic.List<MassSpringParticleData>
                {
                    P(0f, 2f, 0f, 1f, pinned: true),
                    P(0f, 1f, 0f, 1f),
                    P(0f, 0f, 0f, 1f)
                },
                new System.Collections.Generic.List<MassSpringSpringData> { S(0, 1), S(1, 2) },
                new Vector3(0f, -9.81f, 0f), 0.5f, 4, 1f / 15f);
        }

        // Given 3 个质点 + 2 根弹簧的配置
        // When MassSpringBuilder.Build
        // Then 系统的粒子数、弹簧数、索引对应关系与配置一致，参数也整体搬进了 MassSpringParameters
        [Test]
        public void Build_ConfigMapsToSystem()
        {
            MassSpringSystem system = BuildTwoLinkChain();

            Assert.That(system.Particles.Count, Is.EqualTo(3), "质点数应与配置一致");
            Assert.That(system.Springs.Count, Is.EqualTo(2), "弹簧数应与配置一致");
            Assert.That(system.Springs[0].a, Is.EqualTo(0), "第 0 根弹簧的 A 端索引");
            Assert.That(system.Springs[0].b, Is.EqualTo(1), "第 0 根弹簧的 B 端索引");
            Assert.That(system.Springs[1].b, Is.EqualTo(2), "第 1 根弹簧的 B 端索引");
            Assert.That(system.Particles[0].pinned, Is.True, "配置里的 pinned 应落到质点上");
            Assert.That(system.Particles[0].inverseMass, Is.EqualTo(0f), "吊点的 inverseMass 必须为 0");

            Assert.That(system.Parameters.gravity.y, Is.EqualTo(-9.81f), "重力应被搬进参数");
            Assert.That(system.Parameters.globalDamping, Is.EqualTo(0.5f), "全局阻尼应被搬进参数");
            Assert.That(system.Parameters.substeps, Is.EqualTo(4), "子步数应被搬进参数");
            Assert.That(system.Parameters.maxDeltaTime, Is.EqualTo(1f / 15f).Within(1e-6f), "dt 上限应被搬进参数");
        }

        // Given 弹簧配置里 restLength 填 0
        // When MassSpringBuilder.Build
        // Then 原长自动等于两端的初始距离
        [Test]
        public void Build_ZeroRestLength_AutoUsesInitialDistance()
        {
            MassSpringSystem system = BuildTwoLinkChain();

            Assert.That(system.Springs[0].restLength, Is.EqualTo(1f).Within(1e-5f), "0→1 的初始距离是 1 米");
            Assert.That(system.Springs[1].restLength, Is.EqualTo(1f).Within(1e-5f), "1→2 的初始距离是 1 米");
        }

        // Given 显式填写了 restLength
        // When MassSpringBuilder.Build
        // Then 用填进去的值，不做自动覆盖
        [Test]
        public void Build_ExplicitRestLength_IsKept()
        {
            MassSpringSystem system = MassSpringBuilder.Build(
                new System.Collections.Generic.List<MassSpringParticleData> { P(0f, 0f, 0f, 1f), P(1f, 0f, 0f, 1f) },
                new System.Collections.Generic.List<MassSpringSpringData> { S(0, 1, restLength: 0.4f) },
                Vector3.zero, 0f, 1, 1f / 15f);

            Assert.That(system.Springs[0].restLength, Is.EqualTo(0.4f).Within(1e-6f), "显式原长不应被自动距离覆盖");
        }

        // Given 配置里给质点填了初速度
        // When 构建后步进几步再 ResetToInitial
        // Then 复位回到"配置里的速度"，而不是零速度
        [Test]
        public void Build_ConfiguredInitialVelocity_SurvivesReset()
        {
            var velocity = new Vector3(1.25f, 0f, -0.5f);
            MassSpringSystem system = MassSpringBuilder.Build(
                new System.Collections.Generic.List<MassSpringParticleData>
                {
                    P(0f, 0f, 0f, 1f, velocity: velocity)
                },
                null,
                Vector3.zero, 0f, 1, 1f / 15f);

            Assert.That(system.Particles[0].velocity, Is.EqualTo(velocity), "构建后应带上配置的初速度");

            for (int n = 0; n < 5; n++) system.Step(1f / 60f);
            Assert.That(system.Particles[0].position.x, Is.GreaterThan(0f), "带初速度且不收阻尼的质点应往前走");

            system.ResetToInitial();
            Assert.That(system.Particles[0].position, Is.EqualTo(Vector3.zero), "复位应回到配置位置");
            Assert.That(system.Particles[0].velocity.x, Is.EqualTo(velocity.x), "复位应回到配置速度 X");
            Assert.That(system.Particles[0].velocity.z, Is.EqualTo(velocity.z), "复位应回到配置速度 Z");
        }

        // Given 弹簧配置里写了越界的端点索引
        // When MassSpringBuilder.Build
        // Then 异常原样抛出（配置层不吞掉系统层的校验）
        [Test]
        public void Build_OutOfRangeSpringIndex_Throws()
        {
            Assert.That(
                () => MassSpringBuilder.Build(
                    new System.Collections.Generic.List<MassSpringParticleData> { P(0f, 0f, 0f, 1f) },
                    new System.Collections.Generic.List<MassSpringSpringData> { S(0, 7) },
                    Vector3.zero, 0f, 1, 1f / 15f),
                Throws.TypeOf<ArgumentOutOfRangeException>(), "越界索引必须抛异常");
        }

        // Given 同一份配置建两套系统
        // When 各自步进同样多次
        // Then 结果逐位一致（翻译层不引入任何不确定性）
        [Test]
        public void Build_SameConfigTwice_ProducesIdenticalSimulation()
        {
            MassSpringSystem first = BuildTwoLinkChain();
            MassSpringSystem second = BuildTwoLinkChain();

            for (int n = 0; n < 40; n++)
            {
                first.Step(1f / 60f);
                second.Step(1f / 60f);
            }

            for (int i = 0; i < first.Particles.Count; i++)
            {
                Assert.That(second.Particles[i].position.x, Is.EqualTo(first.Particles[i].position.x), "第 " + i + " 个质点 X 应逐位一致");
                Assert.That(second.Particles[i].position.y, Is.EqualTo(first.Particles[i].position.y), "第 " + i + " 个质点 Y 应逐位一致");
                Assert.That(second.Particles[i].position.z, Is.EqualTo(first.Particles[i].position.z), "第 " + i + " 个质点 Z 应逐位一致");
            }
        }
    }

    [TestFixture]
    public class MassSpringBehaviourTests
    {
        private GameObject _host;
        private MassSpringBehaviour _behaviour;

        [SetUp]
        public void SetUp()
        {
            _host = new GameObject("MassSpringBehaviourTests_Host");
            _host.hideFlags = HideFlags.DontSave;
            _behaviour = _host.AddComponent<MassSpringBehaviour>();
            _behaviour.hideFlags = HideFlags.DontSave;
        }

        [TearDown]
        public void TearDown()
        {
            if (_host != null) UnityEngine.Object.DestroyImmediate(_host);
        }

        private void ConfigureValidChain()
        {
            _behaviour.gravity = new Vector3(0f, -9.81f, 0f);
            _behaviour.globalDamping = 0.4f;
            _behaviour.substeps = 4;
            _behaviour.maxDeltaTime = 1f / 15f;
            _behaviour.particles.Clear();
            _behaviour.springs.Clear();
            _behaviour.particles.Add(new MassSpringParticleData { position = new Vector3(0f, 2f, 0f), mass = 1f, pinned = true });
            _behaviour.particles.Add(new MassSpringParticleData { position = new Vector3(0f, 1f, 0f), mass = 1f });
            _behaviour.springs.Add(new MassSpringSpringData { a = 0, b = 1, stiffness = 300f, damping = 1f, restLength = 0f });
        }

        // Given 一个挂了 MassSpringBehaviour 的对象与合法配置
        // When Rebuild
        // Then 构建成功、System 非空、无错误记录
        [Test]
        public void Rebuild_ValidConfig_BuildsSystem()
        {
            ConfigureValidChain();

            Assert.That(_behaviour.Rebuild(), Is.True, "合法配置应构建成功");
            Assert.That(_behaviour.IsBuilt, Is.True, "IsBuilt 应为真");
            Assert.That(_behaviour.LastBuildError, Is.Null, "不应记录错误");
            Assert.That(_behaviour.System.Particles.Count, Is.EqualTo(2), "质点数");
            Assert.That(_behaviour.System.Springs.Count, Is.EqualTo(1), "弹簧数");
        }

        // Given 配置里弹簧端点索引越界
        // When Rebuild
        // Then 返回 false、System 为 null、把原因写进 LastBuildError（不把异常抛给 Unity 外层）
        [Test]
        public void Rebuild_InvalidConfig_ReportsErrorInsteadOfThrowing()
        {
            ConfigureValidChain();
            _behaviour.springs[0].b = 99;

            Assert.That(_behaviour.Rebuild(), Is.False, "非法配置应返回 false");
            Assert.That(_behaviour.System, Is.Null, "非法配置时不应留下半成品系统");
            Assert.That(string.IsNullOrEmpty(_behaviour.LastBuildError), Is.False, "必须记录可读的失败原因");
        }

        // Given 已经构建并步进过一段的系统
        // When 调用 CaptureCurrentAsRest
        // Then 当前位置与当前弹簧长度被写成新的初始布局，Reset 后不再回到老位置
        [Test]
        public void CaptureCurrentAsRest_WritesSimulatedStateBackIntoConfig()
        {
            ConfigureValidChain();
            Assert.That(_behaviour.Rebuild(), Is.True, "前置：构建成功");

            for (int n = 0; n < 30; n++) _behaviour.System.Step(1f / 60f);
            Vector3 simulated = _behaviour.System.Particles[1].position;
            float simulatedLength = Vector3.Distance(_behaviour.System.Particles[0].position, simulated);

            _behaviour.CaptureCurrentAsRest();

            Assert.That(_behaviour.particles[1].position, Is.EqualTo(simulated), "配置里的位置应被更新为模拟后的位置");
            Assert.That(_behaviour.springs[0].restLength, Is.EqualTo(simulatedLength).Within(1e-5f),
                "配置里的原长应被更新为模拟后的实际长度");

            _behaviour.ResetToInitialLayout();
            Assert.That(_behaviour.System.Particles[1].position, Is.EqualTo(simulated).Within(1e-5f),
                "复位应回到新捕获的布局（而不是最初的 2 米吊点配置）");
        }

        // Given 播放模式之外改了参数但没重建
        // When 修改 gravity 后调用 Rebuild
        // Then 系统参数与配置保持一致（Inspector 改动会真的落到系统上）
        [Test]
        public void Rebuild_AfterConfigEdit_ReflectsLatestValues()
        {
            ConfigureValidChain();
            _behaviour.Rebuild();

            _behaviour.gravity = new Vector3(0f, -3.5f, 0f);
            _behaviour.substeps = 7;
            _behaviour.Rebuild();

            Assert.That(_behaviour.System.Parameters.gravity.y, Is.EqualTo(-3.5f), "重力改动应被重建采纳");
            Assert.That(_behaviour.System.Parameters.substeps, Is.EqualTo(7), "子步数改动应被重建采纳");
        }
    }
}
