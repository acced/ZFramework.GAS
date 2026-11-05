using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ZFramework.GAS.Editor
{
    // Authoring scope is GAS assets only. Projects own all other references.
    internal static class SkillReferenceGuard
    {
        internal static IEnumerable<string> Files()=>AssetDatabase.FindAssets("t:GameplayAbilityAsset t:GameplayEffectAsset t:SkillAnimationAsset t:SkillActionModuleAsset")
            .Select(AssetDatabase.GUIDToAssetPath).Distinct();
        internal static string Find(string targetFile,IEnumerable<Object> objects,ICollection<Object> skip=null)
        {
            var targets=new HashSet<Object>(objects);if(targets.Count==0)return null;
            foreach(string path in Files()) {
                if(skip==null && path==targetFile)continue;
                if(path!=targetFile && !AssetDatabase.GetDependencies(path).Contains(targetFile))continue;
                foreach(var owner in AssetDatabase.LoadAllAssetsAtPath(path)) {
                    if(owner==null || skip?.Contains(owner)==true)continue;
                    using var so=new SerializedObject(owner);using var p=so.GetIterator();
                    while(p.Next(true))if(p.propertyType==SerializedPropertyType.ObjectReference && targets.Contains(p.objectReferenceValue))
                        return path+" / "+owner.name+" / "+p.propertyPath;
                }
            }
            return null;
        }
    }
}
