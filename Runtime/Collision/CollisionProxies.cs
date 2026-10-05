// Copyright (c) 2026 PhysicsSimulation. MIT License.
using System;
using UnityEngine;

namespace PhysicsSimulation
{
    /// <summary>
    /// 碰撞代理所在的空间。
    ///
    /// <see cref="Simulation"/>：与求解器的质点坐标同一个空间。布料/软体的质点是组件**局部**坐标，
    /// 所以这里的代理也必须用局部坐标表达（v1.1.0/v1.2.0 的球障碍就是这种）。
    /// <see cref="World"/>：世界坐标。求解时会先把质点变换到世界、推出、再变换回来，
    /// 于是场景里的 Collider 可以直接用，不需要把球半径按 sqrt(3) 那种缩放近似折算进局部空间。
    /// </summary>
    public enum CollisionProxySpace
    {
        Simulation = 0,
        World = 1,
    }

    /// <summary>
    /// 单个碰撞几何的最小接口：把点推到体外至少 <paramref name="skin"/> 远。
    ///
    /// 契约（三个求解器都依赖它，改语义请先改测试）：
    /// 1. 点已经在体外（含正好等于 skin 距离）→ **原样返回**，逐位不变，绝不"顺手拉近"。
    /// 2. 点在体内 → 顶到体外；方向唯一确定，不看速度、不看历史，所以同一个输入永远给同一个输出（确定性）。
    /// 3. 方向未定义（点正好落在球心/轴线上/表面上）→ 用**固定**的备用轴，绝不返回 NaN。
    /// 4. skin 为负按 0 处理：非法值不允许把点往几何体里面塞。
    /// </summary>
    public interface ICollisionProxy
    {
        /// <summary>推出后的位置。skin 是该几何额外的"皮肤厚度"（米，模拟空间或世界空间按登记空间而定）。</summary>
        Vector3 PushOut(Vector3 point, float skin);
    }

    static class CollisionMath
    {
        /// <summary>方向未定义时的备用法线：与 v1.1.0 布料球障碍的约定一致，一律 +Y，保证可复现。</summary>
        public static readonly Vector3 FallbackNormal = new Vector3(0f, 1f, 0f);

        public const float DegenerateSqr = 1e-16f;    // 与旧布料算术同一个阈值，别改，改了逐位回归就红

        public static float SafeSkin(float skin)
        {
            return skin > 0f ? skin : 0f;
        }

