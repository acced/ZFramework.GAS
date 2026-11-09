using System;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using UnityEngine;

namespace ZFramework.GAS.Unity
{
    /// <summary>Implement on ScriptableObject assets that cache definitions and need invalidation.</summary>
    public interface IDefinitionAsset { void InvalidateDefinition(); }

    /// <summary>把运行期定义映射回来源资产，包含缓存失效前的旧定义。弱键不会保活定义。</summary>
    public static class DefinitionSource
    {
#if UNITY_EDITOR
        private static readonly ConditionalWeakTable<object, ScriptableObject> Sources = new();
        private static readonly ConditionalWeakTable<ScriptableObject, List<WeakReference<ScriptableObject>>> Dependents = new();
        private static HashSet<ScriptableObject> _invalidating;
#endif
        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        public static void DependsOn(ScriptableObject parent, ScriptableObject child)
        {
#if UNITY_EDITOR
            if (parent == null || child == null || parent == child) return;
            var entries = Dependents.GetOrCreateValue(child);
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                if (!entries[i].TryGetTarget(out var value) || value == null) { entries.RemoveAt(i); continue; }
                if (value == parent) return;
            }
            entries.Add(new WeakReference<ScriptableObject>(parent));
#endif
        }
        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        public static void InvalidateDependents(ScriptableObject changed)
        {
#if UNITY_EDITOR
            bool root = _invalidating == null; _invalidating ??= new HashSet<ScriptableObject>();
            try
            {
                if (!_invalidating.Add(changed) || !Dependents.TryGetValue(changed, out var entries)) return;
                for (int i = entries.Count - 1; i >= 0; i--)
                    if (entries[i].TryGetTarget(out var parent) && parent != null)
                    {
                        if (parent is IDefinitionAsset asset) asset.InvalidateDefinition();
                        else InvalidateDependents(parent);
                    }
            }
            finally { if (root) _invalidating = null; }
#endif
        }

        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        public static void Register(object definition, ScriptableObject asset)
        {
#if UNITY_EDITOR
            Sources.Remove(definition);
            Sources.Add(definition, asset);
#endif
        }

        public static ScriptableObject Of(object definition)
#if UNITY_EDITOR
            => definition != null && Sources.TryGetValue(definition, out var asset) && asset != null ? asset : null;
#else
            => null;
#endif
    }
}
