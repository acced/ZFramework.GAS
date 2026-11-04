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
        [Serializable] private sealed class Report {public string unity,cpu,note;public int units,frames,bonesPerUnit,peakFourthFrameEvaluations,animatedTransformsPerUnit;public double everyFrameP95,everyFourthP95;public long everyFrameBytes,everyFourthBytes,everyFrameAllocations,everyFourthAllocations,everyFrameEvaluations,everyFourthEvaluations;public int markers;public Run[] runs=new Run[4];public double[] pairedMedianMs=new double[2];}
        [Serializable] private sealed class Run{
            public bool handoff;public int rate,markers,watchedBones,connectedHandoffJobs;public long evaluations;public double p95;
            // Mean ms per frame for all 200 units in the separate staged pass.
            public double observe,animator,asc,scheduled;
            // Main-thread million CPU cycles per measured frame; excludes descheduling and other worker threads.
            public double observeMCycles=-1,animatorMCycles=-1,ascMCycles=-1,scheduledMCycles=-1;
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
        private static readonly Action<PlayableAnimationDriver,bool> Observe=(Action<PlayableAnimationDriver,bool>)Delegate.CreateDelegate(
            typeof(Action<PlayableAnimationDriver,bool>),typeof(PlayableAnimationDriver).GetMethod("ObserveBeforeAnimator",Private));
#if UNITY_EDITOR_WIN
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool QueryThreadCycleTime(IntPtr thread,out ulong cycles);
        private static ulong ReadCycles(IntPtr thread)=>QueryThreadCycleTime(thread,out ulong cycles)?cycles:throw new InvalidOperationException("Unable to read benchmark thread CPU cycles.");
#endif
        private sealed class Cohort : IDisposable{
            private readonly GameObject root=new("200 unit benchmark");
            private readonly Animator[] anim=new Animator[Count];
            private readonly PlayableAnimationDriver[] drivers=new PlayableAnimationDriver[Count];
            private readonly AbilitySystemComponent[] owners=new AbilitySystemComponent[Count];
            public int Markers;public Transform Chest;public int Bones;
            public void Init(GameObject knight,AnimationClip clip,bool handoff){
                var definition=new SkillAnimationDefinition("a",1,true,markers:new[]{new AnimationMarker(.2f)});
                var bindings=new[]{new PlayableClipBinding{name="a",clip=clip}};
                var omit=typeof(PlayableAnimationDriver).GetProperty("OmitHandoffForBenchmark",Private);
                for(int i=0;i<Count;i++){
                    var unit=UnityEngine.Object.Instantiate(knight,root.transform);
                    var a=anim[i]=unit.GetComponent<Animator>();a.cullingMode=AnimatorCullingMode.AlwaysAnimate;
                    if(i==0){var bones=unit.GetComponentsInChildren<Transform>(true);Bones=bones.Length-1;Chest=bones.Single(t=>t.name=="Chest");}
                    var d=drivers[i]=new PlayableAnimationDriver(a,bindings);omit.SetValue(d,!handoff);
                    var asc=owners[i]=new AbilitySystemComponent();var spec=asc.GrantAbility(new GameplayAbilityDefinition());asc.TryActivateAbility(spec);
                    asc.StartTask(spec,new AnimationTask(definition,d,durationOverride:1,onMarker:(_,__)=>Markers++));
                }
            }
            public void CountHandoff(Run run){
                var bones=typeof(PlayableAnimationDriver).GetProperty("WatchedGenericBones",Private);
                var job=typeof(PlayableAnimationDriver).GetProperty("HandoffJobConnected",Private);
                foreach(var driver in drivers){run.watchedBones+=(int)bones.GetValue(driver);if((bool)job.GetValue(driver))run.connectedHandoffJobs++;}
                Assert.Zero(run.watchedBones,"Slash is fully covered by the knight's base controller");Assert.Zero(run.connectedHandoffJobs);
            }
            public void SetRate(int rate){foreach(var d in drivers)d.UpdateRate=new SkillAnimationRate{mode=SkillAnimationFrequency.EveryNFrames,frames=rate};}
            public void Frame(int f){for(int i=0;i<Count;i++){
                Observe(drivers[i],false);anim[i].Update(Dt);owners[i].Tick(Dt);Step(drivers[i],f,Dt,null);
            }}
            public void SampleStages(int f,Run r){
                long observe=0,native=0,asc=0,scheduled=0;
                for(int i=0;i<Count;i++){
                    long a=Stopwatch.GetTimestamp();Observe(drivers[i],false);
                    long b=Stopwatch.GetTimestamp();anim[i].Update(Dt);
                    long c=Stopwatch.GetTimestamp();owners[i].Tick(Dt);
                    long d=Stopwatch.GetTimestamp();Step(drivers[i],f,Dt,null);
                    long e=Stopwatch.GetTimestamp();observe+=b-a;native+=c-b;asc+=d-c;scheduled+=e-d;
                }
                double ms=1000d/(Frames*Stopwatch.Frequency);
                r.observe+=observe*ms;r.animator+=native*ms;r.asc+=asc*ms;r.scheduled+=scheduled*ms;
            }
#if UNITY_EDITOR_WIN
            public void SampleCycles(int f,Run r,IntPtr thread){
                ulong observe=0,native=0,asc=0,scheduled=0;
                for(int i=0;i<Count;i++){
                    ulong a=ReadCycles(thread);Observe(drivers[i],false);
                    ulong b=ReadCycles(thread);anim[i].Update(Dt);
                    ulong c=ReadCycles(thread);owners[i].Tick(Dt);
                    ulong d=ReadCycles(thread);Step(drivers[i],f,Dt,null);
                    ulong e=ReadCycles(thread);
                    observe+=b-a;native+=c-b;asc+=d-c;scheduled+=e-d;
                }
                if(f==0)r.observeMCycles=r.animatorMCycles=r.ascMCycles=r.scheduledMCycles=0;
                const double scale=1d/(Frames*1000000d);
                r.observeMCycles+=observe*scale;r.animatorMCycles+=native*scale;r.ascMCycles+=asc*scale;r.scheduledMCycles+=scheduled*scale;
            }
#endif
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
                note="Wall-clock CPU benchmark in Editor PlayMode; measured cohort always contains 200 units. Normal/control frames are paired with alternating first runner; both use the same controller, clip, LOD, Hold and gameplay. p95 is uninstrumented. observe/animator/asc/scheduled are per-stage MEAN ms across 120 separate instrumented frames, including timestamp overhead. Control omits handoff job and its handles. pairedMedianMs is the median within-frame normal minus control wall time. Windows MCycles use QueryThreadCycleTime on the main thread in a separate pass; exclude worker time, include query overhead and are not elapsed ms. Byte/GC.Alloc probes run separately; -1 means unavailable."};
            using var normal=new Cohort();using var control=new Cohort();
            // Construct the control first; wall-clock sampling order alternates independently.
            control.Init(knight,clip,false);normal.Init(knight,clip,true);var cohorts=new[]{normal,control};
#if UNITY_EDITOR_WIN
            var thread=GetCurrentThread();bool haveCycles=QueryThreadCycleTime(thread,out ulong probeCycles) && probeCycles!=0;
#endif
            report.bonesPerUnit=normal.Bones;report.animatedTransformsPerUnit=AnimationUtility.GetCurveBindings(clip)
                .Where(b=>b.type==typeof(Transform) && b.path.Length>0).Select(b=>b.path).Distinct().Count();Assert.Greater(report.animatedTransformsPerUnit,1);
            for(int mode=0;mode<2;mode++){
                int rate=mode==0?1:4;
                foreach(var c in cohorts)c.SetRate(rate);
                for(int f=0;f<60;f++)foreach(var c in cohorts)c.Frame(f);
                int[] start={normal.Markers,control.Markers};var times=new[]{new double[Frames],new double[Frames]};
                for(int v=0;v<2;v++)report.runs[2*v+mode]=new Run{handoff=v==0,rate=rate};
                for(int f=0;f<Frames;f++)for(int order=0;order<2;order++){
                    int v=(f+order)%2;long before=SkillAnimationManager.EvaluationCount,t=Stopwatch.GetTimestamp();
                    cohorts[v].Frame(f);times[v][f]=(Stopwatch.GetTimestamp()-t)*1000d/Stopwatch.Frequency;
                    int samples=(int)(SkillAnimationManager.EvaluationCount-before);report.runs[2*v+mode].evaluations+=samples;
                    if(v==0 && rate==4)report.peakFourthFrameEvaluations=Math.Max(report.peakFourthFrameEvaluations,samples);
                }
                Assert.Less(Quaternion.Angle(normal.Chest.localRotation,control.Chest.localRotation),.1f,"same visible looping pose in both groups");
                var delta=new double[Frames];for(int f=0;f<Frames;f++)delta[f]=times[0][f]-times[1][f];
                Array.Sort(delta);report.pairedMedianMs[mode]=(delta[59]+delta[60])*.5;
                for(int v=0;v<2;v++){
                    var r=report.runs[2*v+mode];cohorts[v].CountHandoff(r);r.markers=cohorts[v].Markers-start[v];Assert.AreEqual(Count*2,r.markers);
                    Assert.AreEqual(Count*Frames/rate,r.evaluations);Array.Sort(times[v]);r.p95=times[v][113];
                }
                for(int f=0;f<Frames;f++)for(int order=0;order<2;order++){
                    int v=(f+order)%2;cohorts[v].SampleStages(f,report.runs[2*v+mode]);
                }
#if UNITY_EDITOR_WIN
                if(haveCycles)for(int f=0;f<Frames;f++)for(int order=0;order<2;order++){
                    int v=(f+order)%2;cohorts[v].SampleCycles(f,report.runs[2*v+mode],thread);
                }
#endif
            }
            Assert.AreEqual(Count/4,report.peakFourthFrameEvaluations);
            report.everyFrameP95=report.runs[0].p95;report.everyFourthP95=report.runs[1].p95;report.markers=report.runs[0].markers;
            report.everyFrameEvaluations=report.runs[0].evaluations;report.everyFourthEvaluations=report.runs[1].evaluations;
            long probe=GC.GetAllocatedBytesForCurrentThread();GC.KeepAlive(new byte[4096]);
            bool bytesAvailable=GC.GetAllocatedBytesForCurrentThread()-probe>=4096;
            bool callsAvailable=AllocationCalls(()=>GC.KeepAlive(new byte[4096]))>0;
            Assert.IsTrue(bytesAvailable || callsAvailable);
            for(int mode=0;mode<2;mode++){
                normal.SetRate(mode==0?1:4);for(int f=0;f<60;f++)normal.Frame(f);
                long bytes=GC.GetAllocatedBytesForCurrentThread();for(int f=0;f<Frames;f++)normal.Frame(f);
                bytes=bytesAvailable?GC.GetAllocatedBytesForCurrentThread()-bytes:-1;
                long calls=callsAvailable?AllocationCalls(()=>{for(int f=0;f<Frames;f++)normal.Frame(f);}):-1;
                if(mode==0){report.everyFrameBytes=bytes;report.everyFrameAllocations=calls;}
                else{report.everyFourthBytes=bytes;report.everyFourthAllocations=calls;}
            }
            string path=Path.Combine(Directory.GetCurrentDirectory(),"GASValidation~/skill-runtime.json");Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllText(path,JsonUtility.ToJson(report,true));
#endif
            yield return null;
        }
    }
}
