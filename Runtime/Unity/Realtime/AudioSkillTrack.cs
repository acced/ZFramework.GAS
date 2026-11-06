using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ZFramework.GAS.Unity
{
    public interface ISkillAudioService
    {
        IDisposable Play(AudioClip clip, Vector3 position, float volume, bool spatial, bool loop, Scene scene);
    }
    public sealed class DefaultSkillAudioService : ISkillAudioService
    {
        public static readonly DefaultSkillAudioService Instance = new();
        public IDisposable Play(AudioClip clip, Vector3 position, float volume, bool spatial, bool loop, Scene scene)
        {
            var go = new GameObject("GAS Audio · " + clip.name); go.transform.position = position;
            if (scene.IsValid() && scene.isLoaded) SceneManager.MoveGameObjectToScene(go, scene);
            var source = go.AddComponent<AudioSource>(); source.clip = clip; source.volume = volume;
            source.spatialBlend = spatial ? 1 : 0; source.loop = loop; source.Play();
            return new Handle(go);
        }
        private sealed class Handle : IDisposable
        {
            private GameObject _object;
            public Handle(GameObject value) { _object = value; }
            public void Dispose()
            {
                var go = _object; _object = null; if (go == null) return;
                go.GetComponent<AudioSource>().Stop();
                if (Application.isPlaying) UnityEngine.Object.Destroy(go); else UnityEngine.Object.DestroyImmediate(go);
            }
        }
    }
    [Serializable]
    public sealed class AudioSkillTrack : SkillRuntimeTrack
    {
        public AudioClip clip;
        [Range(0, 1)] public float volume = 1;
        public bool spatial = true, loop;
        public override RuntimeTrackDefinition Build()
        {
            if (clip == null) throw new InvalidOperationException("音效轨缺少 AudioClip。");
            return new Definition(Id, start, end, clip, volume, spatial, loop);
        }
        private sealed class Definition : RuntimeTrackDefinition
        {
            private readonly AudioClip _clip; private readonly float _volume; private readonly bool _spatial, _loop;
            public Definition(string id, float start, float end, AudioClip clip, float volume, bool spatial, bool loop) : base(id, start, end)
            { _clip = clip; _volume = volume; _spatial = spatial; _loop = loop; }
            public override IRuntimeTrackHandler Create(SkillExecutionContext context) => new Handler(context, _clip, _volume, _spatial, _loop);
        }
        private sealed class Handler : IRuntimeTrackHandler
        {
            private readonly SkillExecutionContext _context; private readonly AudioClip _clip;
            private readonly float _volume; private readonly bool _spatial, _loop; private IDisposable _sound;
            public Handler(SkillExecutionContext context, AudioClip clip, float volume, bool spatial, bool loop)
            { _context = context; _clip = clip; _volume = volume; _spatial = spatial; _loop = loop; }
            public void Enter() => _sound = (_context.Services.Audio ?? DefaultSkillAudioService.Instance).Play(_clip, _context.Position, _volume, _spatial, _loop, _context.Services.Scene);
            public void Sample(float localTime, float normalizedTime) { }
            public void Exit() { var sound = _sound; _sound = null; sound?.Dispose(); }
            public void Cancel() => Exit();
        }
    }
}
