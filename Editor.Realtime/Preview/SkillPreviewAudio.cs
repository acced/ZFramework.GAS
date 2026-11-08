using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    /// <summary>Each window owns its audition sources. No global AudioUtil preview transport.</summary>
    internal sealed class SkillPreviewAudio : IDisposable
    {
        internal sealed class Channel : IDisposable
        {
            internal readonly AudioSource Source;
            internal int SeekCount;
            private readonly float _start, _end;
            private readonly bool _loop;
            private double _lastStart;
            internal Channel(AudioSkillTrack track, Scene scene)
            {
                var go = new GameObject("GAS Audio Preview"); SceneManager.MoveGameObjectToScene(go, scene);
                go.hideFlags = HideFlags.HideAndDontSave;
                Source = go.AddComponent<AudioSource>(); Source.playOnAwake = false; Source.spatialBlend = 0;
                Source.ignoreListenerPause = true; Source.clip = track.clip; Source.loop = track.loop;
                _start = track.start; _end = track.end; _loop = track.loop;
            }
            internal bool Matches(AudioSkillTrack track) => Source.clip == track.clip && _start == track.start && _end == track.end && _loop == track.loop;
            internal void Sample(AudioSkillTrack track, float time, bool seek, float speed)
            {
                Source.volume = track.volume; Source.pitch = Mathf.Clamp(speed, .01f, 3);
                float local=Math.Max(0,time-track.start);
                double now=EditorApplication.timeSinceStartup;
                // Preview scenes can lose a voice during import or device changes. Resume from the
                // timeline, allowing time for the mixer to start; never restart a naturally ended one-shot.
                bool recover=!seek && (track.loop || local<track.clip.length) && !Source.isPlaying && now-_lastStart>.25;
                if (seek || recover)
                {
                    if (track.loop && track.clip.length > 0) local %= track.clip.length;
                    Source.Stop(); SeekCount++; _lastStart=now;
                    if (local < track.clip.length)
                    {
                        Source.timeSamples = Mathf.Min(track.clip.samples - 1, (int)(local * track.clip.frequency));
                        Source.Play();
                    }
                }
            }
            public void Dispose()
            {
                if (Source != null) { Source.Stop(); UnityEngine.Object.DestroyImmediate(Source.gameObject); }
            }
        }
        private Scene _scene;
        private readonly Dictionary<string, Channel> _channels = new();
        private readonly HashSet<string> _active = new();
        private readonly List<string> _removed = new();
        private SkillAnimationAsset _animation;
        private int _seekVersion;
        private float _time;
        private static readonly Action UpdateAudio = (Action)typeof(AudioImporter).Assembly.GetType("UnityEditor.AudioUtil")
            .GetMethod("UpdateAudio", BindingFlags.Public | BindingFlags.Static).CreateDelegate(typeof(Action));
        private static Scene CreateAudioScene()
        {
            // Unity 6's public NewPreviewScene disables audio sources. Use the editor's
            // preview-scene flag so audition remains isolated from the user's loaded scenes.
            var flags = typeof(EditorSceneManager).Assembly.GetType("UnityEditor.SceneManagement.PreviewSceneFlags");
            var create = flags == null ? null : typeof(EditorSceneManager).GetMethod("NewPreviewScene",
                BindingFlags.Static | BindingFlags.NonPublic, null, new[] { typeof(bool), flags }, null);
            if (create == null) throw new NotSupportedException("此 Unity 版本不提供预览场景音频支持。");
            return (Scene)create.Invoke(null, new[] { (object)false, Enum.Parse(flags, "IsPreviewScene, AllowAutoPlayAudioSources") });
        }
        internal int Count => _channels.Count;
        internal Channel Get(string id) => _channels[id];
        public void Sample(SkillAnimationAsset animation, float time, bool enabled, int seekVersion, float speed = 1)
        {
            bool seek = animation != _animation || seekVersion != _seekVersion || time < _time;
            if (animation != _animation) Clear();
            _active.Clear();
            if (enabled && animation != null)
                foreach (var track in animation.runtimeTracks)
                    if (track is AudioSkillTrack audio && audio.clip != null && time >= audio.start && time < audio.end)
                    {
                        _active.Add(audio.Id);
                        if (_channels.TryGetValue(audio.Id, out var channel) && !channel.Matches(audio))
                        { channel.Dispose(); _channels.Remove(audio.Id); channel = null; }
                        bool created = channel == null;
                        if (created)
                        {
                            if (!_scene.IsValid()) _scene = CreateAudioScene();
                            _channels.Add(audio.Id, channel = new Channel(audio, _scene));
                        }
                        channel.Sample(audio, time, seek || created, speed);
                    }
            _removed.Clear();
            foreach (var item in _channels) if (!_active.Contains(item.Key)) _removed.Add(item.Key);
            foreach (var id in _removed) { _channels[id].Dispose(); _channels.Remove(id); }
            _time = time; _seekVersion = seekVersion; _animation = animation;
            if (_channels.Count > 0) UpdateAudio();
        }
        private void Clear() { foreach (var channel in _channels.Values) channel.Dispose(); _channels.Clear(); }
        public void Dispose()
        {
            Clear();
            if (_scene.IsValid()) EditorSceneManager.ClosePreviewScene(_scene);
            _scene = default; _animation = null;
        }
    }
}
