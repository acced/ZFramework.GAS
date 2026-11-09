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
            if(effects.Count>0)throw new InvalidOperationException("跨文件粘贴前，请先外置为独立效果："+
                string.Join("、",effects.Select(e=>$"{e.name} ({AssetReferences.Key(e)})")));
        }
    }
}
