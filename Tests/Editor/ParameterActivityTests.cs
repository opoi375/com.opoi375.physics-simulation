// Copyright (c) 2026 PhysicsSimulation. MIT License.
using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using PhysicsSimulation;
using PhysicsSimulation.EditorTools;
using UnityEngine;

namespace PhysicsSimulation.Editor.Tests
{
    /// <summary>
    /// "参数到底什么时候生效"的统一契约（v1.6.0 架构改造）。
    ///
    /// 起因：同一个缺陷在四个模块里各长了一次 —— 求解器在构造时把某个字段**拷贝**进内部结构，
    /// 此后 Inspector 上怎么改都毫无反应也不报错：
    ///   * 软体  gravity / damping（v1.2.0 埋，v1.6.0 才修）
    ///   * 质点弹簧 gravity / globalDamping / substeps / maxDeltaTime（本轮修）
    ///   * 布料  mass（一半冻一半活，还能把逆质量写成 Infinity）
    ///   * 流体  substeps / solverIterations（运行期没有下界，设 0 静默冻结）
    ///
    /// 光把这几处补上只能治标：下一个人加字段时仍然可以随手 new 一个拷贝。所以改成**可断言**的契约：
    ///   1. 每个参数字段必须恰好带一个标记（<see cref="RuntimeTunableAttribute"/> 或
    ///      <see cref="RequiresRebuildAttribute"/>，后者必须写理由）；
    ///   2. 求解器必须持有 Behaviour 那个参数**实例本身**（ReferenceEquals），而不是它的副本；
    ///      唯一例外是质点弹簧，它把自己的四个积分参数逐项镜像，那就逐项断言镜像相等。
    /// 这样"新增字段忘了表态""又偷偷拷了一份"都会在测试里直接红。
    /// </summary>
    public class ParameterActivityTests
    {
        static readonly List<UnityEngine.Object> Trash = new List<UnityEngine.Object>();

        [TearDown]
        public void TearDown()
        {
            for (int i = Trash.Count - 1; i >= 0; i--)
                if (Trash[i] != null) UnityEngine.Object.DestroyImmediate(Trash[i]);
            Trash.Clear();
        }

        static IEnumerable<Type> ParameterTypes()
        {
            yield return typeof(SoftBodyParameters);
            yield return typeof(ClothParameters);
            yield return typeof(FluidParameters);
            yield return typeof(MassSpringParameters);
        }

        // ============================================================ ① 每个字段都必须表态

        [TestCaseSource(nameof(ParameterTypeCases))]
        public void EveryParameterField_IsClassifiedExactlyOnce(Type type)
        {
            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
            Assert.That(fields.Length, Is.GreaterThan(0), type.Name + " 一个公开字段都没有？");

            var unclassified = new List<string>();
            foreach (FieldInfo field in fields)
            {
                if (ParameterActivity.Classify(field) == ParameterActivity.Kind.Unclassified)
                    unclassified.Add(field.Name);
            }

            // 未分类 = 没人知道改它要不要重建。这不是风格问题：
            // 软体的 gravity 就是这样被冻了四个版本，用户改了没反应还以为是自己参数没调对。
            Assert.That(unclassified, Is.Empty,
                type.Name + " 有字段没标生效方式（加 [RuntimeTunable] 或 [RequiresRebuild(理由)]）："
                + string.Join(", ", unclassified));
        }

        public static IEnumerable<TestCaseData> ParameterTypeCases()
        {
            foreach (Type type in ParameterTypes())
                yield return new TestCaseData(type).SetName("EveryParameterField_IsClassified_" + type.Name);
        }

        [Test]
        public void EveryRebuildOnlyField_ExplainsWhy()
        {
            var missing = new List<string>();
            foreach (Type type in ParameterTypes())
            {
                foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (ParameterActivity.Classify(field) != ParameterActivity.Kind.RequiresRebuild) continue;
                    string reason = ParameterActivity.RebuildReason(field);
                    if (string.IsNullOrWhiteSpace(reason)) missing.Add(type.Name + "." + field.Name);
                }
            }

            // 光说"要重建"不够：得说清在哪一步被读走了，否则下一个人没法判断能不能绕过。
            Assert.That(missing, Is.Empty, "这些字段标了需要重建却没写理由：" + string.Join(", ", missing));
        }

        [Test]
        public void Classify_TreatsDoubleMarkingAsUnclassified()
        {
            // 两个都标 = 调用方无法判断该不该重建，必须当成未分类被测试抓住
            FieldInfo field = typeof(DoubleMarked).GetField(nameof(DoubleMarked.value));
            Assert.That(ParameterActivity.Classify(field), Is.EqualTo(ParameterActivity.Kind.Unclassified),
                "同时标了 RuntimeTunable 与 RequiresRebuild 的字段必须算未分类");

            FieldInfo plain = typeof(DoubleMarked).GetField(nameof(DoubleMarked.plain));
            Assert.That(ParameterActivity.Classify(plain), Is.EqualTo(ParameterActivity.Kind.Unclassified),
                "什么都没标必须是未分类");
        }

        sealed class DoubleMarked
        {
            [RuntimeTunable]
            [RequiresRebuild("故意两个都标")]
            public float value = 1f;

            public float plain = 1f;
        }

        // ============================================================ ② 求解器不许持有参数副本

