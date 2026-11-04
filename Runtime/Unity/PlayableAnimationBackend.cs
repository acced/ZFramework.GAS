using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Experimental.Animations;
using UnityEngine.Playables;

namespace ZFramework.GAS.Unity
{
    public interface ISkillMovementAdapter
    {
        void ApplyRootMotion(Vector3 deltaPosition, Quaternion deltaRotation);
    }
    [Serializable]
    public sealed class PlayableClipBinding
    {
        public string name;
        public AnimationClip clip;
        [HideInInspector] // Optional editor metadata. Runtime uses the clip, including per-cast overrides.
        public string[] posePaths = Array.Empty<string>();
        // Set by prefab import or scene build; an arbitrary old posePaths array is not proof of coverage.
        [SerializeField,HideInInspector] private AnimationClip playerPoseSource;
        // A Transform curve can write a whole TRS group even when the asset only
        // contains a single axis. Require the full group before dropping its handoff.
        [SerializeField,HideInInspector] private int[] playerChannels;
        [NonSerialized] private Dictionary<string,int> _channelMap;
        internal Dictionary<string,int> PlayerChannels(AnimationClip actual){
            if(PlayerPaths(actual)==null || posePaths.Length!=playerChannels?.Length)return null;
            if(_channelMap==null){_channelMap=new Dictionary<string,int>(posePaths.Length,StringComparer.Ordinal);
                for(int i=0;i<posePaths.Length;i++)_channelMap[posePaths[i]]=playerChannels[i];}
            return _channelMap;
        }
        internal string[] PlayerPaths(AnimationClip actual)=>actual!=null && actual==playerPoseSource?posePaths:null;
#if UNITY_EDITOR
        internal static string[] AuthoredPaths(AnimationClip source){
            var paths=new SortedSet<string>(StringComparer.Ordinal);
            if(source!=null)foreach(var b in UnityEditor.AnimationUtility.GetCurveBindings(source))
                if(b.type==typeof(Transform) && !string.IsNullOrEmpty(b.path))paths.Add(b.path);
            var result=new string[paths.Count];paths.CopyTo(result);return result;
        }
        public void PreparePlayerBindings(){
            var bits=new SortedDictionary<string,int>(StringComparer.Ordinal);
            if(clip!=null)foreach(var b in UnityEditor.AnimationUtility.GetCurveBindings(clip)){
                if(b.type!=typeof(Transform) || string.IsNullOrEmpty(b.path))continue;
                int group=b.propertyName.StartsWith("m_LocalPosition.",StringComparison.Ordinal)?7:
                    b.propertyName.StartsWith("m_LocalScale.",StringComparison.Ordinal)?56:
                    b.propertyName.StartsWith("m_LocalRotation.",StringComparison.Ordinal)?64:127;
                bits.TryGetValue(b.path,out int old);bits[b.path]=old|group;
            }
            posePaths=new string[bits.Count];playerChannels=new int[bits.Count];
            bits.Keys.CopyTo(posePaths,0);bits.Values.CopyTo(playerChannels,0);_channelMap=null;playerPoseSource=clip;
        }
        internal bool BakePosePaths()
        {
            var paths=AuthoredPaths(clip);
            if(posePaths!=null && System.Linq.Enumerable.SequenceEqual(paths,posePaths))return false;
            posePaths=paths;return true;
        }
#endif
    }
    [Serializable]
    public sealed class SkillControllerLayers
    {
        public RuntimeAnimatorController controller;
        [Tooltip("每层的 AvatarMask。预制体导入／构建时自动填写；动态创建配置时可显式提供。")]
        public AvatarMask[] masks;
        [SerializeField,HideInInspector] internal ControllerHandoffCoverage coverage;
        internal static RuntimeAnimatorController Base(RuntimeAnimatorController controller){
            while(controller is AnimatorOverrideController o)controller=o.runtimeAnimatorController;
            return controller;
        }
    }

    [Serializable]
    public sealed class PlayableAnimationBackend : AnimationBackend
    {
        public PlayableClipBinding[] clips = Array.Empty<PlayableClipBinding>();
        public AvatarMask mask;
        public bool additive;
        public bool rootMotion;
        [Tooltip("运行中可能切换到的基础 Controller。OverrideController 自动继承基础 Controller 的遮罩。")]
        public SkillControllerLayers[] alternativeControllers=Array.Empty<SkillControllerLayers>();
        [SerializeField,HideInInspector] private string[] authorRigPaths;
#if UNITY_EDITOR
        internal static AvatarMask[] ControllerMasks(RuntimeAnimatorController controller){
            while(controller is AnimatorOverrideController overrides)controller=overrides.runtimeAnimatorController;
            if(controller is not UnityEditor.Animations.AnimatorController native)return null;
            return Array.ConvertAll(native.layers,l=>l.avatarMask);
        }
        public void BakeRig(Animator animator){
            nativeController=animator!=null?animator.runtimeAnimatorController:null;nativeMasks=ControllerMasks(nativeController);nativeCoverage=ControllerHandoffCoverage.Bake(nativeController);
            if(alternativeControllers!=null)foreach(var binding in alternativeControllers)
                if(binding!=null){binding.masks=ControllerMasks(binding.controller);binding.coverage=ControllerHandoffCoverage.Bake(binding.controller);}
            if(animator==null){authorRigPaths=null;return;}
            var paths=new HashSet<string>(StringComparer.Ordinal);
            foreach(var t in animator.GetComponentsInChildren<Transform>(true))if(t!=animator.transform)
                paths.Add(UnityEditor.AnimationUtility.CalculateTransformPath(t,animator.transform));
            bool complete=animator.hasTransformHierarchy;
            if(animator.avatar!=null && UnityEditor.AssetImporter.GetAtPath(UnityEditor.AssetDatabase.GetAssetPath(animator.avatar)) is UnityEditor.ModelImporter model)
                {foreach(var path in model.transformPaths)if(!string.IsNullOrEmpty(path))paths.Add(path);complete|=model.transformPaths.Length>0;}
            if(!complete){authorRigPaths=null;return;}
            authorRigPaths=new string[paths.Count];paths.CopyTo(authorRigPaths);Array.Sort(authorRigPaths,StringComparer.Ordinal);
        }
#endif
        [SerializeField,HideInInspector] private RuntimeAnimatorController nativeController;
        [SerializeField,HideInInspector] private AvatarMask[] nativeMasks;
        [SerializeField,HideInInspector] private ControllerHandoffCoverage nativeCoverage;
        [Tooltip("额外监视的挂点。与当前片段实际驱动的挂点取并集，不会排除其他骨骼。")]
        public Transform[] exposedAttachments=Array.Empty<Transform>();
        public SkillAnimationRate updateRate;
        public override AnimationBlendCapabilities BlendCapabilities => AnimationBlendCapabilities.Curve | AnimationBlendCapabilities.SeekBlend | AnimationBlendCapabilities.Freeze |
            (rootMotion?AnimationBlendCapabilities.None:AnimationBlendCapabilities.Inertialize);
        public override string BackendId => "playables";
        public override bool Validate(GameObject host, out string error)
        {
            var animator = host != null ? host.GetComponentInChildren<Animator>(true) : null;
            error = animator == null ? "缺少 Animator" : clips == null || clips.Length == 0 ? "Playables 尚未绑定片段" : null;
            for (int i = 0; error == null && i < clips.Length; i++)
            {
                if (clips[i]?.clip == null || string.IsNullOrEmpty(clips[i].name)) error = "片段名称和 AnimationClip 必须填写";
                for (int j = 0; j < i; j++) if (clips[i]?.name == clips[j]?.name) error = "重复片段名";
            }
            if (error == null && rootMotion && animator.GetComponentInParent<ISkillMovementAdapter>() == null) error = "根运动需要项目提供 ISkillMovementAdapter";
            return error == null;
        }
        public override IAnimationDriver CreateDriver(GameObject host)
        {
            var driver=new PlayableAnimationDriver(host.GetComponentInChildren<Animator>(true), clips, mask, additive, rootMotion, updateRate,exposedAttachments,authorRigPaths,nativeController,nativeMasks,alternativeControllers);
            driver.SetBakedCoverage(nativeCoverage);return driver;
        }
    }

