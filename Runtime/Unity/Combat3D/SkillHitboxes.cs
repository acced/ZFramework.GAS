using System;
using UnityEngine;

namespace ZFramework.GAS.Unity
{
    /// <summary>Bind equipment once on the unit's Origin. Skill assets refer to a slot, never a scene instance.</summary>
    [DisallowMultipleComponent, AddComponentMenu("GAS/Skill Hitboxes")]
    public sealed class SkillHitboxes : MonoBehaviour
    {
        [Serializable] public struct Slot { public string key; public Collider collider; }
        public Slot[] slots = Array.Empty<Slot>();

        public Collider Find(string key)
        {
            foreach (var slot in slots) if (slot.key == key) return slot.collider;
            return null;
        }
    }

    /// <summary>World-space primitive sampled from the current weapon pose; also used by the authoring preview.</summary>
    public readonly struct SkillColliderVolume
    {
        public readonly Vector3 Center, HalfSize, Axis;
        public readonly Quaternion Rotation;
        public readonly float Radius;
        public readonly bool IsBox;
        // Axis is the vector from the capsule center to one end of its center line (zero for spheres).
        private SkillColliderVolume(Vector3 center, Vector3 halfSize, Quaternion rotation, Vector3 axis, float radius, bool box)
        { Center = center; HalfSize = halfSize; Rotation = rotation; Axis = axis; Radius = radius; IsBox = box; }

        public static bool TryRead(Collider collider, out SkillColliderVolume volume)
        {
            volume = default;
            if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy) return false;
            var t = collider.transform;
            var s = t.lossyScale; s = new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
            switch (collider)
            {
                case BoxCollider box:
                    volume = new(t.TransformPoint(box.center), Vector3.Scale(box.size, s) * .5f, t.rotation, default, 0, true); break;
                case SphereCollider sphere:
                    volume = new(t.TransformPoint(sphere.center), default, t.rotation, default, sphere.radius * Mathf.Max(s.x, Mathf.Max(s.y, s.z)), false); break;
                case CapsuleCollider capsule:
                    int d = capsule.direction;
                    float r = capsule.radius * Mathf.Max(s[(d + 1) % 3], s[(d + 2) % 3]);
                    var axis = Vector3.zero; axis[d] = Mathf.Max(0, capsule.height * s[d] * .5f - r);
                    volume = new(t.TransformPoint(capsule.center), default, t.rotation, t.rotation * axis, r, false); break;
                default: return false;
            }
            return true;
        }

        internal int Overlap(PhysicsScene scene, Collider[] hits, int layers)
        {
            const QueryTriggerInteraction triggers = QueryTriggerInteraction.Collide;
            if (IsBox) return scene.OverlapBox(Center, HalfSize, hits, Rotation, layers, triggers);
            if (Axis.sqrMagnitude > 0) return scene.OverlapCapsule(Center - Axis, Center + Axis, Radius, hits, layers, triggers);
            return scene.OverlapSphere(Center, Radius, hits, layers, triggers);
        }

        public bool TouchesSphere(Vector3 point, float radius)
        {
            if (IsBox)
            {
                var local = Quaternion.Inverse(Rotation) * (point - Center);
                var nearest = Vector3.Min(HalfSize, Vector3.Max(-HalfSize, local));
                return (local - nearest).sqrMagnitude <= radius * radius;
            }
            float k = Axis.sqrMagnitude == 0 ? 0 : Mathf.Clamp(Vector3.Dot(point - Center, Axis) / Axis.sqrMagnitude, -1, 1);
            return (point - Center - Axis * k).sqrMagnitude <= (Radius + radius) * (Radius + radius);
        }
    }
}
