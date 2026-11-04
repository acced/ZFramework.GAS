using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    // Index direct call sites, not transitive file dependencies. Inherited constraints are
    // composed on the way back to the caller; unrelated sibling actions are never validated.
    internal static class ModuleCallIndex
    {
        private sealed class Site
        {
            public ScriptableObject owner,target;
            public string id;
            public int depth;
            public bool detached;
        }
        private static readonly Dictionary<ScriptableObject,List<Site>> Outgoing=new(), Incoming=new();
        private static readonly Dictionary<string,List<ScriptableObject>> ByPath=new();
        private static readonly HashSet<string> Dirty=new();
        private static bool _ready;
        private static readonly List<ScriptableObject> Owners=new();
        // Undo, direct field writes and ApplyModifiedPropertiesWithoutUndo do not all publish a
        // change event. Verify live direct edges before using the reverse index, without reloading files.
        internal static void Invalidate(IEnumerable<string> paths){if(_ready)Dirty.UnionWith(paths);}
        internal static void Changed(UnityEngine.Object owner)
        {if(_ready && owner is ScriptableObject so)Index(so);}
        private static void Remove(ScriptableObject owner)
        {
            if(!Outgoing.TryGetValue(owner,out var old))return;
            foreach(var site in old)if(Incoming.TryGetValue(site.target,out var list)){
                list.Remove(site);if(list.Count==0)Incoming.Remove(site.target);}
            Outgoing.Remove(owner);
        }
        private static void Index(ScriptableObject owner)
        {
            if(owner is not (SkillActionModuleAsset or GameplayAbilityAsset or SkillAnimationAsset))return;
            if(!Outgoing.TryGetValue(owner,out var sites)){
                Outgoing[owner]=sites=new();
                string path=AssetDatabase.GetAssetPath(owner);
                if(!string.IsNullOrEmpty(path)){
                    if(!ByPath.TryGetValue(path,out var assets))ByPath[path]=assets=new();
                    if(!assets.Contains(owner))assets.Add(owner);}
            }
            int position=0;
            void Unlink(Site old){
                if(Incoming.TryGetValue(old.target,out var links)){
                    links.Remove(old);if(links.Count==0)Incoming.Remove(old.target);}
            }
            void Add(ScriptableObject target,string id,int depth,bool detached)
            {
                int at=position++;
                if(at<sites.Count){
                    var old=sites[at];if(old.target==target && old.id==id && old.depth==depth && old.detached==detached)return;
                    Unlink(old);
                }
                var site=new Site{owner=owner,target=target,id=id,depth=depth,detached=detached};
                if(at<sites.Count)sites[at]=site;else sites.Add(site);
                if(!Incoming.TryGetValue(target,out var calls))Incoming[target]=calls=new();calls.Add(site);
            }
            void Scan(SkillAction action,int depth,bool detached)
            {
                if(action==null || depth>8)return;
                if(action is CallModuleAction c && c.module!=null)Add(c.module,c.Id,depth,detached);
                foreach(var f in SkillActionClipboard.Branches(action.GetType()))
                    if(f.GetValue(action) is SkillAction[] children)
                        foreach(var child in children)Scan(child,depth+1,detached || SkillFlowCommands.IsDetached(action,f.Name));
            }
            if(owner is GameplayAbilityAsset a){
                foreach(var action in a.activationActions??Array.Empty<SkillAction>())Scan(action,1,false);
                if(a.Animation!=null)Add(a.Animation,null,0,false);
            }else if(owner is SkillActionModuleAsset m){foreach(var action in m.actions??Array.Empty<SkillAction>())Scan(action,1,false);}
            else{
                var markers=((SkillAnimationAsset)owner).Markers;
                if(markers!=null)for(int i=0;i<markers.Count;i++)
                    if(markers[i]?.actions is SkillAction[] actions)foreach(var action in actions)Scan(action,1,false);
            }
            while(sites.Count>position){int last=sites.Count-1;Unlink(sites[last]);sites.RemoveAt(last);}
        }
        private static void Ensure()
        {
            if(!_ready){
                foreach(var type in new[]{"GameplayAbilityAsset","SkillActionModuleAsset","SkillAnimationAsset"})
                    foreach(var guid in AssetDatabase.FindAssets("t:"+type))Dirty.Add(AssetDatabase.GUIDToAssetPath(guid));
                _ready=true;
            }
            var paths=Dirty.ToArray();Dirty.Clear();
            foreach(var path in paths){
                if(ByPath.TryGetValue(path,out var owners)){foreach(var o in owners)Remove(o);ByPath.Remove(path);}
                if(AssetDatabase.GetMainAssetTypeAtPath(path)==null)continue;
                foreach(var obj in AssetDatabase.LoadAllAssetsAtPath(path))if(obj is ScriptableObject so)Index(so);
            }
        }
        internal static void ValidateCallers(SkillActionModuleAsset module,SkillDocument doc)
        {
            Ensure();
            if(doc?.Root!=null && !Outgoing.ContainsKey(doc.Root))Index(doc.Root);
            if(doc!=null)foreach(var owner in doc.Assets)if(owner!=null && !Outgoing.ContainsKey(owner))Index(owner);
            Owners.Clear();Owners.AddRange(Outgoing.Keys);
            foreach(var owner in Owners){if(owner==null)Remove(owner);else Index(owner);}
            Owners.Clear();if(!Outgoing.ContainsKey(module))Index(module);
            var checkedContexts=new HashSet<(int,bool)>();
            var chain=new HashSet<ScriptableObject>();
            var visited=new HashSet<(ScriptableObject,int,bool)>();
            void ValidatePath(ScriptableObject target,int depth,bool detached,string call)
            {
                if((depth==0 && !detached) || !checkedContexts.Add((depth,detached)))return;
                try{var ids=new HashSet<string>();foreach(var action in module.actions)SkillFlowCommands.Visit(action,detached,depth+1,ids);}
                catch(InvalidOperationException error){throw new SkillActionException(call,
                    $"调用方 {target.name}（{AssetDatabase.GetAssetPath(target)}），调用动作 {call}：{error.Message}",error);}
            }
            void Walk(ScriptableObject target,int depth,bool detached,string call)
            {
                // The edited module's own outgoing cycles have already failed ValidateStructure.
                // A pre-existing cycle in a caller's sibling branch is not part of this edit.
                if(!chain.Add(target)){if(detached)ValidatePath(target,0,true,call);return;}
                if(!visited.Add((target,depth,detached))){chain.Remove(target);return;}
                if(Incoming.TryGetValue(target,out var callers) && callers.Count>0)
                {
                    foreach(var site in callers)if(site.owner!=null)
                        Walk(site.owner,depth+site.depth,detached||site.detached,site.id??call);
                }
                else ValidatePath(target,depth,detached,call);
                chain.Remove(target);
            }
            Walk(module,0,false,null);
        }
    }
}
