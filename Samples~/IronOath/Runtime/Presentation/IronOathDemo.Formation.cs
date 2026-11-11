using IronOath.Campaign;
using IronOath.Combat;
using UnityEngine;

namespace IronOath.Presentation
{
    public sealed partial class IronOathDemo
    {
        bool _movingUnit;
        int _preparedEncounter = -1;

        void PrepareBattle(int encounter)
        {
            _preparedEncounter = encounter;
            _battle = BattleCatalog.CreateDemo(_campaign.Protagonist == Protagonist.Chloe, encounter,
                unchecked((uint)campaignSeed));
            if (_formation == null)
            {
                _formation = new int[6];
                for (int i = 0; i < _battle.UnitCount; i++)
                {
                    BattleUnit unit = _battle.GetUnit(i);
                    if (unit.Side == BattleSide.Allies && unit.Id < _formation.Length) _formation[unit.Id] = unit.Slot;
                }
            }
            for (int i = 0; i < _battle.UnitCount; i++)
            {
                BattleUnit unit = _battle.GetUnit(i);
                if (unit.Side != BattleSide.Allies || unit.Id >= _formation.Length) continue;
                _battle.MoveUnit(unit.Id, _formation[unit.Id]);
                if (_savedTactics[unit.Id] != null)
                    for (int row = 0; row < BattleUnit.TacticCount; row++) _battle.SetTactic(unit.Id, row, _savedTactics[unit.Id][row]);
            }
        }

        void RememberFormation()
        {
            if (_battle == null) return;
            for (int i = 0; i < _battle.UnitCount; i++)
            {
                BattleUnit unit = _battle.GetUnit(i);
                if (unit.Side != BattleSide.Allies || unit.Id >= _formation.Length) continue;
                _formation[unit.Id] = unit.Slot;
                if (_savedTactics[unit.Id] == null) _savedTactics[unit.Id] = new TacticRule[BattleUnit.TacticCount];
                for (int row = 0; row < BattleUnit.TacticCount; row++) _savedTactics[unit.Id][row] = unit.GetTactic(row);
            }
            Save();
        }

        BattleUnit FindUnit(int id)
        {
            if (_battle == null) return null;
            for (int i = 0; i < _battle.UnitCount; i++) if (_battle.GetUnit(i).Id == id) return _battle.GetUnit(i);
            return null;
        }

        void BuildFormation()
        {
            int encounter = _campaign.CurrentNode != null ? _campaign.CurrentNode.EncounterId : 0;
            if (_battle == null || _battle.State != BattleState.Formation || _preparedEncounter != encounter)
                PrepareBattle(encounter);
            IronOathUi.Label(_screen, "胜负，始于交锋之前。", 65, 143, 1000, 57, 36, IronOathUi.PaleGold);
            IronOathUi.Label(_screen, "双方各 3×3 格 · 最多 6 人 · 红豆 AP 主动 / 蓝豆 PP 被动", 68, 213, 1040, 35, 22, IronOathUi.Muted);
            BuildFormationBoard(BattleSide.Allies, 65, 318);
            BuildFormationBoard(BattleSide.Enemies, 624, 318);
            IronOathUi.Label(_screen, "后排                  中排                   前排", 80, 283, 500, 25, 18, IronOathUi.Gold);
            IronOathUi.Label(_screen, "前排                  中排                   后排", 640, 283, 500, 25, 18, IronOathUi.Gold);
            IronOathUi.Label(_screen, "我方阵列", 65, 730, 505, 40, 25, IronOathUi.Ivory);
            IronOathUi.Label(_screen, "当前遭遇 · 敌方阵列", 624, 730, 505, 40, 25, IronOathUi.Ivory);
            IronOathUi.Label(_screen, _movingUnit ? "移动模式：点我方空格移动，点有人的格子交换。" :
                "选择角色查看战术。三处空位留给推拉、碰撞与元素地面。", 65, 794, 1050, 63, 22, IronOathUi.Muted);
            BattleUnit selected = FindUnit(_selectedUnit) ?? _battle.GetUnit(0);
            _selectedUnit = selected.Id;
            IronOathUi.Button(_screen, _movingUnit ? "取消移动" : "移动站位", 65, 881, 270, 57,
                () => { _movingUnit = !_movingUnit; ShowPage(Page.Formation); }, false, selected.Side == BattleSide.Allies);
            IronOathUi.Button(_screen, "演练当前阵容", 357, 881, 320, 57, () =>
            {
                RememberFormation(); _practice = true; _ticket = null; ShowPage(Page.Battle);
            }, true);
            IronOathUi.Button(_screen, "带队探索", 699, 881, 430, 57,
                () => { RememberFormation(); ShowPage(Page.Dungeon); });
            BuildTactics(selected);
        }

