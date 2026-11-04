using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace ZFramework.GAS.Unity
{
    // The destination clip is the only clip sampled during an inertial transition.
    // A residual in local bone space decays to zero; restart from the visible pose on interruption.
    internal sealed class SkillInertialPose : IDisposable
    {
        private struct Bone
        {
            public TransformStreamHandle Handle;
            public TransformSceneHandle Scene;
            public Vector3 Source, SourceScale, Offset, ScaleOffset;
            public SkillPoseHandoff Pose;
            public Quaternion SourceRotation, RotationOffset;
            public Vector3 Last, Velocity, SourceVelocity, FirstTarget;
            public Vector3 BeforeP,BeforeS;public Quaternion BeforeR;
            public int Revision, RestoredRequest,NativeChannels;
            public bool HasLast, Enabled, ReturnToBase, Watched, Released;
        }
        private struct Job : IAnimationJob
        {
            public NativeArray<Bone> Bones;
            public int Request;
            public float Phase, Duration, Elapsed;
            public bool ToRest;
            public void ProcessRootMotion(AnimationStream stream) { }
            public void ProcessAnimation(AnimationStream stream)
            {
                for(int i=0;i<Bones.Length;i++){
                    var b=Bones[i];var h=b.Handle;if((!b.Watched && !b.Released && !(b.Enabled && Duration>0)) || !h.IsValid(stream) || !b.Scene.IsValid(stream))continue;
                    b.Scene.GetLocalTRS(stream,out var beforeP,out var beforeR,out var beforeS);
                    h.GetLocalTRS(stream,out var p,out var r,out var s);
                    if(b.Released){
                        var actualP=p;var actualR=r;var actualS=s;
                        b.Pose.Pass(ref p,ref r,ref s,beforeP,beforeR,beforeS,b.NativeChannels);
                        if(actualP!=p)h.SetLocalPosition(stream,p);
                        if(actualR!=r)h.SetLocalRotation(stream,r);
                        if(actualS!=s)h.SetLocalScale(stream,s);
                        Bones[i]=b;continue;
                    }
                    b.Pose.Observe(beforeP,beforeR,beforeS,b.ReturnToBase?b.NativeChannels:0);
                    bool blend=Duration>0 && b.Enabled && (Elapsed<Duration || b.RestoredRequest!=Request);
                    if(blend)
                    {
                        if(ToRest || b.ReturnToBase){
                            if(b.Watched){p=b.Pose.Position;r=b.Pose.Rotation;s=b.Pose.Scale;}
                            else{p=beforeP;r=beforeR;s=beforeS;} // controller-owned, transient residual only
                        }
                        if(b.Revision!=Request)
                        {
                            b.Revision=Request;b.FirstTarget=p;b.Offset=b.Source-p;
                            b.ScaleOffset=b.SourceScale-s;b.RotationOffset=b.SourceRotation*Quaternion.Inverse(r);
                        }
                        // Cubic Hermite: outgoing velocity at the start, zero residual and slope at the end.
                        float u=Phase,k=1-u,weight=k*k*(1+2*u);
                        var targetVelocity=Elapsed>.00001f?(p-b.FirstTarget)/Elapsed:Vector3.zero;
                        p+=b.Offset*weight+(b.SourceVelocity-targetVelocity)*(Duration*u*k*k);
                        s+=b.ScaleOffset*weight;r=Quaternion.Slerp(Quaternion.identity,b.RotationOffset,weight)*r;
                        h.SetLocalTRS(stream,p,r,s,false);
                        if(Elapsed>=Duration)b.RestoredRequest=Request;
                        if(stream.deltaTime>.00001f){if(b.HasLast)b.Velocity=(p-b.Last)/stream.deltaTime;b.Last=p;b.HasLast=true;}
                    }
                    else if(b.ReturnToBase){
                        if(b.Pose.P){p=b.Pose.ReturnPosition(p);h.SetLocalPosition(stream,p);}
                        if(b.Pose.R){r=b.Pose.Rotation;h.SetLocalRotation(stream,r);}
                        if(b.Pose.S){s=b.Pose.ReturnScale(s);h.SetLocalScale(stream,s);}
                    }
                    b.Pose.Published(p,r,s,beforeP,beforeR,beforeS);Bones[i]=b;
                }
            }
        }
        internal sealed class Rig
        {
            internal readonly List<(string path,Transform t)> Bones=new();
            // Only actual clip targets get stream handles. Pose ownership begins at Play.
            private readonly Dictionary<string,(string path,Transform t)> _rest=new(StringComparer.Ordinal);
            private readonly HashSet<Transform> _registered=new();
            private readonly Transform _root;
            private readonly List<Transform> _check=new();
            public int Revision {get;private set;}
            private string _layout;
            private readonly string[] _authorPaths;
            private readonly List<(string path,string parent,string name)> _hierarchy=new();
            public Rig(Animator animator,string[] authorPaths=null)
            {
                _root=animator.transform;_authorPaths=authorPaths;RefreshHierarchy();
            }
            private readonly List<(Transform node,Transform parent,string path,string name)> _structure=new();
            public void RefreshHierarchy(bool verifyNames=false)
            {
                _root.GetComponentsInChildren<Transform>(true,_check);
                bool changed=_layout==null || _check.Count!=_structure.Count+1;
                for(int i=1;!changed && i<_check.Count;i++){
                    var old=_structure[i-1];var t=_check[i];
                    changed=old.node!=t || t.parent!=old.parent || (old.path!=null?_root.Find(old.path)!=t:verifyNames && t.name!=old.name);
                }
                if(!changed)return;
                _rest.Clear();_hierarchy.Clear();_structure.Clear();
                var layout=new System.Text.StringBuilder();
                void Visit(Transform parent,string parentPath,bool addressable=true)
                {
                    foreach(Transform child in parent)
                    {
                        var name=child.name;var path=parentPath.Length==0?name:parentPath+"/"+name;
                        bool first=addressable && _rest.TryAdd(path,(path,child));
                        _structure.Add((child,parent,first?path:null,name));
                        if(!first){Visit(child,path,false);continue;}
                        _hierarchy.Add((path,parentPath,name));
                        layout.Append(path.Length).Append(':').Append(path);
                        Visit(child,path);
                    }
                }
                Visit(_root,"");
                // Optimized avatars flatten exposed transforms. Build the sample proxy with
                // their original paths as well; no deoptimization or sampling of the live actor.
                if(_authorPaths!=null){
                    var known=new HashSet<string>(_rest.Keys,StringComparer.Ordinal);
                    void Include(string path){
                        if(string.IsNullOrEmpty(path) || !known.Add(path))return;
                        int slash=path.LastIndexOf('/');string parent=slash<0?"":path.Substring(0,slash);
                        Include(parent);_hierarchy.Add((path,parent,path.Substring(slash+1)));
                        layout.Append(path.Length).Append(':').Append(path);
                    }
                    foreach(var path in _authorPaths)Include(path);
                }
                _layout=layout.ToString();
                for(int i=Bones.Count-1;i>=0;i--){
                    var old=Bones[i];if(!_rest.TryGetValue(old.path,out var now) || now.t!=old.t){
                        _registered.Remove(old.t);Bones.RemoveAt(i);Revision++;}
                }
            }

            // Unity's player GetCurveBindings omits native Transform curves. Sample a transform-only
            // hierarchy on first use, never the actor: no scripts, events, renderers or root motion.
            // Two different input poses distinguish a keyed constant from an untouched transform.
            internal sealed class Bindings{
                public string[] Paths;public readonly Dictionary<string,int> Channels=new(StringComparer.Ordinal);
            }
            private sealed class ClipEntry
            {
                internal readonly Dictionary<string,Bindings> Layouts=new(StringComparer.Ordinal);
                internal readonly Queue<string> Order=new();
                // For generic Transform curves only: path membership is independent of
                // siblings. Retain positive and negative results for the last hierarchy.
                internal Dictionary<string,int> PreviousChannels;
                internal void Clear(){Layouts.Clear();Order.Clear();PreviousChannels=null;}
                internal void Add(string layout,Bindings paths){
                    // Unique equipment hierarchies must not accumulate for the lifetime of a shared clip.
                    if(Order.Count>=64)Layouts.Remove(Order.Dequeue());
                    Layouts.Add(layout,paths);Order.Enqueue(layout);
                }
#if UNITY_EDITOR
                internal int Revision=-1;
                internal Hash128 ImportHash;
#endif
            }
            private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<AnimationClip,ClipEntry> ClipPaths=new();
#if UNITY_EDITOR
            private static int _projectRevision;
            static Rig()
            {
                UnityEditor.EditorApplication.projectChanged+=()=>_projectRevision++;
                UnityEditor.AnimationUtility.onCurveWasModified+=(clip,_,__)=>{if(clip!=null)ClipPaths.Remove(clip);};
            }
#endif
            internal Bindings ClipBindings(AnimationClip clip)
            {
                var entry=ClipPaths.GetOrCreateValue(clip);
#if UNITY_EDITOR
                // A model importer can replace the curves without changing the AnimationClip instance.
                if(entry.Revision!=_projectRevision)
                {
                    string path=UnityEditor.AssetDatabase.GetAssetPath(clip);
                    var hash=string.IsNullOrEmpty(path)?default:UnityEditor.AssetDatabase.GetAssetDependencyHash(path);
                    if(hash!=entry.ImportHash)entry.Clear();
                    entry.ImportHash=hash;entry.Revision=_projectRevision;
                }
#endif
                var layouts=entry.Layouts;
                if(!layouts.TryGetValue(_layout,out var paths))
                {
                    paths=Probe(clip,entry);
                    entry.Add(_layout,paths);
                }
                return paths;
            }
            private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<AnimationClip,object> Edited=new();
            internal static bool HasRuntimeEdits(AnimationClip clip)=>Edited.TryGetValue(clip,out _);
            internal static void Invalidate(AnimationClip clip){if(clip!=null){ClipPaths.Remove(clip);Edited.GetOrCreateValue(clip);}}
            public string[] Resolve(AnimationClip clip,string[] authored,out bool canHold)
            {
                var paths=authored??ClipBindings(clip).Paths;bool complete=true;
                foreach(string path in paths){
                    if(!_rest.TryGetValue(path,out var bone)){complete=false;continue;}
                    if(_registered.Add(bone.t)){Bones.Add(bone);Revision++;}
                }
                canHold=complete && paths.Length>0 && !clip.humanMotion && !clip.hasMotionCurves && !clip.hasRootCurves;
                return paths;
            }
            private Bindings Probe(AnimationClip clip,ClipEntry entry)
            {
                var result=new Bindings{Paths=Array.Empty<string>()};if(_hierarchy.Count==0)return result;
                bool reusable=!clip.humanMotion && !clip.hasMotionCurves && !clip.hasRootCurves;
                var previous=reusable?entry.PreviousChannels:null;
                HashSet<string> required=null;
                if(previous!=null){
                    required=new HashSet<string>(StringComparer.Ordinal);
                    foreach(var node in _hierarchy){
                        if(previous.TryGetValue(node.path,out int bits)){if(bits!=0)result.Channels[node.path]=bits;}
                        else {
                            string path=node.path;
                            while(required.Add(path)){int slash=path.LastIndexOf('/');if(slash<0)break;path=path.Substring(0,slash);}
                        }
                    }
                }
                Bindings Complete(){
                    result.Paths=new string[result.Channels.Count];result.Channels.Keys.CopyTo(result.Paths,0);
                    if(reusable){
                        var next=previous??new Dictionary<string,int>(StringComparer.Ordinal);next.Clear();
                        foreach(var node in _hierarchy){result.Channels.TryGetValue(node.path,out int bits);next[node.path]=bits;}
                        entry.PreviousChannels=next;
                    }
                    return result;
                }
                if(required?.Count==0)return Complete();
                var root=new GameObject("GAS clip binding probe"){hideFlags=HideFlags.HideAndDontSave};
                root.SetActive(false);
                try
                {
                    var proxies=new Dictionary<string,Transform>(StringComparer.Ordinal){{"",root.transform}};
                    foreach(var node in _hierarchy){
                        if(required!=null && !required.Contains(node.path))continue;
                        var copy=new GameObject(node.name){hideFlags=HideFlags.HideAndDontSave};
                        copy.transform.SetParent(proxies[node.parent],false);proxies[node.path]=copy.transform;}
                    if(!clip.legacy){var sampler=root.AddComponent<Animator>();sampler.fireEvents=false;}
                    var animated=result.Channels;
                    for(int pass=1;pass<=2;pass++)
                    {
                        var p=new Vector3(3,-2,4)*pass;var r=Quaternion.Euler(31*pass,-19*pass,17*pass);var s=Vector3.one*(pass+1);
                        foreach(var bone in proxies.Values){bone.SetLocalPositionAndRotation(p,r);bone.localScale=s;}
                        clip.SampleAnimation(root,0);
                        foreach(var pair in proxies){
                            var t=pair.Value;
                            if(pair.Key.Length==0)continue;
                            var pt=t.localPosition;var st=t.localScale;
                            int bits=(pt.x!=p.x?1:0)|(pt.y!=p.y?2:0)|(pt.z!=p.z?4:0)|(st.x!=s.x?8:0)|(st.y!=s.y?16:0)|(st.z!=s.z?32:0)|(t.localRotation!=r?64:0);
                            if(bits!=0){animated.TryGetValue(pair.Key,out int oldBits);animated[pair.Key]=oldBits|bits;}}
                    }
                    return Complete();
                }
                finally{if(Application.isPlaying)UnityEngine.Object.Destroy(root);else UnityEngine.Object.DestroyImmediate(root);}
            }
        }
        private readonly Animator _animator;
        private readonly Rig _rig;
        private NativeArray<Bone> _bones;
        public readonly AnimationScriptPlayable Node;
        private SkillBlend _blend;
        private float _time;
        private readonly List<Transform> _bound=new();
        private readonly List<int> _observed=new();
        private bool _finalSample;
        internal int ObservedCount=>_observed.Count;
        internal bool NeedsJob=>Active || _finalSample || _hasReturning || _hasReleased;
        internal void Sampled(){if(!Active)_finalSample=false;}
        private void IndexObserved(){
            _observed.Clear();for(int i=0;i<_bones.Length;i++){var b=_bones[i];if(b.Watched || b.Released)_observed.Add(i);}
        }
        // Steady playback needs provenance for skill-owned attachments, not a stream job.
        // Capture around the real graph sample; skipped frames do not publish a new skill pose.
        internal void SteadyBefore(){
            foreach(int i in _observed){var t=_bound[i];if(t==null)continue;var b=_bones[i];
                t.GetLocalPositionAndRotation(out b.BeforeP,out b.BeforeR);b.BeforeS=t.localScale;
                b.Pose.Observe(b.BeforeP,b.BeforeR,b.BeforeS);_bones[i]=b;
            }
        }
        internal void SteadyAfter(){
            foreach(int i in _observed){var t=_bound[i];if(t==null)continue;var b=_bones[i];
                t.GetLocalPositionAndRotation(out var p,out var r);b.Pose.Published(p,r,t.localScale,b.BeforeP,b.BeforeR,b.BeforeS);_bones[i]=b;
            }
        }
        internal struct EntryPose{
            public Vector3 P,S;public Quaternion R;
            public EntryPose(Transform t){t.GetLocalPositionAndRotation(out P,out R);S=t.localScale;}
        }
        public void Watch(HashSet<string> incoming,Dictionary<string,EntryPose> prior=null){
            _hasReleased=false;
            for(int i=0;i<_bones.Length;i++){var b=_bones[i];var bone=_rig.Bones[i];
                if(!incoming.Contains(bone.path) || bone.t==null){_hasReleased|=b.Released;continue;}
                if(!b.Watched){
                    var now=new EntryPose(bone.t);
                    if(prior!=null && prior.TryGetValue(bone.path,out var saved)){
                        b.Pose.Begin(saved.P,saved.R,saved.S);b.Pose.Published(now.P,now.R,now.S,saved.P,saved.R,saved.S);
                    }else b.Pose.Begin(now.P,now.R,now.S);
                    b.Watched=true;
                }
                b.ReturnToBase=b.Released=false;_bones[i]=b;
            }IndexObserved();
        }
        // Snapshot BEFORE detaching: disconnecting the Animator can itself change the scene pose.
        public void ObserveEquipment(bool prepareNative=false){
            foreach(int i in _observed){var b=_bones[i];var t=_bound[i];if(t==null)continue;
                t.GetLocalPositionAndRotation(out var p,out var r);
                var s=t.localScale;if(b.Pose.ObserveEquipment(p,r,s))_bones[i]=b;
                // Give the controller the underlying pose, not our previous outgoing sample.
                if(prepareNative && b.ReturnToBase)b.Pose.Restore(t,p,r,s);
            }
        }
        // PreviousInputs can replay a removed skill binding through a still-live Aim.
        // Remember that sample before starting the fade, so it cannot claim a released channel.
        private bool _hasReleased,_hasReturning,_refreshNative;
        private readonly List<AnimatorClipInfo> _clipInfo=new();
        private readonly List<(AnimationClip clip,int layer)> _sources=new(),_previous=new();
        private RuntimeAnimatorController _lastController;
        private ControllerHandoffCoverage _lastCoverage;
        private readonly List<Dictionary<string,int>> _sourceBindings=new();
        internal void CaptureNative(HashSet<string>[] layers,ControllerHandoffCoverage coverage){
            if(!_hasReleased && !_hasReturning)return;
            var controller=_animator.runtimeAnimatorController;_sources.Clear();
            void Add(int layer,bool next){
                if(next)_animator.GetNextAnimatorClipInfo(layer,_clipInfo);else _animator.GetCurrentAnimatorClipInfo(layer,_clipInfo);
                foreach(var info in _clipInfo)if(info.weight>0 && info.clip!=null)_sources.Add((info.clip,layer));
            }
            if(controller!=null && layers!=null)for(int l=0;l<Math.Min(_animator.layerCount,layers.Length);l++){
                if(l!=0 && _animator.GetLayerWeight(l)<=0)continue;
                Add(l,false);if(_animator.IsInTransition(l))Add(l,true);
            }
            if(!_refreshNative && coverage==_lastCoverage && controller==_lastController && _previous.Count==_sources.Count){
                bool same=true;for(int i=0;same && i<_sources.Count;i++)same=_previous[i]==_sources[i];
                if(same)return;
            }
            _refreshNative=false;_lastController=controller;_lastCoverage=coverage;_previous.Clear();_previous.AddRange(_sources);
            _sourceBindings.Clear();foreach(var source in _sources)_sourceBindings.Add(coverage?.NativeChannels(source.clip)??_rig.ClipBindings(source.clip).Channels);
            for(int i=0;i<_bones.Length;i++){var b=_bones[i];if(!b.Released && !b.ReturnToBase)continue;b.NativeChannels=0;
                for(int n=0;n<_sources.Count;n++){
                    var pair=_sources[n];var path=_rig.Bones[i].path;
                    var allowed=layers[pair.layer];if(allowed!=null && !allowed.Contains(path))continue;
                    if(_sourceBindings[n].TryGetValue(path,out var channels))b.NativeChannels|=channels;
                }
                _bones[i]=b;
            }
        }
        public void RememberOutgoing(){
            for(int i=0;i<_bones.Length;i++){var b=_bones[i];if(!b.Watched)continue;
                b.Pose.Leaving();_bones[i]=b;
            }
        }
        public bool FinishUnused(HashSet<string> live){
            bool released=false;_hasReturning=false;
            for(int i=0;i<_bones.Length;i++){var b=_bones[i];var bone=_rig.Bones[i];
                if(!b.Watched || live.Contains(bone.path))continue;
                if(bone.t!=null){
                    b.Pose.Restore(bone.t);
                    bone.t.GetLocalPositionAndRotation(out var p,out var r);b.Pose.Released(p,r,bone.t.localScale);
                }
                b.Released=true;released=_hasReleased=_refreshNative=true;
                b.Watched=b.Enabled=b.ReturnToBase=false;_bones[i]=b;
            }IndexObserved();return released;
        }
        public void RestoreAndForget(){
            _hasReleased=_hasReturning=false;_previous.Clear();
            for(int i=0;i<_bones.Length;i++){var b=_bones[i];var t=_bound[i];
                if(b.Watched && t!=null)b.Pose.Restore(t);
                b.Watched=b.Released=b.Enabled=b.ReturnToBase=false;_bones[i]=b;}
            _observed.Clear();if(Node.IsValid())Cancel();
        }
        public float Remaining=>Mathf.Max(0,_blend.duration-_time);
        public bool Active=>_time<_blend.duration;
        public SkillInertialPose(Animator animator,PlayableGraph graph,Rig rig)
        {
            _animator=animator;_rig=rig;
            Node=AnimationScriptPlayable.Create(graph,new Job(),1);
            Grow();
        }
        // Keep the job and existing residual/velocity history when a newly loaded clip binds extra bones.
        public void Grow()
        {
            _refreshNative=true;
            var job=Node.GetJobData<Job>();
            var old=new Dictionary<Transform,int>();
            for(int i=0;i<_bound.Count;i++)if(_bound[i]!=null)old[_bound[i]]=i;
            var next=new NativeArray<Bone>(_rig.Bones.Count,Allocator.Persistent);
            _bound.Clear();
            for(int i=0;i<next.Length;i++){
                var bone=_rig.Bones[i];var t=bone.t;_bound.Add(t);
                if(t==null)continue;
                next[i]=old.TryGetValue(t,out int previous)?_bones[previous]:new Bone{
                    Handle=_animator.BindStreamTransform(t),Scene=_animator.BindSceneTransform(t)};
            }
            job.Bones=next;Node.SetJobData(job);
            if(_bones.IsCreated)_bones.Dispose();_bones=next;IndexObserved();
        }
        internal sealed class MaskPaths
        {
            private readonly HashSet<string> _active=new(StringComparer.Ordinal);
            public void Select(HashSet<string> result,string[] paths,AvatarMask mask,BoneWeightMask.Definition weights)
            {
                result.Clear();if(paths==null)return;
                _active.Clear();bool masked=mask!=null && mask.transformCount>0;
                // AvatarMask exposes no revision. Read each active path once per transition,
                // including equal-length edits, never once per (bone × mask entry) or per frame.
                if(masked)for(int i=0;i<mask.transformCount;i++)
                    if(mask.GetTransformActive(i))_active.Add(mask.GetTransformPath(i));
                foreach(string path in paths){
                    if(masked && !_active.Contains(path))continue;
                    bool included=true;
                    if(weights!=null)foreach(var b in weights.Bones)if(b.path==path){included=b.weight>0;break;}
                    if(included)result.Add(path);
                }
            }
        }
        public bool Begin(SkillBlend blend,bool toRest,HashSet<string> incoming,HashSet<string> outgoing)
        {
            // Empty destination masks use the normal layer fade. Stop/other transitions must also
            // release channels that were visible only in the outgoing pose.
            if(incoming.Count==0 && !toRest && !Active){Cancel();return false;}
            bool any=false,continuing=Active;_blend=blend;_time=0;_hasReturning=false;_refreshNative=true;
            for(int i=0;i<_bones.Length;i++)
            {
                var bone=_rig.Bones[i];var t=bone.t;var b=_bones[i];bool next=incoming.Contains(bone.path);
                b.Enabled=t!=null && (next || outgoing.Contains(bone.path) || continuing && b.Enabled);
                b.ReturnToBase=(toRest || !next) && (b.Enabled || b.ReturnToBase);_hasReturning|=b.ReturnToBase;
                if(b.Enabled){any=true;b.Source=t.localPosition;b.SourceRotation=t.localRotation;b.SourceScale=t.localScale;}
                b.SourceVelocity=continuing && b.HasLast?b.Velocity:Vector3.zero;b.HasLast=false;b.Velocity=default;_bones[i]=b;
            }
            if(!any){Cancel();return false;}_finalSample=true;
            var job=Node.GetJobData<Job>();job.Request++;job.ToRest=toRest;job.Duration=blend.duration;job.Elapsed=job.Phase=0;Node.SetJobData(job);return true;
        }
        public void Advance(float dt)
        {
            if(!Active)return;
            _time=Mathf.Min(_time+dt,_blend.duration);
            var j=Node.GetJobData<Job>();j.Elapsed=_time;j.Phase=_blend.Evaluate(_time/_blend.duration);Node.SetJobData(j);
        }
        public void ReleaseToBase(){
            _refreshNative=_hasReturning=true;
            var j=Node.GetJobData<Job>();
            for(int i=0;i<_bones.Length;i++){var b=_bones[i];b.ReturnToBase=b.Watched;b.Enabled=false;_bones[i]=b;
            }
            j.Duration=0;Node.SetJobData(j);
        }
        public void Cancel(){_blend=default;_finalSample=false;var j=Node.GetJobData<Job>();j.Duration=0;Node.SetJobData(j);}
        public void Dispose(){if(_bones.IsCreated)_bones.Dispose();}
    }
}
