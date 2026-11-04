using System.Collections.Generic;
using UnityEngine;

namespace ZFramework.GAS.Unity
{
    // Track the base underneath a skill, per channel. A value left in the scene by our
    // preceding sample is not a new controller/script pose (notably with Write Defaults off).
    internal struct SkillPoseHandoff
    {
        private struct Axis
        {
            public float Base,Last,Native;
            public bool Owned,Equipment,IgnoreSkill;
            private float _skill;
            public Axis(float value){Base=Last=Native=_skill=value;Owned=Equipment=IgnoreSkill=false;}
            public void Leaving(){IgnoreSkill=Owned;_skill=Last;}
            public void Released(float output){Last=output;Owned=false;}
            public void Observe(float value,bool authored=false){
                if(authored){Base=Native=value;Equipment=false;return;}
                if(Owned && Mathf.Abs(value-Last)<=1e-6f)return;
                if(!Equipment || Mathf.Abs(value-Native)>1e-6f){Base=value;Equipment=false;}
                Native=value;
            }
            public bool External(float value){
                if(Mathf.Abs(value-Last)<=1e-6f)return false;
                Base=value;Equipment=true;return true;
            }
            public void First(float value){Native=value;Equipment=Mathf.Abs(value-Base)>1e-6f;}
            public bool NeedsReturn=>Owned || Equipment && Mathf.Abs(Base-Last)>1e-6f;
            public float Pass(float native,bool authored){
                if(authored){IgnoreSkill=Equipment=false;return Base=Native=Last=native;}
                // A genuinely new controller sample takes precedence over the equipment
                // edit; an unkeyed binding replaying the same default does not.
                if(IgnoreSkill){
                    if(Mathf.Abs(native-_skill)<=1e-6f)return Last=Base;
                    if(Mathf.Abs(native-Native)>1e-6f && Mathf.Abs(native-Base)>1e-6f)IgnoreSkill=false;
                }
                if(Equipment && Mathf.Abs(native-Native)>1e-6f && Mathf.Abs(native-Base)>1e-6f && Mathf.Abs(native-Last)>1e-6f)Equipment=false;
                if(!Equipment)Native=native;
                return Last=Equipment?Base:native;
            }
            public void Publish(float value,float before){Owned|=Mathf.Abs(value-before)>1e-6f;Last=value;}
        }
        private struct Channels
        {
            private Axis x,y,z;
            public Channels(Vector3 v){x=new Axis(v.x);y=new Axis(v.y);z=new Axis(v.z);}
            public Vector3 Base=>new(x.Base,y.Base,z.Base);
            public bool Owned=>x.NeedsReturn || y.NeedsReturn || z.NeedsReturn;
            public void Leaving(){x.Leaving();y.Leaving();z.Leaving();}
            public void Released(Vector3 v){x.Released(v.x);y.Released(v.y);z.Released(v.z);}
            public void Observe(Vector3 v,int channels){x.Observe(v.x,(channels&1)!=0);y.Observe(v.y,(channels&2)!=0);z.Observe(v.z,(channels&4)!=0);}
            public bool External(Vector3 v)=>x.External(v.x)|y.External(v.y)|z.External(v.z);
            public void First(Vector3 v){x.First(v.x);y.First(v.y);z.First(v.z);}
            public void Publish(Vector3 v,Vector3 before){x.Publish(v.x,before.x);y.Publish(v.y,before.y);z.Publish(v.z,before.z);}
            public Vector3 Pass(Vector3 native,int mask)=>new(x.Pass(native.x,(mask&1)!=0),y.Pass(native.y,(mask&2)!=0),z.Pass(native.z,(mask&4)!=0));
            public Vector3 Restore(Vector3 v)=>new(x.NeedsReturn?x.Base:v.x,y.NeedsReturn?y.Base:v.y,z.NeedsReturn?z.Base:v.z);
        }
        private Channels _position,_scale;
        public Vector3 Position=>_position.Base;
        public Vector3 Scale=>_scale.Base;
        public Quaternion Rotation;
        private Quaternion _lastRotation,_nativeRotation;
        public bool P=>_position.Owned;
        public bool S=>_scale.Owned;
        private bool _rotationOwned,_equipmentRotation,_ignoreSkillRotation;
        private Quaternion _skillRotation;
        private static bool Same(Quaternion a,Quaternion b)=>Mathf.Abs(Quaternion.Dot(a,b))>.999999f;
        public bool R=>_rotationOwned || _equipmentRotation && !Same(Rotation,_lastRotation);
        public void Begin(Vector3 p,Quaternion r,Vector3 s){
            _position=new Channels(p);_scale=new Channels(s);Rotation=_lastRotation=_nativeRotation=r;_rotationOwned=_equipmentRotation=_ignoreSkillRotation=false;
        }
        public void Observe(Vector3 p,Quaternion r,Vector3 s,int channels=0){
            _position.Observe(p,channels);_scale.Observe(s,channels>>3);
            if((channels&64)!=0){Rotation=_nativeRotation=r;_equipmentRotation=false;return;}
            if(!_rotationOwned || !Same(r,_lastRotation)){if(!_equipmentRotation || !Same(r,_nativeRotation)){Rotation=r;_equipmentRotation=false;}_nativeRotation=r;}
        }
        // Unkeyed native bindings may rewrite the same default every evaluation.
        public bool ObserveEquipment(Vector3 p,Quaternion r,Vector3 s){
            bool changed=_position.External(p)|_scale.External(s);
            if(!Same(r,_lastRotation)){Rotation=r;_equipmentRotation=true;return true;}
            return changed;
        }
        public void ObserveFirstNative(Vector3 p,Quaternion r,Vector3 s){
            _position.First(p);_scale.First(s);_nativeRotation=r;_equipmentRotation=!Same(r,Rotation);
        }
        public void Published(Vector3 p,Quaternion r,Vector3 s,Vector3 beforeP,Quaternion beforeR,Vector3 beforeS){
            _position.Publish(p,beforeP);_scale.Publish(s,beforeS);_rotationOwned|=!Same(r,beforeR);_lastRotation=r;
        }
        public void Leaving(){
            _position.Leaving();_scale.Leaving();_skillRotation=_lastRotation;_ignoreSkillRotation=_rotationOwned;
        }
        public void Released(Vector3 p,Quaternion r,Vector3 s){
            _position.Released(p);_scale.Released(s);_lastRotation=r;_rotationOwned=false;
        }
        public void Pass(ref Vector3 p,ref Quaternion r,ref Vector3 s,Vector3 nativeP,Quaternion nativeR,Vector3 nativeS,int channels){
            p=_position.Pass(nativeP,channels);s=_scale.Pass(nativeS,channels>>3);
            if((channels&64)!=0){_equipmentRotation=_ignoreSkillRotation=false;r=Rotation=_lastRotation=_nativeRotation=nativeR;return;}
            if(_ignoreSkillRotation){
                if(Same(nativeR,_skillRotation)){r=_lastRotation=Rotation;return;}
                if(!Same(nativeR,_nativeRotation) && !Same(nativeR,Rotation))_ignoreSkillRotation=false;
            }
            if(_equipmentRotation && !Same(nativeR,_nativeRotation) && !Same(nativeR,Rotation) && !Same(nativeR,_lastRotation))_equipmentRotation=false;
            if(_equipmentRotation)r=Rotation;else r=_nativeRotation=nativeR;
            _lastRotation=r;
        }
        public Vector3 ReturnPosition(Vector3 p)=>_position.Restore(p);
        public Vector3 ReturnScale(Vector3 s)=>_scale.Restore(s);
        public void Restore(Transform bone){
            bone.GetLocalPositionAndRotation(out var p,out var r);Restore(bone,p,r,bone.localScale);
        }
        public void Restore(Transform bone,Vector3 p,Quaternion r,Vector3 s){
            if(P){var next=ReturnPosition(p);if(next!=p)bone.localPosition=next;}
            if(R && !Same(r,Rotation))bone.localRotation=Rotation;
            if(S){var next=ReturnScale(s);if(next!=s)bone.localScale=next;}
        }
    }