        void BuildFormationBoard(BattleSide side, float x, float y)
        {
            for (int row = 0; row < 3; row++)
                for (int column = 0; column < 3; column++)
                {
                    int depth = side == BattleSide.Allies ? 2 - column : column;
                    int slot = row * 3 + depth;
                    BattleUnit occupant = _battle.GetOccupant(side, slot);
                    var cell = IronOathUi.Panel(_screen, "Formation cell " + side + " " + slot,
                        x + column * 174, y + row * 132, 162, 120,
                        occupant != null && occupant.Id == _selectedUnit ? new Color32(66, 74, 68, 245) : IronOathUi.PanelInk, false);
                    if (occupant == null)
                    {
                        IronOathUi.Label(cell, "空 位", 10, 20, 142, 32, 23, IronOathUi.Muted, TextAnchor.MiddleCenter);
                        IronOathUi.Button(cell, "+", 22, 61, 118, 35, () =>
                        {
                            if (side != BattleSide.Allies || FindUnit(_selectedUnit)?.Side != BattleSide.Allies) return;
                            if (_battle.MoveUnit(_selectedUnit, slot))
                            { _movingUnit = false; RememberFormation(); ShowPage(Page.Formation); }
                        });
                    }
                    else
                    {
                        int unitId = occupant.Id;
                        IronOathUi.Label(cell, occupant.Name, 8, 10, 146, 30, 21,
                            side == BattleSide.Allies ? IronOathUi.PaleGold : new Color32(227, 170, 149, 255), TextAnchor.MiddleCenter);
                        IronOathUi.Label(cell, occupant.Definition.Role, 7, 44, 148, 25, 14, IronOathUi.Muted, TextAnchor.MiddleCenter);
                        IronOathUi.Button(cell, "AP " + occupant.AP + "  /  PP " + occupant.PP, 10, 78, 142, 31, () =>
                        {
                            if (_movingUnit && side == BattleSide.Allies)
                            {
                                _battle.MoveUnit(_selectedUnit, slot);
                                _movingUnit = false;
                                RememberFormation();
                            }
                            else { _selectedUnit = unitId; if (side == BattleSide.Enemies) _movingUnit = false; }
                            ShowPage(Page.Formation);
                        });
                    }
                }
        }

