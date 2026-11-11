using IronOath.Campaign;
using IronOath.Combat;
using UnityEngine;
using UnityEngine.UI;

namespace IronOath.Presentation
{
    public sealed partial class IronOathDemo
    {
        bool _resultShown, _resultPending;
        float _resultAt;
        Text _advisor;
        ExpeditionOutcome _settlement;

        void BuildBattle()
        {
            if (_battle == null) PrepareBattle(_ticket != null ? _ticket.EncounterId : 0);
            _resultShown = false;
            _resultPending = false;
            _settlement = null;
            _logCount = 0;
            for (int i = 0; i < _log.Length; i++) _log[i] = "";
            _stage = new BattleStage(_screen, _battle, actors, InspectUnit);
            IronOathUi.Fill(_screen, "Battle upper shade", 0, 105, 1920, 118, new Color(0.025f, .05f, .07f, .72f));
            IronOathUi.Label(_screen, _practice ? "铁誓演武场" : _campaign.CurrentNode?.Name ?? "深渊遭遇",
                580, 121, 760, 50, 32, IronOathUi.PaleGold, TextAnchor.MiddleCenter);
            IronOathUi.Label(_screen, "铁誓小队", 70, 140, 400, 38, 24, IronOathUi.Ivory);
            IronOathUi.Label(_screen, "深渊的来客", 1450, 140, 400, 38, 24, new Color32(236, 183, 159, 255), TextAnchor.MiddleRight);
            _battleCounter = IronOathUi.Label(_screen, "战前就绪 · 点击开战", 660, 177, 600, 28, 18,
                IronOathUi.Muted, TextAnchor.MiddleCenter);
            _advisor = IronOathUi.Label(_screen, "克萝伊：留意白条与甲缝。卖盔甲，还是拿魔核？", 385, 746, 1150, 34,
                20, IronOathUi.PaleGold, TextAnchor.MiddleCenter);
            var inspect = IronOathUi.Panel(_screen, "Unit inspection", 64, 801, 477, 203);
            _inspectText = IronOathUi.Label(inspect, "点击战场角色\n查看护甲、生命与战术状态。", 23, 21, 430, 162, 19, IronOathUi.Ivory);
            var log = IronOathUi.Panel(_screen, "Battle chronicle", 561, 801, 818, 203);
            _battleLog = IronOathUi.Label(log, "战前编排完成。红豆决定主动次数，蓝豆决定反应机会。", 22, 17, 775, 174, 18,
                IronOathUi.Muted);
            var control = IronOathUi.Panel(_screen, "Playback", 1400, 801, 455, 203);
            _playButton = IronOathUi.Button(control, "开 战", 22, 22, 260, 58, ToggleAutoplay, true);
            IronOathUi.Button(control, "单步", 298, 22, 135, 58, AdvanceBattle);
            IronOathUi.Button(control, _speed + "× 速度", 22, 101, 185, 48, () =>
            {
                _speed = _speed == 1 ? 2 : 1;
                var speedText = control.GetComponentsInChildren<Text>();
                for (int i = 0; i < speedText.Length; i++)
                    if (speedText[i].text.EndsWith("× 速度")) speedText[i].text = _speed + "× 速度";
            });
            IronOathUi.Button(control, _practice ? "返回编排" : "撤退", 224, 101, 209, 48, RetreatBattle);
            IronOathUi.Label(control, "白条 · 护甲    红条 · 生命", 22, 163, 410, 24, 14, IronOathUi.Muted, TextAnchor.MiddleCenter);
        }

        void ToggleAutoplay()
        {
            if (_battle.State == BattleState.Formation) { _battle.Start(); ProcessEvents(); }
            if (_battle.State != BattleState.Fighting) return;
            _autoplay = !_autoplay;
            _nextAction = Time.unscaledTime + .3f;
            SetPlayLabel(_autoplay ? "暂停" : "继续");
        }

        void SetPlayLabel(string value)
        {
            if (_playButton != null)
            {
                Text label = _playButton.GetComponentInChildren<Text>();
                if (label != null) label.text = value;
            }
        }

