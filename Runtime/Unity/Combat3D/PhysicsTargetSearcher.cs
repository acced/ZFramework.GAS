using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ZFramework.GAS.Unity
{
    /// <summary>Optional 3D/XZ targeting. Create buffers through this searcher; each query owns its contact scratch.</summary>
    public sealed class PhysicsTargetSearcher : TargetSearcher
    {
        private sealed class Buffer : TargetQueryBuffer
        {
            public readonly Collider[] Contacts;
            public Buffer(int capacity) : base(capacity) => Contacts = new Collider[capacity];
        }

        public override TargetQueryBuffer CreateBuffer(int capacity = 256) => new Buffer(capacity);

        protected override void Collect(TargetQueryDefinition query, SkillExecutionContext context,
            TargetQueryBuffer buffer, Vector3? centerOverride, Vector3 forward, bool captureAim)
        {
            var contacts = ((Buffer)buffer).Contacts;
            var scene = context.Services.Scene;
            var physics = scene.IsValid() ? scene.GetPhysicsScene() : Physics.defaultPhysicsScene;
            var shape = query.ResolveShape(context.Values);
            Vector3 center;
            int count;
            Collider weapon = null;
            if (query.Kind == TargetQueryKind.Hitbox)
            {
                var origin = context.Source != null ? context.Services.Origin : null;
                weapon = origin != null ? origin.GetComponent<SkillHitboxes>().Find(query.SourceKey) : null;
                if (!SkillColliderVolume.TryRead(weapon, out var volume)) return;
                center = volume.Center;
                count = volume.Overlap(weapon.gameObject.scene.GetPhysicsScene(), contacts, query.LayerMask);
            }
            else
            {
                center = centerOverride ?? query.Center(context);
                count = query.Kind == TargetQueryKind.Line
                    ? physics.OverlapBox(center + forward * shape.HalfExtents.z, shape.HalfExtents,
                        contacts, Quaternion.LookRotation(forward), query.LayerMask, QueryTriggerInteraction.Collide)
                    : physics.OverlapSphere(center, shape.Radius, contacts, query.LayerMask, QueryTriggerInteraction.Collide);
            }

            var results = new SkillTargetResults(context, query, buffer);
            if (count == contacts.Length) results.MarkSaturated();
            var facing = new Vector2(forward.x, forward.z).normalized;
            try
            {
                for (int i = 0; i < count; i++)
                {
                    var collider = contacts[i];
                    if (collider == weapon || collider.GetComponentInParent<IAbilitySystemProvider>() is not { } provider) continue;
                    var aim = provider is Component component ? component.transform : collider.transform;
                    var delta = aim.position - center;
                    if (shape.IncludesDirection(facing, delta))
                        results.AddNearest(provider.AbilitySystem, aim, captureAim ? collider.bounds.center : (Vector3?)null, delta.sqrMagnitude);
                }
            }
            finally { Array.Clear(contacts, 0, count); }
        }
    }
}
