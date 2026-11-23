using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    internal sealed class SkillActionSearch : ScriptableObject, ISearchWindowProvider
    {
        private static SkillActionSearch _active;
        private Action<Type> _choose;

        internal static readonly Type[] Types = TypeCache.GetTypesDerivedFrom<SkillAction>()
            .Where(t => !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) != null)
            .OrderBy(t => t.Name).ToArray();

        internal static string MenuName(Type t) => SkillActionFields.For(t).Title + " · " + t.Name;

        public static void Open(Vector2 position, Action<Type> select)
        {
            if (_active != null) DestroyImmediate(_active);
            _active = CreateInstance<SkillActionSearch>();
            _active.hideFlags = HideFlags.HideAndDontSave;
            _active._choose = select;
            SearchWindow.Open(new SearchWindowContext(position), _active);
        }

        public List<SearchTreeEntry> CreateSearchTree(SearchWindowContext _)
        {
            var r = new List<SearchTreeEntry> { new SearchTreeGroupEntry(new GUIContent("搜索动作"), 0) };
            foreach (Type type in Types) r.Add(new SearchTreeEntry(new GUIContent(MenuName(type))) { level = 1, userData = type });
            return r;
        }

        public bool OnSelectEntry(SearchTreeEntry entry, SearchWindowContext _)
        {
            if (entry.userData is not Type t) return false;
            Action<Type> choose = _choose; _choose = null;
            if (_active == this) _active = null;
            choose?.Invoke(t);
            DestroyImmediate(this);
            return true;
        }
    }
}
