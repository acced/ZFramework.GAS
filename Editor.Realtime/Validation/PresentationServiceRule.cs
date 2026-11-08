using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    public sealed class PresentationServiceRule : ISkillValidationRule
    {
        public string Id => "GAS_PRESENTATION";
        public void Validate(ValidationContext context, ScriptableObject asset, List<Issue> issues)
        {
            if(asset is SkillAnimationAsset tracks && tracks.runtimeTracks!=null)
                for(int i=0;i<tracks.runtimeTracks.Length;i++){
                    string service=tracks.runtimeTracks[i] switch{CameraShakeTrack=>"ISkillCameraService",HitStopTrack or TimeScaleTrack=>"ISkillTimeService",LookAtTrack=>"ISkillRigService",_=>null};
                    if(service==null)continue;
                    var prefab=context.Document.PreviewPrefab;
                    if(prefab!=null && prefab.GetComponentsInChildren<MonoBehaviour>(true).Any(b=>b!=null && b.GetType().GetInterfaces().Any(t=>t.Name==service)))continue;
                    if(service=="ISkillRigService" && prefab!=null && prefab.GetComponentInChildren<SkillAnimator>(true)?.Backend is PlayableAnimationBackend)continue;
                    var path=$"runtimeTracks.Array.data[{i}]";
                    context.Add(issues,Id,asset,path,"此轨道需要 "+service+"；可由场景宿主在施放时提供，缺少服务时不播放。",IssueSeverity.Info,track:"@field",elementId:path);
                }
        }
    }
}
