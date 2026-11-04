using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using ZFramework.GAS.Unity;
namespace ZFramework.GAS.Editor
{
    internal static class SkillActionClipboard
    {
        private static Type _type;
        private static string _json;
        private static readonly Dictionary<Type,FieldInfo[]> Fields=new();
        public static FieldInfo[] Branches(Type type)
        {
            if (!Fields.TryGetValue(type,out var fields))
            {
                var result=new List<FieldInfo>();
                for(var current=type;current!=null && current!=typeof(object);current=current.BaseType)
                    result.AddRange(current.GetFields(BindingFlags.DeclaredOnly|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance)
                        .Where(f=>f.FieldType==typeof(SkillAction[]) && f.IsDefined(typeof(SerializeReference),true)));
                Fields[type]=fields=result.ToArray();
            }
            return fields;
        }
        public static bool Available => _json!=null;
        public static void Copy(SkillAction action)
        { _type=action?.GetType(); _json=action==null?null:JsonUtility.ToJson(action); }
        public static void Paste(SerializedProperty list,int index,Func<string,bool> consent=null,Func<string,ActionEffectOwnership.CopyDecision> conflict=null)
            =>Insert(list,index,_type,_json,consent,conflict);
        public static void Duplicate(SerializedProperty list,int index,SkillAction action,Func<string,bool> consent=null)
            =>Insert(list,index,action?.GetType(),action==null?null:JsonUtility.ToJson(action),consent,null);
        private static void Insert(SerializedProperty list,int index,Type type,string json,Func<string,bool> consent,Func<string,ActionEffectOwnership.CopyDecision> conflict)
        {
            if(type==null || json==null)return;
            int n=list.arraySize++; var p=list.GetArrayElementAtIndex(n);
            p.managedReferenceValue=JsonUtility.FromJson(json,type);
            try{RenewIds(p);ActionEffectOwnership.Localize(list,n,1,(ScriptableObject)list.serializedObject.targetObject,true,consent,conflict:conflict);}
            catch{list.DeleteArrayElementAtIndex(n);throw;}
            if(index<n)list.MoveArrayElement(n,index);
        }
        public static void RenewIds(SerializedProperty p)
        {
            if(p.propertyType!=SerializedPropertyType.ManagedReference || p.managedReferenceValue is not SkillAction action) return;
            p.FindPropertyRelative("id").stringValue=Guid.NewGuid().ToString("N");
            foreach(var field in Branches(action.GetType()))
            { var array=p.FindPropertyRelative(field.Name); for(int i=0;i<array.arraySize;i++) RenewIds(array.GetArrayElementAtIndex(i)); }
        }
    }
}