    /// <summary>Base controller runs at its own speed. Only the skill playable is stretched.</summary>
    public sealed class PlayableAnimationDriver : IAdvancedBlendingDriver, ISkillLayerDriver, IBoneWeightDriver, ISkillRigService, ISkillAnimationSpeed, IClipOverrideDriver, IAnimationDebugInfo, ISeekableAnimationDriver, IAnimationDriverLifetime, IDisposable
    {
        private readonly Animator _animator;
        private RuntimeAnimatorController _baseController;
        private readonly PlayableClipBinding[] _bindings;
        private readonly string[] _names;
        private readonly AnimationClip[] _clips;
        public SkillAnimationRate UpdateRate {get;set;}
        private float _accumulated, _periodClock, _lastPeriod;
        private static int _nextPhase;
        private readonly int _phase = System.Threading.Interlocked.Increment(ref _nextPhase);
        private int _lastOffset;
        private bool Due(SkillAnimationRate rate,long frame,float delta)
        {
            if(rate.mode!=SkillAnimationFrequency.EveryNSeconds)
            {
                _lastPeriod=0;
                return rate.mode!=SkillAnimationFrequency.EveryNFrames || (frame+_phase+rate.phaseOffset)%Math.Max(1,rate.frames)==0;
            }
            float period=Mathf.Max(.001f,rate.seconds);
            if(period!=_lastPeriod || _lastOffset!=rate.phaseOffset)
            {
                _lastPeriod=period; _lastOffset=rate.phaseOffset;
                _periodClock=(float)(Math.Abs((_phase+(long)rate.phaseOffset)*.6180339887498949)%1)*period;
            }
            _periodClock+=delta;
            if(_periodClock<period)return false;
            _periodClock%=period;return true;
        }
        private HeldBone[] _held;
        private bool _holding,_publishStop,_parked;
        private struct HeldBone
        {
            public Transform Bone;
            private Vector3 _position,_scale;private Quaternion _rotation;private bool _p,_s,_r;
            public void Before(){if(Bone==null)return;Bone.GetLocalPositionAndRotation(out _position,out _rotation);_scale=Bone.localScale;}
            public void After()
            {
                if(Bone==null)return;
                Bone.GetLocalPositionAndRotation(out var p,out var r);var s=Bone.localScale;
                _p=_position!=p;_r=_rotation!=r;_s=_scale!=s;_position=p;_rotation=r;_scale=s;
            }
            public void Hold()
            {
                if(Bone==null)return;
                if(_p)Bone.localPosition=_position;if(_r)Bone.localRotation=_rotation;if(_s)Bone.localScale=_scale;
            }
        }
        public bool AutomaticEvaluation { get; set; } = true;
        private SkillRootMotionRelay _rootMotion;
        private PlayableGraph _graph;
        private AnimationLayerMixerPlayable _layers;
        private AnimationMixerPlayable _skills;
        private struct PreviousPose : IAnimationJob
        {
            public void ProcessAnimation(AnimationStream stream) { }
            public void ProcessRootMotion(AnimationStream stream) { }
        }
        private AnimationPlayableOutput _output;
        private sealed class ClipState { public AnimationClipPlayable Clip; public float Weight, StartWeight; public bool Loop; public string Name; public HashSet<string> Channels; }
        private List<ClipState> _playing = new();
        private sealed class OutgoingLayer
        {
            public AnimationLayerMixerPlayable Layers;
            public AnimationMixerPlayable Skills;
            public List<ClipState> Clips;
            public SkillBoneLayer Bones;
            public float StartWeight;
            public Playable Base;
            public float Share;
        }
        private readonly List<OutgoingLayer> _outgoing = new();
        private AnimationMixerPlayable _cross;
        private SkillInertialPose _inertial;
#if UNITY_EDITOR
        // Editor benchmark control only: same native controller, graph, clip, LOD and Hold;
        // omit the extra stream handles/job. Never selectable in an asset or in a player.
        internal bool OmitHandoffForBenchmark {get;set;}
#endif
        private SkillInertialPose.Rig _poseRig;
        private bool _canHold=true;
        private ExposedSkillBones _extras;
        private readonly Transform[] _exposedAttachments;
        private readonly string[] _authorRigPaths;
        private readonly RuntimeAnimatorController _configuredController;
        private readonly AvatarMask[] _configuredMasks;
        private readonly SkillControllerLayers[] _alternatives;
        private HashSet<string>[] _layerChannels;
        private ControllerHandoffCoverage _coverage,_bakedCoverage;
        private readonly HashSet<string> _exclusive=new(StringComparer.Ordinal);
        // Capture once on entry, not on every frame. If the character changes controllers
        // while casting, we still know the pose underneath the outgoing skill.
        private readonly Dictionary<string,SkillInertialPose.EntryPose> _nativeEntries=new(StringComparer.Ordinal);
        private readonly List<string> _expiredEntries=new();
        private int _coverageRevision;
        internal void SetBakedCoverage(ControllerHandoffCoverage coverage){_bakedCoverage=coverage;ResolveCoverage();}
        private void ResolveCoverage(){
#if UNITY_EDITOR
            _coverage=ControllerHandoffCoverage.ForEditor(_baseController);
#else
            _coverage=_bakedCoverage;
            if(_coverage?.Matches(_baseController)!=true && _alternatives!=null)
                foreach(var entry in _alternatives)if(entry?.coverage?.Matches(_baseController)==true){_coverage=entry.coverage;break;}
#endif
            if(_coverage?.Matches(_baseController)!=true || !_coverage.Trusted)_coverage=null;
            _coverageRevision=ControllerHandoffCoverage.Revision;
        }
        private void ResolveControllerMasks(){
            var controller=SkillControllerLayers.Base(_baseController);
            var masks=controller==SkillControllerLayers.Base(_configuredController)?_configuredMasks:null;
            if(controller!=null && masks==null && _alternatives!=null)
                foreach(var entry in _alternatives)if(entry!=null && controller==SkillControllerLayers.Base(entry.controller)){masks=entry.masks;break;}
#if UNITY_EDITOR
            masks??=PlayableAnimationBackend.ControllerMasks(controller);
#endif
            ResolveCoverage();
            // null table = unknown controller; null entry = known, unmasked layer.
            // Unity allocates a string for GetTransformPath. Decode once when binding the
            // controller, never while iterating bones or transitioning between its states.
            _layerChannels=masks==null?null:new HashSet<string>[masks.Length];
            if(masks!=null)for(int layer=0;layer<masks.Length;layer++){
                var mask=masks[layer];if(mask==null || mask.transformCount==0)continue;
                var included=_layerChannels[layer]=new HashSet<string>(StringComparer.Ordinal);
                for(int i=0;i<mask.transformCount;i++)if(mask.GetTransformActive(i))included.Add(mask.GetTransformPath(i));
            }
        }
        private bool _evaluating,_scheduling,_draining;
        private bool _pendingGraph;
        private bool Sampling=>_pendingGraph || _evaluating || SkillAnimationManager.Sampling;
        public int TrackedAttachmentCount=>_extras?.Count??0;
        private Queue<Action> _afterSample;
        private void Enqueue(Action change){
            (_afterSample??=new Queue<Action>()).Enqueue(change);
            if(_afterSample.Count==1 && !_pendingGraph && !_evaluating && SkillAnimationManager.Sampling)SkillAnimationManager.AfterSampling(DrainChanges);
        }
        private void DrainChanges(){
            if(Sampling || _scheduling || _draining || _afterSample==null)return;
            _draining=true;
            try{
                // A queued Seek can sample and raise another event. Process that next turn.
                for(int count=_afterSample.Count;count>0 && !_disposed;count--){
                    try{_afterSample.Dequeue()();}catch(Exception error){Debug.LogException(error);}
                }
            }finally{_draining=false;}
        }
        private bool _poseJobConnected;
        internal bool HandoffJobConnected=>_poseJobConnected;
        internal int WatchedGenericBones=>_inertial?.ObservedCount??0;
        private void RefreshPoseJob(){
            bool needed=_inertial!=null && (_inertial.NeedsJob || !AutomaticEvaluation);
            if(_poseJobConnected!=needed){_poseJobConnected=needed;RewireOutput();}
        }
        private void Evaluate(float delta){
            RefreshPoseJob();
            _evaluating=true;SkillAnimationManager.BeginManual();
            try{
                if(_poseJobConnected)_inertial.CaptureNative(_layerChannels,_coverage);else _inertial?.SteadyBefore();
                _extras?.Before();_graph.Evaluate(delta);_extras?.After();
                if(_poseJobConnected)_inertial.Sampled();else _inertial?.SteadyAfter();
            }
            finally{_evaluating=false;SkillAnimationManager.EndManual();if(!_scheduling)DrainChanges();}
        }
        internal void ObserveBeforeAnimator(bool fixedStep){
            if((_extras==null && (_inertial==null || _inertial.ObservedCount==0)) || !AutomaticEvaluation || _parked || !IsValid || !_animator.isActiveAndEnabled ||
                (_animator.updateMode==AnimatorUpdateMode.Fixed)!=fixedStep)return;
            _inertial?.ObserveEquipment(_baseController!=null);_extras?.BeforeAnimator();
        }
        private void BeforeDetach(){_inertial?.ObserveEquipment();_extras?.BeforeAnimator();}
        private void RestoreDetached(){_nativeEntries.Clear();_inertial?.RestoreAndForget();_extras?.Restore();}

