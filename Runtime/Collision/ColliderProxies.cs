// Copyright (c) 2026 PhysicsSimulation. MIT License.
using System.Collections.Generic;
using UnityEngine;

namespace PhysicsSimulation
{
    /// <summary>
    /// 把场景里的 Collider 翻译成碰撞代理（本版按决定：**只做 primitive 解析**）。
    ///
    /// 为什么不直接调 <c>Physics.ComputePenetration</c>：那会让求解器依赖物理场景与 Unity 物理版本，
    /// 纯逻辑层的 EditMode 测试就没法再用闭式解断言，"同参数跑两次逐位一致"这条承诺也就没了。
    /// 所以这里只做一次几何读取 + 解析式最近点，求解器内部依然与 PhysicsScene 无关。
    ///
    /// 不支持的类型（MeshCollider、Terrain、非凸的复合体）返回 <c>null</c>：
    /// 宁可"这个碰撞体没生效"，也不要拿包围盒冒充一个网格墙。
    /// </summary>
    public static class ColliderProxies
    {
        /// <summary>把 Collider 转成世界空间代理；不支持或已禁用则返回 null。</summary>
        public static ICollisionProxy TryFrom(Collider collider)
        {
            if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy) return null;

            var sphere = collider as SphereCollider;
            if (sphere != null) return FromSphere(sphere);

            var box = collider as BoxCollider;
            if (box != null) return FromBox(box);

            var capsule = collider as CapsuleCollider;
            if (capsule != null) return FromCapsule(capsule);

            return null;
        }

        /// <summary>
        /// 重读一份 Collider 列表，替换掉碰撞集合里所有**世界空间**的条目。
        /// 模拟空间的条目（脚本手工加的、布料旧的 <c>AddSphereObstacle</c>）不动——它们不是从场景读来的。
        /// </summary>
        public static int RefreshInto(IList<Collider> colliders, CollisionSet into)
        {
            if (into == null) return 0;
            into.ClearWorldSpace();
            if (colliders == null) return 0;

            int added = 0;
            for (int i = 0; i < colliders.Count; i++)
            {
                ICollisionProxy proxy = TryFrom(colliders[i]);
                if (proxy == null) continue;                 // 不支持 / 禁用 / 毁掉的：跳过，不塞假代理
                into.Add(proxy, CollisionProxySpace.World);
                added++;
            }
            return added;
        }

        static SphereCollisionProxy FromSphere(SphereCollider sphere)
        {
            Transform t = sphere.transform;
            Vector3 center = t.TransformPoint(sphere.center);
            float radius = sphere.radius * MaxAxisScale(t);
            if (!(radius > 0f)) return null;                 // 缩放过小到退化：跳过而不是造一个零半径球
            return new SphereCollisionProxy(center, radius);
        }

        static BoxCollisionProxy FromBox(BoxCollider box)
        {
            Transform t = box.transform;
            Vector3 scale = t.lossyScale;
            Vector3 half = new Vector3(
                Mathf.Abs(box.size.x * 0.5f * scale.x),
                Mathf.Abs(box.size.y * 0.5f * scale.y),
                Mathf.Abs(box.size.z * 0.5f * scale.z));
            if (!(half.x > 0f) || !(half.y > 0f) || !(half.z > 0f)) return null;

            return new BoxCollisionProxy(t.TransformPoint(box.center), half, t.rotation);
        }

        static CapsuleCollisionProxy FromCapsule(CapsuleCollider capsule)
        {
            Transform t = capsule.transform;
            Vector3 scale = t.lossyScale;
            float radius = capsule.radius * MaxAxisScale(t);

            // 轴向按被拉长的方向取：Unity 的 direction 是本地轴（0=X 1=Y 2=Z）
            int axis = capsule.direction;
            Vector3 axisLocal = axis == 0 ? Vector3.right : (axis == 2 ? Vector3.forward : Vector3.up);
            float axisScale = Mathf.Abs(axis == 0 ? scale.x : (axis == 2 ? scale.z : scale.y));
            float diameter = capsule.radius * 2f * axisScale;
            float halfLength = Mathf.Max(0f, (Mathf.Abs(capsule.height) * axisScale - diameter) * 0.5f);

            if (!(radius > 0f)) return null;

            Vector3 center = t.TransformPoint(capsule.center);
            Vector3 worldAxis = t.TransformDirection(axisLocal).normalized;
            Vector3 a = center - worldAxis * halfLength;
            Vector3 b = center + worldAxis * halfLength;
            return new CapsuleCollisionProxy(a, b, radius);
        }

        /// <summary>
        /// 球只有一个半径，非均匀缩放时它在数学上是个椭球。这里取最大轴做外接球近似，
        /// 结果是"宁可稍微厚一点，也不让质点钻进看得见的外面"。文档里会写明这是近似。
        /// </summary>
        static float MaxAxisScale(Transform t)
        {
            Vector3 s = t.lossyScale;
            return Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));
        }
    }
}
