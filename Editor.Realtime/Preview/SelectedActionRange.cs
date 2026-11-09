using System;
using UnityEditor;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    // Resolve only the selected query, at edit/selection time. Inspecting a radius must not run a cast.
    internal static class SelectedActionRange
    {
        internal static TargetShape? Read(SkillDocument doc,int level,out SkillSpawnPosition origin,out string note)
        {
            origin=SkillSpawnPosition.Caster; note=null;
            var sel=doc.Selection;
            if(doc.Root==null || sel.Asset==null || sel.ElementId==null)return null;
            var so=doc.Serialized(sel.Asset);
            SerializedProperty action=null;
            if(sel.Track=="@field") {
                for(string path=sel.ElementId;path.Length>0;) {
                    var field=so.FindProperty(path);
                    if(field?.propertyType==SerializedPropertyType.ManagedReference && field.managedReferenceValue is SkillAction){action=field;break;}
                    int dot=path.LastIndexOf('.'); if(dot<0)break; path=path.Substring(0,dot);
                }
            } else if(sel.Track=="markers" && sel.Asset is SkillAnimationAsset) {
                var list=TimelineCommands.Find(so,"markers",sel.ElementId)?.FindPropertyRelative("actions");
                if(list?.arraySize==1)action=list.GetArrayElementAtIndex(0);
            }
            if(action==null)return null;
            // A projectile's collision field is a filter; its query radius is not its hit radius.
            TargetQuery query=null;
            using(var field=action.Copy()) {
                var end=field.GetEndProperty();
                if(field.Next(true)) do {
                    if(SerializedProperty.EqualContents(field,end))break;
                    if(field.type!=nameof(TargetQuery) || (action.managedReferenceValue is LaunchProjectileAction && field.name=="collision"))continue;
                    if(field.boxedValue is TargetQuery found){query=found;break;}
                } while(field.Next(false));
            }
            if(query == null) return null;
            if(query.kind == TargetQueryKind.Project) { note = "目标由项目提供；几何预演不会运行项目逻辑。"; return null; }
            if(!TargetQuery.IsVolume(query.kind)) return null;
            var def=query.Build(); origin=def.EffectiveOrigin;
            if(origin==SkillSpawnPosition.HitPoint || origin==SkillSpawnPosition.EventPosition) {
                note="范围原点由实际命中或事件提供。"; return null;
            }
            var values=new EffectEvaluationContext(level,doc.Root.ResolveParameters(level,null));
            if(sel.Asset is SkillActionModuleAsset module){
                // A module selected in isolation can have multiple different callers. Never guess a call's bindings.
                EffectEvaluationContext found=null; int callers=0;
                void Search(SerializedObject owner,EffectEvaluationContext parent,int depth){
                    if(owner==null || depth>8 || callers>1)return;
                    using var it=owner.GetIterator();
                    while(it.Next(true)){
                        if(it.propertyType!=SerializedPropertyType.ManagedReference || it.managedReferenceValue is not CallModuleAction call || call.module==null)continue;
                        var caller=InlineScope(owner,it.propertyPath,parent);
                        var child=caller.WithParameters(ParameterScopeAction.ResolveParameters(caller,call.module.parameters?.parameters,call.bindings));
                        if(call.module==module){found=child;callers++;}else Search(doc.Serialized(call.module),child,depth+1);
                        if(callers>1)break;
                    }
                }
                Search(doc.Serialized(doc.Root),values,1);
                if(doc.Root.Animation()!=null)Search(doc.Serialized(doc.Root.Animation()),values,1);
                if(callers==1)values=found;
                else {values=values.WithParameters(ParameterScopeAction.ResolveParameters(values,module.parameters?.parameters,Array.Empty<ModuleParameterBinding>()));
                    note="按模块自身默认参数显示范围。";}
            }
            return def.ResolveShape(InlineScope(so,action.propertyPath,values));
        }

        private static EffectEvaluationContext InlineScope(SerializedObject owner,string path,EffectEvaluationContext values)
        {
            for(int at=path.IndexOf(']');at>=0 && at+1<path.Length;at=path.IndexOf(']',at+1)){
                var parent=owner.FindProperty(path.Substring(0,at+1));
                if(parent?.propertyType==SerializedPropertyType.ManagedReference && parent.managedReferenceValue is InlineParameterScopeAction scope)
                    values=values.WithParameters(ParameterScopeAction.ResolveParameters(values,scope.parameters,scope.bindings));
            }
            return values;
        }
    }
}
