using System;
using UnityEditor;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    // Resolve only the selected query, at edit/selection time. Inspecting a radius must not run a cast.
    internal static class SelectedActionRange
    {
        internal static TargetShape? Read(SkillDocument doc,int level,out SkillOrigin origin,out string note)
        {
            origin=SkillOrigin.Caster; note=null;
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
            TargetQuery query=null;
            using(var field=action.Copy()) {
                var end=field.GetEndProperty();
                if(field.Next(true)) do {
                    if(SerializedProperty.EqualContents(field,end))break;
                    if(field.type!=nameof(TargetQuery))continue;
                    if(field.boxedValue is TargetQuery found){query=found;break;}
                } while(field.Next(false));
            }
            if(query == null) return null;
            if(query.kind == TargetQueryKind.Project) { note = "目标由项目提供；几何预演不会运行项目逻辑。"; return null; }
            if(!TargetQuery.IsVolume(query.kind)) return null;
            var def=query.Build(); origin=def.EffectiveOrigin;
            if(origin==SkillOrigin.EventPosition) {
                note="范围原点由实际事件提供。"; return null;
            }
            var values=new EffectEvaluationContext(level,doc.Root.ResolveParameters(level,null));
            return def.ResolveShape(values);
        }
    }
}
