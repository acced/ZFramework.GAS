using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    internal static class ActionEffectOwnership
    {
        internal static HashSet<GameplayEffectAsset> PrivateEffects(SerializedProperty list,int index,int count,string destination)
        {
            var seen=new HashSet<GameplayEffectAsset>();var result=new HashSet<GameplayEffectAsset>();
            void Visit(GameplayEffectAsset effect) {
                if(effect==null || !seen.Add(effect))return;
                string path=AssetDatabase.GetAssetPath(effect);
                // IsSubAsset can still be false for a just-added, unsaved subasset.
                if(path!=destination && !string.IsNullOrEmpty(path) && AssetDatabase.LoadMainAssetAtPath(path)!=effect)result.Add(effect);
                foreach(var child in AssetReferences.Children(effect).OfType<GameplayEffectAsset>())Visit(child);
            }
            for(int i=0;i<count;i++) {
                using var p=list.GetArrayElementAtIndex(index+i).Copy();using var end=p.GetEndProperty();
                while(p.Next(true) && !SerializedProperty.EqualContents(p,end))
                    if(p.propertyType==SerializedPropertyType.ObjectReference && p.objectReferenceValue is GameplayEffectAsset e)Visit(e);
            }
            return result;
        }
        internal static void RequireShared(SerializedProperty list,int index,int count,ScriptableObject destination)
        {
            var effects=PrivateEffects(list,index,count,AssetDatabase.GetAssetPath(destination));
            if(effects.Count>0)throw new InvalidOperationException("跨文件粘贴／内联前，请先外置为独立效果："+
                string.Join("、",effects.Select(e=>$"{e.name} ({AssetReferences.Key(e)})")));
        }
        internal static HashSet<GameplayEffectAsset> CheckExclusive(SerializedProperty list,int index,int count,string destination)
        {
            var owner=list.serializedObject.targetObject;
            string source=AssetDatabase.GetAssetPath(owner);
            var moving=PrivateEffects(list,index,count,destination);
            foreach(var effect in moving)
                if(AssetDatabase.GetAssetPath(effect)!=source)throw new InvalidOperationException("其他文件的内嵌效果必须先外置："+effect.name);
            var skip=new HashSet<UnityEngine.Object>(moving.Cast<UnityEngine.Object>()){owner};
            string hit=SkillReferenceGuard.Find(source,moving.Cast<UnityEngine.Object>(),skip);
            using var p=list.serializedObject.GetIterator();bool descend=true;
            while(hit==null && p.Next(descend)) {
                descend=true;
                for(int i=0;i<count;i++)if(p.propertyPath==list.GetArrayElementAtIndex(index+i).propertyPath){descend=false;break;}
                if(descend && p.propertyType==SerializedPropertyType.ObjectReference && p.objectReferenceValue is GameplayEffectAsset e && moving.Contains(e))hit=owner.name+" / "+p.propertyPath;
            }
            if(hit!=null)throw new InvalidOperationException("选中动作的私有效果仍被 "+hit+" 共用；请先外置为独立效果。");
            return moving;
        }
        internal static bool Confirm(string message)=>!Application.isBatchMode && EditorUtility.DisplayDialog("不可撤销的资产操作",message,"执行并保存","取消");
    }
}
