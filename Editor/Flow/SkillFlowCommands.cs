using System;
using System.Collections.Generic;
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
                from.DeleteArrayElementAtIndex(index); so.ApplyModifiedProperties(); ValidateOwner(so);
            },true);
        }
        internal static void ValidateOwner(SerializedObject so)
        {
            var ids = new HashSet<string>();
            if (SkillEditorExtensions.Supports(so.targetObject))
                foreach (var entry in SkillEditorExtensions.Entries(so)) ValidateStructure(entry.Actions, ids);
            else ValidateStructure(so.FindProperty("activationActions"), ids);
        }
        public static void ValidateStructure(SerializedProperty list,HashSet<string> ids=null)
        {
            if(list==null)return;
            ids??=new HashSet<string>();
            for(int i=0;i<list.arraySize;i++) Visit(list.GetArrayElementAtIndex(i).managedReferenceValue as SkillAction,false,1,ids);
        }
        internal static void Visit(SkillAction action,bool detached,int depth,HashSet<string> scope)
        {
            if(action==null)return;
            if(depth>8)throw new InvalidOperationException("动作嵌套不能超过八层。");
            if(string.IsNullOrEmpty(action.Id) || !scope.Add(action.Id))throw new SkillActionException(action.Id,"同一资产中的动作对象或 ID 重复。请复制而不是复用实例。");
            if(detached)
            {
                // A draft may lack a prefab or effect. Enforce runtime ownership even while its fields are incomplete.
                try{new SkillActionBuildContext().BuildDetached(new[]{action});}
                catch(InvalidOperationException e){for(Exception inner=e;inner!=null;inner=inner.InnerException)if(inner is SkillOwnershipException)throw new InvalidOperationException(inner.Message,inner);}
            }
            foreach(var field in SkillActionFields.For(action.GetType()).Branches)
                if(field.GetValue(action) is SkillAction[] children) foreach(var child in children)
                    Visit(child,detached || action.IsDetachedBranch(field.Name),depth+1,scope);
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
                    if(kind==null || kind.intValue==(int)MagnitudeKind.Parameter)ids.Add(cursor.stringValue);
                }
            }
            Visit(effect);return ids;
        }
    }
}
