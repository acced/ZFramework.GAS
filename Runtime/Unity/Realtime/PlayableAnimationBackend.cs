using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Experimental.Animations;
using UnityEngine.Playables;

namespace ZFramework.GAS.Unity
{
    public interface ISkillMovementAdapter { void ApplyRootMotion(Vector3 deltaPosition, Quaternion deltaRotation); }
    [Serializable] public sealed class PlayableClipBinding { public string name; public AnimationClip clip; }
    [Serializable] public sealed class PlayableAnimationBackend : AnimationBackend
    {
        public PlayableClipBinding[] clips = Array.Empty<PlayableClipBinding>();
        public AvatarMask mask;
        public bool additive, rootMotion;
        public SkillAnimationRate updateRate;
        public override AnimationBlendCapabilities BlendCapabilities => AnimationBlendCapabilities.Curve | AnimationBlendCapabilities.SeekBlend | AnimationBlendCapabilities.Freeze;
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
        public override IAnimationDriver CreateDriver(GameObject host) =>
            new PlayableAnimationDriver(host.GetComponentInChildren<Animator>(true), clips, mask, additive, rootMotion, updateRate);
    }

    /// <summary>Manual skill overlay. The native Animator keeps its controller, parameters and pending triggers.</summary>
    public sealed class PlayableAnimationDriver : IAdvancedBlendingDriver, ISkillLayerDriver, IBoneWeightDriver, ISkillRigService,
        ISkillAnimationSpeed, IClipOverrideDriver, IAnimationDebugInfo, ISeekableAnimationDriver, IAnimationDriverLifetime, IDisposable
    {
        private readonly Animator _animator;
        private readonly bool _native;
        private readonly DirectorUpdateMode _savedMode;
        private readonly string[] _names;
        private readonly AnimationClip[] _clips;
        public SkillAnimationRate UpdateRate { get; set; }
        public bool AutomaticEvaluation { get; set; } = true;
        public bool IsValid => !_disposed && _animator != null && _graph.IsValid();
        public AnimationBlendCapabilities Capabilities => AnimationBlendCapabilities.Curve | AnimationBlendCapabilities.SeekBlend | AnimationBlendCapabilities.Freeze;
        private float _accumulated, _periodClock, _lastPeriod;
        private static int _nextPhase;
        private readonly int _phase = System.Threading.Interlocked.Increment(ref _nextPhase);
        private int _lastOffset;
        private SkillRootMotionRelay _rootMotion;
        private bool _rootMotionActive, _restoreRootMotion, _disposed, _stopping, _settled, _publishNextPose;
        private PlayableGraph _graph;
        private AnimationPlayableOutput _output;
        private AnimationLayerMixerPlayable _layers;
        private AnimationMixerPlayable _skills, _cross;
        private AnimationClipPlayable _current;
        private Playable _basePose;
        private struct ClipState { public AnimationClipPlayable Clip; public float Length, Weight, StartWeight; public bool Loop; public string Name; }
        private List<ClipState> _playing = new();
        private sealed class OutgoingLayer
        {
            public AnimationLayerMixerPlayable Layers;
            public AnimationMixerPlayable Skills;
            public List<ClipState> Clips;
            public SkillBoneLayer Bones;
            public float StartWeight, Share;
            public Playable Base;
        }
        private readonly List<OutgoingLayer> _outgoing = new();
        private SkillBoneLayer _bones;
        private BoneWeightMask.Definition _weights, _nextWeights;
        private readonly AvatarMask _defaultMask;
        private readonly bool _defaultAdditive;
        private AvatarMask _nextMask, _mask;
        private bool _nextAdditive, _additive;
        private float _nextWeight = 1, _targetWeight = 1, _fade, _elapsed, _layerWeight, _startWeight, _localScale = 1;
        private SkillBlend _blend;
        private double _clipSpeed;
        private AnimationClip _nextClip;
        private Playable LayerOutput => _bones != null ? (Playable)_bones.After : _layers;
        private void AssertMutable() => GasAssert.That(!SkillAnimationManager.Sampling, "Do not mutate skill animation from animation callbacks.");
        private void Release(ClipState clip) => _graph.DestroyPlayable(clip.Clip);
        public void SetNextBoneWeights(BoneWeightMask.Definition mask) => _nextWeights = mask;
        public void SetNextClip(AnimationClip clip) => _nextClip = clip;
        public void SetLocalScale(float scale)
        { AssertMutable(); if (!IsValid || _localScale == scale) return; _localScale = scale; _skills.SetSpeed(scale); foreach (var old in _outgoing) old.Skills.SetSpeed(scale); }
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
        public void ConfigureNextLayer(AvatarMask mask, bool additive, float weight)
        { _nextMask = mask ?? _defaultMask; _nextAdditive = additive || (mask == null && _defaultAdditive); _nextWeight = Mathf.Clamp01(weight); }
        public int CopyWeights(Span<ClipWeight> into)
        {
            int n=0;
            for(int i=0;i<_outgoing.Count;i++){var layer=_outgoing[i];foreach(var c in layer.Clips) if(n<into.Length) into[n++]=new ClipWeight(c.Name,c.Weight*layer.Layers.GetInputWeight(1)*_cross.GetInputWeight(i));}
            float share=_cross.IsValid()?_cross.GetInputWeight(_outgoing.Count):1;
            foreach(var c in _playing) if(n<into.Length) into[n++]=new ClipWeight(c.Name,c.Weight*_layerWeight*share);
            return n;
        }
        private readonly List<AimLease> _aims=new();
        private sealed class AimLease:ISkillPresentationLease
        {
            public PlayableAnimationDriver Driver; public SkillExecutionContext Context;
            public AnimationScriptPlayable Node;
            public void Sample(float value){
                if(Driver==null || !Driver.IsValid)return;
                Driver.AssertMutable();
                if(!Node.IsValid())return;var job=Node.GetJobData<SkillAimJob>();
                var services=Context.Services;var target=services.Target;
                job.Weight=target!=null || services.HasTargetPosition?Mathf.Clamp01(value):0;
                job.Target=target!=null?target.position:services.TargetPosition;Node.SetJobData(job);
            }
            public void Dispose(){
                var d=Driver;if(d==null)return;Driver=null;
                d.AssertMutable();
                Detach(d);
            }
            private void Detach(PlayableAnimationDriver d){
                if(!d.IsValid || !d._aims.Remove(this))return;
                if(Node.IsValid())d._graph.DestroyPlayable(Node);d.RewireOutput();
            }
        }
        public ISkillPresentationLease Aim(SkillExecutionContext context,string chain)
        {
            if(!IsValid || context==null || string.IsNullOrEmpty(chain))return null;
            var transform=_animator.transform.Find(chain);
            if(transform==null)foreach(var child in _animator.GetComponentsInChildren<Transform>())if(child.name==chain){transform=child;break;}
            if(transform==null)return null;
            var lease=new AimLease{Driver=this,Context=context};
            AssertMutable(); AttachAim(lease,transform);
            return lease;
        }
        private void AttachAim(AimLease lease,Transform transform){
            if(lease.Driver==null || !IsValid || transform==null)return;
            _output.SetWeight(1);
            lease.Node=AnimationScriptPlayable.Create(_graph,new SkillAimJob{Bone=_animator.BindStreamTransform(transform)},1);
            _aims.Add(lease);RewireOutput();
        }
        private void RewireOutput()
        {
            foreach(var aim in _aims)if(aim.Node.GetInput(0).IsValid())aim.Node.DisconnectInput(0);
            Playable tail=_cross.IsValid()?(Playable)_cross:LayerOutput;
            foreach(var aim in _aims)
            {
                _graph.Connect(tail,0,aim.Node,0);aim.Node.SetInputWeight(0,1);tail=aim.Node;
            }
            _output.SetSourcePlayable(tail);
        }
        public PlayableAnimationDriver(Animator animator, PlayableClipBinding[] clips, AvatarMask mask = null,
            bool additive = false, bool rootMotion = false, SkillAnimationRate updateRate = default)
        {
            AssertMutable();
            _animator = animator != null ? animator : throw new ArgumentNullException(nameof(animator));
            _defaultMask = _mask = _nextMask = mask; _defaultAdditive = _additive = _nextAdditive = additive;
            UpdateRate = updateRate;
            _names = new string[clips.Length]; _clips = new AnimationClip[clips.Length];
            for (int i = 0; i < clips.Length; i++) { _names[i] = clips[i].name; _clips[i] = clips[i].clip; }
            // Keep the controller instance and its parameter/trigger state. Drive its native
            // graph on the same clock as the overlay so neither writes on skipped frames.
            if(animator.runtimeAnimatorController!=null && !animator.isInitialized && animator.isActiveAndEnabled)animator.Update(0);
            var nativeGraph = animator.playableGraph;
            _native = animator.runtimeAnimatorController != null && nativeGraph.IsValid();
            _graph = _native ? nativeGraph : PlayableGraph.Create("GAS Skill Animation");
            if (_native) _savedMode = _graph.GetTimeUpdateMode();
            _graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            _layers=AnimationLayerMixerPlayable.Create(_graph,2);_skills=AnimationMixerPlayable.Create(_graph,0);
            _basePose=AnimationMixerPlayable.Create(_graph,0);
            _graph.Connect(_basePose,0,_layers,0);_layers.SetInputWeight(0,1);
            _graph.Connect(_skills, 0, _layers, 1); _layers.SetInputWeight(1, 0);
            if (mask != null) _layers.SetLayerMaskFromAvatarMask(1, mask);
            _layers.SetLayerAdditive(1, additive);
            _output = AnimationPlayableOutput.Create(_graph, "Skill", animator); _output.SetSourcePlayable(_layers); _output.SetSortingOrder(100); _output.SetAnimationStreamSource(AnimationStreamSource.PreviousInputs); _graph.Play();
            if (rootMotion) _rootMotion = animator.gameObject.AddComponent<SkillRootMotionRelay>();
            SkillAnimationManager.Register(this);
        }
        public float Play(string name, float duration, bool loop) => Play(name, duration, loop, new SkillBlend(0));
        public float Play(string name, float duration, bool loop, float blendIn) => Play(name, duration, loop, new SkillBlend(blendIn));
        public float Play(string clipName, float duration, bool loop, in SkillBlend blendIn)
        {
            if (!IsValid) throw new ObjectDisposedException(nameof(PlayableAnimationDriver));
            AssertMutable();
            int index = Array.IndexOf(_names, clipName); var animation = _nextClip!=null?_nextClip:index>=0?_clips[index]:null; _nextClip=null;
            if(animation==null) return 0;
            _accumulated = 0; _publishNextPose = true;
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
            _playing.Add(new ClipState { Clip = _current, Loop = loop, Length=animation.length, Name=clipName });
            _startWeight = _layerWeight;
            _fade = blendIn.duration * (actual / Mathf.Max(.0001f, animation.length)); _elapsed = 0; _stopping = false;
            if (_rootMotion != null)
            {
                if (!_rootMotionActive) _restoreRootMotion = _animator.applyRootMotion;
                _rootMotionActive = true; _animator.applyRootMotion = true;
                _rootMotion.Movement = _animator.GetComponentInParent<ISkillMovementAdapter>();
            }
            Update(0); return actual;
        }
        private void ChangeLayer()
        {
            LimitLayers();
            int index=_outgoing.Count;
            var old=new OutgoingLayer { Layers=_layers,Skills=_skills,Clips=_playing,StartWeight=_layerWeight,Bones=_bones,Base=_basePose };
            foreach(var aim in _aims) if(aim.Node.GetInput(0).IsValid())aim.Node.DisconnectInput(0);
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
            _basePose=AnimationMixerPlayable.Create(_graph,0);
            if(_weights!=null)
            {
                _bones=new SkillBoneLayer(_graph,_animator,_weights,_basePose,next,_native);
            }
            else if(_basePose.IsValid()){_graph.Connect(_basePose,0,next,0);next.SetInputWeight(0,1);}
            _layers = next; _skills = AnimationMixerPlayable.Create(_graph, 0);
            _graph.Connect(_skills, 0, next, 1); next.SetInputWeight(1, 0);
            _mask = _nextMask; _additive = _nextAdditive;
            if (_mask != null) next.SetLayerMaskFromAvatarMask(1, _mask);
            next.SetLayerAdditive(1, _additive);_skills.SetSpeed(_localScale);
            _graph.Connect(LayerOutput,0,_cross,index+1);_cross.SetInputWeight(index+1,0);RewireOutput();
            _playing = new List<ClipState>(); _layerWeight = 0;
        }
        private void LimitLayers()
        {
            if(_outgoing.Count<MaxHistory || !_cross.IsValid())return;
            int count=_outgoing.Count+1,drop=0;
            Span<float> shares=stackalloc float[count];
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
                _playing[i] = c; _graph.Connect(c.Clip,0,_skills,i);_skills.SetInputWeight(i,c.Weight);}
        }
        private void DestroyOld(OutgoingLayer layer)
        {
            foreach(var clip in layer.Clips)Release(clip);
            layer.Bones?.Destroy(_graph);_graph.DestroyPlayable(layer.Skills);_graph.DestroyPlayable(layer.Layers);_graph.DestroyPlayable(layer.Base);
        }
        private void BeginFade(in SkillBlend blend)
        {
            _settled = false; _blend = blend;
            foreach (var layer in _outgoing)
            {
                layer.StartWeight = layer.Layers.GetInputWeight(1);
                if (blend.mode == SkillBlendMode.Freeze) foreach (var clip in layer.Clips) clip.Clip.SetSpeed(0);
            }
            for (int i=0; i<_playing.Count; i++)
            {
                var clip = _playing[i]; clip.StartWeight = clip.Weight; _playing[i] = clip;
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
        private const int MaxHistory = 4;
        public bool SeekBlend(float normalizedTime, in SkillBlend blend)
        {
            if (!IsValid || blend.duration<=0)return false;
            AssertMutable();
            if(!_current.IsValid() || _stopping)return false;
            var animation = _current.GetAnimationClip();
            var previous=_playing[_playing.Count-1]; bool loop = previous.Loop;
            BeginFade(blend);
            if(_cross.IsValid()){_nextMask=_mask;_nextAdditive=_additive;_nextWeights=_weights;ChangeLayer();_nextWeights=null;ConfigureNextLayer(null,false,1);}
            LimitClips();
            _current = AnimationClipPlayable.Create(_graph, animation); _current.SetSpeed(_clipSpeed);
            _current.SetTime(Mathf.Clamp01(normalizedTime) * animation.length);
            int port = _playing.Count; _skills.SetInputCount(port+1); _graph.Connect(_current, 0, _skills, port);
            _playing.Add(new ClipState { Clip = _current, Loop = loop, Length=animation.length, Name=previous.Name });
            _elapsed = 0; _fade = blend.duration; _startWeight = _layerWeight; Update(0);
            return true;
        }
        public void Advance(float normalizedTime) { }
        internal void SetEntryTime(float normalizedTime) => Seek(normalizedTime);
        public void Seek(float normalizedTime)
        {
            AssertMutable(); if (!IsValid || !_current.IsValid()) return;
            if (_rootMotion != null) _rootMotion.Suppressed = true;
            try { _current.SetTime(_current.GetAnimationClip().length * normalizedTime); Evaluate(0); }
            finally { if (_rootMotion != null) _rootMotion.Suppressed = false; }
        }
        public void Stop() => Stop(0);
        public void Stop(float blendOut) => Stop(new SkillBlend(blendOut));
        public void Stop(in SkillBlend blendOut)
        {
            AssertMutable(); if (_disposed) return;
            RestoreRootMotion(); if (!IsValid) return;
            SetLocalScale(1); BeginFade(blendOut);
            _fade = Math.Max(0, blendOut.duration); _elapsed = 0; _stopping = true; _startWeight = _layerWeight;
            _publishNextPose = true; Update(0);
        }
        private void Evaluate(float delta)
        {
            SkillAnimationManager.BeginManual();
            try {
                _graph.Evaluate(delta);
            } finally { SkillAnimationManager.EndManual(); }
        }
        internal void ScheduledUpdate(long frame, float delta, ISkillAnimationLodPolicy policy)
        {
            if (!AutomaticEvaluation || !IsValid || !_animator.isActiveAndEnabled) return;
            if (!_publishNextPose && !_current.IsValid() && _outgoing.Count == 0 && _aims.Count == 0 && !_native) return;
            _accumulated += delta;
            var rate = _rootMotionActive ? default : policy?.Rate(_animator) ?? UpdateRate;
            if (!Due(rate, frame, delta) && !_publishNextPose) return;
            float step = _accumulated; _accumulated = 0; Update(step); Evaluate(step);
            _publishNextPose = false; SkillAnimationManager.EvaluationCount++;
        }
        internal void Update(float dt)
        {
            if (!IsValid) return;
            foreach (var state in _playing)
            {
                var clip = state.Clip; float length = state.Length;
                if (length > 0 && clip.GetTime() >= length)
                {
                    if (state.Loop) clip.SetTime(clip.GetTime() % length);
                    else { clip.SetTime(length); clip.SetSpeed(0); }
                }
            }
            if (_settled) return;
            _elapsed += dt*_localScale;
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
            if (complete) { ReleaseOutgoing(); _settled = true; }
            if (_stopping)
            {
                _layerWeight = _startWeight * (1 - t); _layers.SetInputWeight(1, _layerWeight);
                if (complete)
                {
                    ReleaseClips(false);
                    if(_aims.Count==0)_output.SetWeight(0);
                }
            }
            else if (_current.IsValid())
            {
                for (int i = 0; i < _playing.Count; i++)
                {
                    var clip = _playing[i];
                    clip.Weight = i == _playing.Count - 1 ? (_playing.Count == 1 ? 1 : t) : clip.StartWeight * (1 - t);
                    _playing[i] = clip; _skills.SetInputWeight(i, clip.Weight);
                }
                _layerWeight = crossing?_targetWeight:Mathf.Lerp(_startWeight, _targetWeight, t); _layers.SetInputWeight(1, _layerWeight);
                if (complete && _playing.Count > 1) ReleaseClips(true);
            }
        }
        private void ReleaseClips(bool keepCurrent)
        {
            int last = keepCurrent ? _playing.Count - 1 : -1;
            var keep = keepCurrent ? _playing[last] : default;
            for (int i = 0; i < _playing.Count; i++)
            { _skills.DisconnectInput(i); if (i != last) Release(_playing[i]); }
            _playing.Clear(); _skills.SetInputCount(keepCurrent ? 1 : 0);
            if (keepCurrent) { _playing.Add(keep); _graph.Connect(keep.Clip, 0, _skills, 0); _skills.SetInputWeight(0, 1); }
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
            if (_disposed) return; AssertMutable(); _disposed = true;
            RestoreRootMotion();
            if (_graph.IsValid())
            {
                if (_native)
                {
                    foreach (var layer in _outgoing) layer.Bones?.RemoveCapture(_graph);
                    _bones?.RemoveCapture(_graph);
                    var overlay = _output.GetSourcePlayable();
                    _graph.DestroyOutput(_output);
                    if (overlay.IsValid()) _graph.DestroySubgraph(overlay);
                }
                else _graph.Destroy();
            }
            if(_native && _graph.IsValid()){
                _graph.SetTimeUpdateMode(_savedMode);
                if(_animator!=null && _animator.isActiveAndEnabled){
                    // Re-register the native Animator with the player loop without re-entering its state.
                    bool keep=_animator.keepAnimatorStateOnDisable;_animator.keepAnimatorStateOnDisable=true;
                    _animator.enabled=false;_animator.enabled=true;_animator.keepAnimatorStateOnDisable=keep;
                }
            }
            _bones?.Dispose(); foreach (var layer in _outgoing) layer.Bones?.Dispose(); _outgoing.Clear();
            foreach (var aim in _aims) aim.Driver = null; _aims.Clear();
            if (_rootMotion != null) {
                _rootMotion.Movement = null;
                if (Application.isPlaying) UnityEngine.Object.Destroy(_rootMotion); else UnityEngine.Object.DestroyImmediate(_rootMotion);
            }
            SkillAnimationManager.Unregister(this);
        }
    }
}
