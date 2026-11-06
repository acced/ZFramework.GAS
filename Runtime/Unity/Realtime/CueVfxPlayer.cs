using System;
using System.Collections.Generic;
using GameplayTags;
using UnityEngine;

namespace ZFramework.GAS.Unity
{
    /// <summary>宿主初始化 ASC 后调用 Bind；之后自动监听 Cue 并在 LateUpdate 更新表现。</summary>
    [DisallowMultipleComponent, AddComponentMenu("ZFramework GAS/Cue VFX Player")]
    public sealed class CueVfxPlayer : MonoBehaviour
    {
        [SerializeField, InspectorName("特效映射库")] private CueVfxLibrary library;
        public CueVfxLibrary Library => library;
        private AbilitySystemComponent _owner;
        private readonly Dictionary<GameplayTag, Action<GameplayCueParameters>> _registered = new();
        private readonly List<Entry> _entries = new();
        private readonly List<CueVfxInstance> _tails = new();
        private sealed class Entry
        {
            public CueVfxBinding Binding;
            public int Count;
            public CueVfxInstance Instance;
        }

        public void SetLibrary(CueVfxLibrary value)
        { Unsubscribe(); library = value; if (isActiveAndEnabled) Subscribe(); }

        public void Bind(AbilitySystemComponent owner)
        { Unsubscribe(); _owner = owner; if (isActiveAndEnabled) Subscribe(); }

        private void OnEnable() => Subscribe();
        private void OnDisable() => Unsubscribe();
        private void OnDestroy() => Unsubscribe();

        private void Subscribe()
        {
            if (_owner?.CueRouter == null || library == null || _entries.Count > 0) return;
            foreach (var binding in library.bindings)
            {
                if (binding == null) continue;
                var tag = binding.cueTag.Resolve();
                if (!tag.IsValid || binding.prefab == null || binding.FindAnchor(transform) == null)
                { Debug.LogWarning($"Cue VFX 映射无效：{binding.cueTag.Name}，请检查标签、Prefab 与挂点路径。", this); continue; }
                _entries.Add(new Entry { Binding = binding });
                if (_registered.ContainsKey(tag)) continue;
                Action<GameplayCueParameters> handler = cue => { if (cue.CueTag == tag) OnCue(cue); };
                _registered.Add(tag, handler); _owner.CueRouter.Register(tag, handler);
            }
        }

        private void OnCue(GameplayCueParameters cue)
        {
            if (!ReferenceEquals(cue.Target, _owner)) return;
            foreach (var entry in _entries)
            {
                // 映射精确匹配；路由的父标签匹配不应额外生成另一份特效。
                if (entry.Binding.cueTag.Name != cue.CueTag.Name) continue;
                if (cue.Event == GameplayCueEvent.OnActive)
                {
                    if (entry.Count++ > 0) continue;
                    try { entry.Instance = new CueVfxInstance(entry.Binding, transform, false); }
                    catch (Exception ex) { entry.Count = 0; Debug.LogException(ex, this); }
                }
                else if (cue.Event == GameplayCueEvent.Removed && entry.Count > 0 && --entry.Count == 0)
                {
                    entry.Instance?.Stop();
                    if (entry.Instance?.Root != null) _tails.Add(entry.Instance);
                    entry.Instance = null;
                }
            }
        }

        private void LateUpdate()
        {
            foreach (var entry in _entries) entry.Instance?.Tick(Time.deltaTime);
            for (int i = _tails.Count - 1; i >= 0; i--) if (!_tails[i].Tick(Time.deltaTime)) _tails.RemoveAt(i);
        }

        private void Unsubscribe()
        {
            foreach (var pair in _registered) _owner?.CueRouter?.Unregister(pair.Key, pair.Value);
            _registered.Clear();
            foreach (var entry in _entries) entry.Instance?.Dispose();
            _entries.Clear();
            foreach (var tail in _tails) tail.Dispose();
            _tails.Clear();
        }
    }
}