        void AdvanceBattle()
        {
            if (_battle == null || _resultShown || _resultPending) return;
            if (_battle.State == BattleState.Formation) { _battle.Start(); ProcessEvents(); }
            if (_battle.State == BattleState.Fighting) { _battle.Step(); ProcessEvents(); }
            _stage.Refresh();
            if (_battleCounter != null) _battleCounter.text = "第 " + _battle.ActionNumber + " 次行动  ·  " +
                (_battle.State == BattleState.Fighting ? "自动演算" : OutcomeName(_battle.State));
            InspectUnit(_selectedUnit);
            if (_battle.State == BattleState.Victory || _battle.State == BattleState.Defeat || _battle.State == BattleState.Draw)
            {
                _autoplay = false;
                SetPlayLabel("战斗结束");
                _resultPending = true;
                _resultAt = Time.unscaledTime + .65f;
            }
        }

        void ProcessEvents()
        {
            for (int i = 0; i < _battle.EventCount; i++)
            {
                BattleEvent evt = _battle.GetEvent(i);
                _stage.Present(evt);
                string source = FindUnit(evt.SourceId)?.Name ?? "战场";
                string target = FindUnit(evt.TargetId)?.Name ?? "";
                string line = null;
                switch (evt.Kind)
                {
                    case BattleEventKind.Action:
                        line = source + "  →  " + BattleCatalog.GetSkill(evt.SkillId).Name;
                        break;
                    case BattleEventKind.Hit:
                        line = target + "  护甲 −" + evt.ArmorDamage.ToString("0") + " / 生命 −" + evt.HealthDamage.ToString("0");
                        break;
                    case BattleEventKind.ArmorBroken:
                        line = target + "  破甲崩溃 · PP −1";
                        if (_advisor != null) _advisor.text = "雷恩：护甲碎了！趁现在打断他们的节奏。";
                        break;
                    case BattleEventKind.StatusApplied:
                        line = target + "  " + LocalStatus(evt.Status);
                        if (_advisor != null) _advisor.text = (evt.Status & BattleUnit.GapStatuses) != 0
                            ? "克萝伊：状态打开了甲缝，穿甲手准备！"
                            : "克萝伊：铺垫已经做好，接上对应的元素技能！";
                        break;
                    case BattleEventKind.Skipped: line = source + (evt.Status != CombatStatus.None ? "  被控制，跳过行动" : "  没有符合条件的可用战术"); break;
                    case BattleEventKind.Defeated: line = target + "  已被击倒"; break;
                    case BattleEventKind.Miss: line = target + "  闪避了攻击"; break;
                    case BattleEventKind.Guarded: line = source + "  消耗蓝豆，发动格挡"; break;
                    case BattleEventKind.Healed: line = target + "  得到治疗"; break;
                    case BattleEventKind.Collision: line = "重装碰撞 · " + target; break;
                    case BattleEventKind.Moved: line = target + "  位置改变"; break;
                }
                if (line != null) AddLog(line);
            }
            _stage.Refresh();
        }

        void AddLog(string value)
        {
            if (_logCount < _log.Length) _log[_logCount++] = value;
            else
            {
                for (int i = 1; i < _log.Length; i++) _log[i - 1] = _log[i];
                _log[_log.Length - 1] = value;
            }
            if (_battleLog != null) _battleLog.text = string.Join("\n", _log, 0, _logCount);
        }

        void InspectUnit(int id)
        {
            _selectedUnit = id;
            BattleUnit unit = FindUnit(id);
            if (unit == null || _inspectText == null) return;
            _inspectText.text = unit.Name + "  ·  " + unit.Definition.Role + "\n" +
                "护甲 " + unit.Armor.ToString("0") + " / " + unit.MaxArmor.ToString("0") +
                "    生命 " + unit.Health.ToString("0") + " / " + unit.MaxHealth.ToString("0") + "\n" +
                "AP " + unit.AP + "  PP " + unit.PP + "    缓冲 " + (unit.EffectiveBuffer * 100).ToString("0") + "%\n" +
                (unit.HasGap ? "甲缝破绽已暴露" : "站姿完好 · 甲片缓冲生效") + "\n" + LocalStatus(unit.Status);
        }

