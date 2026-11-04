using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace ZFramework.GAS.Unity
{
    [DisallowMultipleComponent, AddComponentMenu("ZFramework GAS/Skill Animator")]
    public sealed class SkillAnimator : MonoBehaviour
    {
        [SerializeReference] private AnimationBackend backend = new AnimatorBackend();
        [SerializeField] private List<SkillClipOverrideSet> clipOverrides = new();
        private static readonly ConditionalWeakTable<IAnimationDriver,SkillAnimator> Owners=new();
        private IAnimationDriver _driver;
        private PlayableAnimationDriver _retiring;
        private AnimationBackend _retiringBackend;
        private SkillBlend _exitBlend;
#if UNITY_EDITOR
        private bool _settingsDirty;
        private string _builtSettings;
        private string Settings()=>backend==null?null:backend.GetType().FullName+":"+JsonUtility.ToJson(backend);
#endif
        public void PushOverride(SkillClipOverrideSet set)
        {
            if(set==null)return;clipOverrides.Add(set);
            if(_driver is PlayableAnimationDriver playable)WarmOverrides(playable,set);
        }
        private static void WarmOverrides(PlayableAnimationDriver driver,SkillClipOverrideSet set)
        {if(set!=null && set.clips!=null)foreach(var item in set.clips)if(item.clip!=null)driver.Prewarm(item.clip);}
        // Loading/spawn code may also call this before enabling input (including dynamically loaded variants).
        public void Prewarm(SkillClipOverrideSet variant=null)
        {
            if(!isActiveAndEnabled || backend is not PlayableAnimationBackend configured || !configured.Validate(gameObject,out _))return;
            var driver=(PlayableAnimationDriver)Driver;
            foreach(var clip in configured.clips)driver.Prewarm(clip.clip);
            foreach(var set in clipOverrides)WarmOverrides(driver,set);
            WarmOverrides(driver,variant);
        }
        public bool PopOverride(SkillClipOverrideSet set)
        { int at=clipOverrides.LastIndexOf(set); if(at<0) return false; clipOverrides.RemoveAt(at); return true; }
        internal static string ResolveClip(IAnimationDriver driver,string clip,SkillClipOverrideSet variant)
        {
            SkillClipOverrideSet.Entry e=default;
            bool found=false, acceptsClip=driver is IClipOverrideDriver;
            if(Owners.TryGetValue(driver,out var host) && host!=null)
                for(int i=host.clipOverrides.Count-1;i>=0 && !found;i--) found=host.clipOverrides[i]!=null && host.clipOverrides[i].TryGet(clip,out e,acceptsClip);
            if(!found && variant!=null) found=variant.TryGet(clip,out e,acceptsClip);
            if(driver is IClipOverrideDriver playable) playable.SetNextClip(found?e.clip:null);
            return found && !string.IsNullOrEmpty(e.replacement)?e.replacement:clip;
        }
        internal static void RecordExit(IAnimationDriver driver,in SkillBlend blend){
            if(driver is PlayableAnimationDriver playable)playable.SetExitBlend(blend);
            if(Owners.TryGetValue(driver,out var host) && host!=null)host._exitBlend=blend;
        }
        public AnimationBackend Backend => backend;
        public IAnimationDriver Driver
        {
            get
            {
                if(!isActiveAndEnabled)throw new InvalidOperationException("SkillAnimator 未启用，不能播放技能动画。");
#if UNITY_EDITOR
                // OnValidate may run on Unity's loading thread. Compare only backend
                // settings on the next request; inspector edits do not interrupt a current cast.
                if(_settingsDirty){_settingsDirty=false;if(_driver!=null && _builtSettings!=Settings())Release();}
#endif
                if(_retiring!=null && ReferenceEquals(_retiringBackend,backend)
#if UNITY_EDITOR
                    && _builtSettings==Settings()
#endif
                    && _retiring.Reclaim()){
                    _driver=_retiring;Owners.Add(_driver,this);_retiring=null;_retiringBackend=null;
                }else FinishRetirement();
                if (_driver is IAnimationDriverLifetime lifetime && !lifetime.IsValid) Release();
                if (_driver != null) return _driver;
                if (backend == null) throw new InvalidOperationException("SkillAnimator 未设置动画后端。");
                if (!backend.Validate(gameObject, out string error)) throw new InvalidOperationException(error);
                _driver=backend.CreateDriver(gameObject); Owners.Remove(_driver); Owners.Add(_driver,this);
#if UNITY_EDITOR
                _builtSettings=Settings();
#endif
                return _driver;
            }
        }
        public void SetBackend(AnimationBackend value)
        {
            // An explicit reconfiguration can mutate a SerializeReference backend in place.
            // Never reclaim a retiring graph built before this call, including in a player.
            Release(true);
            _retiringBackend=null;
            backend = value;
        }
        private void FinishRetirement(){var previous=_retiring;_retiring=null;_retiringBackend=null;previous?.Dispose();}
        private void Release(bool allowFade=false)
        {
            var driver = _driver; _driver = null; if(driver!=null) Owners.Remove(driver);
            var exit=_exitBlend;_exitBlend=default;
            if(allowFade && Application.isPlaying && gameObject.activeInHierarchy && driver is PlayableAnimationDriver playable && playable.Retire()){
                _retiring=playable;_retiringBackend=backend;return;
            }
            try {
                if(allowFade && Application.isPlaying && gameObject.activeInHierarchy && driver is not PlayableAnimationDriver && exit.duration>0){
                    if(driver is IAdvancedBlendingDriver advanced)advanced.Stop(exit);
                    else if(driver is IBlendingAnimationDriver blending)blending.Stop(exit.duration);
                    else driver?.Stop();
                }else driver?.Stop();
            } finally { if (driver is IDisposable disposable) disposable.Dispose(); }
        }
        private void OnDisable(){if(!gameObject.activeInHierarchy)FinishRetirement();Release(true);}
        private void OnDestroy(){FinishRetirement();Release();}
#if UNITY_EDITOR
        private void OnValidate()=>_settingsDirty=true;
#endif
#if UNITY_EDITOR
        // Optional authoring metadata for tools. Playback resolves the actual clip independently.
        public void Bake()
        {
            if(this==null || backend is not PlayableAnimationBackend playable || playable.clips==null)return;
            bool changed=false;foreach(var binding in playable.clips) if(binding!=null)changed|=binding.BakePosePaths();
            if(changed) UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}
