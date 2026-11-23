using System;
using IronOath.Campaign;
using IronOath.Combat;

namespace IronOath
{
    /// <summary>One conversion from a finished GAS battle into the campaign's atomic reward transaction.</summary>
    public static class BattleRewards
    {
        public static ExpeditionOutcome FromBattle(ExpeditionTicket ticket, BattleSimulation battle)
        {
            if (ticket == null) throw new ArgumentNullException(nameof(ticket));
            if (battle == null) throw new ArgumentNullException(nameof(battle));
            if (battle.State != BattleState.Victory && battle.State != BattleState.Defeat && battle.State != BattleState.Draw)
                throw new InvalidOperationException("Only a finished battle can produce expedition rewards.");

            bool victory = battle.State == BattleState.Victory;
            var awards = victory ? new LootAward[battle.LootCount] : Array.Empty<LootAward>();
            for (int i = 0; i < awards.Length; i++)
            {
                BattleLoot loot = battle.GetLoot(i);
                ItemKind kind = loot.Kind == BattleLootKind.Core ? ItemKind.MagicCore : loot.Kind == BattleLootKind.IntactArmor
                    ? ItemKind.IntactArmor : ticket.Depth == 1 ? ItemKind.FathersSignet :
                        ticket.Depth == 2 ? ItemKind.MothersLens : ItemKind.ChurchDispatch;
                int condition = 100;
                if (kind == ItemKind.IntactArmor)
                {
                    for (int unitIndex = 0; unitIndex < battle.UnitCount; unitIndex++)
                    {
                        BattleUnit source = battle.GetUnit(unitIndex);
                        if (source.Id != loot.SourceUnitId || source.MaxArmor <= 0) continue;
                        condition = Math.Max(80, Math.Min(100,
                            (int)Math.Round(source.Armor / source.MaxArmor * 100, MidpointRounding.ToEven)));
                        break;
                    }
                }
                awards[i] = new LootAward(kind, condition);
            }
            return new ExpeditionOutcome {
                TicketId = ticket.Id,
                Victory = victory,
                Coins = victory ? 35 + ticket.Depth * 15 : 0,
                Loot = awards
            };
        }
    }
}
