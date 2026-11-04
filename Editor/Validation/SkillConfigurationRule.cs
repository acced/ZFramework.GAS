using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    public sealed class SkillConfigurationRule : ISkillValidationRule
    {
        public string Id => "GAS_ACTION";
        public void Validate(ValidationContext context, ScriptableObject asset, List<Issue> issues)
        {
            try
            {
                if (asset is GameplayAbilityAsset skill) { skill.ResolveParameters(1); skill.BuildActions(); }
                else if (asset is SkillAnimationAsset animation) { _ = animation.Definition; }
                else if (asset is SkillActionModuleAsset module) module.Build();
            }
            catch (Exception error)
            {
                string path = "", actionId = (error as SkillActionException)?.ActionId;
                using var so = new SerializedObject(asset); var p = so.GetIterator();
                while (!string.IsNullOrEmpty(actionId) && p.Next(true))
                    if (p.name == "id" && p.propertyType == SerializedPropertyType.String && p.stringValue == actionId)
                    { path = p.propertyPath.Substring(0, p.propertyPath.LastIndexOf('.')); break; }
                context.Add(issues, Id, asset, path, error.Message, track: path.Length > 0 ? "@field" : null, elementId: path);
            }
            var known = new HashSet<string>();
            foreach (var p in SkillNumberDrawer.Parameters(asset is SkillActionModuleAsset ? asset : context.Document.Root)) if (p != null) known.Add(p.Id);
            using var serialized = new SerializedObject(asset); var field = serialized.GetIterator();
            if(asset is SkillAnimationAsset tracks && tracks.runtimeTracks!=null)
                for(int i=0;i<tracks.runtimeTracks.Length;i++){
                    string service=tracks.runtimeTracks[i] switch{CameraShakeTrack=>"ISkillCameraService",HitStopTrack or TimeScaleTrack=>"ISkillTimeService",LookAtTrack=>"ISkillRigService",_=>null};
                    if(service==null)continue;
                    var prefab=context.Document.PreviewPrefab;
                    if(prefab!=null && prefab.GetComponentsInChildren<MonoBehaviour>(true).Any(b=>b!=null && b.GetType().GetInterfaces().Any(t=>t.Name==service)))continue;
                    if(service=="ISkillRigService" && prefab!=null && prefab.GetComponentInChildren<SkillAnimator>(true)?.Backend is PlayableAnimationBackend)continue;
                    var path=$"runtimeTracks.Array.data[{i}]";
                    context.Add(issues,"GAS_PRESENTATION",asset,path,"此轨道需要 "+service+"；可由场景宿主在施放时提供，缺少服务时不播放。",IssueSeverity.Info,track:"@field",elementId:path);
                }
            bool HasParameter(SerializedProperty property,string id)
            {
                var local=SkillNumberDrawer.LocalScope(property);
                return local!=null?local.Any(p=>p!=null && p.Id==id):known.Contains(id);
            }
            while (field.Next(true))
            {
                // Resolve every effect reference at its use site (including cost/cooldown and legacy
                // markers). A shared effect can legitimately run with different module-local parameters.
                if(asset is not GameplayEffectAsset && field.propertyType==SerializedPropertyType.ObjectReference && field.objectReferenceValue is GameplayEffectAsset effect){
                    foreach(string id in SkillFlowCommands.RequiredEffectParameters(effect))
                        if(!HasParameter(field,id))context.Add(issues,Id,asset,field.propertyPath,"引用的效果需要参数 "+id+"，当前作用域没有声明。",track:"@field",elementId:field.propertyPath);
                }
                if(asset is GameplayEffectAsset)continue;
                if (field.propertyType != SerializedPropertyType.String || string.IsNullOrEmpty(field.stringValue)) continue;
                if (field.name != "parameterId" && field.name != "booleanParameterId" && field.name != "callerParameterId") continue;
                int dot = field.propertyPath.LastIndexOf('.');
                var parent = dot >= 0 ? serialized.FindProperty(field.propertyPath.Substring(0, dot)) : null;
                var kind = parent?.FindPropertyRelative("kind");
                // Magnitude fields are inactive for fixed/source-only values.
                if (parent?.FindPropertyRelative("captureAttributeId") != null && kind != null && kind.enumValueIndex != (int)MagnitudeKind.Parameter && kind.enumValueIndex != (int)MagnitudeKind.Calculation) continue;
                if (!HasParameter(field,field.stringValue)) context.Add(issues, Id, asset, field.propertyPath, "引用的技能参数已删除，或不在当前参数集中。请重新绑定。", track:"@field", elementId:field.propertyPath);
            }
        }
    }
}
