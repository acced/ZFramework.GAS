using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    // A session singleton survives managed domain reload; restarting Unity also starts a new Undo history.
    // Only the publication bit is undoable. File images and owner IDs must survive both Undo and Redo.
    internal sealed class ModuleFileJournal : ScriptableSingleton<ModuleFileJournal>
    {
        [Serializable] internal sealed class Entry
        {
            public ModulePublication token;
            public ZFramework.GAS.Unity.SkillActionModuleAsset retained,live;
            public string owner,path,guid,list,original,callJson;
            public string[] inlineIds;
            public int index,group;
            public byte[] data,meta;
            public bool published,obsolete,repairOnly;
            public string recovery;
            public InlineEffects effects;
        }
        // Native redo destroys the original private effects before undoRedoPerformed.
        // EditorJsonUtility stores instance IDs, which are then dead. Capture the reference
        // slots separately and only reconstruct missing objects on a failed publication.
        [Serializable] internal sealed class InlineEffects{
            [Serializable] private struct Link{public string path;public int target;}
            [Serializable] private sealed class Effect{public string id,json;public GameplayEffectAsset repair;public Link[] links;public bool parked,external;public HideFlags flags;}
            [SerializeField] private Effect[] effects;
            [SerializeField] private Link[] actions;
            internal static InlineEffects Capture(SkillActionModuleAsset original, GameplayEffectAsset[] owned){
                if(owned.Length==0)return null;
                Link[] Links(ScriptableObject obj){
                    var result=new List<Link>();using var so=new SerializedObject(obj);using var p=so.GetIterator();
                    while(p.Next(true))if(p.propertyType==SerializedPropertyType.ObjectReference && p.objectReferenceValue is GameplayEffectAsset fx){
                        int n=Array.IndexOf(owned,fx);if(n>=0)result.Add(new Link{path=p.propertyPath,target=n});}
                    return result.ToArray();
                }
                var snapshot=new InlineEffects{actions=Links(original),effects=new Effect[owned.Length]};
                for(int i=0;i<owned.Length;i++)snapshot.effects[i]=new Effect{id=GlobalObjectId.GetGlobalObjectIdSlow(owned[i]).ToString(),
                    json=EditorJsonUtility.ToJson(owned[i]),links=Links(owned[i])};
                return snapshot;
            }
            // Only the journal-created copies are eligible. Treat them as a group so a
            // private parent -> child link is not mistaken for an external use of the child.
            internal bool ParkUnusedRepairs(ScriptableObject owner){
                var candidates=new HashSet<UnityEngine.Object>();string path=AssetDatabase.GetAssetPath(owner);
                foreach(var fx in effects)if(fx.repair!=null && AssetDatabase.GetAssetPath(fx.repair)==path)candidates.Add(fx.repair);
                if(candidates.Count==0)return false;
                if(SkillReferenceGuard.Find(path,candidates,candidates,path)!=null){
                    var keep=new HashSet<UnityEngine.Object>();
                    void Retain(GameplayEffectAsset fx){
                        if(!candidates.Contains(fx) || !keep.Add(fx))return;
                        foreach(var child in AssetReferences.Children(fx))if(child is GameplayEffectAsset nested)Retain(nested);
                    }
                    foreach(GameplayEffectAsset fx in candidates)
                        if(!keep.Contains(fx) && SkillReferenceGuard.Find(path,new[]{fx},candidates,path)!=null)Retain(fx);
                    foreach(var fx in effects)if(fx.repair!=null && keep.Contains(fx.repair))fx.external=true;
                    if(keep.Count!=0)Debug.LogWarning("修复效果仍被引用，原对象及其编辑历史保留；重做时技能将使用独立的私有效果："+path);
                    candidates.ExceptWith(keep);
                }
                // Undo records refer to Unity object identity. Removing a subasset is
                // reversible only if we keep this very object until its redo branch expires.
                foreach(var fx in effects)if(fx.repair!=null && candidates.Contains(fx.repair)){
                    fx.flags=fx.repair.hideFlags;
                    AssetDatabase.RemoveObjectFromAsset(fx.repair);
                    fx.parked=true;fx.external=false;fx.repair.hideFlags|=HideFlags.DontUnloadUnusedAsset;
                }
                return candidates.Count!=0;
            }
            internal void DiscardParked(){
                foreach(var fx in effects)if(fx.parked && fx.repair!=null){
                    if(!EditorUtility.IsPersistent(fx.repair))UnityEngine.Object.DestroyImmediate(fx.repair);
                    else if((fx.flags & HideFlags.DontUnloadUnusedAsset)==0)fx.repair.hideFlags&=~HideFlags.DontUnloadUnusedAsset;
                    fx.repair=null;fx.parked=false;
                }
            }
            internal void Bind(SkillActionModuleAsset inline,ScriptableObject owner){
                var resolved=new GameplayEffectAsset[effects.Length];var created=new bool[effects.Length];
                var previous=new GameplayEffectAsset[effects.Length];
                for(int i=0;i<effects.Length;i++){var fx=effects[i];
                    if(GlobalObjectId.TryParse(fx.id,out var id))resolved[i]=GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id) as GameplayEffectAsset;
                    previous[i]=fx.repair;
                    if(resolved[i]!=null)continue;
                    // An editor extension may have adopted a parked object in the meantime.
                    // It is then someone else's asset; never move it back or destroy it.
                    if(fx.parked && fx.repair!=null && EditorUtility.IsPersistent(fx.repair)){
                        if((fx.flags & HideFlags.DontUnloadUnusedAsset)==0)fx.repair.hideFlags&=~HideFlags.DontUnloadUnusedAsset;
                        fx.parked=false;fx.external=true;
                    }
                    if(!fx.external && fx.repair!=null){
                        if(fx.parked){
                            fx.repair.hideFlags=fx.flags;
                            try{AssetDatabase.AddObjectToAsset(fx.repair,owner);fx.parked=false;}
                            catch{fx.repair.hideFlags|=HideFlags.DontUnloadUnusedAsset;throw;}
                        }
                        resolved[i]=fx.repair;continue;
                    }
                    // An outside asset deliberately acquired that earlier object. Do not
                    // redirect its pointer or silently make the inline action share it.
                    var copy=ScriptableObject.CreateInstance<GameplayEffectAsset>();
                    if(fx.external && fx.repair!=null)EditorUtility.CopySerialized(fx.repair,copy);
                    else EditorJsonUtility.FromJsonOverwrite(fx.json,copy);
                    AssetDatabase.AddObjectToAsset(copy,owner);
                    created[i]=!fx.external;fx.external=false;fx.repair=resolved[i]=copy;
                }
                void Reconnect(ScriptableObject obj,Link[] links){
                    using var so=new SerializedObject(obj);
                    foreach(var link in links)so.FindProperty(link.path).objectReferenceValue=resolved[link.target];
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
                var replacements=new Dictionary<GameplayEffectAsset,GameplayEffectAsset>();
                for(int i=0;i<effects.Length;i++)if(previous[i]!=null && previous[i]!=resolved[i])
                    replacements[previous[i]]=resolved[i];
                for(int i=0;i<effects.Length;i++){
                    if(resolved[i]!=effects[i].repair)continue;
                    if(created[i]){Reconnect(resolved[i],effects[i].links);continue;}
                    // Parked or copied live data can have edited/reordered children.
                    // Only redirect references to the objects that actually forked.
                    if(replacements.Count==0)continue;
                    using var so=new SerializedObject(resolved[i]);using var p=so.GetIterator();
                    while(p.Next(true))if(p.propertyType==SerializedPropertyType.ObjectReference && p.objectReferenceValue is GameplayEffectAsset old
                        && replacements.TryGetValue(old,out var next))p.objectReferenceValue=next;
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
                Reconnect(inline,actions);
            }
        }
        [SerializeField] internal List<Entry> entries=new();
    }

}
