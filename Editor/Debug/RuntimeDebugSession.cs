using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    [InitializeOnLoad]
    public static class RuntimeDebugSession
    {
        private static readonly List<MonoBehaviour> Units = new();
        private static readonly Dictionary<AbilitySystemComponent, string> Names = new();
        private static double _nextCapture, _nextScan;
        private static MonoBehaviour _observed;
        private static bool _followSelection = true;
        public static event Action<RuntimeSnapshot> Changed;
        public static RuntimeSnapshot Current { get; private set; }
        public static MonoBehaviour Observed => _observed != null ? _observed : null;
        public static IReadOnlyList<MonoBehaviour> Providers => Units;
        public static bool FollowSelection
        {
            get => _followSelection;
            set { _followSelection = value; Refresh(); }
        }

        static RuntimeDebugSession()
        {
            EditorApplication.update += Update;
            EditorApplication.playModeStateChanged += PlayModeChanged;
            Selection.selectionChanged += SelectionChanged;
        }
        private static void PlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode || state == PlayModeStateChange.EnteredEditMode) Clear();
            if (state == PlayModeStateChange.EnteredPlayMode) { _nextScan = 0; _nextCapture = 0; }
        }
        private static void SelectionChanged() { if (FollowSelection) Refresh(); }
        private static void Update()
        {
            if (!EditorApplication.isPlaying || EditorApplication.isCompiling) return;
            double now = EditorApplication.timeSinceStartup;
            if (now < _nextCapture) return;
            _nextCapture = now + .1;
            Refresh();
        }
        public static void Observe(MonoBehaviour provider)
        {
            if (provider != null && provider is not IAbilitySystemProvider)
                throw new ArgumentException("Unit must implement IAbilitySystemProvider.", nameof(provider));
            _followSelection = false; _observed = provider;
            Refresh();
        }
        public static void RefreshProviders()
        {
            Units.Clear(); Names.Clear();
            if (!EditorApplication.isPlaying) return;
            foreach (var component in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (component == null || component is not IAbilitySystemProvider provider ||
                    !component.gameObject.scene.IsValid() || !component.gameObject.scene.isLoaded ||
                    EditorUtility.IsPersistent(component) || (component.hideFlags & HideFlags.DontSave) != 0) continue;
                Units.Add(component);
                if (provider.AbilitySystem != null) Names[provider.AbilitySystem] = UnitLabel(component);
            }
            Units.Sort((a, b) => string.CompareOrdinal(UnitLabel(a), UnitLabel(b)));
            _nextScan = EditorApplication.timeSinceStartup + 1;
        }
        public static string UnitLabel(MonoBehaviour unit)
        {
            if (unit == null) return "未选择单位";
            string path = unit.name;
            for (var parent = unit.transform.parent; parent != null; parent = parent.parent) path = parent.name + "/" + path;
            return unit.gameObject.scene.name + " / " + path + " (" + unit.GetType().Name + ")";
        }
        public static MonoBehaviour ProviderInParents(GameObject selected)
        {
            if (selected == null || EditorUtility.IsPersistent(selected)) return null;
            foreach (var component in selected.GetComponentsInParent<MonoBehaviour>(true))
                if (component is IAbilitySystemProvider) return component;
            return null;
        }
        private static string SourceName(object source)
        {
            if (source is AbilitySystemComponent asc && Names.TryGetValue(asc, out var name)) return name;
            if (source is UnityEngine.Object obj) return obj != null ? obj.name : "已销毁单位";
            return source?.ToString() ?? "未知来源";
        }
        public static void Refresh()
        {
            if (!EditorApplication.isPlaying) return;
            if (EditorApplication.timeSinceStartup >= _nextScan) RefreshProviders();
            if (FollowSelection) _observed = ProviderInParents(Selection.activeGameObject);
            if (_observed == null || _observed is not IAbilitySystemProvider provider || provider.AbilitySystem == null)
            { Publish(null); return; }
            Names[provider.AbilitySystem] = UnitLabel(_observed);
            Publish(RuntimeSnapshot.Capture(provider.AbilitySystem, SourceName, Current, EditorApplication.timeSinceStartup));
        }
        /// <summary>同一入口供编辑器刷新和自动化测试发布快照；不会推进或改变单位。</summary>
        public static void Publish(RuntimeSnapshot snapshot) { Current = snapshot; SkillEventRecorder.Observe(snapshot?.Owner); Changed?.Invoke(snapshot); }
        public static void Clear()
        {
            SkillEventRecorder.Clear(); _observed = null; Units.Clear(); Names.Clear(); _nextCapture = _nextScan = 0;
            Publish(null);
        }
    }
}