        private string[] _activePosePaths;
        private readonly SkillInertialPose.MaskPaths _maskPaths=new();
        private readonly HashSet<string> _visible=new(StringComparer.Ordinal);
        private readonly Stack<HashSet<string>> _freeChannels=new();
        private HashSet<string> _incoming=new(StringComparer.Ordinal);
        private HashSet<string> RentChannels()=>_freeChannels.Count==0?new HashSet<string>(StringComparer.Ordinal):_freeChannels.Pop();
        private void Release(ClipState state){_graph.DestroyPlayable(state.Clip);state.Channels.Clear();_freeChannels.Push(state.Channels);}
        private int _rigRevision;
        private void CaptureVisible()
        {
            _visible.Clear();
            void Include(List<ClipState> clips){foreach(var clip in clips)if(clip.Weight>0 && clip.Channels!=null)_visible.UnionWith(clip.Channels);}
            if(_layerWeight>0)Include(_playing);
            foreach(var old in _outgoing)Include(old.Clips);
        }
        /// <summary>Call after editing a clip's curves in a player (e.g. legacy SetCurve).</summary>
        public static void InvalidateClipBindings(AnimationClip clip){SkillInertialPose.Rig.Invalidate(clip);ControllerHandoffCoverage.ClipChanged();}
        /// <summary>Resolve an asset/rig combination during loading, before the first cast.</summary>
        public void Prewarm(AnimationClip clip)
        {
            if(!IsValid || _retiring || clip==null)return;
            if(Sampling){QueueWarm(clip);return;}
            _poseRig.RefreshHierarchy(true);ResolvePose(clip,PathsForAttachments(clip),out _);
            if(_coverage!=null)SkillChannelGroups(clip);
            // Preload native channel provenance as well, so newly entered controller states
            // don't have to probe a previously unseen clip while an old bone is handing off.
            if(_animator.runtimeAnimatorController is { } controller)
                foreach(var native in controller.animationClips)if(native!=null)_poseRig.ClipBindings(native);
        }
        private void QueueWarm(AnimationClip clip)=>Enqueue(()=>Prewarm(clip));
        private string[] PathsForAttachments(AnimationClip clip){
#if UNITY_EDITOR
            // Authoring curves can change before the prefab artifact is reimported.
            if(_animator.isHuman || !_animator.hasTransformHierarchy)return PlayableClipBinding.AuthoredPaths(clip);
#else
            if(!SkillInertialPose.Rig.HasRuntimeEdits(clip))foreach(var binding in _bindings){
                var paths=binding.PlayerPaths(clip);if(paths!=null)return paths;
            }
#endif
            return null;
        }
        private string[] ResolvePose(AnimationClip clip,string[] authored,out bool hold)
        {
            var paths=_poseRig.Resolve(clip,authored,out hold);
            if(_rigRevision!=_poseRig.Revision){_rigRevision=_poseRig.Revision;_held=null;_holding=false;_inertial?.Grow();}
            return paths;
        }
        private SkillBoneLayer _bones;
        private BoneWeightMask.Definition _weights,_nextWeights;
        public void SetNextBoneWeights(BoneWeightMask.Definition mask)=>_nextWeights=mask;
        private Playable LayerOutput=>_bones!=null?(Playable)_bones.After:_layers;
        private readonly List<AimLease> _aims=new();
        private sealed class AimLease:ISkillPresentationLease
        {
            public PlayableAnimationDriver Driver; public SkillExecutionContext Context;
            public AnimationScriptPlayable Node;
            public void Sample(float value){
                if(Driver==null || !Driver.IsValid)return;
                if(Driver.Sampling){QueueSample(value);return;}
                if(!Node.IsValid())return;var job=Node.GetJobData<SkillAimJob>();
                var services=Context.Services;var target=services.Target;
                job.Weight=target!=null || services.HasTargetPosition?Mathf.Clamp01(value):0;
                job.Target=target!=null?target.position:services.TargetPosition;Node.SetJobData(job);
            }
            private void QueueSample(float value)=>Driver.Enqueue(()=>Sample(value));
            public void Dispose(){
                var d=Driver;if(d==null)return;Driver=null;
                if(d.Sampling){d.Enqueue(()=>Detach(d));return;}
                Detach(d);
            }
            private void Detach(PlayableAnimationDriver d){
                if(!d.IsValid || !d._aims.Remove(this))return;
                if(Node.IsValid())d._graph.DestroyPlayable(Node);d.RewireOutput();if(d._stopping)d._publishStop=true;
            }
        }
        public ISkillPresentationLease Aim(SkillExecutionContext context,string chain)
        {
            if(!IsValid || _retiring || context==null || string.IsNullOrEmpty(chain))return null;
            var transform=_animator.transform.Find(chain);
            if(transform==null)foreach(var child in _animator.GetComponentsInChildren<Transform>())if(child.name==chain){transform=child;break;}
            if(transform==null)return null;
            var lease=new AimLease{Driver=this,Context=context};
            if(Sampling)Enqueue(()=>AttachAim(lease,transform));else AttachAim(lease,transform);
            return lease;
        }
        private void AttachAim(AimLease lease,Transform transform){
            if(lease.Driver==null || !IsValid || _retiring || transform==null)return;
            if(_parked){_output.SetTarget(_animator);_parked=false;_output.SetWeight(1);}
            lease.Node=AnimationScriptPlayable.Create(_graph,new SkillAimJob{Bone=_animator.BindStreamTransform(transform)},1);
            _aims.Add(lease);RewireOutput();
        }
        private void RewireOutput()
        {
            // Unwire all consumers first: the old tail may already feed Aim when we
            // insert a returning-bone job upstream of it.
            foreach(var aim in _aims)if(aim.Node.GetInput(0).IsValid())aim.Node.DisconnectInput(0);
            Playable tail=_cross.IsValid()?(Playable)_cross:LayerOutput;
            if(_inertial!=null)
            {
                var node=_inertial.Node;if(node.GetInput(0).IsValid())node.DisconnectInput(0);
                if(_poseJobConnected){_graph.Connect(tail,0,node,0);node.SetInputWeight(0,1);tail=node;}
            }
            foreach(var aim in _aims)
            {
                _graph.Connect(tail,0,aim.Node,0);aim.Node.SetInputWeight(0,1);tail=aim.Node;
            }
            _output.SetSourcePlayable(tail);
        }
        private Playable _basePose;
        private readonly AvatarMask _defaultMask;
        private readonly bool _defaultAdditive;
        private AvatarMask _nextMask, _mask;
        private bool _nextAdditive, _additive;
        private float _nextWeight = 1, _targetWeight = 1;
        private SkillBlend _blend;
        private double _clipSpeed;
        public AnimationBlendCapabilities Capabilities => AnimationBlendCapabilities.Curve | AnimationBlendCapabilities.Freeze | AnimationBlendCapabilities.SeekBlend |
            (_rootMotion==null && _animator!=null && !_animator.isHuman && _animator.hasTransformHierarchy?AnimationBlendCapabilities.Inertialize:AnimationBlendCapabilities.None);
        private bool StartInertial(SkillBlend blend,float seconds,bool stop=false)
        {
            if(seconds<=0 && _inertial?.Active==true)seconds=_inertial.Remaining;
            if(seconds>0 && _poseRig.Bones.Count>0 && (blend.mode==SkillBlendMode.Inertialize || _inertial?.Active==true) && (Capabilities & AnimationBlendCapabilities.Inertialize)!=0)
            {
                _inertial??=new SkillInertialPose(_animator,_graph,_poseRig);
                blend.duration=seconds;bool started=_inertial.Begin(blend,stop,_incoming,_visible);RefreshPoseJob();return started;
            }
            _inertial?.Cancel();RefreshPoseJob();return false;
        }
        public void ConfigureNextLayer(AvatarMask mask, bool additive, float weight)
        { _nextMask = mask ?? _defaultMask; _nextAdditive = additive || (mask == null && _defaultAdditive); _nextWeight = Mathf.Clamp01(weight); }

