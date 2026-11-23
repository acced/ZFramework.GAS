using System;
using IronOath.Combat;
using UnityEngine;

namespace IronOath.Presentation
{
    /// <summary>World-space board geometry. UI follows these feet; it never determines unit positions.</summary>
    internal static class BattleStageLayout
    {
        internal const float Width = 19.2f;
        internal const float Height = 10.8f;
        internal const float FigureWidth = 1.18f;
        internal const float FigureHeight = 1.7f;

        internal static Vector3 GetFootPoint(BattleSide side, int slot)
        {
            if (!Formation.IsValid(slot)) throw new ArgumentOutOfRangeException(nameof(slot));
            int row = Formation.Row(slot), depth = Formation.Depth(slot);
            float direction = side == BattleSide.Allies ? -1 : 1;
            return new Vector3(direction * (2 + depth * 2 + (row - 1) * .24f), 1.5f - row * 1.3f, 0);
        }

        internal static float GetOrthographicSize(float aspect)
            => Mathf.Max(Height * .5f, Width / (Mathf.Max(.01f, aspect) * 2));

        internal static int GetRowOrder(int slot, int offset = 0)
            => Formation.Row(slot) * 100 + offset;
    }
}
