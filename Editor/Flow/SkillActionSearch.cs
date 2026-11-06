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
        private Action<Type> _choose;
        internal static readonly Type[] Types=TypeCache.GetTypesDerivedFrom<SkillAction>().Where(t=>!t.IsAbstract && t.GetConstructor(Type.EmptyTypes)!=null).OrderBy(t=>t.Name).ToArray();
        internal static string MenuName(Type t) => SkillActionFields.For(t).Title + " · " + t.Name;
        private static string Key(Type t)=>t.AssemblyQualifiedName;
        public static void Open(Vector2 position,Action<Type> select)
        {var p=CreateInstance<SkillActionSearch>();p.hideFlags=HideFlags.HideAndDontSave;p._choose=select;SearchWindow.Open(new SearchWindowContext(position),p);}
        public static void Favorite(Type t) {string key="GAS.FavoriteAction."+Key(t);EditorPrefs.SetBool(key,!EditorPrefs.GetBool(key));}
        public List<SearchTreeEntry> CreateSearchTree(SearchWindowContext _)
        {
            var r=new List<SearchTreeEntry>{new SearchTreeGroupEntry(new GUIContent("搜索动作"),0)};
            void Group(string label,IEnumerable<Type> items){var list=items.ToArray();if(list.Length==0)return;r.Add(new SearchTreeGroupEntry(new GUIContent(label),1));foreach(var t in list)r.Add(new SearchTreeEntry(new GUIContent(MenuName(t))){level=2,userData=t});}
            Group("收藏",Types.Where(t=>EditorPrefs.GetBool("GAS.FavoriteAction."+Key(t)))); Group("最近使用",EditorPrefs.GetString("GAS.RecentActions").Split('|').Select(s=>Types.FirstOrDefault(t=>Key(t)==s)).Where(t=>t!=null));Group("全部动作",Types);return r;
        }
        public bool OnSelectEntry(SearchTreeEntry entry,SearchWindowContext _)
        {if(entry.userData is not Type t)return false;EditorPrefs.SetString("GAS.RecentActions",string.Join("|",new[]{Key(t)}.Concat(EditorPrefs.GetString("GAS.RecentActions").Split('|')).Where(s=>s.Length>0).Distinct().Take(8))); _choose(t);DestroyImmediate(this);return true;}
    }
}
