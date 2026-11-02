using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    /// <summary>Preview-only meshes. Queries and parameters come from the authored runtime configuration.</summary>
    internal sealed class SkillPreviewTools : IDisposable
    {
        private readonly List<(Mesh mesh, Matrix4x4 matrix, bool owned, Color color)> _ghosts = new();
        private readonly Mesh _lines = new() { hideFlags = HideFlags.HideAndDontSave };
        private readonly Material _material;
        private readonly List<Vector3> _vertices = new();
        private readonly List<Color> _colors = new();
        private readonly List<Vector3> _trajectory = new();
        private readonly SkillPreviewAudio _audio = new();
        public SkillPreviewTools()
        {
            var shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null) return;
            _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            _material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            _material.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off); _material.SetInt("_ZWrite", 0);
        }
        public void Rebuild(GameObject instance, SkillDocument doc, Action<float> pose, bool ghosts, bool trajectory, bool ranges, bool dummy, float distance, int level)
        {
            ClearGhosts(); _vertices.Clear(); _colors.Clear();
            float duration = doc.Root?.Animation?.Duration ?? 1;
            if (trajectory && _trajectory.Count == 0)
            {
                for (int i = 0; i <= 60; i++) { pose(duration * i / 60); _trajectory.Add(instance.transform.position); }
            }
            if (trajectory) for (int i = 1; i < _trajectory.Count; i++) Line(_trajectory[i - 1] + Vector3.up * .04f, _trajectory[i] + Vector3.up * .04f, Color.yellow);
            if (ghosts)
            {
                Capture(instance, pose, Mathf.Max(0, doc.Playback.Time - 3 / Mathf.Max(1, doc.Root.Animation.FrameRate)), new Color(.25f, .65f, 1, .15f));
                Capture(instance, pose, Mathf.Min(duration, doc.Playback.Time + 3 / Mathf.Max(1, doc.Root.Animation.FrameRate)), new Color(1, .65f, .25f, .15f));
            }
            pose(doc.Playback.Time);
            var position = instance.transform.position;
            if (dummy)
            {
                var center = position + instance.transform.forward * distance;
                Circle(center + Vector3.up * .03f, .3f, Color.gray);
                Line(center, center + Vector3.up * 1.8f, Color.gray);
                Line(center + Vector3.up * 1.3f - instance.transform.right * .4f, center + Vector3.up * 1.3f + instance.transform.right * .4f, Color.gray);
                Circle(center + Vector3.up * 1.7f, .16f, Color.gray);
            }
            if (ranges)
            {
                var values = new EffectEvaluationContext(level, doc.Root.ResolveParameters(level));
                Queries(doc.Root.activationActions, values, instance.transform, distance, 0);
                if (doc.Root.Animation != null)
                {
                    var markers = doc.Serialized(doc.Root.Animation).FindProperty("markers");
                    for (int i = 0; i < markers.arraySize; i++)
                    {
                        var actions = markers.GetArrayElementAtIndex(i).FindPropertyRelative("actions");
                        for (int j = 0; j < actions.arraySize; j++)
                            if (actions.GetArrayElementAtIndex(j).managedReferenceValue is SkillAction action) Queries(new[] { action }, values, instance.transform, distance, 0);
                    }
                }
            }
            _lines.Clear(); _lines.SetVertices(_vertices); _lines.SetColors(_colors);
            var indices = new int[_vertices.Count]; for (int i = 0; i < indices.Length; i++) indices[i] = i;
            _lines.SetIndices(indices, MeshTopology.Lines, 0); _lines.RecalculateBounds();
        }
        public void Invalidate() => _trajectory.Clear();
        private void Capture(GameObject instance, Action<float> pose, float time, Color color)
        {
            pose(time);
            foreach (var renderer in instance.GetComponentsInChildren<Renderer>())
            {
                if (!renderer.enabled || renderer.GetComponentInParent<CueVfxBehaviour>() != null || renderer is ParticleSystemRenderer || renderer is TrailRenderer) continue;
                if (renderer is SkinnedMeshRenderer skin)
                { var mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave }; skin.BakeMesh(mesh); _ghosts.Add((mesh, skin.localToWorldMatrix, true, color)); }
                else if (renderer.TryGetComponent<MeshFilter>(out var filter) && filter.sharedMesh != null)
                    _ghosts.Add((filter.sharedMesh, renderer.localToWorldMatrix, false, color));
            }
        }
        private void Queries(SkillAction[] actions, EffectEvaluationContext values, Transform source, float distance, int depth)
        {
            if (actions == null || depth >= 8) return;
            foreach (var action in actions)
            {
                if (action is ApplyEffectAction apply) Query(apply.targets.Build(), values, source, distance);
                if (action is CreateAreaAction area) Query(area.targets.Build(), values, source, distance);
                if (action is ConditionalAction condition) { Queries(condition.whenTrue, values, source, distance, depth + 1); Queries(condition.whenFalse, values, source, distance, depth + 1); }
                if (action is WaitSkillAction wait) Queries(wait.onSuccess, values, source, distance, depth + 1);
                if (action is CallModuleAction call && call.module != null)
                {
                    var parameters = new Dictionary<string, SkillParameterValue>();
                    if (call.module.parameters != null) foreach (var p in call.module.parameters.parameters) parameters.Add(p.Id, p.Resolve(values.Level));
                    foreach (var binding in call.bindings) parameters[binding.moduleParameterId] = values.Parameters.Get(binding.callerParameterId);
                    Queries(call.module.actions, values.WithParameters(new SkillParameterValues(parameters)), source, distance, depth + 1);
                }
            }
        }
        private void Query(TargetQueryDefinition query, EffectEvaluationContext values, Transform source, float distance)
        {
            if (query.Kind < TargetQueryKind.TargetPoint) return;
            float radius = query.Radius.Resolve(values), angle = query.Kind == TargetQueryKind.Sector ? query.Angle.Resolve(values) : 360;
            var center = source.position + Vector3.up * .04f + (query.Kind == TargetQueryKind.TargetPoint ? source.forward * distance : Vector3.zero);
            var color = SkillColors.Cue;
            if (query.Kind == TargetQueryKind.Line)
            {
                float half = query.Width.Resolve(values) * .5f, height = query.Height.Resolve(values) * .5f;
                var a = center - source.right * half; var b = center + source.right * half;
                foreach (float y in new[] { -height, height })
                { var up = Vector3.up * y; Line(a + up, b + up, color); Line(a + up, a + up + source.forward * radius, color); Line(b + up, b + up + source.forward * radius, color); Line(a + up + source.forward * radius, b + up + source.forward * radius, color); }
                foreach (var p in new[] { a, b, a + source.forward * radius, b + source.forward * radius }) Line(p - Vector3.up * height, p + Vector3.up * height, color);
                return;
            }
            Vector3 At(float degrees) => center + Quaternion.AngleAxis(degrees, Vector3.up) * source.forward * radius;
            for (int i = 1; i <= 48; i++) Line(At(-angle / 2 + angle * (i - 1) / 48), At(-angle / 2 + angle * i / 48), color);
            if (angle < 360) { Line(center, At(-angle / 2), color); Line(center, At(angle / 2), color); }
            // Physical area is a sphere. Show its vertical radius, too.
            if (query.Kind != TargetQueryKind.Sector)
                for (int i = 1; i <= 48; i++) { float a = (i - 1) * Mathf.PI / 24, b = i * Mathf.PI / 24; Line(center + new Vector3(0, Mathf.Sin(a), Mathf.Cos(a)) * radius, center + new Vector3(0, Mathf.Sin(b), Mathf.Cos(b)) * radius, color * new Color(1, 1, 1, .35f)); }
        }
        private void Circle(Vector3 center, float radius, Color color)
        { for (int i = 1; i <= 32; i++) { float a = (i - 1) * Mathf.PI / 16, b = i * Mathf.PI / 16; Line(center + new Vector3(Mathf.Sin(a), 0, Mathf.Cos(a)) * radius, center + new Vector3(Mathf.Sin(b), 0, Mathf.Cos(b)) * radius, color); } }
        private void Line(Vector3 a, Vector3 b, Color color) { _vertices.Add(a); _vertices.Add(b); _colors.Add(color); _colors.Add(color); }
        public void Draw(PreviewRenderUtility preview)
        {
            if (_material == null) return;
            foreach (var ghost in _ghosts)
            {
                var block = new MaterialPropertyBlock(); block.SetColor("_Color", ghost.color);
                for (int i = 0; i < ghost.mesh.subMeshCount; i++) preview.DrawMesh(ghost.mesh, ghost.matrix, _material, i, block);
            }
            if (_vertices.Count > 0) preview.DrawMesh(_lines, Matrix4x4.identity, _material, 0);
        }
        public void Audio(SkillAnimationAsset animation, float time, bool enabled, int seekVersion = 0, float speed = 1)
            => _audio.Sample(animation, time, enabled, seekVersion, speed);
        private void ClearGhosts() { foreach (var ghost in _ghosts) if (ghost.owned) UnityEngine.Object.DestroyImmediate(ghost.mesh); _ghosts.Clear(); }
        public void Dispose() { _audio.Dispose(); ClearGhosts(); UnityEngine.Object.DestroyImmediate(_lines); if (_material != null) UnityEngine.Object.DestroyImmediate(_material); }
    }
}
