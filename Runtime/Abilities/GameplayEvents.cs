using System;
using System.Collections.Generic;
using GameplayTags;

namespace ZFramework.GAS
{
    /// <summary>Logic messages are separate from presentation Cues.</summary>
    public readonly struct GameplayEvent
    {
        public readonly GameplayTag Tag;
        public readonly object Source;
        public readonly SkillParameterValue Payload;
        public readonly EffectEvaluationContext Context;
        public readonly System.Numerics.Vector3? Position;

        public GameplayEvent(GameplayTag tag, object source, SkillParameterValue payload,
            EffectEvaluationContext context = null, System.Numerics.Vector3? position = null)
        {
            Tag = tag; Source = source; Payload = payload; Context = context; Position = position;
        }
    }

    /// <summary>单位级玩法事件通道：支持全量 <see cref="Received"/> 与层级化 O(depth) 订阅。</summary>
    public sealed class GameplayEventChannel
    {
        private struct Entry { public Action<GameplayEvent> Handler; public int Version; }

        private Dictionary<int, List<Entry>> _scoped;
        private List<int> _emptyKeys;
        private int _scopedCount, _dispatchDepth, _sequence;
        private bool _needsCompaction;

        public event Action<GameplayEvent> Received;

        public void Subscribe(GameplayTag tag, Action<GameplayEvent> handler)
        {
            if (!tag.IsValid) throw new ArgumentException("Event tag is not valid.", nameof(tag));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _scoped ??= new Dictionary<int, List<Entry>>();
            int key = tag.RuntimeIndex;
            if (!_scoped.TryGetValue(key, out var list)) _scoped.Add(key, list = new List<Entry>(2));
            list.Add(new Entry { Handler = handler, Version = _sequence });
            _scopedCount++;
        }

        public bool Unsubscribe(GameplayTag tag, Action<GameplayEvent> handler)
        {
            if (!tag.IsValid || handler == null || _scoped == null || !_scoped.TryGetValue(tag.RuntimeIndex, out var list))
                return false;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Handler != handler) continue;
                _scopedCount--;
                if (_dispatchDepth > 0) { list[i] = default; _needsCompaction = true; }
                else { list.RemoveAt(i); if (list.Count == 0) _scoped.Remove(tag.RuntimeIndex); }
                return true;
            }
            return false;
        }

        public void Send(in GameplayEvent message)
        {
            Received?.Invoke(message);
            if (_scopedCount == 0 || !message.Tag.IsValid) return;

            int snapshot = ++_sequence;
            _dispatchDepth++;
            try
            {
                ReadOnlySpan<GameplayTag> hierarchy = message.Tag.HierarchyTags;
                for (int h = 0; h < hierarchy.Length; h++)
                {
                    if (!_scoped.TryGetValue(hierarchy[h].RuntimeIndex, out var list)) continue;
                    int count = list.Count;
                    for (int i = 0; i < count && i < list.Count; i++)
                    {
                        Entry e = list[i];
                        if (e.Handler != null && e.Version < snapshot) e.Handler.Invoke(message);
                    }
                }
            }
            finally
            {
                if (--_dispatchDepth == 0 && _needsCompaction) Compact();
            }
        }

        internal void Clear()
        {
            Received = null;
            _scopedCount = 0;
            if (_scoped == null) return;
            if (_dispatchDepth == 0) { _scoped.Clear(); _needsCompaction = false; return; }
            foreach (var list in _scoped.Values)
                for (int i = 0; i < list.Count; i++) list[i] = default;
            _needsCompaction = true;
        }

        private void Compact()
        {
            _needsCompaction = false;
            _emptyKeys?.Clear();
            foreach (var pair in _scoped)
            {
                List<Entry> list = pair.Value;
                for (int i = list.Count - 1; i >= 0; i--)
                    if (list[i].Handler == null) list.RemoveAt(i);
                if (list.Count == 0) (_emptyKeys ??= new List<int>()).Add(pair.Key);
            }
            if (_emptyKeys == null) return;
            for (int i = 0; i < _emptyKeys.Count; i++) _scoped.Remove(_emptyKeys[i]);
            _emptyKeys.Clear();
        }
    }
}
