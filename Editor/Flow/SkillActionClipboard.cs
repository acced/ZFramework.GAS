using System;
using UnityEditor;
using UnityEngine;
using ZFramework.GAS.Unity;
namespace ZFramework.GAS.Editor
{
    internal static class SkillActionClipboard
    {
        private static Type _type;
        private static string _json;
        public static bool Available => _json!=null;
        public static void Copy(SkillAction action)
        { _type=action?.GetType(); _json=action==null?null:JsonUtility.ToJson(action); }
        public static void Paste(SerializedProperty list,int index)=>Insert(list,index,_type,_json);
        public static void Duplicate(SerializedProperty list,int index,SkillAction action)
            =>Insert(list,index,action?.GetType(),action==null?null:JsonUtility.ToJson(action));
        private static void Insert(SerializedProperty list,int index,Type type,string json)
        {
            if(type==null || json==null)return;
            int n=list.arraySize++;var p=list.GetArrayElementAtIndex(n);
            p.managedReferenceValue=JsonUtility.FromJson(json,type);
            try{RenewIds(p);ActionEffectOwnership.RequireShared(list,n,1,(ScriptableObject)list.serializedObject.targetObject);}
            catch{list.DeleteArrayElementAtIndex(n);throw;}
            if(index<n)list.MoveArrayElement(n,index);
        }
        public static void RenewIds(SerializedProperty p)
        {
            if(p.propertyType!=SerializedPropertyType.ManagedReference || p.managedReferenceValue is not SkillAction action) return;
            p.FindPropertyRelative("id").stringValue=Guid.NewGuid().ToString("N");
            foreach(var field in SkillActionFields.For(action.GetType()).Branches)
            { var array=p.FindPropertyRelative(field.Name); for(int i=0;i<array.arraySize;i++) RenewIds(array.GetArrayElementAtIndex(i)); }
        }
    }
}
