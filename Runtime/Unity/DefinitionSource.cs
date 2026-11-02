using System;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace ZFramework.GAS.Unity
{
    /// <summary>把运行期定义映射回来源资产，包含缓存失效前的旧定义。弱键不会保活定义。</summary>
    public static class DefinitionSource
    {
        private static readonly ConditionalWeakTable<object, ScriptableObject> Sources = new();

        public static void Register(object definition, ScriptableObject asset)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            lock (Sources)
            {
                Sources.Remove(definition);
                Sources.Add(definition, asset);
            }
        }

        public static ScriptableObject Of(object definition)
            => definition != null && Sources.TryGetValue(definition, out var asset) && asset != null ? asset : null;
    }
}
