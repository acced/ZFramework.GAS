using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    internal static class SkillFlowCommands
    {
        const string Element=".Array.data[";
        public static int IndexIn(string list,string path)
            => path.StartsWith(list+Element,StringComparison.Ordinal) && int.TryParse(path.Substring(list.Length+Element.Length).TrimEnd(']'),out int n)?n:-1;

        public static void Move(SkillDocument doc,ScriptableObject owner,string path,string destination,int at)
        {
            int split=path.LastIndexOf(Element,StringComparison.Ordinal); string source=path.Substring(0,split);
            if(destination.StartsWith(path+".",StringComparison.Ordinal)) throw new InvalidOperationException("不能把动作移动到自己的分支。");
            EditCommand.Run(doc,"移动流程动作",owner,so=>
            {
                var from=so.FindProperty(source); var to=so.FindProperty(destination); int index=IndexIn(source,path);
                if(at<0) at=to.arraySize;
                if(source==destination) { from.MoveArrayElement(index,at>index?at-1:at); return; }
                var action=from.GetArrayElementAtIndex(index).managedReferenceValue;
                int end=to.arraySize++; to.GetArrayElementAtIndex(end).managedReferenceValue=action;
                if(at!=end) to.MoveArrayElement(end,at);
                // Inserting into an ancestor can shift the original path. Resolve its containing array by managed parent ID.
                if(source.StartsWith(destination+Element,StringComparison.Ordinal))
                {
                    int close=source.IndexOf(']',destination.Length+Element.Length); int parent=int.Parse(source.Substring(destination.Length+Element.Length,close-destination.Length-Element.Length));
                    if(parent>=at) from=so.FindProperty(destination+Element+(parent+1)+source.Substring(close));
                }
                from.DeleteArrayElementAtIndex(index); so.ApplyModifiedProperties(); ValidateOwner(so,doc);
            },true);
        }
        internal static void ValidateOwner(SerializedObject so,SkillDocument doc)
        {
            void Owner(SerializedObject source)
            {
                var ids=new HashSet<string>();
                if(source.targetObject is SkillAnimationAsset)
                {var markers=source.FindProperty("markers"); for(int i=0;i<markers.arraySize;i++) ValidateStructure(markers.GetArrayElementAtIndex(i).FindPropertyRelative("actions"),ids);}
                else ValidateStructure(source.FindProperty(source.targetObject is GameplayAbilityAsset?"activationActions":"actions"),ids);
            }
            Owner(so);
            if(so.targetObject is SkillActionModuleAsset module)
                ModuleCallIndex.ValidateCallers(module,doc);
        }
        internal static bool IsDetached(SkillAction action,string branch) => branch=="onCancel" ||
            branch=="onHit" && action is LaunchProjectileAction p && p.ownership==SkillTaskOwnership.Scene ||
            branch=="onPeriod" && action is CreateAreaAction a && a.ownership==SkillTaskOwnership.Scene;
        public static void ValidateStructure(SerializedProperty list,HashSet<string> ids=null)
        {
            if(list==null)return;
            ids??=new HashSet<string>();
            for(int i=0;i<list.arraySize;i++) Visit(list.GetArrayElementAtIndex(i).managedReferenceValue as SkillAction,false,1,ids);
        }
        internal static void Visit(SkillAction action,bool detached,int depth,HashSet<string> scope)
        {
            if(action==null)return;
            if(depth>8)throw new InvalidOperationException("动作／模块嵌套不能超过八层。");
            if(string.IsNullOrEmpty(action.Id) || !scope.Add(action.Id))throw new SkillActionException(action.Id,"同一资产中的动作对象或 ID 重复。请复制而不是复用实例。");
            if(detached)
            {
                // A draft may lack a prefab or effect. Enforce runtime ownership even while its fields are incomplete.
                try{new SkillActionBuildContext().BuildDetached(new[]{action});}
                catch(InvalidOperationException e){for(Exception inner=e;inner!=null;inner=inner.InnerException)if(inner is SkillOwnershipException)throw new InvalidOperationException(inner.Message,inner);}
            }
            if(action is CallModuleAction c && c.module!=null)
            {var moduleIds=new HashSet<string>(); foreach(var inner in c.module.actions)Visit(inner,detached,depth+1,moduleIds);}
            foreach(var field in SkillActionClipboard.Branches(action.GetType()))
                if(field.GetValue(action) is SkillAction[] children) foreach(var child in children)
                    Visit(child,detached || IsDetached(action,field.Name),depth+1,scope);
        }
        internal static HashSet<string> RequiredEffectParameters(GameplayEffectAsset effect)
        {
            var seen=new HashSet<GameplayEffectAsset>();var ids=new HashSet<string>();
            void Visit(GameplayEffectAsset current)
            {
                if(current==null || !seen.Add(current))return;
                using var data=new SerializedObject(current);var cursor=data.GetIterator();
                while(cursor.Next(true)){
                    if(cursor.propertyType==SerializedPropertyType.ObjectReference && cursor.objectReferenceValue is GameplayEffectAsset child){Visit(child);continue;}
                    if(cursor.name!="parameterId" || cursor.propertyType!=SerializedPropertyType.String || string.IsNullOrEmpty(cursor.stringValue))continue;
                    string path=cursor.propertyPath;
                    if(path.StartsWith("durationMagnitude.") && !data.FindProperty("useDurationMagnitude").boolValue ||
                        path.StartsWith("periodMagnitude.") && !data.FindProperty("usePeriodMagnitude").boolValue)continue;
                    var kind=data.FindProperty(path.Substring(0,path.LastIndexOf('.')+1)+"kind");
                    if(kind==null || kind.intValue==(int)MagnitudeKind.Parameter || kind.intValue==(int)MagnitudeKind.Calculation)ids.Add(cursor.stringValue);
                }
            }
            Visit(effect);return ids;
        }
        internal static IEnumerable<string> EffectParameters(SerializedProperty action)
        {
            var ids=new HashSet<string>();
            if(action.managedReferenceValue is InlineParameterScopeAction)return ids;
            var cursor=action.Copy();var end=action.GetEndProperty();bool descend=true;
            while(cursor.Next(descend) && !SerializedProperty.EqualContents(cursor,end)){
                descend=!(cursor.propertyType==SerializedPropertyType.ManagedReference && cursor.managedReferenceValue is InlineParameterScopeAction);
                if(cursor.propertyType==SerializedPropertyType.ObjectReference && cursor.objectReferenceValue is GameplayEffectAsset effect)
                    ids.UnionWith(RequiredEffectParameters(effect));
            }
            return ids;
        }
        private static IEnumerable<SerializedProperty> ParameterLinks(SerializedProperty root)
        {
            var cursor=root.Copy();var end=root.GetEndProperty();
            bool descend=true;
            while(cursor.Next(descend) && !SerializedProperty.EqualContents(cursor,end))
            {
                // Only the caller side of a nested scope binding belongs to the enclosing module.
                descend=!(cursor.name=="actions" && cursor.isArray && cursor.propertyPath.EndsWith(".actions",StringComparison.Ordinal) &&
                    cursor.serializedObject.FindProperty(cursor.propertyPath.Substring(0,cursor.propertyPath.Length-8))?.managedReferenceValue is InlineParameterScopeAction);
                if(cursor.propertyType==SerializedPropertyType.String && !string.IsNullOrEmpty(cursor.stringValue) &&
                    (cursor.name=="parameterId" || cursor.name=="booleanParameterId" || cursor.name=="callerParameterId")) yield return cursor.Copy();
            }
        }
        public static void ConvertTask(SkillDocument doc,SkillAnimationAsset asset,string id)
        {
            EditCommand.Run(doc,"任务事件转为动作",asset,so=>
            {
                var tasks=so.FindProperty("taskEvents");int index=-1;
                for(int i=0;i<tasks.arraySize;i++)if(tasks.GetArrayElementAtIndex(i).FindPropertyRelative("id").stringValue==id){index=i;break;}
                if(index<0)throw new InvalidOperationException("任务已被删除。");
                var entry=tasks.GetArrayElementAtIndex(index);var spawner=entry.FindPropertyRelative("spawner").managedReferenceValue as TaskSpawner;
                if(spawner==null)throw new InvalidOperationException("请先配置执行器。");
                var markers=so.FindProperty("markers");var m=markers.GetArrayElementAtIndex(markers.arraySize++);
                m.FindPropertyRelative("id").stringValue=id;m.FindPropertyRelative("key").stringValue="";
                m.FindPropertyRelative("time").floatValue=entry.FindPropertyRelative("time").floatValue;m.FindPropertyRelative("effect").objectReferenceValue=null;
                var actions=m.FindPropertyRelative("actions");actions.arraySize=1;
                actions.GetArrayElementAtIndex(0).managedReferenceValue=spawner is ProjectileSpawner projectile?(SkillAction)projectile.ToAction():new RunSpawnerAction{spawner=spawner};
                tasks.DeleteArrayElementAtIndex(index);
            },true);
            doc.Selection.Select(asset,"markers",id);doc.OpenFlow();
        }
        public static void Extract(SkillDocument doc,ScriptableObject owner,string path,int index,int count,string assetPath,Func<string,bool> consent=null)
        {
            if(System.IO.File.Exists(assetPath) || AssetDatabase.LoadMainAssetAtPath(assetPath)!=null) throw new InvalidOperationException("请选择新资产路径。");
            string operation=$"提取模块「{System.IO.Path.GetFileNameWithoutExtension(assetPath)}」";
            string createdGuid=null;
            try { EditCommand.Transaction(doc,operation,owner,()=>
            {
                using var so=new SerializedObject(owner);var list=so.FindProperty(path);
                var actions=new SkillAction[count];var ids=new HashSet<string>();
                for(int i=0;i<count;i++)
                {
                    var p=list.GetArrayElementAtIndex(index+i);
                    actions[i]=p.managedReferenceValue as SkillAction;
                    foreach(var link in ParameterLinks(p)) ids.Add(link.stringValue);
                    ids.UnionWith(EffectParameters(p));
                }
                var scope=SkillNumberDrawer.LocalScope(list.GetArrayElementAtIndex(index)) ?? SkillNumberDrawer.Parameters(owner is SkillAnimationAsset?doc.Root:owner);
                var declarations=scope.Where(p=>p!=null && ids.Contains(p.Id)).ToArray();
                if(declarations.Length!=ids.Count) throw new InvalidOperationException("所选动作有缺失的参数绑定。");
                var module=ScriptableObject.CreateInstance<SkillActionModuleAsset>(); module.actions=actions;
                string originalActions=EditorJsonUtility.ToJson(module);
                ModuleFileJournal.InlineEffects inlineEffects=null;
                SkillParameterSetAsset set=null;
                if(declarations.Length>0)
                {
                    set=ScriptableObject.CreateInstance<SkillParameterSetAsset>(); set.name="参数";set.parameters=declarations;
                    module.parameters=set;
                }
                var call=new CallModuleAction{module=module,bindings=declarations.Select(p=>new ModuleParameterBinding{moduleParameterId=p.Id,callerParameterId=p.Id}).ToArray()};
                try
                {
                    for(int i=1;i<count;i++) list.DeleteArrayElementAtIndex(index+1);
                    list.GetArrayElementAtIndex(index).managedReferenceValue=call;
                    so.ApplyModifiedProperties(); ValidateOwner(so,doc);
                    ActionEffectOwnership.CheckExclusive(module,assetPath,owner,consent);
                    AssetDatabase.CreateAsset(module,assetPath);createdGuid=AssetDatabase.AssetPathToGUID(assetPath);
                    if(set!=null)AssetDatabase.AddObjectToAsset(set,module);
                    using(var moduleData=new SerializedObject(module)){
                        var moved=ActionEffectOwnership.Localize(moduleData.FindProperty("actions"),0,count,module,false);
                        if(moved.Count>0)inlineEffects=ModuleFileJournal.InlineEffects.Capture(module,moved.Keys.ToArray());
                        moduleData.ApplyModifiedPropertiesWithoutUndo();
                        if(moved.Count>0)doc?.Layout?.CopyMovedEffectLayouts(moved.ToDictionary(p=>AssetReferences.Key(p.Key),p=>AssetReferences.Key(p.Value)));
                        foreach(var source in moved.Keys)Undo.DestroyObjectImmediate(source);
                    }
                    // Persist the reference after its previously transient target has a GUID.
                    using var persisted=new SerializedObject(owner);
                    persisted.FindProperty(path).GetArrayElementAtIndex(index).FindPropertyRelative("module").objectReferenceValue=module;
                    persisted.ApplyModifiedPropertiesWithoutUndo(); EditorUtility.SetDirty(owner);
                }
                catch
                {
                    EditCommand.Cleanup(()=>{if(set!=null && !EditorUtility.IsPersistent(set))UnityEngine.Object.DestroyImmediate(set);});
                    EditCommand.Cleanup(()=>{if(module!=null && !EditorUtility.IsPersistent(module))UnityEngine.Object.DestroyImmediate(module);});
                    throw;
                }
                EditorUtility.SetDirty(module); AssetDatabase.SaveAssetIfDirty(module);
                ModuleFileUndo.Track(assetPath,owner,path,index,originalActions,call,operation,inlineEffects,actions.Select(a=>a.Id).ToArray());
                // The journal now owns rollback (including retaining a still-referenced module).
                createdGuid=null;
            }); } catch {
                EditCommand.Cleanup(()=>{
                    if(!string.IsNullOrEmpty(createdGuid) && AssetDatabase.AssetPathToGUID(assetPath)==createdGuid &&
                        !AssetDatabase.DeleteAsset(assetPath))throw new System.IO.IOException("不能回滚模块文件："+assetPath);
                });
                throw;
            }
        }
        public static void Inline(SkillDocument doc,ScriptableObject owner,string path,int index,Func<string,bool> consent=null,Func<string,ActionEffectOwnership.CopyDecision> conflict=null)
        {
            EditCommand.Run(doc,"内联共享模块",owner,so=>
            {
                var list=so.FindProperty(path);var call=list.GetArrayElementAtIndex(index).managedReferenceValue as CallModuleAction;
                if(call?.module==null) throw new InvalidOperationException("缺少模块。");
                var bindings=call.bindings.ToDictionary(b=>b.moduleParameterId,b=>b.callerParameterId);
                var children=call.module.actions;
                using var moduleData=new SerializedObject(call.module);
                var sourceList=moduleData.FindProperty("actions");
                bool preserveScope=false;
                for(int i=0;i<children.Length;i++){
                    var item=sourceList.GetArrayElementAtIndex(i);
                    preserveScope |= EffectParameters(item).Any(id=>!bindings.TryGetValue(id,out var caller) || caller!=id) ||
                        ParameterLinks(item).Any(link=>!bindings.ContainsKey(link.stringValue));
                }
                if(preserveScope)
                {
                    var scoped=new InlineParameterScopeAction{bindings=call.bindings,parameters=call.module.parameters?.parameters??Array.Empty<SkillParameter>(),
                        actions=children.Select(a=>(SkillAction)JsonUtility.FromJson(JsonUtility.ToJson(a),a.GetType())).ToArray()};
                    var slot=list.GetArrayElementAtIndex(index);slot.managedReferenceValue=scoped;SkillActionClipboard.RenewIds(slot);
                    ActionEffectOwnership.Localize(list,index,1,owner,true,consent,confirmReuse:true,conflict:conflict);
                    so.ApplyModifiedProperties();ValidateOwner(so,doc);return;
                }
                list.DeleteArrayElementAtIndex(index);
                for(int i=0;i<children.Length;i++)
                {
                    int n=list.arraySize++;var p=list.GetArrayElementAtIndex(n);var action=children[i];
                    p.managedReferenceValue=JsonUtility.FromJson(JsonUtility.ToJson(action),action.GetType()); SkillActionClipboard.RenewIds(p);
                    foreach(var link in ParameterLinks(p))
                    {
                        if(!bindings.TryGetValue(link.stringValue,out var id)) throw new InvalidOperationException("模块使用私有参数；请先绑定到调用方参数再内联。");
                        link.stringValue=id;
                    }
                    list.MoveArrayElement(n,index+i);
                }
                ActionEffectOwnership.Localize(list,index,children.Length,owner,true,consent,confirmReuse:true,conflict:conflict);
                so.ApplyModifiedProperties(); ValidateOwner(so,doc);
            },true);
        }
    }
}
