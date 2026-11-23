using System;

namespace IronOath.Combat
{
    public static class BattleCatalog
    {
        private static readonly BattleSkillDefinition[] Skills = BuildSkills();
        public static BattleSkillDefinition GetSkill(BattleSkillId id)
        { if ((uint)id >= (uint)Skills.Length) throw new ArgumentOutOfRangeException(nameof(id)); return Skills[(int)id]; }

        private static BattleSkillDefinition[] BuildSkills()
        {
            var s = new BattleSkillDefinition[(int)BattleSkillId.Pass + 1];
            Put(new BattleSkillDefinition(BattleSkillId.SwordStrike, "铁誓斩", "同时损伤白条与红条；无甲目标承受完整基础伤害。", 1, 0, 1, .9f, .65f));
            Put(new BattleSkillDefinition(BattleSkillId.ShieldBash, "盾缘破势", "满甲也必定失衡：闪避与格挡归零，封锁 PP。再次冲击合成击倒。", 1, 0, .75f, .7f, .3f, blunt: true));
            Put(new BattleSkillDefinition(BattleSkillId.HammerCrush, "碎铁重锤", "重锤合成击倒；对冰封目标造成 250% 白条伤害并碎冰。", 1, 0, 1.1f, 1.5f, .2f, blunt: true));
            Put(new BattleSkillDefinition(BattleSkillId.Hook, "锁链钩拉", "拉向前排一格；失衡升级击倒，撞上队友时按装甲与吨位结算碰撞。", 1, 0, .7f, .6f, .35f, blunt: true));
            Put(new BattleSkillDefinition(BattleSkillId.PreciseThrust, "甲缝穿刺", "对破绽目标造成完整穿甲红伤，保留白条；刺剑不打碎冰封。", 1, 0, 1.05f, .12f, 1, precision: true));
            Put(new BattleSkillDefinition(BattleSkillId.Soak, "潮汐药剂", "施加潮湿并留下水面，配合寒冰或雷电合成硬控。", 1, 0, .25f, .1f, .6f, physical: false));
            Put(new BattleSkillDefinition(BattleSkillId.Frost, "霜痕", "潮湿 + 寒冰合成冰封；格子留下冰面，位移踏入后滑倒。", 1, 0, .8f, .4f, .6f, physical: false));
            Put(new BattleSkillDefinition(BattleSkillId.Lightning, "引雷针", "潮湿 + 雷电合成麻痹；厚甲会把更强电伤传到十字相邻队友。", 1, 0, .95f, .25f, .8f, physical: false));
            Put(new BattleSkillDefinition(BattleSkillId.OilFlask, "黑油瓶", "施加油污并铺油，后续火焰触发双倍基础爆燃。", 1, 0, .15f, 0, .5f, physical: false));
            Put(new BattleSkillDefinition(BattleSkillId.Firebomb, "烈火爆燃", "油污或捕网 + 火焰触发双倍爆燃，消耗油/网并燃烧。", 1, 0, .9f, .65f, .75f, physical: false));
            Put(new BattleSkillDefinition(BattleSkillId.Net, "猎人捕网", "无视护甲定身并打开甲缝；火焰会引燃捕网。", 1, 0, .15f, 0, .4f));
            Put(new BattleSkillDefinition(BattleSkillId.Heal, "炼金急救", "为受伤友军恢复生命；不能复活，不超过最大生命。", 1, 0, 1.35f, 0, 0, physical: false));
            Put(new BattleSkillDefinition(BattleSkillId.Guard, "坚守", "消耗 1 PP 格挡普通物理命中，降低 30% 双伤；失衡或破绽时失效。", 0, 1, 0, 0, 0));
            Put(new BattleSkillDefinition(BattleSkillId.Assist, "铁誓协击", "十字相邻友军命中后消耗 1 PP 追击；反应不再连锁触发反应。", 0, 1, .45f, .4f, .6f));
            Put(new BattleSkillDefinition(BattleSkillId.Backstab, "影步背刺", "绕后攻击打开本次命中的甲缝，完整穿甲且保留白条。", 1, 0, .9f, .12f, 1, precision: true, backstab: true));
            Put(new BattleSkillDefinition(BattleSkillId.Collision, "吨位碰撞", "碰撞基础伤害 = 6 + 当前护甲 × 0.15 + 吨位 × 3。", 0, 0, 0, .65f, .55f));
            Put(new BattleSkillDefinition(BattleSkillId.Conduction, "铁甲引雷", "邻格电伤基础 = 4 + 导体当前护甲 × 0.18。", 0, 0, 0, .25f, .8f, physical: false));
            Put(new BattleSkillDefinition(BattleSkillId.Burn, "灼烧", "燃烧在目标每次行动机会开始时造成 6 点生命伤害。", 0, 0, 0, 0, 1, physical: false));
            Put(new BattleSkillDefinition(BattleSkillId.Pass, "等待", "没有可用战术时消耗一个行动机会，防止战斗停滞。", 1, 0, 0, 0, 0));
            return s;
            void Put(BattleSkillDefinition skill) { s[(int)skill.Id] = skill; }
        }

