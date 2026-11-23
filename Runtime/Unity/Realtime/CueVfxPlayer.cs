using System;
using System.Collections.Generic;
using GameplayTags;
using UnityEngine;

namespace ZFramework.GAS.Unity
{
    [DisallowMultipleComponent, AddComponentMenu("ZFramework GAS/Cue VFX Player")]
    public sealed class CueVfxPlayer : MonoBehaviour
    {
        [SerializeField, InspectorName("特效映射库")] private CueVfxLibrary library;
        public CueVfxLibrary Library => library;

        private AbilitySystemComponent _owner;
        private readonly List<GameplayTag> _registeredTags = new();
        private readonly List<Entry> _entries = new();
        private readonly List<CueVfxInstance> _tails = new();
        private Action<GameplayCueParameters> _onCue;

        private sealed class Entry
        {
            public GameplayTag Tag;
            public CueVfxBinding Binding;
            public int Count;
            public CueVfxInstance Instance;
        }

        public void SetLibrary(CueVfxLibrary value) { Unsubscribe(); library = value; if (isActiveAndEnabled) Subscribe(); }
        public void Bind(AbilitySystemComponent owner) { Unsubscribe(); _owner = owner; if (isActiveAndEnabled) Subscribe(); }
        private void OnEnable() => Subscribe();
        private void OnDisable() => Unsubscribe();
        private void OnDestroy() => Unsubscribe();

        private void Subscribe()
        {
            if (_owner?.CueRouter == null || library?.bindings == null || _entries.Count > 0) return;
            _onCue ??= OnCue;
            for (int i = 0; i < library.bindings.Length; i++)
            {
                CueVfxBinding binding = library.bindings[i];
                if (binding == null) continue;
                GameplayTag tag = binding.cueTag.Resolve();
                if (!tag.IsValid || binding.prefab == null || binding.FindAnchor(transform) == null)
                {
                    Debug.LogWarning($"Cue VFX 映射无效：{binding.cueTag.Name}，请检查标签、Prefab 与挂点路径。", this);
                    continue;
                }
                _entries.Add(new Entry { Tag = tag, Binding = binding });
                if (_registeredTags.Contains(tag)) continue;
                _registeredTags.Add(tag);
                _owner.CueRouter.Register(tag, _owner, _onCue);
            }
        }

        private void OnCue(GameplayCueParameters cue)
        {
            if (!ReferenceEquals(cue.Target, _owner)) return;
            for (int i = 0; i < _entries.Count; i++)
            {
                Entry e = _entries[i];
                if (e.Tag != cue.CueTag) continue;
                if (cue.Event == GameplayCueEvent.OnActive)
                {
                    if (e.Count++ > 0) continue;
                    try { e.Instance = new CueVfxInstance(e.Binding, transform, false); }
                    catch (Exception ex) { e.Count = 0; Debug.LogException(ex, this); }
                }
                else if (cue.Event == GameplayCueEvent.Removed && e.Count > 0 && --e.Count == 0)
                {
                    e.Instance?.Stop();
                    if (e.Instance?.Root != null) _tails.Add(e.Instance);
                    e.Instance = null;
                }
                else if (cue.Event == GameplayCueEvent.Executed)
                {
                    try
                    {
                        var burst = new CueVfxInstance(e.Binding, transform, false);
                        burst.MarkOneShot();
                        if (burst.Root != null) _tails.Add(burst);
                    }
                    catch (Exception ex) { Debug.LogException(ex, this); }
                }
            }
        }

        private void LateUpdate()
        {
            float dt = Time.deltaTime;
            for (int i = 0; i < _entries.Count; i++) _entries[i].Instance?.Tick(dt);
            for (int i = _tails.Count - 1; i >= 0; i--)
                if (!_tails[i].Tick(dt)) _tails.RemoveAt(i);
        }

        private void Unsubscribe()
        {
            GameplayCueRouter router = _owner?.CueRouter;
            if (router != null && _onCue != null)
                for (int i = 0; i < _registeredTags.Count; i++) router.Unregister(_registeredTags[i], _owner, _onCue);
            _registeredTags.Clear();
            for (int i = 0; i < _entries.Count; i++) _entries[i].Instance?.Dispose();
            _entries.Clear();
            for (int i = 0; i < _tails.Count; i++) _tails[i].Dispose();
            _tails.Clear();
        }
    }
}
