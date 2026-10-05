// Copyright (c) 2026 PhysicsSimulation. MIT License.
using System;
using System.Collections.Generic;
using NUnit.Framework;
using PhysicsSimulation;
using UnityEngine;

namespace PhysicsSimulation.Editor.Tests
{
    /// <summary>
    /// v1.3.0 碰撞代理的几何行为规格（纯逻辑层：不读场景、不碰 PhysicsScene，所以每条都能闭式断言）。
    ///
    /// 约定（全文件通用）：
    ///   PushOut(point, skin) 的语义是"把点推到几何体表面之外至少 skin 的距离"。
    ///   点已经在外面（含正好贴着 skin 距离）→ 原样返回，绝不"顺手"拉近。
    ///   点在体内 → 沿**最小穿透深度**的面法线顶出去（盒子的语义，球/胶囊只有一个方向）。
    ///   半空间（平面）是特例：体=法线反侧，无论多深都顶到 Surface 侧，这样软体落地才不会被吞掉。
    /// </summary>
    public class CollisionProxyTests
    {
        const float Skin = 0.02f;

        static int Bits(float v) { return BitConverter.SingleToInt32Bits(v); }

        static void AssertFinite(Vector3 v, string scene)
        {
            Assert.That(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z)
                        || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z),
                Is.False, scene + "：结果必须是有限值，实际 " + v);
        }

        // ------------------------------------------------------------------ 球

        [Test]
        public void Sphere_OutsidePoint_IsUntouchedBitForBit()
        {
            // Given：半径 1 的球，一个明显在"表面 + 皮肤"之外的点
            var sphere = new SphereCollisionProxy(Vector3.zero, 1f);
            var outside = new Vector3(2f, 0.5f, -3f);

            // When
            Vector3 result = sphere.PushOut(outside, Skin);

            // Then：不碰。逐位一致——不能因为"算了又没改"引入浮点误差
            Assert.That(Bits(result.x), Is.EqualTo(Bits(outside.x)), "体外点的 x 必须逐位不变");
            Assert.That(Bits(result.y), Is.EqualTo(Bits(outside.y)), "体外点的 y 必须逐位不变");
            Assert.That(Bits(result.z), Is.EqualTo(Bits(outside.z)), "体外点的 z 必须逐位不变");
        }

        [Test]
        public void Sphere_InsidePoint_IsPushedToSurfacePlusSkin()
        {
            // Given：球心原点、半径 1，点在球心偏 +x 0.5 处
            var sphere = new SphereCollisionProxy(Vector3.zero, 1f);

            // When
            Vector3 result = sphere.PushOut(new Vector3(0.5f, 0f, 0f), Skin);

            // Then：顶到半径 1 + 皮肤 0.02，方向仍是原来的径向方向
            Assert.That(result.magnitude, Is.EqualTo(1.02f).Within(1e-5f),
                "体内点应当被顶到 表面+皮肤 处");
            Assert.That(result.z, Is.EqualTo(0f).Within(1e-6f), "不该产生垂直于径向的分量");
            Assert.That(result.y, Is.EqualTo(0f).Within(1e-6f), "不该产生垂直于径向的分量");
        }

        [Test]
        public void Sphere_ArbitraryDirection_PreservesRadialDirection()
        {
            // Given：偏心球 + 体内一点，方向故意取成三个分量都不为 0 的一般位置
            var center = new Vector3(1f, -2f, 0.5f);
            var sphere = new SphereCollisionProxy(center, 2f);
            Vector3 inside = center + new Vector3(0.3f, 0.4f, -1.2f);

            // When
            Vector3 result = sphere.PushOut(inside, 0f);

            // Then：结果 = 球心 + 原方向 × 半径
            Vector3 direction = (result - center).normalized;
            Vector3 expectedDirection = (inside - center).normalized;
            Assert.That(direction.x, Is.EqualTo(expectedDirection.x).Within(1e-5f), "推出方向必须沿径向，不能偏向坐标轴");
            Assert.That(direction.y, Is.EqualTo(expectedDirection.y).Within(1e-5f), "推出方向必须沿径向");
            Assert.That(direction.z, Is.EqualTo(expectedDirection.z).Within(1e-5f), "推出方向必须沿径向");
            Assert.That((result - center).magnitude, Is.EqualTo(2f).Within(1e-5f), "半径 2 的皮肤 0 ⇒ 落在表面上");
        }

        [Test]
        public void Sphere_ExactlyAtCenter_PushesOutAlongFixedAxisNotNaN()
        {
            // Given：点与球心完全重合——径向长度为 0，方向在数学上未定义
            var center = new Vector3(3f, 3f, 3f);
            var sphere = new SphereCollisionProxy(center, 1f);

            // When
            Vector3 result = sphere.PushOut(center, Skin);

            // Then：必须给一个**确定**方向顶出去，绝不允许 0/0 产生 NaN
            AssertFinite(result, "球心重合");
            Assert.That(result, Is.EqualTo(center + new Vector3(0f, 1.02f, 0f)),
                "方向未定义时应当用固定的 +Y 顶出，保证确定性（这是 v1.1.0 布料就有的约定）");
        }

        [Test]
        public void Sphere_ZeroAndNegativeSkin_BehaveTheSameAsNoSkin()
        {
            // Given：贴在球内 0.5 处的点
            var sphere = new SphereCollisionProxy(Vector3.zero, 1f);
            Vector3 inside = new Vector3(0f, 0.5f, 0f);

            // When
            Vector3 withSkin = sphere.PushOut(inside, 0f);
            Vector3 negativeSkin = sphere.PushOut(inside, -0.5f);

            // Then：皮肤为 0 与为负（非法）都应当当作"贴表面"，而不是把点往球心里塞
            Assert.That(withSkin.magnitude, Is.EqualTo(1f).Within(1e-5f), "皮肤 0 应落在表面");
            Assert.That(negativeSkin.magnitude, Is.GreaterThanOrEqualTo(1f - 1e-5f),
                "负皮肤不得把点推到球体内部去");
        }

        // ------------------------------------------------------------------ 盒

        [Test]
        public void Box_InsidePoint_IsPushedOutByShallowestFace()
        {
            // Given：单位盒（半尺寸 1,1,1），点贴近 +X 面内侧
            var box = new BoxCollisionProxy(Vector3.zero, Vector3.one, Quaternion.identity);

            // When
            Vector3 result = box.PushOut(new Vector3(0.9f, 0f, 0f), Skin);

            // Then：+X 面穿透最浅（0.1），所以顶 +X；顶到 1 + 皮肤
            Assert.That(result.x, Is.EqualTo(1.02f).Within(1e-5f), "应沿最浅穿透面 +X 顶出");
            Assert.That(result.y, Is.EqualTo(0f).Within(1e-5f), "不该被无关的面带偏");
            Assert.That(result.z, Is.EqualTo(0f).Within(1e-5f), "不该被无关的面带偏");
        }

        [Test]
        public void Box_NearCorner_UsesTheDiagonalClosestPoint()
        {
            // Given：单位盒，点在 +X/+Y 棱外侧、距离小于皮肤
            var box = new BoxCollisionProxy(Vector3.zero, Vector3.one, Quaternion.identity);
            Vector3 nearEdge = new Vector3(1.005f, 1.005f, 0f);

            // When
            Vector3 result = box.PushOut(nearEdge, Skin);

            // Then：棱外侧最近点是 (1,1,0)，推出方向是对角线，而不是某一根轴
            Assert.That(result.x, Is.EqualTo(result.y).Within(1e-5f), "对角推出 ⇒ 两个分量对称");
            Assert.That(Mathf.Abs(result.z), Is.LessThan(1e-5f), "z 方向没有穿透，不该动");
            float fromEdge = (result - new Vector3(1f, 1f, 0f)).magnitude;
            Assert.That(fromEdge, Is.EqualTo(Skin).Within(1e-4f), "推出后离最近表面点应当正好隔一层皮肤");
        }

        [Test]
        public void Box_JustOutsideAFace_IsPushedToSkinDistance()
        {
            // Given：点在 +X 面外 0.005，皮肤 0.02
            var box = new BoxCollisionProxy(Vector3.zero, Vector3.one, Quaternion.identity);

            // When
            Vector3 result = box.PushOut(new Vector3(1.005f, 0f, 0f), Skin);

            // Then：被推到 1 + 皮肤
            Assert.That(result.x, Is.EqualTo(1.02f).Within(1e-5f), "皮肤不足时必须补足到 skin");
        }

        [Test]
        public void Box_FarOutside_IsUntouchedBitForBit()
        {
            // Given：远离盒子的点
            var box = new BoxCollisionProxy(Vector3.zero, Vector3.one, Quaternion.identity);
            Vector3 outside = new Vector3(1.1f, 5f, -2f);

            // When / Then：逐位不动
            Vector3 result = box.PushOut(outside, Skin);
            Assert.That(Bits(result.x), Is.EqualTo(Bits(outside.x)), "盒外点的 x 必须逐位不变");
            Assert.That(Bits(result.y), Is.EqualTo(Bits(outside.y)), "盒外点的 y 必须逐位不变");
            Assert.That(Bits(result.z), Is.EqualTo(Bits(outside.z)), "盒外点的 z 必须逐位不变");
        }

        [Test]
        public void Box_Rotated45Degrees_PushesAlongRotatedNormal()
        {
            // Given：绕 Y 转 45° 的单位盒，点在其 +X 面内侧 0.1
            Quaternion rotation = Quaternion.Euler(0f, 45f, 0f);
            var box = new BoxCollisionProxy(Vector3.zero, Vector3.one, rotation);
            Vector3 localInside = new Vector3(0.9f, 0f, 0f);                  // 局部 +X 面内侧
            Vector3 point = rotation * localInside;                            // 变换到世界

            // When
            Vector3 result = box.PushOut(point, Skin);

            // Then：推出方向是**旋转后的**面法线，而不是世界 +X
            Vector3 expectedNormal = rotation * Vector3.right;
            Vector3 moved = (result - point);
            Assert.That(moved.sqrMagnitude, Is.GreaterThan(1e-8f), "体内点必然要被推动");
            Assert.That(Vector3.Dot(moved.normalized, expectedNormal), Is.EqualTo(1f).Within(1e-4f),
                "推出方向必须跟着盒子一起转，否则旋转的墙就变成斜的");
            AssertFinite(result, "旋转盒");
        }

        [Test]
        public void Box_DeepInside_StillExitsByTheShallowestAxis()
        {
            // Given：盒子偏心几何：半尺寸 (0.2, 3, 3)，点在原点上偏 +X 0.15 ⇒ 距 +X 面 0.05，距 ±Y/±Z 面 2.85
            var box = new BoxCollisionProxy(Vector3.zero, new Vector3(0.2f, 3f, 3f), Quaternion.identity);

            // When
            Vector3 result = box.PushOut(new Vector3(0.15f, 0f, 0f), 0f);

            // Then：走最薄的那一轴（板状碰撞体落地时应当从最近面出来，不是从侧面）
            Assert.That(result.x, Is.EqualTo(0.2f).Within(1e-5f), "薄板应当沿薄方向脱出");
            Assert.That(Mathf.Abs(result.y), Is.LessThan(1e-6f), "不该产生 y 分量");
        }

        // ------------------------------------------------------------------ 胶囊

        [Test]
        public void Capsule_InsideCylinderBody_IsPushedRadially()
        {
            // Given：竖直胶囊（轴段 (0,-1,0)-(0,1,0)，半径 0.5），点在柱体内侧
            var capsule = new CapsuleCollisionProxy(new Vector3(0f, -1f, 0f), new Vector3(0f, 1f, 0f), 0.5f);

            // When
            Vector3 result = capsule.PushOut(new Vector3(0.2f, 0f, 0f), Skin);

            // Then：沿离开轴线的方向顶到 半径+皮肤
            Assert.That(result.y, Is.EqualTo(0f).Within(1e-6f), "柱体内推出只沿径向，不该沿轴向滑");
            AssertThatFromAxis(result, new Vector3(0f, 0f, 0f), 0.52f, "柱体推出距离");
        }

        [Test]
        public void Capsule_BeyondTheCap_IsPushedOffTheSphereCap()
        {
            // Given：同一个胶囊，点在 +Y 端帽外面但仍在半径以内
            var capsule = new CapsuleCollisionProxy(new Vector3(0f, -1f, 0f), new Vector3(0f, 1f, 0f), 0.5f);

            // When
            Vector3 result = capsule.PushOut(new Vector3(0f, 1.3f, 0f), Skin);

            // Then：按球帽处理 ⇒ 顶到 1 + 0.5 + 皮肤
            Assert.That(result.y, Is.EqualTo(1.52f).Within(1e-5f), "端帽外应按球面推出");
            AssertThatFromAxis(result, new Vector3(0f, 1f, 0f), 0.52f, "端帽推出距离");
        }

        [Test]
        public void Capsule_DegenerateZeroLengthSegment_IsStillSafe()
        {
            // Given：轴段塌成一个点（Unity 里 size 小于直径时会发生）
            var capsule = new CapsuleCollisionProxy(new Vector3(0f, 1f, 0f), new Vector3(0f, 1f, 0f), 0.5f);

            // When：点在塌缩中心
            Vector3 result = capsule.PushOut(new Vector3(0f, 1f, 0f), Skin);

            // Then：退化不产生 NaN，按球处理顶出去
            AssertFinite(result, "零长轴段");
            Assert.That((result - new Vector3(0f, 1f, 0f)).magnitude, Is.EqualTo(0.52f).Within(1e-5f),
                "轴段退化时应当退化成球，而不是崩掉");
        }

        // ------------------------------------------------------------------ 平面（半空间）

        [Test]
        public void Plane_AboveWithinSkin_IsPushedBackUp()
        {
            // Given：地面 y=0，法线 +Y，皮肤 0.02；点离地 0.01（小于皮肤）
            var plane = new PlaneCollisionProxy(Vector3.zero, Vector3.up);

            // When
            Vector3 result = plane.PushOut(new Vector3(0.5f, 0.01f, -2f), Skin);

            // Then：顶回 0.02，水平分量不动
            Assert.That(result.y, Is.EqualTo(0.02f).Within(1e-6f), "皮肤不足要补足");
            Assert.That(Bits(result.x), Is.EqualTo(Bits(0.5f)), "平面只沿法线作用，切向不该动");
            Assert.That(Bits(result.z), Is.EqualTo(Bits(-2f)), "平面只沿法线作用，切向不该动");
        }

        [Test]
        public void Plane_DeepBelow_IsStillPushedToTheSurfaceSide()
        {
            // Given：点已经穿到地面以下 3 米（软体高速下坠最容易撞出这种状态）
            var plane = new PlaneCollisionProxy(Vector3.zero, Vector3.up);

            // When
            Vector3 result = plane.PushOut(new Vector3(0f, -3f, 0f), 0.1f);

            // Then：半空间没有"体内多深"的概念，一律顶回面上 + 皮肤；绝不能只减去 0.1
            Assert.That(result.y, Is.EqualTo(0.1f).Within(1e-6f),
                "穿深很深时也必须回到地面之上，否则软体会被地面吞掉");
        }

        [Test]
        public void Plane_UnnormalizedNormal_IsNormalizedOnConstruction()
        {
            // Given：法线给了个没归一化的 (0, 5, 0)
            var plane = new PlaneCollisionProxy(Vector3.zero, new Vector3(0f, 5f, 0f));

            // When：推一个地面下 0.25 的点
            Vector3 result = plane.PushOut(new Vector3(1f, -0.25f, 1f), 0f);

            // Then：法线必须是单位长度，否则推出距离会被法线长度放大 5 倍
            Assert.That(plane.Normal.magnitude, Is.EqualTo(1f).Within(1e-6f), "构造时应当归一化法线");
            Assert.That(result.y, Is.EqualTo(0f).Within(1e-5f), "非单位法线不得放大推出距离");
        }

        // ------------------------------------------------------------------ 参数校验

        [Test]
        public void Constructors_RejectNonFiniteOrDegenerateGeometry()
        {
            // Given/When/Then：非法几何必须在构造时就炸，而不是等到 Step 里污染成 NaN
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new SphereCollisionProxy(Vector3.zero, -1f), "负半径球必须拒绝");
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new SphereCollisionProxy(new Vector3(float.NaN, 0f, 0f), 1f), "含 NaN 的球心必须拒绝");
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new BoxCollisionProxy(Vector3.zero, new Vector3(1f, 0f, 1f), Quaternion.identity), "半尺寸含 0 的盒子必须拒绝");
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new CapsuleCollisionProxy(Vector3.zero, Vector3.zero, 0f), "半径 0 的胶囊必须拒绝");
            Assert.Throws<ArgumentException>(
                () => new PlaneCollisionProxy(Vector3.zero, Vector3.zero), "零法线平面方向未定义，必须拒绝");
        }

        static void AssertThatFromAxis(Vector3 point, Vector3 axisPoint, float expected, string scene)
        {
            float distance = (point - axisPoint).magnitude;
            Assert.That(distance, Is.EqualTo(expected).Within(1e-5f), scene + "应等于 半径+皮肤，实际 " + distance);
        }

        // ------------------------------------------------------------------ 集合

        [Test]
        public void CollisionSet_KeepsInsertionOrderAndCanBeCleared()
        {
            // Given：往里塞三个不同空间的代理
            var set = new CollisionSet();
            var sphere = new SphereCollisionProxy(Vector3.zero, 1f);
            var box = new BoxCollisionProxy(Vector3.zero, Vector3.one, Quaternion.identity);
            var plane = new PlaneCollisionProxy(Vector3.zero, Vector3.up);
            set.Add(sphere, CollisionProxySpace.Simulation);
            set.Add(box, CollisionProxySpace.World);
            set.Add(plane, CollisionProxySpace.World);

            // When / Then：按插入顺序可读、可数、可清空——迭代顺序就是确定性的一部分
            Assert.That(set.Count, Is.EqualTo(3), "应当记住三个代理");
            Assert.That(set[0].Proxy, Is.SameAs(sphere), "第 0 个必须是第一个加进来的球");
            Assert.That(set[0].Space, Is.EqualTo(CollisionProxySpace.Simulation), "空间标记要按登记时的值读回来");
            Assert.That(set[1].Space, Is.EqualTo(CollisionProxySpace.World), "第二个应当是世界空间");
            set.Clear();
            Assert.That(set.Count, Is.EqualTo(0), "清空后应当没有代理");
        }

        [Test]
        public void CollisionSet_RejectsNullProxy()
        {
            // Given：Unity 里 MeshCollider 之类的"暂不支持"会给出 null
            var set = new CollisionSet();

            // When / Then：null 代理必须当场拒绝，不能等到求解时空引用
            Assert.Throws<ArgumentNullException>(() => set.Add(null, CollisionProxySpace.World),
                "空代理必须拒绝");
        }
    }
}
