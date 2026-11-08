using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    /// <summary>读取预览角色的 CueVfxLibrary；不运行游戏逻辑，按时间轴位置重建特效和粒子尾迹。</summary>
    public sealed class CueVfxPreview : IDisposable
    {
        private readonly GameObject _owner;
        private readonly CueVfxLibrary _library;
        private readonly Dictionary<(int binding, float start), CueVfxInstance> _instances = new();
        private readonly HashSet<(int binding, float start)> _used = new();
        private readonly List<(int binding, float start)> _remove = new();
        private readonly List<(float start, float end)> _ranges = new();

        public CueVfxPreview(GameObject owner)
        { _owner = owner; _library = owner.GetComponent<CueVfxPlayer>()?.Library; }

        public string ConfigurationWarning(string cueTag)
        {
            if (string.IsNullOrEmpty(cueTag)) return null;
            if (_owner.GetComponent<CueVfxPlayer>() == null)
                return "角色未配置 Cue VFX Player，无法预览特效「" + cueTag + "」。";
            if (_library == null) return "角色的 Cue VFX Player 未指定特效映射库。";
            bool found = false;
            foreach (var binding in _library.bindings)
            {
                if (binding == null || binding.cueTag.Name != cueTag) continue;
                found = true;
                if (binding.prefab == null) return "Cue「" + cueTag + "」的映射未指定特效 Prefab。";
                if (binding.FindAnchor(_owner.transform) == null) return "Cue「" + cueTag + "」的挂点不存在：" + binding.anchorPath;
            }
            return found ? null : "特效映射库未配置 Cue「" + cueTag + "」。";
        }

        /// <param name="samplePose">输入授权动画时间。固定在世界的特效使用区间开始时的挂点姿势。</param>
        public void Sample(SkillAnimationAsset animation, float time, Action<float> samplePose)
        {
            _used.Clear();
            try
            {
                if (_library == null || animation == null) return;
                using var so = new SerializedObject(animation);
                var regions = so.FindProperty("cueRegions");
                for (int index = 0; index < _library.bindings.Length; index++)
                {
                    var binding = _library.bindings[index];
                    if (binding == null || binding.prefab == null) continue;
                    _ranges.Clear();
                    for (int r = 0; r < regions.arraySize; r++)
                    {
                        var region = regions.GetArrayElementAtIndex(r);
                        if (region.FindPropertyRelative("cueTag.m_Name").stringValue != binding.cueTag.Name) continue;
                        float start = region.FindPropertyRelative("start").floatValue;
                        float end = region.FindPropertyRelative("end").floatValue;
                        end = end < 0 ? animation.Duration : Mathf.Min(end, animation.Duration);
                        if (start < end) _ranges.Add((start, end));
                    }
                    _ranges.Sort((a, b) => a.start.CompareTo(b.start));
                    // 运行时同标签重叠区间按引用计数维持一份表现；预览合并区间保持同样语义。
                    for (int r = 0; r < _ranges.Count; r++)
                    {
                        var range = _ranges[r];
                        while (r + 1 < _ranges.Count && _ranges[r + 1].start <= range.end)
                            range.end = Mathf.Max(range.end, _ranges[++r].end);
                        float tail = binding.stopMode == CueVfxStopMode.StopEmitting ? Mathf.Max(.01f, binding.tailSeconds) : 0;
                        if (time < range.start || time >= range.end + tail) continue;
                        var key = (index, range.start);
                        _used.Add(key);
                        if (!_instances.TryGetValue(key, out var instance))
                        {
                            samplePose(binding.follow ? time : range.start);
                            instance = new CueVfxInstance(binding, _owner.transform, true);
                            _instances.Add(key, instance);
                        }
                        samplePose(time);
                        instance.SamplePreview(time - range.start, range.end - range.start);
                    }
                }
            }
            finally
            {
                samplePose(time);
                _remove.Clear();
                foreach (var pair in _instances) if (!_used.Contains(pair.Key)) _remove.Add(pair.Key);
                foreach (var key in _remove) { _instances[key].Dispose(); _instances.Remove(key); }
            }
        }

        public void Dispose()
        {
            foreach (var instance in _instances.Values) instance.Dispose();
            _instances.Clear();
        }
    }
}
