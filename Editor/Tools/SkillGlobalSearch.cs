using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using ZFramework.GAS.Unity;
namespace ZFramework.GAS.Editor
{
    internal static class SkillContentIndex
    {
        internal sealed class Hit
        {
            public GameplayAbilityAsset Skill; public ScriptableObject Asset; public string Path, Id, Text;
        }
        private static readonly Dictionary<GameplayAbilityAsset,List<Hit>> Cache=new();
        private static readonly Dictionary<GameplayAbilityAsset, Dictionary<ScriptableObject,(string path,int dirty)>> Dependencies = new();
        static SkillContentIndex(){ Undo.undoRedoPerformed += InvalidateUndo; }
        internal static void Clear(){Cache.Clear(); Dependencies.Clear();}
        private static void Drop(GameplayAbilityAsset root){Cache.Remove(root);Dependencies.Remove(root);}
        internal static void InvalidatePaths(HashSet<string> paths)
        {
            foreach(var pair in Dependencies.Where(p=>p.Key==null || p.Value.Values.Any(d=>paths.Contains(d.path))).ToArray()) Drop(pair.Key);
        }
        private static void InvalidateUndo()
        {
            foreach(var pair in Dependencies.Where(p=>p.Key==null || p.Value.Any(d=>d.Key==null || EditorUtility.GetDirtyCount(d.Key)!=d.Value.dirty)).ToArray()) Drop(pair.Key);
        }
        internal static void Invalidate(UnityEngine.Object obj)
        {
            if (obj==null) return;
            foreach(var pair in Cache.Where(p=>p.Key==obj || p.Value.Any(h=>h.Asset==obj)).ToList()) Drop(pair.Key);
        }
        private static List<Hit> Build(GameplayAbilityAsset root)
        {
            var list=new List<Hit>(); var visited=new HashSet<ScriptableObject>();
            void Visit(ScriptableObject asset)
            {
                if(asset==null || !visited.Add(asset)) return;
                list.Add(new Hit{Skill=root,Asset=asset,Text=asset.name,Path=""});
                var children=new HashSet<ScriptableObject>();
                using var so=new SerializedObject(asset); var p=so.GetIterator();
                var actions=new Stack<(int depth,string id)>();
                while(p.Next(true))
                {
                    int depth=p.depth;while(actions.Count>0 && actions.Peek().depth>=depth) actions.Pop();
                    var type=p.propertyType;
                    if(type!=SerializedPropertyType.String && type!=SerializedPropertyType.ManagedReference && type!=SerializedPropertyType.ObjectReference)continue;
                    string text=null;
                    if(type==SerializedPropertyType.ManagedReference && p.managedReferenceValue is SkillAction a)
                    {actions.Push((depth,a.Id)); text=a.GetType().Name+" "+SkillFlowView.ActionTitle(a);}
                    else if(type==SerializedPropertyType.String)
                    {text=p.stringValue;if(string.IsNullOrEmpty(text) || p.name=="id" && !p.propertyPath.StartsWith("parameters.Array.data[",StringComparison.Ordinal))continue;}
                    else if(type==SerializedPropertyType.ObjectReference && p.objectReferenceValue is ScriptableObject child)
                    {
                        text=child.name;
                        if(child is GameplayEffectAsset || SkillEditorExtensions.Supports(child) || child is SkillActionModuleAsset || child is SkillParameterSetAsset)children.Add(child);
                    }
                    if(!string.IsNullOrEmpty(text)) list.Add(new Hit{Skill=root,Asset=asset,Path=p.propertyPath,Text=text,Id=actions.Count>0?actions.Peek().id:null});
                }
                foreach(var child in children) Visit(child);
            }
            Visit(root); Dependencies[root]=visited.ToDictionary(a=>a,a=>(AssetDatabase.GetAssetPath(a),EditorUtility.GetDirtyCount(a))); return list;
        }
        private static List<Hit> ForSkill(GameplayAbilityAsset root)
        {
            if (!Cache.TryGetValue(root, out var hits)) Cache[root] = hits = Build(root);
            return hits;
        }

        public static void CountParameterUses(GameplayAbilityAsset root, Dictionary<string, int> counts)
        {
            counts.Clear();
            if (root == null) return;
            foreach (var hit in ForSkill(root))
            {
                // Declarations and free text remain searchable; only bindings count as uses.
                if (!hit.Path.EndsWith("ParameterId", StringComparison.OrdinalIgnoreCase) || hit.Path.EndsWith(".moduleParameterId",StringComparison.Ordinal)) continue;
                counts.TryGetValue(hit.Text, out int count);
                counts[hit.Text] = count + 1;
            }
        }

        public static void Search(string query,List<Hit> results)
        {
            results.Clear(); if(string.IsNullOrWhiteSpace(query)) return;
            foreach(var root in SkillIndex.Abilities)
                foreach(var h in ForSkill(root)) if(h.Text.IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0) results.Add(h);
        }
    }
    internal sealed class SkillGlobalSearch : EditorWindow
    {
        private readonly List<SkillContentIndex.Hit> _hits=new();
        private ToolbarSearchField _field; private ListView _list;
        [MenuItem("Window/ZFramework GAS/全局查找")]
        public static void Open()=>ShowQuery("");
        public static void ShowQuery(string query)
        {var w=GetWindow<SkillGlobalSearch>("GAS 全局查找");if(w._field==null)w.CreateGUI();w._field.value=query;w.Refresh();w._field.Focus();}
        private void CreateGUI()
        {
            rootVisualElement.Clear();_field=new ToolbarSearchField{tooltip="动作类型、参数、标签或资产名称"};rootVisualElement.Add(_field);
            _list=new ListView(_hits,32,()=>new Label(),(el,i)=>{var h=_hits[i];((Label)el).text=h.Skill.name+" › "+h.Asset.name+"   "+h.Text;el.tooltip=h.Path;});
            _list.style.flexGrow=1;rootVisualElement.Add(_list); _field.RegisterValueChangedCallback(_=>Refresh());
            _list.itemsChosen+=items=>{foreach(SkillContentIndex.Hit hit in items){var w=SkillEditorWindow.Open();w.Show(hit.Skill);
                w.Document.Selection.Select(hit.Asset,string.IsNullOrEmpty(hit.Path)?null:"@field",hit.Path);
                if(hit.Id!=null && SkillActionNavigation.Select(w.Document,hit.Id)) w.Document.OpenFlow();break;}};
        }
        private void Refresh(){SkillContentIndex.Search(_field.value,_hits);_list.RefreshItems();}
    }
}