        [Test]
        public void SoftBody_SolverSharesTheBehaviourParameterInstance()
        {
            var go = new GameObject("Soft");
            Trash.Add(go);
            var body = go.AddComponent<SoftBodyBehaviour>();
            body.sourceMesh = Track<Mesh>(SoftBodyDemoTools.BuildBoxMesh(0.3f, 0.3f, 0.3f, 2));
            body.autoSimulate = false;
            body.Rebuild();
            Assert.That(body.IsBuilt, Is.True, body.LastBuildError);

            // 同一个实例 ⇒ 任何 [RuntimeTunable] 字段天然实时生效，不需要谁去同步。
            // 反过来说：如果哪天有人在这里传了一份拷贝，这条会立刻红。
            Assert.That(ReferenceEquals(body.Simulation.Parameters, body.parameters), Is.True,
                "软体求解器拿的不是 Behaviour 的那个参数实例（改 Inspector 会静默无效）");
        }

        [Test]
        public void Cloth_SolverSharesTheBehaviourParameterInstance()
        {
            var go = new GameObject("Cloth");
            Trash.Add(go);
            var cloth = go.AddComponent<ClothBehaviour>();
            cloth.autoSimulate = false;
            cloth.Rebuild();
            Assert.That(cloth.IsBuilt, Is.True, cloth.LastBuildError);

            Assert.That(ReferenceEquals(cloth.System.Parameters, cloth.parameters), Is.True,
                "布料求解器拿的不是 Behaviour 的那个参数实例");
        }

        [Test]
        public void Fluid_SolverSharesTheBehaviourParameterInstance()
        {
            var go = new GameObject("Fluid");
            Trash.Add(go);
            var fluid = go.AddComponent<FluidBehaviour>();
            fluid.autoSimulate = false;
            fluid.volumeSize = new Vector3(0.2f, 0.2f, 0.2f);
            fluid.Rebuild();
            Assert.That(fluid.Simulation, Is.Not.Null, fluid.LastBuildError);

            Assert.That(ReferenceEquals(fluid.Simulation.Parameters, fluid.parameters), Is.True,
                "流体求解器拿的不是 Behaviour 的那个参数实例");
        }

        [Test]
        public void MassSpring_MirrorsEveryRuntimeTunableFieldIntoTheSystem()
        {
            // 质点弹簧是唯一有"第二份参数"的模块（MassSpringSystem 自带 Parameters）。
            // 那就把它标为 RuntimeTunable 的字段逐项盯住：Step 之后必须与 Behaviour 完全相等。
            var go = new GameObject("MS");
            Trash.Add(go);
            var body = go.AddComponent<MassSpringBehaviour>();
            body.autoSimulate = false;
            body.particles.Add(new MassSpringParticleData { position = Vector3.zero, mass = 1f });
            Assert.That(body.Rebuild(), Is.True, body.LastBuildError);

            body.gravity = new Vector3(1f, -2f, 3f);
            body.globalDamping = 0.75f;
            body.substeps = 5;
            body.maxDeltaTime = 1f / 40f;
            body.Step(1f / 120f);

            MassSpringParameters p = body.System.Parameters;
            Assert.That(p.gravity, Is.EqualTo(body.gravity), "gravity 没镜像");
            Assert.That(p.globalDamping, Is.EqualTo(body.globalDamping), "globalDamping 没镜像");
            Assert.That(p.substeps, Is.EqualTo(body.substeps), "substeps 没镜像");
            Assert.That(p.maxDeltaTime, Is.EqualTo(body.maxDeltaTime), "maxDeltaTime 没镜像");
        }

        [Test]
        public void MassSpringParameters_AreAllRuntimeTunable()
        {
            // 这五个字段都不影响拓扑，全部该实时生效。谁被标成 RequiresRebuild 就是契约退化。
            foreach (FieldInfo field in typeof(MassSpringParameters).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.That(ParameterActivity.Classify(field), Is.EqualTo(ParameterActivity.Kind.RuntimeTunable),
                    "MassSpringParameters." + field.Name + " 不影响拓扑，应当是可实时调");
            }
        }

        // ============================================================ ③ 现行行为抽查

        [Test]
        public void SoftBody_SpringStiffness_RequiresRebuild_VolumeStiffnessDoesNot()
        {
            // 契约里最容易搞错的一对：同为"刚度"，一个烤进 Spring、一个每步重算。
            var go = new GameObject("Soft2");
            Trash.Add(go);
            var body = go.AddComponent<SoftBodyBehaviour>();
            body.sourceMesh = Track<Mesh>(SoftBodyDemoTools.BuildBoxMesh(0.3f, 0.3f, 0.3f, 2));
            body.autoSimulate = false;
            body.Rebuild();
            Assert.That(body.IsBuilt, Is.True, body.LastBuildError);

            float springAtBuild = body.Simulation.System.Springs[0].stiffness;
            body.parameters.springStiffness = springAtBuild + 500f;
            body.Step(1f / 60f);
            Assert.That(body.Simulation.System.Springs[0].stiffness, Is.EqualTo(springAtBuild),
                "springStiffness 不该在运行期生效（它是 readonly 的弹簧属性），契约写的是需要重建");

            // 另一半由标记本身保证：volumeStiffness 每步被读，所以标 RuntimeTunable。
            // 这里只钉"标记与行为一致"，不重复测物理（那是 SoftBodySimulationTests 的事）。
            FieldInfo volume = typeof(SoftBodyParameters).GetField(nameof(SoftBodyParameters.volumeStiffness));
            Assert.That(ParameterActivity.Classify(volume), Is.EqualTo(ParameterActivity.Kind.RuntimeTunable),
                "volumeStiffness 每步都被读，不该标成需要重建");
        }

        static T Track<T>(T o) where T : UnityEngine.Object { Trash.Add(o); return o; }
    }
}
