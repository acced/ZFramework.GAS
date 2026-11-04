using System;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Animations;
#endif

namespace ZFramework.GAS.Unity
{
    // Only channels written by EVERY possible base state are safe to leave to the
    // controller. A path in its clip union (or Write Defaults) is not such a guarantee.
    [Serializable] internal sealed class ControllerHandoffCoverage
    {
        [SerializeField] private RuntimeAnimatorController source;
        [SerializeField] private string[] paths;
        [SerializeField] private int[] channels;
        [SerializeField] private AnimationClip[] clips;
        [SerializeField] private ClipChannels[] native;
        [Serializable] private sealed class ClipChannels{
            public AnimationClip clip;public string[] paths;public int[] channels;
            [NonSerialized] private Dictionary<string,int> _map;
            internal Dictionary<string,int> Read(){
                if(_map==null){_map=new Dictionary<string,int>(paths.Length,StringComparer.Ordinal);
                    for(int i=0;i<paths.Length;i++)_map[paths[i]]=channels[i];}
                return _map;
            }
        }
        internal Dictionary<string,int> NativeChannels(AnimationClip clip){
            if(native!=null && !SkillInertialPose.Rig.HasRuntimeEdits(clip))
                foreach(var c in native)if(c.clip==clip)return c.Read();
            return null;
        }
        private Dictionary<string,int> _lookup;
        internal static int Revision{get;private set;}
        internal static void ClipChanged()=>Revision++;
        internal bool Trusted{
            get{if(clips==null)return false;foreach(var clip in clips)if(clip==null || SkillInertialPose.Rig.HasRuntimeEdits(clip))return false;return true;}
        }
        internal bool Matches(RuntimeAnimatorController current)=>current!=null && current==source && paths!=null && channels!=null && paths.Length==channels.Length;
        internal bool Covers(string path,int required){
            if(required==0 || paths.Length==0)return false;
            if(_lookup==null){
                _lookup=new Dictionary<string,int>(paths.Length,StringComparer.Ordinal);
                for(int i=0;i<paths.Length;i++)_lookup[paths[i]]=channels[i];
            }
            return _lookup.TryGetValue(path,out int bits) && (bits&required)==required;
        }
#if UNITY_EDITOR
        private static System.Runtime.CompilerServices.ConditionalWeakTable<RuntimeAnimatorController,ControllerHandoffCoverage> _cache=new();
        static ControllerHandoffCoverage(){
            EditorApplication.projectChanged+=Invalidate;Undo.undoRedoPerformed+=Invalidate;
            AnimationUtility.onCurveWasModified+=(clip,binding,change)=>Invalidate();
        }
        private static void Invalidate(){_cache=new();ClipChanged();}
        internal static ControllerHandoffCoverage ForEditor(RuntimeAnimatorController c)=>c==null?null:_cache.GetValue(c,Bake);
        internal static ControllerHandoffCoverage Bake(RuntimeAnimatorController c){
            var result=new ControllerHandoffCoverage{source=c};
            // Mutable overrides, empty states and blend trees (including zero-weight
            // direct trees) use normal tracking. Upper layers cannot establish coverage.
            if(c is not AnimatorController controller || controller.layers.Length==0)return result;
            var layer=controller.layers[0];
            var clips=new HashSet<AnimationClip>();
            bool Collect(AnimatorStateMachine m){
                if(m==null)return false;
                foreach(var s in m.states){
                    if(s.state.motion is not AnimationClip clip || clip.humanMotion)return false;
                    clips.Add(clip);
                }
                foreach(var sub in m.stateMachines)if(!Collect(sub.stateMachine))return false;
                return true;
            }
            if(layer.syncedLayerIndex>=0 || !Collect(layer.stateMachine) || clips.Count==0)return result;
            var unsupported=new HashSet<AnimationClip>();
            Dictionary<string,int> Keyed(AnimationClip clip){
                var keyed=new Dictionary<string,int>(StringComparer.Ordinal);
                foreach(var b in AnimationUtility.GetCurveBindings(clip)){
                    if(b.type!=typeof(Transform) || string.IsNullOrEmpty(b.path))continue;
                    string p=b.propertyName;int bit=p switch{
                        "m_LocalPosition.x"=>1,"m_LocalPosition.y"=>2,"m_LocalPosition.z"=>4,
                        "m_LocalScale.x"=>8,"m_LocalScale.y"=>16,"m_LocalScale.z"=>32,
                        "m_LocalRotation.x"=>64,"m_LocalRotation.y"=>128,"m_LocalRotation.z"=>256,"m_LocalRotation.w"=>512,_=>0};
                    if(bit==0){
                        // Euler/importer-specific bindings still use the sampled provenance
                        // on release; they cannot prove that a channel is always driven.
                        unsupported.Add(clip);
                    }
                    if(bit!=0){keyed.TryGetValue(b.path,out int old);keyed[b.path]=old|bit;}
                }
                return keyed;
            }
            var keyedClips=new Dictionary<AnimationClip,Dictionary<string,int>>();
            var data=new List<ClipChannels>();
            foreach(var clip in controller.animationClips){
                if(clip==null || clip.humanMotion || keyedClips.ContainsKey(clip))continue;
                var keys=Keyed(clip);keyedClips.Add(clip,keys);if(unsupported.Contains(clip))continue;
                var record=new ClipChannels{clip=clip,paths=new string[keys.Count],channels=new int[keys.Count]};
                int i=0;foreach(var key in keys){record.paths[i]=key.Key;record.channels[i++]=(key.Value&63)|((key.Value&960)!=0?64:0);}
                data.Add(record);
            }
            result.native=data.ToArray();
            Dictionary<string,int> common=null;
            foreach(var clip in clips){
                if(!keyedClips.TryGetValue(clip,out var keyed))keyed=Keyed(clip);
                if(common==null)common=new Dictionary<string,int>(keyed,StringComparer.Ordinal);
                else foreach(string path in new List<string>(common.Keys)){
                    keyed.TryGetValue(path,out int bits);common[path]&=bits;
                }
            }
            var names=new List<string>();var flags=new List<int>();
            foreach(var pair in common){
                int bits=pair.Value&63;if((pair.Value&960)==960)bits|=64;
                if(bits!=0){names.Add(pair.Key);flags.Add(bits);}
            }
            result.clips=new AnimationClip[clips.Count];clips.CopyTo(result.clips);
            result.paths=names.ToArray();result.channels=flags.ToArray();return result;
        }
#endif
    }
}
