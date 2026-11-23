using GameplayTags;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace ZFramework.GAS
{
    public enum GameplayCueEvent { Executed, OnActive, Removed }

    public readonly struct GameplayCueParameters
    {
        public readonly GameplayTag CueTag;
        public readonly GameplayCueEvent Event;
        public readonly object Target;
        public readonly object Source;
        public readonly int StackCount;

        public GameplayCueParameters(GameplayTag cueTag, GameplayCueEvent cueEvent, object target, object source, int stackCount)
        {
            CueTag = cueTag; Event = cueEvent; Target = target; Source = source; StackCount = stackCount;
        }
    }

    /// <summary>按标签层级与可选目标分桶索引的 Cue 路由器，O(depth) 零分配派发。</summary>
    public sealed class GameplayCueRouter
    {
        private struct Entry { public Action<GameplayCueParameters> Handler; public int Version; }

        private readonly struct Key : IEquatable<Key>
        {
            public readonly object Target;
            public readonly int TagIndex;
            public Key(object target, int tagIndex) { Target = target; TagIndex = tagIndex; }
            public bool Equals(Key other) => TagIndex == other.TagIndex && ReferenceEquals(Target, other.Target);
            public override bool Equals(object obj) => obj is Key other && Equals(other);
            public override int GetHashCode() => (RuntimeHelpers.GetHashCode(Target) * 397) ^ TagIndex;
        }

        private readonly Dictionary<Key, List<Entry>> _buckets = new();
        private List<Key> _emptyKeys;
        private int _handlerCount, _globalCount, _dispatchDepth, _sequence;
        private bool _needsCompaction;

        public int HandlerCount => _handlerCount;

        public void Register(GameplayTag cueTag, Action<GameplayCueParameters> handler)
            => Add(cueTag, null, handler);

        public void Register(GameplayTag cueTag, object target, Action<GameplayCueParameters> handler)
            => Add(cueTag, target ?? throw new ArgumentNullException(nameof(target)), handler);

        public bool Unregister(GameplayTag cueTag, Action<GameplayCueParameters> handler)
            => Remove(cueTag, null, handler);

        public bool Unregister(GameplayTag cueTag, object target, Action<GameplayCueParameters> handler)
            => target != null && Remove(cueTag, target, handler);

        public int Dispatch(in GameplayCueParameters parameters)
        {
            GasDebugHooks.NotifyCue(parameters);
            if (_handlerCount == 0 || !parameters.CueTag.IsValid) return 0;

            int dispatched = 0, snapshot = ++_sequence;
            bool hasGlobal = _globalCount > 0, hasTargeted = parameters.Target != null && _handlerCount > _globalCount;
            _dispatchDepth++;
            try
            {
                ReadOnlySpan<GameplayTag> hierarchy = parameters.CueTag.HierarchyTags;
                for (int h = 0; h < hierarchy.Length; h++)
                {
                    int tagIndex = hierarchy[h].RuntimeIndex;
                    if (hasGlobal && _buckets.TryGetValue(new Key(null, tagIndex), out var global))
                        dispatched += DispatchBucket(global, in parameters, snapshot);
                    if (hasTargeted && _buckets.TryGetValue(new Key(parameters.Target, tagIndex), out var targeted))
                        dispatched += DispatchBucket(targeted, in parameters, snapshot);
                }
            }
            finally
            {
                if (--_dispatchDepth == 0 && _needsCompaction) Compact();
            }
            return dispatched;
        }

        public void Clear()
        {
            _handlerCount = _globalCount = 0;
            if (_dispatchDepth == 0) { _buckets.Clear(); _needsCompaction = false; return; }
            foreach (var list in _buckets.Values)
                for (int i = 0; i < list.Count; i++) list[i] = default;
            _needsCompaction = true;
        }

        private void Add(GameplayTag cueTag, object target, Action<GameplayCueParameters> handler)
        {
            if (!cueTag.IsValid) throw new ArgumentException("Cue tag is not valid.", nameof(cueTag));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            var key = new Key(target, cueTag.RuntimeIndex);
            if (!_buckets.TryGetValue(key, out var list)) _buckets.Add(key, list = new List<Entry>(1));
            list.Add(new Entry { Handler = handler, Version = _sequence });
            _handlerCount++;
            if (target == null) _globalCount++;
        }

        private bool Remove(GameplayTag cueTag, object target, Action<GameplayCueParameters> handler)
        {
            if (!cueTag.IsValid || handler == null) return false;
            var key = new Key(target, cueTag.RuntimeIndex);
            if (!_buckets.TryGetValue(key, out var list)) return false;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Handler != handler) continue;
                _handlerCount--;
                if (target == null) _globalCount--;
                if (_dispatchDepth > 0) { list[i] = default; _needsCompaction = true; }
                else { list.RemoveAt(i); if (list.Count == 0) _buckets.Remove(key); }
                return true;
            }
            return false;
        }

        private static int DispatchBucket(List<Entry> list, in GameplayCueParameters parameters, int snapshot)
        {
            int dispatched = 0, count = list.Count;
            for (int i = 0; i < count && i < list.Count; i++)
            {
                Entry e = list[i];
                if (e.Handler == null || e.Version >= snapshot) continue;
                e.Handler.Invoke(parameters);
                dispatched++;
            }
            return dispatched;
        }

        private void Compact()
        {
            _needsCompaction = false;
            _emptyKeys?.Clear();
            foreach (var pair in _buckets)
            {
                List<Entry> list = pair.Value;
                for (int i = list.Count - 1; i >= 0; i--)
                    if (list[i].Handler == null) list.RemoveAt(i);
                if (list.Count == 0) (_emptyKeys ??= new List<Key>()).Add(pair.Key);
            }
            if (_emptyKeys == null) return;
            for (int i = 0; i < _emptyKeys.Count; i++) _buckets.Remove(_emptyKeys[i]);
            _emptyKeys.Clear();
        }
    }
}
