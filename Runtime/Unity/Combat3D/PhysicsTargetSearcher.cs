using UnityEngine;
using UnityEngine.SceneManagement;

namespace ZFramework.GAS.Unity
{
    public sealed class PhysicsTargetSearcher : TargetSearcher
    {
        private sealed class Buffer : TargetQueryBuffer
        {
            public readonly Collider[] Contacts;
            public Buffer(int capacity) : base(capacity) => Contacts = new Collider[capacity];
        }

        public override TargetQueryBuffer CreateBuffer(int capacity = 256) => new Buffer(capacity);

        protected override void Collect(TargetQueryDefinition query, SkillExecutionContext context,
            TargetQueryBuffer buffer, Vector3? centerOverride, Vector3 forward)
        {
            Collider[] contacts = ((Buffer)buffer).Contacts;
            Scene scene = context.Services.Scene;
            PhysicsScene physics = scene.IsValid() ? scene.GetPhysicsScene() : Physics.defaultPhysicsScene;
            TargetShape shape = query.ResolveShape(context.Values);
            Vector3 dir = forward.sqrMagnitude > 1e-8f ? forward.normalized : Vector3.forward;
            Vector3 center;
            int count;
            Collider weapon = null;

            if (query.Kind == TargetQueryKind.Hitbox)
            {
                Transform origin = context.Source != null ? context.Services.Origin : null;
                weapon = origin != null ? origin.GetComponent<SkillHitboxes>()?.Find(query.SourceKey) : null;
                if (!SkillColliderVolume.TryRead(weapon, out var volume)) return;
                center = volume.Center;
                count = volume.Overlap(weapon.gameObject.scene.GetPhysicsScene(), contacts, query.LayerMask);
            }
            else
            {
                center = centerOverride ?? query.Center(context);
                count = query.Kind == TargetQueryKind.Line
                    ? physics.OverlapBox(center + dir * shape.HalfExtents.z, shape.HalfExtents, contacts, Quaternion.LookRotation(dir), query.LayerMask, QueryTriggerInteraction.Collide)
                    : physics.OverlapSphere(center, shape.Radius, contacts, query.LayerMask, QueryTriggerInteraction.Collide);
            }

            var results = new SkillTargetResults(context, query, buffer);
            if (count == contacts.Length) results.MarkSaturated();
            var flat = new Vector2(dir.x, dir.z);
            Vector2 facing = flat.sqrMagnitude > 1e-8f ? flat.normalized : new Vector2(0f, 1f);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    Collider col = contacts[i];
                    if (col == null || col == weapon || col.GetComponentInParent<IAbilitySystemProvider>() is not { } provider) continue;
                    Transform aim = provider is Component comp ? comp.transform : col.transform;
                    Vector3 delta = aim.position - center;
                    if (shape.IncludesDirection(facing, delta))
                        results.AddNearest(provider.AbilitySystem, aim, aim.position, delta.sqrMagnitude);
                }
            }
            finally { System.Array.Clear(contacts, 0, count); }
        }
    }
}