        void BuildTactics(BattleUnit selected)
        {
            bool editable = selected.Side == BattleSide.Allies;
            var panel = IronOathUi.Panel(_screen, "Tactics", 1187, 151, 668, 787);
            IronOathUi.Label(panel, selected.Name, 30, 27, 600, 46, 33, IronOathUi.PaleGold);
            IronOathUi.Label(panel, selected.Definition.Role + "  ·  " + selected.Definition.WeaponName,
                32, 82, 600, 33, 20, IronOathUi.Muted);
            IronOathUi.Label(panel, "生命 " + selected.MaxHealth.ToString("0") + "   护甲 " + selected.MaxArmor.ToString("0") +
                "   甲片缓冲 " + (selected.Definition.Buffer * 100).ToString("0") + "%", 32, 127, 600, 31, 20, IronOathUi.Ivory);
            IronOathUi.Rule(panel, 30, 178, 608);
            IronOathUi.Label(panel, editable ? "优先级从上到下 · 点击切换条件与目标" : "敌方战术 · 可查看，不可改写",
                32, 198, 600, 28, 18, IronOathUi.Muted);
            for (int row = 0; row < BattleUnit.TacticCount; row++)
            {
                int tacticRow = row;
                TacticRule tactic = selected.GetTactic(row);
                float y = 249 + row * 113;
                IronOathUi.Label(panel, (row + 1).ToString("00"), 30, y + 12, 45, 40, 25,
                    tactic.Enabled ? IronOathUi.Gold : IronOathUi.Muted);
                IronOathUi.Button(panel, BattleCatalog.GetSkill(tactic.Skill).Name, 83, y, 350, 43,
                    () => CycleTacticSkill(selected, tacticRow), false, editable);
                IronOathUi.Button(panel, tactic.Enabled ? "启用" : "关闭", 453, y, 92, 43, () =>
                {
                    _battle.SetTactic(selected.Id, tacticRow, new TacticRule(tactic.Skill, tactic.Condition, tactic.Priority, !tactic.Enabled));
                    RememberFormation(); ShowPage(Page.Formation);
                }, tactic.Enabled, editable);
                IronOathUi.Button(panel, "↑", 560, y, 70, 43, () =>
                {
                    TacticRule above = selected.GetTactic(tacticRow - 1);
                    _battle.SetTactic(selected.Id, tacticRow - 1, tactic);
                    _battle.SetTactic(selected.Id, tacticRow, above);
                    RememberFormation(); ShowPage(Page.Formation);
                }, false, editable && row > 0);
                IronOathUi.Button(panel, ConditionName(tactic.Condition), 83, y + 51, 265, 37, () =>
                {
                    var condition = (TacticCondition)(((int)tactic.Condition + 1) % 10);
                    _battle.SetTactic(selected.Id, tacticRow, new TacticRule(tactic.Skill, condition, tactic.Priority, tactic.Enabled));
                    RememberFormation(); ShowPage(Page.Formation);
                }, false, editable);
                IronOathUi.Button(panel, PriorityName(tactic.Priority), 365, y + 51, 265, 37, () =>
                {
                    var priority = (TargetPriority)(((int)tactic.Priority + 1) % 5);
                    _battle.SetTactic(selected.Id, tacticRow, new TacticRule(tactic.Skill, tactic.Condition, priority, tactic.Enabled));
                    RememberFormation(); ShowPage(Page.Formation);
                }, false, editable);
            }
            IronOathUi.Label(panel, "先失衡，再推撞 → 击倒\n潮湿 + 寒冰 / 雷电 → 冰封 / 麻痹", 32, 722, 600, 48, 17, IronOathUi.Muted);
        }

        void CycleTacticSkill(BattleUnit unit, int row)
        {
            TacticRule old = unit.GetTactic(row);
            int index = -1;
            for (int i = 0; i < unit.Definition.SkillCount; i++) if (unit.Definition.GetSkill(i) == old.Skill) index = i;
            for (int offset = 1; offset <= unit.Definition.SkillCount; offset++)
            {
                BattleSkillId candidate = unit.Definition.GetSkill((index + offset) % unit.Definition.SkillCount);
                if (candidate == BattleSkillId.Guard || candidate == BattleSkillId.Assist) continue;
                _battle.SetTactic(unit.Id, row, new TacticRule(candidate, old.Condition, old.Priority, old.Enabled));
                break;
            }
            RememberFormation(); ShowPage(Page.Formation);
        }

        static string ConditionName(TacticCondition condition)
        {
            switch (condition)
            {
                case TacticCondition.TargetArmored: return "条件 · 目标仍有护甲";
                case TacticCondition.TargetUnbalanced: return "条件 · 目标失衡";
                case TacticCondition.TargetGap: return "条件 · 甲缝已暴露";
                case TacticCondition.TargetWet: return "条件 · 目标潮湿";
                case TacticCondition.TargetFrozen: return "条件 · 目标冰封";
                case TacticCondition.TargetLowHealth: return "条件 · 目标残血";
                case TacticCondition.AllyWounded: return "条件 · 友军受伤";
                case TacticCondition.TargetOiled: return "条件 · 目标沾油";
                case TacticCondition.TargetRooted: return "条件 · 目标定身";
                default: return "条件 · 无条件";
            }
        }

        static string PriorityName(TargetPriority priority)
        {
            switch (priority)
            {
                case TargetPriority.LowestHealth: return "目标 · 生命最低";
                case TargetPriority.HighestArmor: return "目标 · 护甲最高";
                case TargetPriority.Backmost: return "目标 · 后排优先";
                case TargetPriority.SameRow: return "目标 · 同排优先";
                default: return "目标 · 前排优先";
            }
        }
    }
}
