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
#if UNITY_EDITOR
        private bool _settingsDirty;
        private string _builtSettings;
        private string Settings()=>backend==null?null:backend.GetType().FullName+":"+JsonUtility.ToJson(backend);
#endif
        public void PushOverride(SkillClipOverrideSet set)
        {
            if(set==null)return;clipOverrides.Add(set);
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
        public void SetBackend(AnimationBackend value) { Release(); backend = value; }
        private void Release()
        {
            var driver = _driver; _driver = null; if (driver == null) return;
            Owners.Remove(driver);
            try { if (driver is IBlendingAnimationDriver blend) blend.Stop(0); else driver.Stop(); }
            finally { if (driver is IDisposable resource) resource.Dispose(); }
        }
        private void OnDisable() => Release();
        private void OnDestroy() => Release();
#if UNITY_EDITOR
        private void OnValidate()=>_settingsDirty=true;
#endif
    }
}