        public static BattleSimulation CreateDemo(bool chloeLeader = false, int encounterIndex = 0, uint seed = 1)
        {
            if (encounterIndex < 0 || encounterIndex > 5) throw new ArgumentOutOfRangeException(nameof(encounterIndex));
            int archetype = encounterIndex % 3;
            float depthScale = encounterIndex < 3 ? 1 : 1.1f;
            var battle = new BattleSimulation(seed);
            battle.AddUnit(new BattleUnit(Rayn(), BattleSide.Allies, 3));
            battle.AddUnit(new BattleUnit(Chloe(), BattleSide.Allies, chloeLeader ? 4 : 7));
            battle.AddUnit(new BattleUnit(Ally("brann", "布兰", "碎甲重卫", "铸铁重锤", 128, 78, 30, 16,
                new[] { BattleSkillId.HammerCrush, BattleSkillId.Guard },
                new[] { new TacticRule(BattleSkillId.HammerCrush, TacticCondition.TargetFrozen), new TacticRule(BattleSkillId.HammerCrush, TacticCondition.TargetUnbalanced), new TacticRule(BattleSkillId.HammerCrush) }), BattleSide.Allies, 0));
            battle.AddUnit(new BattleUnit(Ally("mira", "米菈", "潮汐炼金师", "水银试剂", 88, 26, 25, 28,
                new[] { BattleSkillId.Soak, BattleSkillId.Heal },
                new[] { new TacticRule(BattleSkillId.Heal, TacticCondition.AllyWounded, TargetPriority.LowestHealth), new TacticRule(BattleSkillId.Soak, priority: TargetPriority.HighestArmor) }), BattleSide.Allies, 2));
            battle.AddUnit(new BattleUnit(Ally("sable", "瑟柏", "霜雷术士", "引雷杖", 90, 22, 29, 24,
                new[] { BattleSkillId.Frost, BattleSkillId.Lightning },
                new[] { new TacticRule(BattleSkillId.Lightning, TacticCondition.TargetWet, TargetPriority.HighestArmor), new TacticRule(BattleSkillId.Frost) }), BattleSide.Allies, 5));
            battle.AddUnit(new BattleUnit(Ally("wren", "瑞恩娜", "灰羽游猎者", "捕网与刺剑", 95, 38, 28, 30,
                new[] { BattleSkillId.Net, BattleSkillId.PreciseThrust, BattleSkillId.Backstab },
                new[] { new TacticRule(BattleSkillId.PreciseThrust, TacticCondition.TargetGap), new TacticRule(BattleSkillId.Net) }), BattleSide.Allies, chloeLeader ? 7 : 4));

            if (archetype == 0)
            {
                AddEnemy("ledger", "赃物账房", "父亲的肩甲", 70, 115, 20, 17, 3, EnemyKind.Humanoid, true, 3, BattleSkillId.SwordStrike, BattleSkillId.Guard);
                AddEnemy("dockguard", "码头铁卫", "走私剑盾", 84, 92, 19, 14, 3, EnemyKind.Humanoid, false, 0, BattleSkillId.ShieldBash, BattleSkillId.Guard);
                AddEnemy("hookman", "铁钩打手", "锈钩", 72, 42, 21, 20, 3, EnemyKind.Humanoid, false, 6, BattleSkillId.Hook);
                AddEnemy("arsonist", "走私燃灯偶", "火油喷口", 62, 25, 21, 23, 3, EnemyKind.Construct, false, 4, BattleSkillId.OilFlask, BattleSkillId.Firebomb);
                AddEnemy("cutpurse", "无灯异魔", "黑刃", 62, 28, 20, 26, 3, EnemyKind.Demon, false, 7, BattleSkillId.PreciseThrust);
            }
            else if (archetype == 1)
            {
                AddEnemy("cryptwraith", "潮墓异魔", "结晶爪", 82, 60, 24, 24, 3, EnemyKind.Demon, false, 3, BattleSkillId.Soak, BattleSkillId.Frost);
                AddEnemy("bellkeeper", "沉钟守卫", "墓石重锤", 95, 96, 26, 14, 3, EnemyKind.Construct, false, 0, BattleSkillId.HammerCrush);
                AddEnemy("rime", "霜息畸体", "冰骨", 70, 50, 25, 22, 3, EnemyKind.Demon, false, 6, BattleSkillId.Frost);
                AddEnemy("cultist", "失声修士", "禁忌法器", 72, 42, 23, 25, 3, EnemyKind.Humanoid, false, 4, BattleSkillId.Lightning);
                AddEnemy("relicguard", "遗物看守", "母亲的护符", 76, 65, 21, 19, 3, EnemyKind.Humanoid, true, 7, BattleSkillId.PreciseThrust);
                battle.SetSurface(BattleSide.Allies, 4, SurfaceKind.Water);
                battle.SetSurface(BattleSide.Enemies, 4, SurfaceKind.Water);
            }
            else
            {
                AddEnemy("sentinel", "黑铁宣誓者", "教会战锤", 130, 160, 29, 14, 4, EnemyKind.Construct, false, 3, BattleSkillId.HammerCrush);
                AddEnemy("brasshound", "黄铜猎犬", "齿轮獠牙", 80, 72, 25, 27, 3, EnemyKind.Construct, false, 0, BattleSkillId.ShieldBash);
                AddEnemy("vessel", "古神容器", "旧日棘刺", 90, 84, 27, 23, 3, EnemyKind.Demon, false, 6, BattleSkillId.Hook);
                AddEnemy("warden", "密契执事", "银纹刺剑", 82, 90, 25, 21, 3, EnemyKind.Humanoid, true, 4, BattleSkillId.Backstab);
                AddEnemy("ember", "余烬构装", "火焰喷口", 78, 68, 24, 18, 3, EnemyKind.Construct, false, 7, BattleSkillId.Firebomb);
            }
            return battle;

            void AddEnemy(string id, string name, string weapon, float hp, float armor, float attack, float speed,
                int ap, EnemyKind kind, bool relic, int slot, params BattleSkillId[] skills)
            {
                BattleSkillId primary = skills[0];
                TacticRule[] tactics;
                if (primary == BattleSkillId.OilFlask) tactics = new[] { new TacticRule(BattleSkillId.Firebomb, TacticCondition.TargetOiled), new TacticRule(primary) };
                else if (primary == BattleSkillId.Soak && skills.Length > 1) tactics = new[] { new TacticRule(BattleSkillId.Frost, TacticCondition.TargetWet), new TacticRule(primary) };
                else tactics = new[] { new TacticRule(primary) };
                var definition = new UnitDefinition(id, name, kind == EnemyKind.Humanoid ? "黑石港敌对者" : "深渊守卫", weapon,
                    hp * depthScale, armor * depthScale, attack * depthScale, speed, ap, 2, armor >= 80 ? .6f : .35f, .04f, .45f, armor >= 80 ? 3 : 1.5f, kind, relic, skills, tactics);
                battle.AddUnit(new BattleUnit(definition, BattleSide.Enemies, slot));
            }
        }