        static string LocalStatus(CombatStatus status)
        {
            if (status == CombatStatus.None) return "无异常状态";
            return status.ToString().Replace("Unbalanced", "失衡").Replace("KnockedDown", "击倒")
                .Replace("Wet", "潮湿").Replace("Frozen", "冰封").Replace("Paralyzed", "麻痹")
                .Replace("Oiled", "油污").Replace("Rooted", "定身").Replace("Slipped", "冰面滑倒")
                .Replace("Burning", "燃烧").Replace("Exposed", "甲缝破绽").Replace(", ", " · ");
        }

        static string OutcomeName(BattleState state)
            => state == BattleState.Victory ? "胜利" : state == BattleState.Draw ? "僵持" : state == BattleState.Defeat ? "失利" : "准备";

        void ShowBattleResult()
        {
            _resultPending = false;
            _resultShown = true;
            var blocker = IronOathUi.Fill(_screen, "Settlement input shield", 0, 0, 1920, 1080, new Color(0, 0, 0, .38f));
            blocker.raycastTarget = true;
            var panel = IronOathUi.Panel(_screen, "Battle settlement", 540, 270, 840, 454);
            IronOathUi.Label(panel, OutcomeName(_battle.State), 50, 35, 740, 70, 50,
                IronOathUi.PaleGold, TextAnchor.MiddleCenter);
            IronOathUi.Label(panel, _practice ? "演练结束 · 不结算经营资源" :
                _battle.State == BattleState.Victory ? "收起武器，看看这场胜利留下了什么。" : "活着回到店里，总有下一次机会。",
                50, 117, 740, 46, 22, IronOathUi.Ivory, TextAnchor.MiddleCenter);
            string loot = "";
            if (_practice)
            {
                loot = "演练不发放金币或物品。\n本次演算发现 " + _battle.LootCount + " 件可回收战利品。";
            }
            else
            {
                _settlement = IronOath.BattleRewards.FromBattle(_ticket, _battle);
                for (int i = 0; i < _settlement.Loot.Length; i++)
                {
                    LootAward award = _settlement.Loot[i];
                    ItemDefinition item = ItemCatalog.Get(award.Kind);
                    loot += (i > 0 ? "\n" : "") + item.Name + "   " + item.Width + "×" + item.Height + " 格";
                }
                if (_settlement.Victory) loot = "金币 +" + _settlement.Coins + " G\n" + loot;
                if (_settlement.Loot.Length == 0) loot += _settlement.Victory
                    ? "未获得可回收物品。下次试试保甲或彻底破甲。" : "本次撤离没有战利品。";
            }
            IronOathUi.Label(panel, loot, 70, 189, 700, 159, 20, IronOathUi.Muted, TextAnchor.MiddleCenter);
            IronOathUi.Button(panel, _practice ? "返回战前编排" : "清点战利品 · 返回当铺", 170, 369, 500, 55,
                CompleteBattle, true);
        }

        void CompleteBattle()
        {
            if (_practice)
            {
                _battle = null; ShowPage(Page.Formation); return;
            }
            CampaignResult result = _campaign.SettleExpedition(_settlement ?? IronOath.BattleRewards.FromBattle(_ticket, _battle));
            _ticket = null;
            _battle = null;
            Save();
            ShowPage(Page.Shop);
            Toast(result.Message, result.Succeeded);
        }

        void RetreatBattle()
        {
            if (_resultPending || _resultShown) return;
            _autoplay = false;
            if (_practice) { _battle = null; ShowPage(Page.Formation); return; }
            CampaignResult result = _campaign.AbandonExpedition();
            _ticket = null; _battle = null;
            Save(); ShowPage(Page.Dungeon); Toast(result.Message, result.Succeeded);
        }
    }
}
