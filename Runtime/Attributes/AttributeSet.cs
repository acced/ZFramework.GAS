using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace ZFramework.GAS
{
    /// <summary>实体持有的固定属性表；常规 ID（0..127）直查数组 O(1)，ASC.Clear 保留结构供复用。</summary>
    public sealed class AttributeSet
    {
        private const int FastLookupLimit = 128;

        private readonly Dictionary<AttributeId, GameplayAttribute> _attributes;
        private readonly List<GameplayAttribute> _list;
        private readonly Action<GameplayAttribute, float, float> _forwardChanged;
        private GameplayAttribute[] _fast = Array.Empty<GameplayAttribute>();

        public event Action<GameplayAttribute, float, float> OnAttributeChanged;
        public int Count => _list.Count;

        public AttributeSet(int capacity = 0)
        {
            _attributes = new Dictionary<AttributeId, GameplayAttribute>(capacity);
            _list = new List<GameplayAttribute>(capacity);
            _forwardChanged = (a, prev, cur) => OnAttributeChanged?.Invoke(a, prev, cur);
        }

        internal Dictionary<AttributeId, float> CaptureValues()
        {
            var values = new Dictionary<AttributeId, float>(_list.Count);
            for (int i = 0; i < _list.Count; i++) values.Add(_list[i].Id, _list[i].CurrentValue);
            return values;
        }

        public void CopyTo(List<GameplayAttribute> results)
        {
            results.Clear();
            for (int i = 0; i < _list.Count; i++) results.Add(_list[i]);
        }

        public GameplayAttribute Add(AttributeId id, float baseValue = 0f)
        {
            var attribute = new GameplayAttribute(id, baseValue);
            _attributes.Add(id, attribute);
            attribute.OnCurrentValueChanged += _forwardChanged;
            _list.Add(attribute);
            uint index = (uint)id.Value;
            if (index < FastLookupLimit)
            {
                if (index >= (uint)_fast.Length) Array.Resize(ref _fast, (int)index + 1);
                _fast[index] = attribute;
            }
            return attribute;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGet(AttributeId id, out GameplayAttribute attribute)
        {
            uint index = (uint)id.Value;
            GameplayAttribute[] fast = _fast;
            if (index < (uint)fast.Length)
            {
                attribute = fast[index];
                return attribute != null;
            }
            return _attributes.TryGetValue(id, out attribute);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public GameplayAttribute Get(AttributeId id) => TryGet(id, out var a) ? a : null;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float GetCurrent(AttributeId id, float fallback = 0f) => TryGet(id, out var a) ? a.CurrentValue : fallback;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float GetBase(AttributeId id, float fallback = 0f) => TryGet(id, out var a) ? a.BaseValue : fallback;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool SetBase(AttributeId id, float value)
        {
            if (!TryGet(id, out var a)) return false;
            a.BaseValue = value;
            return true;
        }
    }
}
