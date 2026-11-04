using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    // Only the destination slice is remapped. A standalone effect is a shared definition by design.
    internal static class ActionEffectOwnership
    {
        private static IEnumerable<SerializedProperty> Links(SerializedProperty item)
        {
            using var p=item.Copy();using var end=item.GetEndProperty();
            while(p.Next(true) && !SerializedProperty.EqualContents(p,end))
                if(p.propertyType==SerializedPropertyType.ObjectReference && p.objectReferenceValue is GameplayEffectAsset)
                    yield return p.Copy();
        }
        private static bool Private(GameplayEffectAsset effect,string destination)
        {
            string path=AssetDatabase.GetAssetPath(effect);
            return !string.IsNullOrEmpty(path) && path!=destination && AssetDatabase.LoadMainAssetAtPath(path)!=effect;
        }
        internal enum CopyDecision { Cancel, Reuse, New }
        // Hash the source configuration, including private descendants. Object instance IDs
        // are not stable across imports; encode serialized references by GUID:fileID instead.
        private static string Fingerprint(GameplayEffectAsset root)
        {
            var text=new System.Text.StringBuilder();var seen=new HashSet<GameplayEffectAsset>();
            void Visit(GameplayEffectAsset effect){
                if(!seen.Add(effect))return;
                using var so=new SerializedObject(effect);using var p=so.GetIterator();
                bool descend=true;
                while(p.Next(descend)){
                    descend=p.depth!=0 || !(p.name.StartsWith("m_",StringComparison.Ordinal) || p.name.StartsWith("editorCopy",StringComparison.Ordinal));
                    if(!descend)continue;
                    text.Append(p.propertyPath).Append(':');
                    if(p.propertyType==SerializedPropertyType.ObjectReference){
                        var obj=p.objectReferenceValue;
                        if(obj!=null)text.Append(AssetReferences.Key(obj));
                    }else if(!p.hasChildren)text.Append(p.contentHash.ToString("X8"));
                    text.Append(';');
                }
                foreach(var child in AssetReferences.Children(effect).OfType<GameplayEffectAsset>()){
                    var path=AssetDatabase.GetAssetPath(child);
                    if(!string.IsNullOrEmpty(path) && AssetDatabase.LoadMainAssetAtPath(path)!=child)Visit(child);
                }
            }
            Visit(root);return Hash128.Compute(text.ToString()).ToString();
        }
        private static Dictionary<GameplayEffectAsset,GameplayEffectAsset> Gather(SerializedProperty list,int index,int count,string path,
            Func<GameplayEffectAsset,GameplayEffectAsset> lookup=null)
        {
            var map=new Dictionary<GameplayEffectAsset,GameplayEffectAsset>();
            void Visit(GameplayEffectAsset effect)
            {
                if(!Private(effect,path) || map.ContainsKey(effect))return;
                var existing=lookup?.Invoke(effect);map.Add(effect,existing);if(existing!=null)return;
                foreach(var child in AssetReferences.Children(effect).OfType<GameplayEffectAsset>())Visit(child);
            }
            for(int i=0;i<count;i++)foreach(var p in Links(list.GetArrayElementAtIndex(index+i)))Visit((GameplayEffectAsset)p.objectReferenceValue);
            return map;
        }
        internal static void CheckExclusive(SkillActionModuleAsset pending,string destination,ScriptableObject source,Func<string,bool> consent)
        {
            using var so=new SerializedObject(pending);var list=so.FindProperty("actions");
            var moving=Gather(list,0,list.arraySize,destination);
            if(moving.Count==0)return;
            foreach(var effect in moving.Keys)
                if(AssetDatabase.GetAssetPath(effect)!=AssetDatabase.GetAssetPath(source))
                    throw new InvalidOperationException($"「{effect.name}」是其他文件的内嵌效果；请先外置为共享效果。");
            var targets=moving.Keys.Cast<UnityEngine.Object>().ToArray();
            string hit=SkillReferenceGuard.Find(AssetDatabase.GetAssetPath(source),targets,targets);
            if(hit!=null)throw new InvalidOperationException($"内嵌效果「{string.Join("、",moving.Keys.Select(e=>e.name))}」仍被 {hit} 共用。请一起提取相关动作，或先改用共享效果资产。");
            Require(consent,"移动以下私有效果将改变 GUID:fileID。编辑器会沿用节点布局；外部 Addressables、存档或自定义键不会自动更新。需要保留这些键时请取消。",moving.Keys);
        }

        private static void Require(Func<string,bool> consent,string reason,IEnumerable<GameplayEffectAsset> effects)
        {
            string msg=reason+"\n"+string.Join("\n",effects.Select(e=>e.name+" · "+AssetReferences.Key(e)));
            if(consent==null)throw new InvalidOperationException(msg);
            if(!consent(msg))throw new OperationCanceledException();
        }
        internal static bool ConfirmCopy(string msg)=>!Application.isBatchMode && EditorUtility.DisplayDialog("私有效果的归属",msg,"使用目标文件的副本","取消本次操作");
        internal static bool ConfirmMove(string msg)=>!Application.isBatchMode && EditorUtility.DisplayDialog("移动私有效果",msg,"移动并更换标识","取消");
        private static CopyDecision ResolveConflict(string msg)
        {
            if(Application.isBatchMode)return CopyDecision.Cancel;
            return EditorUtility.DisplayDialogComplex("私有效果内容已更改",msg,"复用旧副本","取消本次操作","生成新副本") switch{
                0=>CopyDecision.Reuse,2=>CopyDecision.New,_=>CopyDecision.Cancel};
        }
        internal static Dictionary<GameplayEffectAsset,GameplayEffectAsset> Localize(SerializedProperty list,int index,int count,
            ScriptableObject destination,bool undo,Func<string,bool> consent=null,bool confirmReuse=false,Func<string,CopyDecision> conflict=null)
        {
            string path=AssetDatabase.GetAssetPath(destination);
            var known=new Dictionary<string,List<(GameplayEffectAsset effect,string hash,string local,string accepted,int version)>>(StringComparer.Ordinal);
            var hashes=new Dictionary<GameplayEffectAsset,string>();var versions=new Dictionary<GameplayEffectAsset,int>();
            var exact=new Dictionary<GameplayEffectAsset,GameplayEffectAsset>();var stale=new Dictionary<GameplayEffectAsset,GameplayEffectAsset>();
            if(undo && !string.IsNullOrEmpty(path)){
                foreach(var effect in AssetDatabase.LoadAllAssetsAtPath(path).OfType<GameplayEffectAsset>()){
                    using var data=new SerializedObject(effect);string key=data.FindProperty("editorCopyOrigin").stringValue;
                    if(string.IsNullOrEmpty(key))continue;
                    if(!known.TryGetValue(key,out var copies))known[key]=copies=new();
                    copies.Add((effect,data.FindProperty("editorCopyHash").stringValue,data.FindProperty("editorCopyLocalHash").stringValue,
                        data.FindProperty("editorCopyAcceptedHash").stringValue,data.FindProperty("editorCopyVersion").intValue));
                }
            }
            GameplayEffectAsset Inspect(GameplayEffectAsset source){
                if(!undo)return null;
                string key=AssetReferences.Key(source);string hash=hashes[source]=Fingerprint(source);
                if(!known.TryGetValue(key,out var copies)){versions[source]=1;return null;}
                var sorted=copies.OrderByDescending(c=>c.version);
                versions[source]=sorted.First().version+1;
                var closest=sorted.FirstOrDefault(c=>c.hash==hash || c.accepted==hash);
                // A newer copy may have local edits. Silently switching back to an older
                // clean copy would also switch the destination's effect/stack identity.
                if(closest.effect!=null && closest.local==Fingerprint(closest.effect))return exact[source]=closest.effect;
                stale[source]=closest.effect!=null?closest.effect:sorted.First().effect;return null;
            }
            var map=Gather(list,index,count,path,Inspect);
            if(map.Count==0)return map;
            if(string.IsNullOrEmpty(path))throw new InvalidOperationException("请先保存目标资产，再复制内嵌效果。");
            if(stale.Count>0){
                string msg="来源或目标副本的内容已更改。复用保留并接受目标当前数值；生成新副本会复制冲突效果的整棵私有效果树，各版本分开叠层和移除。独立共享效果仍然共享。\n"+
                    string.Join("\n",stale.Keys.Select(e=>e.name+" · "+AssetReferences.Key(e)));
                var choice=(conflict??ResolveConflict)(msg);if(choice==CopyDecision.Cancel)throw new OperationCanceledException();

                var force=new HashSet<GameplayEffectAsset>();
                void ForceTree(GameplayEffectAsset e){
                    if(!Private(e,path) || !force.Add(e))return;
                    foreach(var child in AssetReferences.Children(e).OfType<GameplayEffectAsset>())ForceTree(child);
                }
                if(choice==CopyDecision.New)foreach(var e in stale.Keys)ForceTree(e);
                map=Gather(list,index,count,path,e=>{
                    if(!hashes.ContainsKey(e))Inspect(e);
                    if(force.Contains(e))return null;
                    return exact.TryGetValue(e,out var match)?match:choice==CopyDecision.Reuse && stale.TryGetValue(e,out var old)?old:null;
                });
                var firstCopies=map.Where(p=>p.Value==null && !known.ContainsKey(AssetReferences.Key(p.Key))).Select(p=>p.Key).ToArray();
                if(firstCopies.Length>0)Require(consent,"这些效果在目标文件还没有副本。复制后将与来源及其其他调用方分开叠层和移除。",firstCopies);
                if(choice==CopyDecision.Reuse){
                    // Follow the destination tree actually reused, not the newest unrelated
                    // local copy of a child. A local addition/removal is an intentional edit.
                    var acknowledged=new HashSet<GameplayEffectAsset>();
                    void Accept(GameplayEffectAsset source,GameplayEffectAsset copy){
                        if(copy==null || !acknowledged.Add(copy))return;
                        using var data=new SerializedObject(copy);
                        data.FindProperty("editorCopyLocalHash").stringValue=Fingerprint(copy);
                        data.FindProperty("editorCopyAcceptedHash").stringValue=Fingerprint(source);
                        data.ApplyModifiedProperties();EditorUtility.SetDirty(copy);
                        var children=AssetReferences.Children(source).OfType<GameplayEffectAsset>().Where(c=>Private(c,path))
                            .Distinct().ToDictionary(c=>AssetReferences.Key(c));
                        foreach(var child in AssetReferences.Children(copy).OfType<GameplayEffectAsset>()){
                            if(AssetDatabase.GetAssetPath(child)!=path)continue;
                            using var props=new SerializedObject(child);
                            if(children.TryGetValue(props.FindProperty("editorCopyOrigin").stringValue,out var from))Accept(from,child);
                        }
                    }
                    foreach(var pair in map)if(pair.Value!=null && stale.ContainsKey(pair.Key))Accept(pair.Key,pair.Value);
                }
            }else if(undo && (map.Any(pair=>pair.Value==null) || confirmReuse))
                Require(consent,"使用副本会与原文件及其其他调用方分开叠层和移除。要继续共享，请取消本次操作并先改用独立的效果资产。",map.Keys);
            var fresh=map.Where(pair=>pair.Value==null).Select(pair=>pair.Key).ToArray();
            foreach(var source in fresh){
                var copy=UnityEngine.Object.Instantiate(source);copy.name=source.name;
                AssetDatabase.AddObjectToAsset(copy,destination);if(undo)Undo.RegisterCreatedObjectUndo(copy,"复制动作的内嵌效果");
                map[source]=copy;
            }
            foreach(var source in fresh){
                var copy=map[source];using var so=new SerializedObject(copy);
                foreach(var pair in map)AssetReferences.Replace(so,pair.Key,pair.Value);
                so.FindProperty("editorCopyOrigin").stringValue=undo?AssetReferences.Key(source):"";
                so.FindProperty("editorCopyHash").stringValue=undo?hashes[source]:"";
                so.FindProperty("editorCopyAcceptedHash").stringValue="";
                so.FindProperty("editorCopyVersion").intValue=undo?versions[source]:0;
                so.ApplyModifiedPropertiesWithoutUndo();EditorUtility.SetDirty(copy);
            }
            // Fingerprint only after every child pointer points into the destination file.
            foreach(var source in fresh){
                var copy=map[source];using var so=new SerializedObject(copy);
                so.FindProperty("editorCopyLocalHash").stringValue=undo?Fingerprint(copy):"";
                so.ApplyModifiedPropertiesWithoutUndo();EditorUtility.SetDirty(copy);
            }
            for(int i=0;i<count;i++)
                foreach(var p in Links(list.GetArrayElementAtIndex(index+i)))
                    if(map.TryGetValue((GameplayEffectAsset)p.objectReferenceValue,out var copy))p.objectReferenceValue=copy;
            return map;
        }
    }
}
