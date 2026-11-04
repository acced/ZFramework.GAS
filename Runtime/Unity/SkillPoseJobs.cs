using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
namespace ZFramework.GAS.Unity
{
    // A pair of stream jobs surrounds one skill layer. Read the incoming pose before this
    // layer, then blend just the named bones back towards it after Unity applies AvatarMask.
    internal sealed class SkillBoneLayer : IDisposable
    {
        internal struct Pose { public Vector3 Position, Scale; public Quaternion Rotation; }
        private struct Job : IAnimationJob
        {
            public NativeArray<TransformStreamHandle> Bones;
            public NativeArray<TransformSceneHandle> SceneBones;
            public NativeArray<float> Weights;
            public NativeArray<Pose> Incoming;
            public bool Capture;
            public bool ReadNativePose;
            public void ProcessRootMotion(AnimationStream s){}
            public void ProcessAnimation(AnimationStream s)
            {
                for(int i=0;i<Bones.Length;i++)
                {
                    var h=Bones[i]; if(!h.IsValid(s))continue;
                    if(Capture)
                    {
                        var scene=SceneBones[i];
                        Incoming[i]=ReadNativePose && scene.IsValid(s)
                            ? new Pose{Position=scene.GetLocalPosition(s),Rotation=scene.GetLocalRotation(s),Scale=scene.GetLocalScale(s)}
                            : new Pose{Position=h.GetLocalPosition(s),Rotation=h.GetLocalRotation(s),Scale=h.GetLocalScale(s)};
                    }
                    else
                    {
                        var p=Incoming[i];var w=Weights[i];h.SetLocalPosition(s,Vector3.Lerp(p.Position,h.GetLocalPosition(s),w));
                        h.SetLocalRotation(s,Quaternion.Slerp(p.Rotation,h.GetLocalRotation(s),w));h.SetLocalScale(s,Vector3.Lerp(p.Scale,h.GetLocalScale(s),w));
                    }
                }
            }
        }
        private NativeArray<TransformStreamHandle> _handles;
        private NativeArray<TransformSceneHandle> _scene;
        private NativeArray<float> _weights;
        private NativeArray<Pose> _poses;
        public readonly AnimationScriptPlayable Before,After;
        public SkillBoneLayer(PlayableGraph graph,Animator animator,BoneWeightMask.Definition def,Playable incoming,AnimationLayerMixerPlayable layer)
        {
            int n=def.Bones.Length;
            _handles=new NativeArray<TransformStreamHandle>(n,Allocator.Persistent);_weights=new NativeArray<float>(n,Allocator.Persistent);_poses=new NativeArray<Pose>(n,Allocator.Persistent);
            _scene=new NativeArray<TransformSceneHandle>(n,Allocator.Persistent);
            for(int i=0;i<n;i++){var b=def.Bones[i];var t=animator.transform.Find(b.path);if(t!=null){_handles[i]=animator.BindStreamTransform(t);_scene[i]=animator.BindSceneTransform(t);}_weights[i]=b.weight;}
            var j=new Job{Bones=_handles,SceneBones=_scene,Weights=_weights,Incoming=_poses,Capture=true}; Before=AnimationScriptPlayable.Create(graph,j,1);
            j.Capture=false; After=AnimationScriptPlayable.Create(graph,j,1);
            if(incoming.IsValid()){graph.Connect(incoming,0,Before,0);Before.SetInputWeight(0,1);}
            graph.Connect(Before,0,layer,0);layer.SetInputWeight(0,1);graph.Connect(layer,0,After,0);After.SetInputWeight(0,1);
        }
        public void UseNativeBase(bool enabled=true){var job=Before.GetJobData<Job>();job.ReadNativePose=enabled;Before.SetJobData(job);}
        public void Destroy(PlayableGraph g){g.DestroyPlayable(Before);g.DestroyPlayable(After);Dispose();}
        public void Dispose(){if(_handles.IsCreated)_handles.Dispose();if(_scene.IsCreated)_scene.Dispose();if(_weights.IsCreated)_weights.Dispose();if(_poses.IsCreated)_poses.Dispose();}
    }
    internal struct SkillAimJob:IAnimationJob
    {
        public TransformStreamHandle Bone;
        public Vector3 Target;
        public float Weight;
        public void ProcessRootMotion(AnimationStream s){}
        public void ProcessAnimation(AnimationStream s)
        {
            if(Weight<=0 || !Bone.IsValid(s))return;
            var d=Target-Bone.GetPosition(s);if(d.sqrMagnitude<.00001f)return;
            var prior=Bone.GetRotation(s);var aim=Quaternion.FromToRotation(prior*Vector3.forward,d.normalized)*prior;
            Bone.SetRotation(s,Quaternion.Slerp(prior,aim,Weight));
        }
    }
}
