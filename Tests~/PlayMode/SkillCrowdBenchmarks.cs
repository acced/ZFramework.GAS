using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using ZFramework.GAS.Unity;
#if UNITY_EDITOR
using UnityEditor;
using Unity.Profiling;
#endif
namespace ZFramework.GAS.Tests
{
    public sealed class SkillCrowdBenchmarks
    {
        [Serializable] private sealed class Report {
            public string unity,cpu,note;public int units,frames,bonesPerUnit,animatedTransformsPerUnit,peakFourthFrameEvaluations,markers;
            public double everyFrameP95,everyFourthP95;public long everyFrameEvaluations,everyFourthEvaluations,everyFrameAllocations,everyFourthAllocations;
            public double[] ascMeanMs=new double[2],manualAnimationMeanMs=new double[2];
        }
#if UNITY_EDITOR
        private static long AllocationCalls(Action work)
        {
            bool previous = UnityEngine.Profiling.Profiler.enabled;
            try
            {
                UnityEngine.Profiling.Profiler.enabled = true;
                using var meter = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC.Alloc", 262144,
                    ProfilerRecorderOptions.StartImmediately | ProfilerRecorderOptions.CollectOnlyOnCurrentThread);
                work(); meter.Stop();
                if (!meter.Valid || meter.Count >= meter.Capacity) return -1;
                long calls = 0;
                for (int i = 0; i < meter.Count; i++) calls += meter.GetSample(i).Count;
                return calls;
            }
            finally { UnityEngine.Profiling.Profiler.enabled = previous; }
        }
#endif
#if UNITY_EDITOR
        private const int Count=200,Frames=120;
        private const float Dt=1f/60;
        private const System.Reflection.BindingFlags Private=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
        private static readonly Action<PlayableAnimationDriver,long,float,ISkillAnimationLodPolicy> Step=
            (Action<PlayableAnimationDriver,long,float,ISkillAnimationLodPolicy>)Delegate.CreateDelegate(typeof(Action<PlayableAnimationDriver,long,float,ISkillAnimationLodPolicy>),
                typeof(PlayableAnimationDriver).GetMethod("ScheduledUpdate",Private));
        private sealed class Cohort : IDisposable{
            private readonly GameObject root=new("200 unit benchmark");
            private readonly PlayableAnimationDriver[] drivers=new PlayableAnimationDriver[Count];
            private readonly AbilitySystemComponent[] owners=new AbilitySystemComponent[Count];
            public int Markers;public Transform Chest;public int Bones;
            public void Init(GameObject knight,AnimationClip clip){
                var definition=new SkillAnimationDefinition("a",1,true,markers:new[]{new AnimationMarker(.2f)});
                var bindings=new[]{new PlayableClipBinding{name="a",clip=clip}};
                for(int i=0;i<Count;i++){
                    var unit=UnityEngine.Object.Instantiate(knight,root.transform);
                    var a=unit.GetComponent<Animator>();a.cullingMode=AnimatorCullingMode.AlwaysAnimate;
                    if(i==0){var bones=unit.GetComponentsInChildren<Transform>(true);Bones=bones.Length-1;Chest=bones.Single(t=>t.name=="Chest");}
                    var d=drivers[i]=new PlayableAnimationDriver(a,bindings);
                    var asc=owners[i]=new AbilitySystemComponent();var spec=asc.GrantAbility(new GameplayAbilityDefinition());asc.TryActivateAbility(spec);
                    asc.StartTask(spec,new AnimationTask(definition,d,durationOverride:1,onMarker:(_,__)=>Markers++));
                }
            }
            public void SetRate(int rate){foreach(var d in drivers)d.UpdateRate=new SkillAnimationRate{mode=SkillAnimationFrequency.EveryNFrames,frames=rate};}
            public void Frame(int f){for(int i=0;i<Count;i++){owners[i].Tick(Dt);Step(drivers[i],f,Dt,null);}}
            public void Stages(int f,Report r,int mode){
                long t=Stopwatch.GetTimestamp();foreach(var owner in owners)owner.Tick(Dt);long mid=Stopwatch.GetTimestamp();
                foreach(var driver in drivers)Step(driver,f,Dt,null);
                double ms=1000d/(Stopwatch.Frequency*Frames);r.ascMeanMs[mode]+=(mid-t)*ms;r.manualAnimationMeanMs[mode]+=(Stopwatch.GetTimestamp()-mid)*ms;
            }
            public void Dispose(){foreach(var o in owners)o?.Clear();foreach(var d in drivers)d?.Dispose();UnityEngine.Object.DestroyImmediate(root);}
        }
#endif
        [UnityTest,Explicit("200 animated units; run on the fixed validation machine."),Category("Performance")]
        public IEnumerator TwoHundredUnitsKeepIdenticalGameplayAtBothRates(){
#if UNITY_EDITOR
            var knight=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GAS Samples/BasicCombat/Prefabs/Knight.prefab");
            Assert.IsNotNull(knight,"Generate BasicCombat before benchmarking.");
            var ctrl=knight.GetComponent<Animator>().runtimeAnimatorController;var clip=Array.Find(ctrl.animationClips,c=>c.name=="Slash");Assert.IsNotNull(clip);
            var report=new Report{units=Count,frames=Frames,unity=Application.unityVersion,cpu=SystemInfo.processorType,
                note="Warm Editor PlayMode, 200 animated knights, 120 frames at 1/60. Each frame ticks gameplay and the scheduler; the latter now advances the native controller itself. Do not call Animator.Update as well: it would evaluate the controller twice. Each rate has a separate staged timing and main-thread GC.Alloc pass. Historical no-handoff controls are retained in SkillPerformanceBaseline.json."};
            using var cohort=new Cohort();cohort.Init(knight,clip);report.bonesPerUnit=cohort.Bones;
            report.animatedTransformsPerUnit=AnimationUtility.GetCurveBindings(clip).Where(b=>b.type==typeof(Transform) && b.path.Length>0).Select(b=>b.path).Distinct().Count();
            Assert.Greater(report.animatedTransformsPerUnit,1);Assert.IsNotNull(cohort.Chest);
            Assert.Greater(AllocationCalls(()=>GC.KeepAlive(new byte[4096])),0,"GC.Alloc must detect a control allocation");
            for(int mode=0;mode<2;mode++){
                int rate=mode==0?1:4;cohort.SetRate(rate);for(int f=0;f<60;f++)cohort.Frame(f);
                var times=new double[Frames];int markers=cohort.Markers;long evaluations=SkillAnimationManager.EvaluationCount;
                for(int f=0;f<Frames;f++){
                    long before=SkillAnimationManager.EvaluationCount,t=Stopwatch.GetTimestamp();cohort.Frame(f);
                    times[f]=(Stopwatch.GetTimestamp()-t)*1000d/Stopwatch.Frequency;
                    if(rate==4)report.peakFourthFrameEvaluations=Math.Max(report.peakFourthFrameEvaluations,(int)(SkillAnimationManager.EvaluationCount-before));
                }
                long count=SkillAnimationManager.EvaluationCount-evaluations;report.markers=cohort.Markers-markers;
                Assert.AreEqual(Count*2,report.markers);Assert.AreEqual(Count*Frames/rate,count);Array.Sort(times);
                for(int f=0;f<Frames;f++)cohort.Stages(f,report,mode);
                long allocs=AllocationCalls(()=>{for(int f=0;f<Frames;f++)cohort.Frame(f);});Assert.AreEqual(0,allocs);
                if(mode==0){report.everyFrameP95=times[113];report.everyFrameEvaluations=count;report.everyFrameAllocations=allocs;}
                else{report.everyFourthP95=times[113];report.everyFourthEvaluations=count;report.everyFourthAllocations=allocs;}
            }
            Assert.AreEqual(Count/4,report.peakFourthFrameEvaluations);
            string path=Path.Combine(Directory.GetCurrentDirectory(),"GASValidation~/skill-runtime.json");Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllText(path,JsonUtility.ToJson(report,true));
#endif
            yield return null;
        }
    }
}
