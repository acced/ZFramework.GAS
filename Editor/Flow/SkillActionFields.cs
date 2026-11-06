using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    // Discover only serialized branches and fields explicitly chosen for the compact node.
    internal sealed class SkillActionFields
    {
        private static readonly Dictionary<Type, SkillActionFields> Cache = new();
        internal readonly string Title;
        internal readonly List<FieldInfo> Branches = new();
        internal readonly List<(string path, string label)> Inline = new();
        internal static SkillActionFields For(Type type)
        {
            if (!Cache.TryGetValue(type, out var fields)) Cache.Add(type, fields = new SkillActionFields(type));
            return fields;
        }
        private SkillActionFields(Type type)
        {
            Title = type.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName ?? ObjectNames.NicifyVariableName(type.Name);
            for (var t = type; t != typeof(SkillAction); t = t.BaseType)
                foreach (var f in t.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (f.FieldType == typeof(SkillAction[]) && f.IsDefined(typeof(SerializeReference), true)) Branches.Add(f);
                    var field = f.GetCustomAttribute<FlowFieldAttribute>();
                    if (field != null)
                        Inline.Add((field.Child == null ? f.Name : f.Name + "." + field.Child, field.Label));
                    if (f.FieldType == typeof(TargetQuery) && !f.IsNotSerialized && (f.IsPublic || f.IsDefined(typeof(SerializeField)))
                        && field?.Child != nameof(TargetQuery.relation))
                        Inline.Add((f.Name + "." + nameof(TargetQuery.relation), "目标关系"));
                }
        }
    }
}