        public static UnitDefinition Rayn() => Ally("rayn", "雷恩", "少东家 · 铁誓统领", "铁誓剑盾", 136, 88, 31, 32,
            new[] { BattleSkillId.ShieldBash, BattleSkillId.SwordStrike, BattleSkillId.Guard, BattleSkillId.Assist },
            new[] { new TacticRule(BattleSkillId.ShieldBash, TacticCondition.TargetArmored), new TacticRule(BattleSkillId.SwordStrike) });
        public static UnitDefinition Chloe() => Ally("chloe", "克萝伊", "少当家 · 秘宝猎手", "细刺剑与炼金腰带", 103, 43, 33, 34,
            new[] { BattleSkillId.PreciseThrust, BattleSkillId.Backstab, BattleSkillId.Net },
            new[] { new TacticRule(BattleSkillId.PreciseThrust, TacticCondition.TargetGap), new TacticRule(BattleSkillId.Net) });

        private static UnitDefinition Ally(string id, string name, string role, string weapon, float hp, float armor,
            float attack, float speed, BattleSkillId[] skills, TacticRule[] tactics)
            => new UnitDefinition(id, name, role, weapon, hp, armor, attack, speed, 5, 3, armor >= 70 ? .5f : .3f,
                .08f, .65f, armor >= 70 ? 2.5f : 1, EnemyKind.Humanoid, false, skills, tactics);
    }
}
