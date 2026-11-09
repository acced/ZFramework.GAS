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
        private readonly List<(Mesh mesh, Matrix4x4 matrix, Color color)> _ghosts = new();
        private readonly Mesh _lines = new() { hideFlags = HideFlags.HideAndDontSave };
        private readonly Material _material;
        private readonly List<Vector3> _vertices = new();
        private readonly List<Color> _colors = new();
        private readonly List<Vector3> _trajectory = new();
        private AnimationClip _trajectoryClip;
        private bool _moves;
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
        public void Rebuild(GameObject instance, SkillDocument doc, Action<float> pose, bool ghosts, bool trajectory, Action simulation)
        {
            ClearGhosts(); _vertices.Clear(); _colors.Clear();
            float duration = doc.Root?.Animation()?.Duration ?? 1;
            if (trajectory) for (int i = 1; i < _trajectory.Count; i++) Line(_trajectory[i - 1] + Vector3.up * .04f, _trajectory[i] + Vector3.up * .04f, Color.yellow);
            if (ghosts && doc.Root?.Animation() != null)
            {
                Capture(instance, pose, Mathf.Max(0, doc.Playback.Time - 3 / Mathf.Max(1, doc.Root.Animation().FrameRate)), new Color(.25f, .65f, 1, .15f));
                Capture(instance, pose, Mathf.Min(duration, doc.Playback.Time + 3 / Mathf.Max(1, doc.Root.Animation().FrameRate)), new Color(1, .65f, .25f, .15f));
            }
            pose(doc.Playback.Time);
            simulation();
            if(_vertices.Count==0)return; // Draw skips the previous line mesh when this frame has no geometry.
            _lines.Clear(); _lines.SetVertices(_vertices); _lines.SetColors(_colors);
            var indices = new int[_vertices.Count]; for (int i = 0; i < indices.Length; i++) indices[i] = i;
            _lines.SetIndices(indices, MeshTopology.Lines, 0); _lines.RecalculateBounds();
        }
        internal bool HasTrajectory(AnimationClip clip,Transform root,float duration,Action<float> pose,float currentTime)
        {
            if(_trajectoryClip==clip && _trajectory.Count>0)return _moves;
            _trajectoryClip=clip; _moves=false; _trajectory.Clear();
            try {
                for(int i=0;i<=60;i++){
                    pose(duration*i/60); var position=root.position; _trajectory.Add(position);
                    if((position-_trajectory[0]).sqrMagnitude>1e-8f)_moves=true;
                }
            } finally { pose(currentTime); }
            return _moves;
        }
        public void Invalidate() { _trajectoryClip=null; _trajectory.Clear(); }
        private void Capture(GameObject instance, Action<float> pose, float time, Color color)
        {
            pose(time);
            foreach (var renderer in instance.GetComponentsInChildren<Renderer>())
            {
                if (!renderer.enabled || renderer.GetComponentInParent<CueVfxBehaviour>() != null || renderer is ParticleSystemRenderer || renderer is TrailRenderer) continue;
                if (renderer is SkinnedMeshRenderer skin)
                { var mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave }; skin.BakeMesh(mesh); _ghosts.Add((mesh, skin.localToWorldMatrix, color)); }
                else if (renderer.TryGetComponent<MeshFilter>(out var filter) && filter.sharedMesh != null) {
                    // Spine reuses its mesh when scrubbing. Freeze the sampled vertices before posing the next frame.
                    var mesh=UnityEngine.Object.Instantiate(filter.sharedMesh); mesh.hideFlags=HideFlags.HideAndDontSave;
                    _ghosts.Add((mesh,renderer.localToWorldMatrix,color));
                }
            }
        }
        internal void Query(TargetShape shape, Transform source, Vector3 origin, float yaw = 0)
        {
            if (!TargetQuery.IsVolume(shape.Kind)) return;
            float radius = shape.Radius, angle = shape.Angle;
            var center = origin + Vector3.up * .04f;
            var forward = source.rotation * Quaternion.Euler(0, yaw, 0) * Vector3.forward;
            var right = Quaternion.LookRotation(forward) * Vector3.right;
            var color = SkillColors.Cue;
            if (shape.Kind == TargetQueryKind.Line)
            {
                float half = shape.HalfExtents.x, height = shape.HalfExtents.y;
                var a = center - right * half; var b = center + right * half;
                foreach (float y in new[] { -height, height })
                { var up = Vector3.up * y; Line(a + up, b + up, color); Line(a + up, a + up + forward * radius, color); Line(b + up, b + up + forward * radius, color); Line(a + up + forward * radius, b + up + forward * radius, color); }
                foreach (var p in new[] { a, b, a + forward * radius, b + forward * radius }) Line(p - Vector3.up * height, p + Vector3.up * height, color);
                return;
            }
            Vector3 At(float degrees) => center + Quaternion.AngleAxis(degrees, Vector3.up) * forward * radius;
            for (int i = 1; i <= 48; i++) Line(At(-angle / 2 + angle * (i - 1) / 48), At(-angle / 2 + angle * i / 48), color);
            if (angle < 360) { Line(center, At(-angle / 2), color); Line(center, At(angle / 2), color); }
            // Physical area is a sphere. Show its vertical radius, too.
            if (shape.Kind != TargetQueryKind.Sector)
                for (int i = 1; i <= 48; i++) { float a = (i - 1) * Mathf.PI / 24, b = i * Mathf.PI / 24; Line(center + new Vector3(0, Mathf.Sin(a), Mathf.Cos(a)) * radius, center + new Vector3(0, Mathf.Sin(b), Mathf.Cos(b)) * radius, color * new Color(1, 1, 1, .35f)); }
        }
        internal void Hitbox(SkillColliderVolume v)
        {
            var color = SkillColors.Cue;
            if (v.IsBox)
            {
                Vector3 Corner(int bits) => v.Center + v.Rotation * Vector3.Scale(v.HalfSize,
                    new Vector3((bits & 1) == 0 ? -1 : 1, (bits & 2) == 0 ? -1 : 1, (bits & 4) == 0 ? -1 : 1));
                for (int i = 0; i < 8; i++) for (int axis = 1; axis <= 4; axis *= 2)
                    if ((i & axis) == 0) Line(Corner(i), Corner(i | axis), color);
                return;
            }
            var up = v.Axis.sqrMagnitude > 0 ? v.Axis.normalized : Vector3.up;
            var x = Vector3.Cross(up, Mathf.Abs(up.y) < .9f ? Vector3.up : Vector3.right).normalized;
            var z = Vector3.Cross(x, up);
            for (int i = 0; i < 32; i++)
            {
                float a = i * Mathf.PI / 16, b = (i + 1) * Mathf.PI / 16;
                Vector3 At(float t, Vector3 first, Vector3 second) => (first * Mathf.Cos(t) + second * Mathf.Sin(t)) * v.Radius;
                for (int sign = -1; sign <= 1; sign += 2)
                {
                    var end = v.Center + sign * v.Axis;
                    Line(end + At(a,x,z), end + At(b,x,z), color);
                    if (i >= 16) continue;
                    Line(end + At(a,x,up*sign), end + At(b,x,up*sign), color);
                    Line(end + At(a,z,up*sign), end + At(b,z,up*sign), color);
                }
                if (i % 8 == 0) Line(v.Center - v.Axis + At(a,x,z), v.Center + v.Axis + At(a,x,z), color);
            }
        }
        internal void Circle(Vector3 center, float radius, Color color)
        { for (int i = 1; i <= 32; i++) { float a = (i - 1) * Mathf.PI / 16, b = i * Mathf.PI / 16; Line(center + new Vector3(Mathf.Sin(a), 0, Mathf.Cos(a)) * radius, center + new Vector3(Mathf.Sin(b), 0, Mathf.Cos(b)) * radius, color); } }
        internal void Line(Vector3 a, Vector3 b, Color color) { _vertices.Add(a); _vertices.Add(b); _colors.Add(color); _colors.Add(color); }
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
        private void ClearGhosts() { foreach (var ghost in _ghosts) UnityEngine.Object.DestroyImmediate(ghost.mesh); _ghosts.Clear(); }
        public void Dispose() { _audio.Dispose(); ClearGhosts(); UnityEngine.Object.DestroyImmediate(_lines); if (_material != null) UnityEngine.Object.DestroyImmediate(_material); }
    }
}
