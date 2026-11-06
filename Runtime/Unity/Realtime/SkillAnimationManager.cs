using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace ZFramework.GAS.Unity
{
    public enum SkillAnimationFrequency { EveryFrame, EveryNFrames, EveryNSeconds }
    [Serializable] public struct SkillAnimationRate
    {
        public SkillAnimationFrequency mode;
        [Min(1)] public int frames;
        [Min(.001f)] public float seconds;
        public int phaseOffset;
    }
    public interface ISkillAnimationLodPolicy { SkillAnimationRate Rate(Animator animator); }

    // Only animation sampling is scheduled here. Markers, windows and actions remain on ASC.Tick.
    [DefaultExecutionOrder(-10000),AddComponentMenu("")]
    public sealed class SkillAnimationManager : MonoBehaviour
    {
        private static SkillAnimationManager _instance;
        private readonly List<PlayableAnimationDriver> _drivers=new();
        public static ISkillAnimationLodPolicy Policy {get;set;}
        public static long EvaluationCount {get;internal set;}
        #if UNITY_ASSERTIONS
        private static int _manualSamples;
        internal static bool Sampling => NativeSampling || _manualSamples > 0;
        internal static void BeginManual() => _manualSamples++;
        internal static void EndManual() => _manualSamples--;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void InstallHooks()=>SetPoseHooks(true);
        #else
        internal static bool Sampling=>false;
        [System.Diagnostics.Conditional("UNITY_ASSERTIONS")] internal static void BeginManual(){}
        [System.Diagnostics.Conditional("UNITY_ASSERTIONS")] internal static void EndManual(){}
        #endif
        internal static void Register(PlayableAnimationDriver driver)
        {
            if(!Application.isPlaying) return;
            if(_instance==null)
            {
                var go=new GameObject("GAS Animation Scheduler"){hideFlags=HideFlags.HideInHierarchy};
                _instance=go.AddComponent<SkillAnimationManager>();DontDestroyOnLoad(go);
            }
            _instance._drivers.Add(driver);
        }
        internal static void Unregister(PlayableAnimationDriver driver)
        {
            if(_instance==null) return;
            int i=_instance._drivers.IndexOf(driver); if(i>=0)_instance._drivers[i]=null;
        }
        #if UNITY_ASSERTIONS
        // Only assertion builds mark the native sampling window.
        private struct BeforeAnimation { }
        private struct AfterAnimation { }
        internal static bool NativeSampling {get;private set;}
        private static void EndNative(){NativeSampling=false;}
        private static void SetPoseHooks(bool install)
        {
            var loop=PlayerLoop.GetCurrentPlayerLoop();
            bool Edit(ref PlayerLoopSystem group){
                var children=group.subSystemList;if(children==null)return false;
                bool changed=false;var next=new List<PlayerLoopSystem>(children.Length+1);
                foreach(var entry in children){
                    if(entry.type==typeof(BeforeAnimation) || entry.type==typeof(AfterAnimation)){changed=true;continue;}
                    var child=entry;changed|=Edit(ref child);
                    bool fixedStep=child.type==typeof(FixedUpdate.DirectorFixedUpdate);
                    if(install && (fixedStep || child.type==typeof(PreLateUpdate.DirectorUpdateAnimationBegin))){
                        next.Add(new PlayerLoopSystem{type=typeof(BeforeAnimation),updateDelegate=BeginNative});changed=true;
                    }
                    next.Add(child);
                    if(install && (fixedStep || child.type==typeof(PreLateUpdate.DirectorUpdateAnimationEnd))){
                        next.Add(new PlayerLoopSystem{type=typeof(AfterAnimation),updateDelegate=EndNative});changed=true;
                    }
                }
                if(changed)group.subSystemList=next.ToArray();return changed;
            }
            if(Edit(ref loop))PlayerLoop.SetPlayerLoop(loop);
        }
        private static void BeginNative()=>NativeSampling=true;
        #endif
        private void LateUpdate()
        {
            int count=_drivers.Count;
            for(int i=0;i<count;i++)
            {
                var driver=_drivers[i]; if(driver==null) continue;
                try
                {
                    if(!driver.IsValid){driver.Dispose();continue;}
                    driver.ScheduledUpdate(Time.frameCount,Time.deltaTime,Policy);
                }
                catch(Exception ex) { Debug.LogException(ex); }
            }
            _drivers.RemoveAll(static d=>d==null);
        }
        private void OnDestroy(){if(_instance==this){_instance=null; foreach(var d in _drivers)d?.Dispose();_drivers.Clear();}}
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetPolicy(){
#if UNITY_ASSERTIONS
            SetPoseHooks(false);NativeSampling=false;_manualSamples=0;
#endif
            Policy=null;
        }
    }
}
