using System;
using UnityEngine;

namespace ZFramework.GAS.Unity
{
    [DisallowMultipleComponent, AddComponentMenu("GAS/Skill Hitboxes")]
    public sealed class SkillHitboxes : MonoBehaviour
    {
        [Serializable] public struct Slot { public string key; public Collider collider; }
        public Slot[] slots = Array.Empty<Slot>();

        public Collider Find(string key)
        {
            if (slots == null || string.IsNullOrEmpty(key)) return null;
            for (int i = 0; i < slots.Length; i++)
                if (slots[i].key == key) return slots[i].collider;
            return null;
        }
    }

    public readonly struct SkillColliderVolume
    {
        public readonly Vector3 Center, HalfSize, Axis;
        public readonly Quaternion Rotation;
        public readonly float Radius;
        public readonly bool IsBox;

        private SkillColliderVolume(Vector3 center, Vector3 halfSize, Quaternion rotation, Vector3 axis, float radius, bool box)
        {
            Center = center; HalfSize = halfSize; Rotation = rotation; Axis = axis; Radius = radius; IsBox = box;
        }

        public static bool TryRead(Collider collider, out SkillColliderVolume volume)
        {
            volume = default;
            if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy) return false;
            Transform t = collider.transform;
            Vector3 s = t.lossyScale;
            s = new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
            switch (collider)
            {
                case BoxCollider box:
                    volume = new(t.TransformPoint(box.center), Vector3.Scale(box.size, s) * .5f, t.rotation, default, 0f, true);
                    return true;
                case SphereCollider sphere:
                    volume = new(t.TransformPoint(sphere.center), default, t.rotation, default, sphere.radius * Mathf.Max(s.x, Mathf.Max(s.y, s.z)), false);
                    return true;
                case CapsuleCollider capsule:
                    int d = capsule.direction;
                    float r = capsule.radius * Mathf.Max(s[(d + 1) % 3], s[(d + 2) % 3]);
                    Vector3 axis = Vector3.zero; axis[d] = Mathf.Max(0f, capsule.height * s[d] * .5f - r);
                    volume = new(t.TransformPoint(capsule.center), default, t.rotation, t.rotation * axis, r, false);
                    return true;
                default:
                    return false;
            }
        }

        internal int Overlap(PhysicsScene scene, Collider[] hits, int layers)
        {
            const QueryTriggerInteraction t = QueryTriggerInteraction.Collide;
            return IsBox ? scene.OverlapBox(Center, HalfSize, hits, Rotation, layers, t)
                : Axis.sqrMagnitude > 0f ? scene.OverlapCapsule(Center - Axis, Center + Axis, Radius, hits, layers, t)
                : scene.OverlapSphere(Center, Radius, hits, layers, t);
        }

        public bool TouchesSphere(Vector3 point, float radius)
        {
            if (IsBox)
            {
                Vector3 local = Quaternion.Inverse(Rotation) * (point - Center);
                Vector3 nearest = Vector3.Min(HalfSize, Vector3.Max(-HalfSize, local));
                return (local - nearest).sqrMagnitude <= radius * radius;
            }
            float k = Axis.sqrMagnitude == 0f ? 0f : Mathf.Clamp(Vector3.Dot(point - Center, Axis) / Axis.sqrMagnitude, -1f, 1f);
            return (point - Center - Axis * k).sqrMagnitude <= (Radius + radius) * (Radius + radius);
        }
    }
}