        public static void RequireFinite(Vector3 v, string name)
        {
            if (float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z)
                || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z))
            {
                throw new ArgumentOutOfRangeException(name, name + " 必须是有限向量，当前 " + v);
            }
        }

        public static void RequireFinite(float value, string name)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(name, name + " 必须是有限数，当前 " + value);
            }
        }
    }

    /// <summary>球。算术与 v1.1.0 布料的球障碍逐字保持一致，所以那条旧路径可以逐位回归。</summary>
    public sealed class SphereCollisionProxy : ICollisionProxy
    {
        public Vector3 Center { get; private set; }
        public float Radius { get; private set; }

        public SphereCollisionProxy(Vector3 center, float radius)
        {
            CollisionMath.RequireFinite(center, "center");
            CollisionMath.RequireFinite(radius, "radius");
            if (!(radius > 0f))
            {
                throw new ArgumentOutOfRangeException("radius", "球半径必须是正有限值（米），当前 " + radius);
            }
            Center = center;
            Radius = radius;
        }

        public Vector3 PushOut(Vector3 point, float skin)
        {
            float surface = Radius + CollisionMath.SafeSkin(skin);
            Vector3 radial = point - Center;
            float distanceSqr = radial.sqrMagnitude;
            if (distanceSqr >= surface * surface) return point;         // 体外：逐位不动

            if (distanceSqr <= CollisionMath.DegenerateSqr)
            {
                // 与球心重合：径向方向未定义，给一个固定方向顶出去，绝不产生 NaN
                return Center + CollisionMath.FallbackNormal * surface;
            }

            float distance = (float)Math.Sqrt(distanceSqr);
            return Center + radial * (surface / distance);
        }
    }

    /// <summary>方向包围盒（OBB）。体内点沿"穿透最浅"的那个面脱出，外面点沿最近表面点脱出。</summary>
    public sealed class BoxCollisionProxy : ICollisionProxy
    {
        public Vector3 Center { get; private set; }
        public Vector3 HalfExtents { get; private set; }
        public Quaternion Rotation { get; private set; }

        public BoxCollisionProxy(Vector3 center, Vector3 halfExtents, Quaternion rotation)
        {
            CollisionMath.RequireFinite(center, "center");
            CollisionMath.RequireFinite(halfExtents, "halfExtents");
            if (!(halfExtents.x > 0f) || !(halfExtents.y > 0f) || !(halfExtents.z > 0f))
            {
                throw new ArgumentOutOfRangeException("halfExtents",
                    "盒子半尺寸三个分量都必须是正有限值（米），当前 " + halfExtents);
            }
            if (float.IsNaN(rotation.x) || float.IsNaN(rotation.y) || float.IsNaN(rotation.z) || float.IsNaN(rotation.w)
                || float.IsInfinity(rotation.x) || float.IsInfinity(rotation.y)
                || float.IsInfinity(rotation.z) || float.IsInfinity(rotation.w))
            {
                throw new ArgumentOutOfRangeException("rotation", "旋转不允许 NaN / Infinity");
            }
            Center = center;
            HalfExtents = halfExtents;
            Rotation = rotation;
        }

        public Vector3 PushOut(Vector3 point, float skin)
        {
            float thickness = CollisionMath.SafeSkin(skin);
            Quaternion inverse = Quaternion.Inverse(Rotation);
            Vector3 local = inverse * (point - Center);         // 进盒子自己的坐标系

            float hx = HalfExtents.x, hy = HalfExtents.y, hz = HalfExtents.z;
            float ax = Mathf.Abs(local.x), ay = Mathf.Abs(local.y), az = Mathf.Abs(local.z);

            if (ax <= hx && ay <= hy && az <= hz)
            {
                // 在体内：沿离表面最近（穿透最浅）的那个轴脱出。薄板落地时这样才不会被"甩"到侧面。
                float dx = hx - ax, dy = hy - ay, dz = hz - az;
                if (dx <= dy && dx <= dz) local.x = (local.x >= 0f ? 1f : -1f) * (hx + thickness);
                else if (dy <= dz) local.y = (local.y >= 0f ? 1f : -1f) * (hy + thickness);
                else local.z = (local.z >= 0f ? 1f : -1f) * (hz + thickness);
                return Center + Rotation * local;
            }

            Vector3 clamped = new Vector3(
                Mathf.Clamp(local.x, -hx, hx),
                Mathf.Clamp(local.y, -hy, hy),
                Mathf.Clamp(local.z, -hz, hz));

            Vector3 delta = local - clamped;                    // 指向"离开最近表面点"的方向
            float distanceSqr = delta.sqrMagnitude;
            if (distanceSqr >= thickness * thickness) return point;     // 已在皮肤之外：逐位不动

            if (distanceSqr <= CollisionMath.DegenerateSqr)
            {
                // 正好贴在某个表面上：用那个面的法线，别用 0/0
                float ex = ax - hx, ey = ay - hy, ez = az - hz;
                if (ex >= ey && ex >= ez) local.x = (local.x >= 0f ? 1f : -1f) * (hx + thickness);
                else if (ey >= ez) local.y = (local.y >= 0f ? 1f : -1f) * (hy + thickness);
                else local.z = (local.z >= 0f ? 1f : -1f) * (hz + thickness);
                return Center + Rotation * local;
            }

            float distance = (float)Math.Sqrt(distanceSqr);
            local = clamped + delta * (thickness / distance);
            return Center + Rotation * local;
        }
    }

    /// <summary>胶囊：线段 + 半径。柱身沿径向脱出，端帽按球处理；轴段退化时整体退化成球。</summary>
    public sealed class CapsuleCollisionProxy : ICollisionProxy
    {
        public Vector3 SegmentA { get; private set; }
        public Vector3 SegmentB { get; private set; }
        public float Radius { get; private set; }

        public CapsuleCollisionProxy(Vector3 segmentA, Vector3 segmentB, float radius)
        {
            CollisionMath.RequireFinite(segmentA, "segmentA");
            CollisionMath.RequireFinite(segmentB, "segmentB");
            CollisionMath.RequireFinite(radius, "radius");
            if (!(radius > 0f))
            {
                throw new ArgumentOutOfRangeException("radius", "胶囊半径必须是正有限值（米），当前 " + radius);
            }
            SegmentA = segmentA;
            SegmentB = segmentB;
            Radius = radius;
        }

        public Vector3 PushOut(Vector3 point, float skin)
        {
            float surface = Radius + CollisionMath.SafeSkin(skin);
            Vector3 axis = SegmentB - SegmentA;
            Vector3 toPoint = point - SegmentA;
            float axisSqr = axis.sqrMagnitude;

            float t;
            if (axisSqr <= CollisionMath.DegenerateSqr) t = 0f;                  // 零长轴段：退化成球
            else
            {
                t = Vector3.Dot(toPoint, axis) / axisSqr;
                if (t < 0f) t = 0f;
                else if (t > 1f) t = 1f;
            }

            Vector3 closest = SegmentA + axis * t;
            Vector3 radial = point - closest;
            float distanceSqr = radial.sqrMagnitude;
            if (distanceSqr >= surface * surface) return point;                  // 体外：逐位不动

            if (distanceSqr <= CollisionMath.DegenerateSqr)
            {
                // 点落在轴线/球心上：方向未定义，用固定轴，绝不 NaN
                return closest + CollisionMath.FallbackNormal * surface;
            }

            float distance = (float)Math.Sqrt(distanceSqr);
            return closest + radial * (surface / distance);
        }
    }

    /// <summary>
    /// 平面＝半空间：法线指向"可以待着"的那一侧。
    /// 与球/盒不同，它没有"体内多深"的概念——穿到地面以下 3 米也一律顶回面上 + 皮肤。
    /// 这是软体能稳稳落在地上、而不是被地面吞掉的关键。
    /// </summary>
    public sealed class PlaneCollisionProxy : ICollisionProxy
    {
        public Vector3 Point { get; private set; }
        public Vector3 Normal { get; private set; }

        public PlaneCollisionProxy(Vector3 pointOnPlane, Vector3 normal)
        {
            CollisionMath.RequireFinite(pointOnPlane, "pointOnPlane");
            CollisionMath.RequireFinite(normal, "normal");
            if (normal.sqrMagnitude <= CollisionMath.DegenerateSqr)
            {
                throw new ArgumentException("平面法线长度不能为 0：半空间的方向未定义", "normal");
            }
            Point = pointOnPlane;
            Normal = normal.normalized;                    // 归一化在构造期做完，非单位法线不许放大推出距离
        }

        public Vector3 PushOut(Vector3 point, float skin)
        {
            float thickness = CollisionMath.SafeSkin(skin);
            float signedDistance = Vector3.Dot(point - Point, Normal);
            if (signedDistance >= thickness) return point;                 // 在可待着的一侧且够远：逐位不动
            return point + Normal * (thickness - signedDistance);
        }
    }
}