        private AnimationClipPlayable _current;
        private bool _retiring,_retireQueued,_finishPose;
        private bool _reclaimed;
        private int _retireTicket;
        private SkillBlend _exitBlend;
        internal void SetExitBlend(in SkillBlend blend){
            if(Sampling)QueueExitBlend(blend);else _exitBlend=blend;
        }
        private void QueueExitBlend(SkillBlend blend)=>Enqueue(()=>SetExitBlend(blend));
        internal bool Retire(){
            if(!IsValid || _pendingGraph || !AutomaticEvaluation || !_animator.isActiveAndEnabled || _parked)return false;
            int ticket=++_retireTicket;
            if(Sampling){
                // Keep Stop in FIFO order with any subsequent Play. Reclaim only cancels
                // disposal; asking for the driver is not a request to keep the old clip alive.
                _retireQueued=true;
                Enqueue(()=>{
                    if(!_stopping)Stop(_exitBlend);
                    if(ticket==_retireTicket){_retireQueued=false;_retiring=true;_reclaimed=false;}
                });return true;
            }
            _reclaimed=false;
            if(!_stopping){
                if(!_current.IsValid() || _exitBlend.duration<=0)return false;
                Stop(_exitBlend);
            }
            if(!_current.IsValid() && _inertial?.Active!=true)return false;
            _retiring=true;return true;
        }
        internal bool Reclaim(){
            if(!IsValid || (!_retiring && !_retireQueued) || _parked)return false;
            ++_retireTicket;_retireQueued=_retiring=false;
            if(Sampling)Enqueue(()=>_reclaimed=true);else _reclaimed=true;
            return true;
        }
        private float _fade, _elapsed, _layerWeight, _startWeight;
        private bool _stopping, _disposed, _rootMotionActive,_ordinaryRelease;
        private AnimationClip _nextClip;
        private float _localScale=1;
        public void SetLocalScale(float scale)
        { if(Sampling){QueueScale(scale);return;}if(!IsValid || _localScale==scale)return;_localScale=scale; _layers.SetSpeed(scale);foreach(var old in _outgoing)old.Layers.SetSpeed(scale); }
        private void QueueScale(float scale)=>Enqueue(()=>SetLocalScale(scale));
        public void SetNextClip(AnimationClip clip) => _nextClip=clip;
        public int CopyWeights(Span<ClipWeight> into)
        {
            int n=0;
            for(int i=0;i<_outgoing.Count;i++){var layer=_outgoing[i];foreach(var c in layer.Clips) if(n<into.Length) into[n++]=new ClipWeight(c.Name,c.Weight*layer.Layers.GetInputWeight(1)*_cross.GetInputWeight(i));}
            float share=_cross.IsValid()?_cross.GetInputWeight(_outgoing.Count):1;
            foreach(var c in _playing) if(n<into.Length) into[n++]=new ClipWeight(c.Name,c.Weight*_layerWeight*share);
            return n;
        }
        public bool IsValid => !_disposed && _animator != null && (_pendingGraph || _graph.IsValid());
        private Dictionary<string,int> SkillChannelGroups(AnimationClip clip){
#if !UNITY_EDITOR
            if(!SkillInertialPose.Rig.HasRuntimeEdits(clip))foreach(var binding in _bindings)
                if(binding.PlayerChannels(clip) is { } baked)return baked;
#endif
            return _poseRig.ClipBindings(clip).Channels;
        }
        private void TrackIncoming(AnimationClip clip){
            _exclusive.Clear();
            var bindings=_coverage!=null && _layerChannels?.Length>0 && _animator.cullingMode==AnimatorCullingMode.AlwaysAnimate
                ?SkillChannelGroups(clip):null;
            foreach(var bone in _poseRig.Bones){
                var path=bone.path;if(!_incoming.Contains(path) || bone.t==null)continue;
                if(bindings!=null && (_layerChannels[0]==null || _layerChannels[0].Contains(path)) &&
                   bindings.TryGetValue(path,out int channels) && _coverage.Covers(path,channels)){
                    if(!_nativeEntries.ContainsKey(path))_nativeEntries.Add(path,new SkillInertialPose.EntryPose(bone.t));
                }else _exclusive.Add(path);
            }
            if(_exclusive.Count!=0){
                _inertial??=new SkillInertialPose(_animator,_graph,_poseRig);
                _inertial.Watch(_exclusive,_nativeEntries);RefreshPoseJob();
                foreach(string path in _exclusive)_nativeEntries.Remove(path);
            }
        }
        private void RecheckCoverage(){
            if(_nativeEntries.Count==0)return;
            if(_coverage==null || (_layerChannels==null || _layerChannels.Length==0) || _animator.cullingMode!=AnimatorCullingMode.AlwaysAnimate){LoseCoverage();return;}
            _exclusive.Clear();
            void Check(List<ClipState> states){
                foreach(var state in states){
                    var channels=SkillChannelGroups(state.Clip.GetAnimationClip());
                    foreach(string path in state.Channels)if(_nativeEntries.ContainsKey(path) &&
                        ((_layerChannels[0]!=null && !_layerChannels[0].Contains(path)) || !channels.TryGetValue(path,out int bits) || !_coverage.Covers(path,bits)))_exclusive.Add(path);
                }
            }
            Check(_playing);foreach(var layer in _outgoing)Check(layer.Clips);
            PromoteToWatched();
        }
        private void PromoteToWatched(){
            if(_exclusive.Count==0)return;
            _inertial??=new SkillInertialPose(_animator,_graph,_poseRig);
            _inertial.Watch(_exclusive,_nativeEntries);foreach(string path in _exclusive)_nativeEntries.Remove(path);
            RefreshPoseJob();if(_stopping)_ordinaryRelease=true;
        }
        private void LoseCoverage(){
            if(_nativeEntries.Count==0)return;
            _exclusive.Clear();_exclusive.UnionWith(_nativeEntries.Keys);
            PromoteToWatched();
        }
        private void SyncController(){
            var current=_animator.runtimeAnimatorController;
            if(current==_baseController){
                if(_coverageRevision!=ControllerHandoffCoverage.Revision){ResolveControllerMasks();RecheckCoverage();}
                else if(_nativeEntries.Count!=0 && _animator.cullingMode!=AnimatorCullingMode.AlwaysAnimate)LoseCoverage();
                return;
            }
            LoseCoverage();
            bool present=current!=null;_baseController=current;ResolveControllerMasks();
            _output.SetAnimationStreamSource(present?AnimationStreamSource.PreviousInputs:AnimationStreamSource.DefaultValues);
            _output.SetSortingOrder(100);
            void SetBase(AnimationLayerMixerPlayable layer,SkillBoneLayer bones,ref Playable pose){
                if(bones!=null)bones.UseNativeBase(present);
                if(present && !pose.IsValid()){
                    pose=AnimationScriptPlayable.Create(_graph,new PreviousPose());
                    var input=bones!=null?(Playable)bones.Before:layer;
                    _graph.Connect(pose,0,input,0);input.SetInputWeight(0,1);
                }else if(!present && pose.IsValid()){
                    var input=bones!=null?(Playable)bones.Before:layer;
                    input.DisconnectInput(0);_graph.DestroyPlayable(pose);pose=Playable.Null;
                }
            }
            SetBase(_layers,_bones,ref _basePose);
            foreach(var old in _outgoing)SetBase(old.Layers,old.Bones,ref old.Base);
        }
        private bool _restoreRootMotion;
        public PlayableAnimationDriver(Animator animator, PlayableClipBinding[] clips, AvatarMask mask = null, bool additive = false, bool rootMotion = false, SkillAnimationRate updateRate = default,Transform[] exposedAttachments=null,string[] authorRigPaths=null,RuntimeAnimatorController configuredController=null,AvatarMask[] nativeMasks=null,SkillControllerLayers[] alternativeControllers=null)
        {
            _alternatives=alternativeControllers;_exposedAttachments=exposedAttachments==null?null:(Transform[])exposedAttachments.Clone();_authorRigPaths=authorRigPaths;_configuredController=configuredController;_configuredMasks=nativeMasks;_bindings=clips;
            UpdateRate=updateRate;
            _animator = animator != null ? animator : throw new ArgumentNullException(nameof(animator));
            _defaultMask = _mask = _nextMask = mask; _defaultAdditive = _additive = _nextAdditive = additive;
            _baseController = animator.runtimeAnimatorController;ResolveControllerMasks();
            _names = new string[clips.Length]; _clips = new AnimationClip[clips.Length];
            for (int i = 0; i < clips.Length; i++) { _names[i] = clips[i].name; _clips[i] = clips[i].clip; }
            if(SkillAnimationManager.Sampling){
                _pendingGraph=true;
                SkillAnimationManager.AfterSampling(()=>{if(!_disposed && _animator!=null)Initialize(rootMotion);});
            }else Initialize(rootMotion);
        }
        private void Initialize(bool rootMotion){
            _pendingGraph=false;var animator=_animator;var mask=_defaultMask;bool additive=_defaultAdditive;
            _poseRig=new SkillInertialPose.Rig(animator,_authorRigPaths);
            _graph = PlayableGraph.Create("GAS Skill Animation"); _graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            _layers = AnimationLayerMixerPlayable.Create(_graph, 2); _skills = AnimationMixerPlayable.Create(_graph, 0);
            _output = AnimationPlayableOutput.Create(_graph, "Skill", null);
            if (_baseController != null)
            {
                // Consume the preceding Animator output without replacing its state machine.
                _output.SetAnimationStreamSource(AnimationStreamSource.PreviousInputs);
                _output.SetSortingOrder(100);
                var basePose = AnimationScriptPlayable.Create(_graph, new PreviousPose());
                _basePose = basePose;
                _graph.Connect(basePose, 0, _layers, 0); _layers.SetInputWeight(0, 1);
            }
            _graph.Connect(_skills, 0, _layers, 1); _layers.SetInputWeight(1, 0);
            if (mask != null) _layers.SetLayerMaskFromAvatarMask(1, mask);
            _layers.SetLayerAdditive(1, additive);
            _output.SetSourcePlayable(_layers); _output.SetTarget(animator); _graph.Play();
            if (_baseController != null) _output.SetWeight(0);
            SkillAnimationManager.Register(this);
            if (rootMotion)
            {
                _rootMotion = animator.gameObject.AddComponent<SkillRootMotionRelay>();
            }
            DrainChanges();
        }
        public float Play(string clipName, float duration, bool loop) => Play(clipName, duration, loop, 0);
        public float Play(string clipName, float duration, bool loop, float blendIn)
            => Play(clipName, duration, loop, new SkillBlend(blendIn));
        public float Play(string clipName, float duration, bool loop, in SkillBlend blendIn)
        {
            if (!IsValid || _retiring) throw new ObjectDisposedException(nameof(PlayableAnimationDriver));
            if(Sampling)return QueuePlay(clipName,duration,loop,blendIn);
            _exitBlend=default;
            int index = Array.IndexOf(_names, clipName); var animation = _nextClip!=null?_nextClip:index>=0?_clips[index]:null; _nextClip=null;
            if(animation==null) return 0;
            SyncController();_inertial?.RememberOutgoing();CaptureVisible();_poseRig.RefreshHierarchy();
            var authored=PathsForAttachments(animation);_activePosePaths=ResolvePose(animation,authored,out _canHold);
            _incoming=RentChannels();
            _maskPaths.Select(_incoming,_activePosePaths,_nextMask,_nextWeights);
            if(_animator.isHuman || !_animator.hasTransformHierarchy){
                (_extras??=new ExposedSkillBones(_animator)).Begin(_incoming,_exposedAttachments,
                    authored!=null || _animator.hasTransformHierarchy || _authorRigPaths?.Length>0);
            }else if(_incoming.Count>0
#if UNITY_EDITOR
                && !OmitHandoffForBenchmark
#endif
            ){
                TrackIncoming(animation);
            }
            _finishPose=true;_accumulated=0;_holding=false;_publishStop=false;
            if(_parked){_output.SetTarget(_animator);_parked=false;}
            _output.SetWeight(1);
            SetLocalScale(1); BeginFade(blendIn);
            if (_cross.IsValid() || _mask != _nextMask || _additive != _nextAdditive || _weights!=_nextWeights) ChangeLayer();
            if(_mask!=null)_layers.SetLayerMaskFromAvatarMask(1,_mask);
            _nextWeights=null;
            _targetWeight = _nextWeight;
            ConfigureNextLayer(null, false, 1);
            LimitClips();
            _current = AnimationClipPlayable.Create(_graph, animation);
            float actual = duration > 0 ? duration : animation.length;
            _clipSpeed = actual > 0 ? animation.length / actual : 1;
            _current.SetSpeed(_clipSpeed);
            int port = _playing.Count; _skills.SetInputCount(port + 1); _graph.Connect(_current, 0, _skills, port);
            _playing.Add(new ClipState { Clip = _current, Loop = loop, Name=clipName,Channels=_incoming });
            _startWeight = _layerWeight;
            float remaining=_reclaimed && _stopping?Mathf.Max(_fade-_elapsed,_inertial?.Remaining??0):0;
            _fade = Mathf.Max(remaining,blendIn.duration * (actual / Mathf.Max(.0001f, animation.length)));_reclaimed=false; _elapsed = 0; _stopping = false;
            if(StartInertial(blendIn,_fade))_fade=0;
            if (_rootMotion != null)
            {
                if (!_rootMotionActive) _restoreRootMotion = _animator.applyRootMotion;
                _rootMotionActive = true; _animator.applyRootMotion = true;
                _rootMotion.Movement = _animator.GetComponentInParent<ISkillMovementAdapter>();
            }
            Update(0); return actual;
        }
        private float QueuePlay(string name,float duration,bool loop,SkillBlend blend){
            var i=Array.IndexOf(_names,name);var replacement=_nextClip;var clip=replacement!=null?replacement:i>=0?_clips[i]:null;
            var mask=_nextMask;var weights=_nextWeights;bool add=_nextAdditive;float weight=_nextWeight;
            _nextClip=null;_nextWeights=null;ConfigureNextLayer(null,false,1);
            if(clip==null)return 0;
            Enqueue(()=>{
                var futureClip=_nextClip;var futureWeights=_nextWeights;var futureMask=_nextMask;var futureAdd=_nextAdditive;var futureWeight=_nextWeight;
                try{_nextClip=replacement;_nextWeights=weights;_nextMask=mask;_nextAdditive=add;_nextWeight=weight;
                    if(!_retiring)Play(name,duration,loop,blend);}
                finally{_nextClip=futureClip;_nextWeights=futureWeights;_nextMask=futureMask;_nextAdditive=futureAdd;_nextWeight=futureWeight;}
            });
            return duration>0?duration:clip.length;
        }
        // Cross-fade complete masked poses, each relative to the base. Fading the old
        // layer under the new one would count the base twice at the midpoint.
        private void ChangeLayer()
        {
            LimitLayers();
            int index=_outgoing.Count;
            var old=new OutgoingLayer { Layers=_layers,Skills=_skills,Clips=_playing,StartWeight=_layerWeight,Bones=_bones,Base=_basePose };
            foreach(var aim in _aims) if(aim.Node.GetInput(0).IsValid())aim.Node.DisconnectInput(0);
            if(_inertial!=null && _inertial.Node.GetInput(0).IsValid())_inertial.Node.DisconnectInput(0);
            if(!_cross.IsValid())
            {
                _cross=AnimationMixerPlayable.Create(_graph,2);
                _graph.Connect(LayerOutput,0,_cross,0);_cross.SetInputWeight(0,1);
            }
            else _cross.SetInputCount(index+2);
            for(int i=0;i<index;i++)_outgoing[i].Share=_cross.GetInputWeight(i);
            old.Share=_cross.GetInputWeight(index);_outgoing.Add(old);
            _bones=null; _weights=_nextWeights;
            var next = AnimationLayerMixerPlayable.Create(_graph, 2);
            _basePose=_baseController!=null?(Playable)AnimationScriptPlayable.Create(_graph,new PreviousPose()):Playable.Null;
            if(_weights!=null)
            {
                _bones=new SkillBoneLayer(_graph,_animator,_weights,_basePose,next);
                if(_baseController!=null)_bones.UseNativeBase();
            }
            else if(_basePose.IsValid()){_graph.Connect(_basePose,0,next,0);next.SetInputWeight(0,1);}
            _layers = next; _skills = AnimationMixerPlayable.Create(_graph, 0);
            _graph.Connect(_skills, 0, next, 1); next.SetInputWeight(1, 0);
            _mask = _nextMask; _additive = _nextAdditive;
            if (_mask != null) next.SetLayerMaskFromAvatarMask(1, _mask);
            next.SetLayerAdditive(1, _additive);next.SetSpeed(_localScale);
            _graph.Connect(LayerOutput,0,_cross,index+1);_cross.SetInputWeight(index+1,0);RewireOutput();
            _playing = new List<ClipState>(); _layerWeight = 0;
        }
        // Repeated interruptions must not retain an unbounded number of playables/native masks.
        // When all slots are busy, retire the least visible input and renormalize the surviving pose.
        private const int MaxHistory=4;
        private void LimitLayers()
        {
            if(_outgoing.Count<MaxHistory || !_cross.IsValid())return;
            int count=_outgoing.Count+1,drop=0;
            float[] shares=new float[count];
            for(int i=0;i<count;i++){shares[i]=_cross.GetInputWeight(i); if(i<_outgoing.Count && shares[i]<shares[drop])drop=i; _cross.DisconnectInput(i);}
            var old=_outgoing[drop];_outgoing.RemoveAt(drop);
            DestroyOld(old);_cross.SetInputCount(count-1);
            float sum=1-shares[drop];
            for(int i=0;i<count-1;i++)
            {
                Playable input=i==_outgoing.Count?LayerOutput:(_outgoing[i].Bones!=null?(Playable)_outgoing[i].Bones.After:_outgoing[i].Layers);
                _graph.Connect(input,0,_cross,i);
                _cross.SetInputWeight(i,sum>.00001f?shares[i<drop?i:i+1]/sum:(i==count-2?1:0));
            }
        }
        private void LimitClips()
        {
            if(_playing.Count<MaxHistory)return;
            int drop=0; for(int i=1;i<_playing.Count;i++)if(_playing[i].Weight<_playing[drop].Weight)drop=i;
            for(int i=0;i<_playing.Count;i++)_skills.DisconnectInput(i);
            var old=_playing[drop];_playing.RemoveAt(drop);Release(old);
            _skills.SetInputCount(_playing.Count);float sum=1-old.Weight;
            for(int i=0;i<_playing.Count;i++){
                var c=_playing[i];c.StartWeight=c.Weight=sum>.00001f?c.Weight/sum:(i==_playing.Count-1?1:0);
                _graph.Connect(c.Clip,0,_skills,i);_skills.SetInputWeight(i,c.Weight);}
        }
        private void DestroyOld(OutgoingLayer layer)
        {
            foreach(var clip in layer.Clips)Release(clip);
            layer.Bones?.Destroy(_graph);_graph.DestroyPlayable(layer.Skills);_graph.DestroyPlayable(layer.Layers);
            if(layer.Base.IsValid())_graph.DestroyPlayable(layer.Base);
        }
        private void BeginFade(in SkillBlend blend)
        {
            _blend = blend;
            foreach (var layer in _outgoing)
            {
                layer.StartWeight = layer.Layers.GetInputWeight(1);
                if (blend.mode == SkillBlendMode.Freeze) foreach (var clip in layer.Clips) clip.Clip.SetSpeed(0);
            }
            foreach (var clip in _playing)
            {
                clip.StartWeight = clip.Weight;
                if (blend.mode == SkillBlendMode.Freeze) clip.Clip.SetSpeed(0);
            }
        }
        private void ReleaseOutgoing()
        {
            if (_outgoing.Count == 0) return;
            _graph.DestroyPlayable(_cross);_cross=default;
            foreach (var layer in _outgoing) DestroyOld(layer);
            _outgoing.Clear();
            RewireOutput();
        }
        public bool SeekBlend(float normalizedTime, in SkillBlend blend)
        {
            if (!IsValid || _retiring || blend.duration<=0)return false;
            if(Sampling){QueueSeekBlend(normalizedTime,blend);return true;}
            if(!_current.IsValid() || _stopping)return false;
            var animation = _current.GetAnimationClip();
            var previous=_playing[_playing.Count-1]; bool loop = previous.Loop;
            CaptureVisible();_incoming=RentChannels();_incoming.UnionWith(previous.Channels);BeginFade(blend);
            if(_cross.IsValid()){_nextMask=_mask;_nextAdditive=_additive;_nextWeights=_weights;ChangeLayer();_nextWeights=null;ConfigureNextLayer(null,false,1);}
            LimitClips();
            _current = AnimationClipPlayable.Create(_graph, animation); _current.SetSpeed(_clipSpeed);
            _current.SetTime(Mathf.Clamp01(normalizedTime) * animation.length);
            int port = _playing.Count; _skills.SetInputCount(port+1); _graph.Connect(_current, 0, _skills, port);
            _playing.Add(new ClipState { Clip = _current, Loop = loop, Name=previous.Name,Channels=_incoming });
            _elapsed = 0; _fade = StartInertial(blend,blend.duration)?0:blend.duration; _startWeight = _layerWeight; Update(0);
            return true;
        }
        private void QueueSeekBlend(float time,SkillBlend blend)=>Enqueue(()=>SeekBlend(time,blend));
        private void QueuePosition(float time,bool entry)=>Enqueue(()=>PositionClip(time,entry));
        public void Advance(float normalizedTime) { }
        internal void SetEntryTime(float normalizedTime) => PositionClip(normalizedTime, true);
        public void Seek(float normalizedTime) => PositionClip(normalizedTime, false);
        private void PositionClip(float normalizedTime, bool preserveEntryBlend)
        {
            if (!IsValid || _retiring) return;
            if(Sampling){QueuePosition(normalizedTime,preserveEntryBlend);return;}
            if(!_current.IsValid())return;
            if(!preserveEntryBlend)_inertial?.Cancel();
            if (_rootMotion != null) _rootMotion.Suppressed = true;
            try { _current.SetTime(_current.GetAnimationClip().length * normalizedTime); Evaluate(0); }
            finally { if (_rootMotion != null) _rootMotion.Suppressed = false; }
        }
        public void Stop() => Stop(0);
        public void Stop(float blendOut) => Stop(new SkillBlend(blendOut));
        public void Stop(in SkillBlend blendOut)
        {
            if (_disposed || _retiring) return;
            if(Sampling){QueueStop(blendOut);return;}
            RestoreRootMotion();
            if (!IsValid) return;
            SyncController();_holding=false;_publishStop=_finishPose=true;
            SetLocalScale(1);_inertial?.RememberOutgoing();CaptureVisible();BeginFade(blendOut);
            _fade = Math.Max(0, blendOut.duration); _elapsed = 0; _stopping = true; _startWeight = _layerWeight;
            _ordinaryRelease=false;
            if(StartInertial(blendOut,_fade,true))_fade=0;
            else _ordinaryRelease=_inertial!=null;
            Update(0);
        }
        private void QueueStop(SkillBlend blend)=>Enqueue(()=>Stop(blend));
        internal void ScheduledUpdate(long frame,float delta,ISkillAnimationLodPolicy policy)
        {
            DrainChanges();_scheduling=true;
            try{Step(frame,delta,policy);}finally{_scheduling=false;DrainChanges();}
        }
        private void Step(long frame,float delta,ISkillAnimationLodPolicy policy)
        {
            if(_retiring && (!IsValid || !_animator.isActiveAndEnabled)){Dispose();return;}
            if(!AutomaticEvaluation || !IsValid || !_animator.isActiveAndEnabled) return;
            if(!_publishStop && !_current.IsValid() && _outgoing.Count==0 && _inertial?.Active!=true && _aims.Count==0)
            {_holding=false;_accumulated=0;return;}
            _accumulated+=delta;
            var rate=_rootMotionActive || !_canHold && _baseController!=null ? default : policy?.Rate(_animator)??UpdateRate;
            bool due=Due(rate,frame,delta);
            // A separate live Animator continues to write its base pose on skipped frames.
            // Hold just the sampled skill's changed local channels; leave unmasked limbs and character movement alone.
            bool hold=_baseController!=null && (rate.mode==SkillAnimationFrequency.EveryNSeconds ||
                rate.mode==SkillAnimationFrequency.EveryNFrames && rate.frames>1);
            if(!_publishStop && !(hold && !_holding) && !due)
            {if(hold && _holding)for(int i=0;i<_held.Length;i++)_held[i].Hold();return;}
            float step=_accumulated; _accumulated=0; Update(step);
            if(!IsValid)return;
            if(hold)
            {
                if(_held==null){
                    _held=new HeldBone[_poseRig.Bones.Count];
                    for(int i=0;i<_held.Length;i++)_held[i].Bone=_poseRig.Bones[i].t;
                }
                for(int i=0;i<_held.Length;i++)_held[i].Before();
            }
            Evaluate(step);
            if(_finishPose && _elapsed>=_fade && _inertial?.Active!=true){
                _finishPose=false;CaptureVisible();
                // A live Aim can keep this output connected even after all clips have gone.
                if(!_stopping || _aims.Count!=0){
                    if(_nativeEntries.Count>0){
                        _expiredEntries.Clear();foreach(string path in _nativeEntries.Keys)if(!_visible.Contains(path))_expiredEntries.Add(path);
                        foreach(string path in _expiredEntries)_nativeEntries.Remove(path);
                    }
                    if(_inertial?.FinishUnused(_visible)==true && _baseController==null){_output.SetTarget(null);_output.SetTarget(_animator);}
                    _extras?.FinishUnused(_stopping);
                }
            }
            if(_stopping && !_current.IsValid() && _inertial?.Active!=true && (_aims.Count==0 || _retiring) && !_parked){
                BeforeDetach();_output.SetWeight(0);_output.SetTarget(null);_parked=true;RestoreDetached();if(_retiring){Dispose();return;}
            }
            _publishStop=false;SkillAnimationManager.EvaluationCount++;
            _holding=hold;
            if(hold)for(int i=0;i<_held.Length;i++)_held[i].After();
        }
        internal void Update(float dt)
        {
            if (!IsValid) return;
            SyncController();
            foreach (var state in _playing)
            {
                var clip = state.Clip; var animation = clip.GetAnimationClip(); if (animation == null) continue;
                float length = animation.length;
                if (length > 0 && clip.GetTime() >= length)
                {
                    if (state.Loop) clip.SetTime(clip.GetTime() % length);
                    else { clip.SetTime(length); clip.SetSpeed(0); }
                }
            }
            _elapsed += dt*_localScale;
            _inertial?.Advance(dt*_localScale);
            bool complete = _fade <= 0 || _elapsed >= _fade;
            float t = complete ? 1 : _blend.Evaluate(_elapsed / _fade);
            bool crossing=_cross.IsValid();
            for(int i=0;i<_outgoing.Count;i++)
            {
                var layer=_outgoing[i];
                if(_stopping)layer.Layers.SetInputWeight(1,layer.StartWeight*(1-t));
                else _cross.SetInputWeight(i,layer.Share*(1-t));
            }
            if(crossing && !_stopping)_cross.SetInputWeight(_outgoing.Count,t);
            if (complete) ReleaseOutgoing();
            if (_stopping)
            {
                _layerWeight = _startWeight * (1 - t); _layers.SetInputWeight(1, _layerWeight);
                if (complete)
                {
                    ReleaseClips(false);
                    if(_ordinaryRelease){_ordinaryRelease=false;_inertial.ReleaseToBase();RefreshPoseJob();}
                }
            }
            else if (_current.IsValid())
            {
                for (int i = 0; i < _playing.Count; i++)
                {
                    var clip = _playing[i];
                    clip.Weight = i == _playing.Count - 1 ? (_playing.Count == 1 ? 1 : t) : clip.StartWeight * (1 - t);
                    _skills.SetInputWeight(i, clip.Weight);
                }
                _layerWeight = crossing?_targetWeight:Mathf.Lerp(_startWeight, _targetWeight, t); _layers.SetInputWeight(1, _layerWeight);
                if (complete && _playing.Count > 1) ReleaseClips(true);
            }
        }
        private void ReleaseClips(bool keepCurrent)
        {
            var keep = keepCurrent && _playing.Count > 0 ? _playing[_playing.Count - 1] : null;
            for (int i = 0; i < _playing.Count; i++)
            { _skills.DisconnectInput(i); if (_playing[i] != keep) Release(_playing[i]); }
            _playing.Clear(); _skills.SetInputCount(keep == null ? 0 : 1);
            if (keep != null) { _playing.Add(keep); _graph.Connect(keep.Clip, 0, _skills, 0); _skills.SetInputWeight(0, 1); }
            else _current = default;
        }
        private void RestoreRootMotion()
        {
            if (_rootMotion != null) _rootMotion.Movement = null;
            if (_rootMotionActive && _animator != null) _animator.applyRootMotion = _restoreRootMotion;
            _rootMotionActive = false;
        }
        public void Dispose()
        {
            if(_disposed)return;
            if(_pendingGraph){_disposed=true;_pendingGraph=false;_afterSample?.Clear();return;}
            if(Sampling){Enqueue(Dispose);return;}
            _disposed=true;_afterSample?.Clear();
            RestoreRootMotion();
            if(!_parked)BeforeDetach();
            if (_output.IsOutputValid()) _output.SetTarget(null);
            if (_graph.IsValid()) _graph.Destroy();
            if(!_parked)RestoreDetached();
            _inertial?.Dispose();
            _bones?.Dispose(); foreach(var layer in _outgoing) layer.Bones?.Dispose(); _outgoing.Clear();
            foreach(var aim in _aims) aim.Driver=null; _aims.Clear();
            if (_rootMotion != null)
            {
                _rootMotion.Movement = null;
                if (Application.isPlaying) UnityEngine.Object.Destroy(_rootMotion); else UnityEngine.Object.DestroyImmediate(_rootMotion);
            }
            SkillAnimationManager.Unregister(this);
        }
    }


}
