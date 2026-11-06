// Copyright (c) 2026 PhysicsSimulation. MIT License.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace PhysicsSimulation
{
    /// <summary>
    /// 显式互碰组：一个场景级的注册表，把若干 <see cref="IParticleBody"/> 接成"同组才互碰"。
    ///
    /// 为什么是显式组件而不是"场景里所有体自动互碰"：
    ///  * 自动 = 隐式全局状态，会改变已发布版本的默认行为，而且测试要额外想办法关掉它；
    ///  * 显式 = 不注册就零开销，逐位保持 v1.5.0 的行为；
    ///  * 而且组在"加入/退出"这一个时机里一次性把代理挂到双方的碰撞集合上，
    ///    N 个体每人每步只重建自己那一次索引（O(N)），不是每对重建（O(N²)）。
    ///
    /// 求解方式沿用现有代理契约：每个体各自把自己投影出别人的球云，顺序求解、动量不守恒。
    /// 所以两个体互相挤的时候，谁退让得多取决于步进顺序，这是本版**已知**的近似，
    /// 不是 bug；要动量正确得改成按逆质量分配修正量，那会破掉 <c>ICollisionProxy.PushOut(point, skin)</c>
    /// 的签名，留到后续版本再议。
    /// </summary>
    [AddComponentMenu("Physics Simulation/Particle Interaction Group")]
    [DisallowMultipleComponent]
    public sealed class ParticleInteractionGroup : MonoBehaviour
    {

        readonly List<IParticleBody> _members = new List<IParticleBody>();
        readonly List<ParticleCloudProxy> _proxies = new List<ParticleCloudProxy>();

        /// <summary>组里的体数。注意：只有 1 个成员时它拿不到任何代理（没有别人可碰，也不碰自己）。</summary>
        public int MemberCount { get { return _members.Count; } }

        /// <summary>已经建好的球云代理数（与成员数相同，代理是每体一份、共享给别人的）。</summary>
        public int ProxyCount { get { return _proxies.Count; } }

        /// <summary>
        /// 当前成员（只读视图，登记顺序就是加入顺序）。
        /// 给演示工具与测试用：没有它，外部就只能靠 FindObjectsOfType 猜谁是本组的体。
        /// </summary>
        public System.Collections.Generic.IReadOnlyList<IParticleBody> Members
        {
            get { return _members; }
        }

        public bool Contains(IParticleBody body)
        {
            return body != null && _members.IndexOf(body) >= 0;
        }

        /// <summary>取某个体的球云代理；不在组里返回 null。</summary>
        public ParticleCloudProxy ProxyOf(IParticleBody body)
        {
            int index = body == null ? -1 : _members.IndexOf(body);
            return index < 0 ? null : _proxies[index];
        }

        /// <summary>
        /// 加入一个体。幂等：重复加入直接忽略，绝不出现"同一朵云被挂两次"（那等于把它推两倍）。
        /// </summary>
        public void Add(IParticleBody body)
        {
            if (body == null)
            {
                throw new ArgumentNullException("body", "互碰组成员不允许为 null：宁可直接炸，也不要静默少一个互碰对象");
            }
            if (body.ParticleCollisions == null)
            {
                throw new ArgumentException("成员的碰撞集合为 null，无法挂别人的球云代理", "body");
            }
            if (_members.IndexOf(body) >= 0) return;

            _members.Add(body);
            _proxies.Add(new ParticleCloudProxy(body));
            body.CloudGroup = this;         // 回写：体自己必须知道在哪个组里，否则重建与变换注入都会漏

            int mine = _members.Count - 1;
            for (int i = 0; i < mine; i++)
            {
                // 双向挂：新成员看见所有旧云，每个旧成员也看见新云。挂别人的时候用 World 空间，
                // 因为球云存的就是世界坐标，而各体的质点在自己的局部空间里。
                _members[mine].ParticleCollisions.Add(_proxies[i], CollisionProxySpace.World, this);
                _members[i].ParticleCollisions.Add(_proxies[mine], CollisionProxySpace.World, this);
            }
        }

        /// <summary>退出一个体，同时把它在所有人身上挂的代理摘干净。不在组里返回 false。</summary>
        public bool Remove(IParticleBody body)
        {
            int index = body == null ? -1 : _members.IndexOf(body);
            if (index < 0) return false;

            ParticleCloudProxy mine = _proxies[index];
            _members.RemoveAt(index);
            _proxies.RemoveAt(index);
            if (ReferenceEquals(body.CloudGroup, this)) body.CloudGroup = null;

            for (int i = 0; i < _members.Count; i++)
            {
                if (_members[i].ParticleCollisions != null) _members[i].ParticleCollisions.Remove(mine);
            }
            if (body.ParticleCollisions != null)
            {
                for (int i = 0; i < _proxies.Count; i++) body.ParticleCollisions.Remove(_proxies[i]);
            }
            return true;
        }

        /// <summary>
        /// 体重建之后重新接线。<b>必须存在</b>：软体/布料/质点弹簧的 <c>Rebuild()</c> 会换掉整个碰撞集合，
        /// 手工挂进去的代理会跟着一起没了 —— 流体那边的容器代理已经踩过一次（编辑器里看着正常，
        /// 一进 Play 代理数变 0，一箱水直落到 y = -92）。
        /// </summary>
        public bool Reattach(IParticleBody body)
        {
            if (!Contains(body)) return false;
            Remove(body);
            Add(body);
            return true;
        }

        /// <summary>强制刷新所有代理（诊断/工具用）。返回真的重建了几个。</summary>
        public int RefreshAll()
        {
            int rebuilt = 0;
            for (int i = 0; i < _proxies.Count; i++) rebuilt += _proxies[i].Refresh();
            return rebuilt;
        }

        void OnDisable()
        {
            // 组件被停用时把线拆干净：留着的话别人下次步进会拿到一个不再更新的旧云，
            // 那种"半个体的碰撞"比完全不碰更难查。
            for (int i = _members.Count - 1; i >= 0; i--) Remove(_members[i]);
        }
    }
}
