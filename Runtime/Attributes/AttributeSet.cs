using System;
using System.Collections.Generic;

namespace ZFramework.GAS
{
    /// <summary>
    /// 一个实体持有的固定属性表。初始化时添加，ASC.Clear 保留这些属性供复用。
    /// 属性对象在实体创建时分配，运行期的读取、修改与聚合均无分配。
    /// </summary>
    public sealed class AttributeSet
    {
        private readonly Dictionary<AttributeId, GameplayAttribute> _attributes;
        private readonly Action<GameplayAttribute, float, float> _forwardChanged;

        /// <summary>集合内任一属性的 CurrentValue 变化时触发。</summary>
        public event Action<GameplayAttribute, float, float> OnAttributeChanged;

        public AttributeSet(int capacity = 0)
        {
            _attributes = new Dictionary<AttributeId, GameplayAttribute>(capacity);
            // 缓存转发委托：否则每次 Add 订阅都会新建一个委托对象。
            _forwardChanged = (attribute, previous, current) => { OnAttributeChanged?.Invoke(attribute, previous, current); };
        }

        public int Count => _attributes.Count;
        internal Dictionary<AttributeId, float> CaptureValues()
        {
            var values = new Dictionary<AttributeId, float>(_attributes.Count);
            foreach (var pair in _attributes) values.Add(pair.Key, pair.Value.CurrentValue);
            return values;
        }

        /// <summary>清空并填充调用方列表。列表容量足够时不分配；返回的是属性对象的引用。</summary>
        public void CopyTo(List<GameplayAttribute> results)
        {
            results.Clear();
            foreach (var pair in _attributes) results.Add(pair.Value);
        }

        /// <summary>
        /// 新增属性。重复添加视为配置错误直接抛出——静默返回旧值会让重复初始化难以定位。
        /// 需要「有则改、无则建」的调用方请显式使用 <see cref="TryGet"/>。
        /// </summary>
        public GameplayAttribute Add(AttributeId id, float baseValue = 0f)
        {
            var attribute = new GameplayAttribute(id, baseValue);
            attribute.OnCurrentValueChanged += _forwardChanged;
            _attributes.Add(id, attribute);
            return attribute;
        }

        public bool TryGet(AttributeId id, out GameplayAttribute attribute) => _attributes.TryGetValue(id, out attribute);

        /// <summary>取属性；不存在返回 null。需要订阅变化事件时用这个。</summary>
        public GameplayAttribute Get(AttributeId id) => _attributes.TryGetValue(id, out GameplayAttribute attribute) ? attribute : null;

        /// <summary>读取当前值。属性不存在时返回 <paramref name="fallback"/>，该默认值在调用处可见。</summary>
        public float GetCurrent(AttributeId id, float fallback = 0f)
            => _attributes.TryGetValue(id, out GameplayAttribute attribute) ? attribute.CurrentValue : fallback;

    }
}