    // Retargeted/optimized clips do not expose reliable Transform paths in a player.
    // Watch only surviving, non-muscle transforms and restore only channels the graph changed.
    internal sealed class ExposedSkillBones
    {
        private sealed class Entry { public Transform T;public SkillPoseHandoff Pose;public bool Sampled;public Vector3 BeforeP,BeforeS;public Quaternion BeforeR; }
        private readonly Animator _animator;
        private readonly List<Transform> _hierarchy=new();
        private readonly HashSet<Transform> _skeleton=new();
        private readonly Dictionary<Transform,Entry> _known=new();
        private readonly List<Entry> _entries=new();
        private readonly HashSet<Transform> _incoming=new();
        public int Count=>_entries.Count;
        public ExposedSkillBones(Animator animator){_animator=animator;}
        public void Begin(HashSet<string> paths,Transform[] explicitBones,bool completePaths){
            _incoming.Clear();_skeleton.Clear();
            if(_animator.isHuman)for(int i=0;i<(int)HumanBodyBones.LastBone;i++)
                for(var t=_animator.GetBoneTransform((HumanBodyBones)i);t!=null && _skeleton.Add(t);t=t.parent){}
            for(int i=_entries.Count-1;i>=0;i--){var t=_entries[i].T;
                if(t!=null && t.IsChildOf(_animator.transform) && !_skeleton.Contains(t))continue;
                _known.Remove(t);_entries.RemoveAt(i);
            }
            bool restrict=explicitBones!=null && explicitBones.Length!=0;
            bool Allowed(Transform t)=>!_skeleton.Contains(t);
            if(completePaths){
                bool listed=false;
                foreach(var path in paths){
                    var t=_animator.transform.Find(path);
                    if(t!=null){if(Allowed(t))Watch(t);continue;}
                    if(_animator.hasTransformHierarchy)continue;
                    if(!listed){_animator.GetComponentsInChildren(true,_hierarchy);listed=true;}
                    // Optimize Game Objects flattens exposed attachments; names need not be unique.
                    var leaf=path.Substring(path.LastIndexOf('/')+1);
                    foreach(var exposed in _hierarchy)if(Allowed(exposed) && exposed.name==leaf)Watch(exposed);
                }
            }else{
                // Optimized avatars can flatten authored paths. Without complete curve paths,
                // preserve unknown attachments instead of guessing from leaf names.
                _animator.GetComponentsInChildren(true,_hierarchy);
                foreach(var t in _hierarchy)if(t!=_animator.transform && !_skeleton.Contains(t))Watch(t);
            }
            if(restrict)foreach(var t in explicitBones)
                if(t!=null && t!=_animator.transform && t.IsChildOf(_animator.transform) && Allowed(t))Watch(t);
        }
        public void FinishUnused(bool stopped){
            for(int i=_entries.Count-1;i>=0;i--){var e=_entries[i];
                if(!stopped && _incoming.Contains(e.T))continue;
                if(e.T!=null)e.Pose.Restore(e.T);
                _known.Remove(e.T);_entries.RemoveAt(i);
            }
        }
        private void Watch(Transform t){
            if(t==null || !_incoming.Add(t) || _known.ContainsKey(t))return;
            var e=new Entry{T=t};t.GetLocalPositionAndRotation(out var p,out var r);
            e.Pose.Begin(p,r,t.localScale);_entries.Add(e);_known.Add(t,e);
        }
        public void BeforeAnimator(){foreach(var e in _entries)if(e.T!=null){
            e.T.GetLocalPositionAndRotation(out var p,out var r);e.Pose.ObserveEquipment(p,r,e.T.localScale);
        }}
        public void Before(){foreach(var e in _entries)if(e.T!=null){
            var t=e.T;t.GetLocalPositionAndRotation(out e.BeforeP,out e.BeforeR);e.BeforeS=t.localScale;
            if(!e.Sampled){e.Pose.ObserveFirstNative(e.BeforeP,e.BeforeR,e.BeforeS);e.Sampled=true;}
            else e.Pose.Observe(e.BeforeP,e.BeforeR,e.BeforeS);
        }}
        public void After(){foreach(var e in _entries)if(e.T!=null){
            e.T.GetLocalPositionAndRotation(out var p,out var r);
            e.Pose.Published(p,r,e.T.localScale,e.BeforeP,e.BeforeR,e.BeforeS);
        }}
        public void Restore(){foreach(var e in _entries)if(e.T!=null){e.Pose.Restore(e.T);}_entries.Clear();_known.Clear();_incoming.Clear();}
    }
}
