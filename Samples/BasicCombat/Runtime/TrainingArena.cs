using System;
using System.Collections.Generic;
using GameplayTags;
using UnityEngine;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Samples.BasicCombat
{
    /// <summary>
    /// 训练场宿主：演示一个游戏需要为 GAS 做的全部接线——
    /// 创建 Cue 路由与 ASC、通过技能资产请求施放、
    /// 固定步长推进、把 Cue 事件变成特效，以及提供投射物所需的场景服务。
    /// </summary>
    public sealed class TrainingArena : MonoBehaviour
    {
        [Serializable]
        public sealed class Skill
        {
            public string displayName;
            public GameplayAbilityAsset asset;
            public string Name => asset != null ? asset.DisplayName : displayName;
            public Color color = Color.white;
        }

        [SerializeField] private CombatUnit hero, dummy;
        [Tooltip("投射物出手点（剑尖）")]
        [SerializeField] private Transform castPoint;
        [Tooltip("投射物瞄准点（假人胸口）")]
        [SerializeField] private Transform aimPoint;
        [SerializeField] private Skill[] skills = new Skill[0];
        [SerializeField] private AbilitySetAsset abilitySet;
        [SerializeField] private SkillClipOverrideSet axeOverride;
        [SerializeField] private GameplayAbilityAsset boltSkill;
        [SerializeField] private GameplayAbilityAsset bounceSkill;
        [SerializeField] private CombatUnit[] secondary=Array.Empty<CombatUnit>();
        private float[] _resetAt;
        private bool _fan=true,_qHeld;
        public bool FanMode=>_fan;
        private readonly Dictionary<AbilitySourceIdentity, int> _teams = new();
        private bool MatchesTeam(AbilitySourceIdentity source, AbilitySystemComponent target, TargetRelation relation)
            => _teams.TryGetValue(source, out int from) && _teams.TryGetValue(target.SourceIdentity, out int to)
                && target.Attributes.GetCurrent(CombatUnit.Health) > 0 && (relation == TargetRelation.Ally ? from == to : from != to);
        private SkillAnimator _equipped;
        [Tooltip("体力每秒回复")]
        [SerializeField] private float staminaRegen = 16;
        [Header("表现")]
        [SerializeField] private Material burstMaterial;
        [SerializeField] private Mesh burstMesh;

        private GameplayCueRouter _router;
        private ArenaPresentation _presentation;
        private Animator _dummyAnimator;
        private readonly List<Popup> _popups = new();
        private readonly List<Effect> _effects = new();
        private string _toast;
        private float _toastTime, _shake, _dummyResetAt = -1;
        private bool _resetting;
        private GUIStyle _popupStyle, _labelStyle, _smallStyle, _titleStyle, _keyStyle, _countStyle;
        private MaterialPropertyBlock _block;
        private Vector3 _cameraHome;
        private Transform _camera;
        private Texture2D _white;

        private struct Popup { public string Text; public Vector3 Position; public Color Color; public float Age; public float Size; }
        private sealed class Effect { public Transform Transform; public Renderer[] Renderers; public float Age, Life, From, To; public Color Color; public bool Fade; }

        private void Awake()
        {
            if (!TagsRegistered()) { enabled = false; return; }
            _router = new GameplayCueRouter();
            _presentation = gameObject.AddComponent<ArenaPresentation>();
            var searcher = new TargetSearcher(16);
            void Bind(CombatUnit unit, int team, Transform launch = null)
            {
                var services = new SkillUnitServices {
                    Origin = unit.transform, LaunchPoint = launch, TargetRelations = MatchesTeam, Searcher = searcher, Scene = gameObject.scene
                };
                services.Set<ISkillCameraService>(_presentation);
                services.Set<ISkillTimeService>(_presentation);
                services.Set<ISkillRigService>(_presentation);
                unit.Initialize(_router, services);
                _teams.Add(unit.AbilitySystem.SourceIdentity, team);
            }
            Bind(hero, 0, castPoint);
            Bind(dummy, 1);
            _dummyAnimator = dummy.GetComponentInChildren<Animator>();
            _resetAt = new float[secondary.Length];
            for(int i=0;i<secondary.Length;i++){var u=secondary[i];Bind(u,1);u.HealthChanged+=OnHealthChanged;_resetAt[i]=-1;}
            if (abilitySet != null) abilitySet.GrantTo(hero.SkillRunner);
            else { foreach (var skill in skills) hero.SkillRunner.Grant(skill.asset);hero.SkillRunner.Grant(bounceSkill); }

            if (GameplayTagManager.RequestTag(SampleTags.CueHit, out var hitTag)) _router.Register(hitTag, OnHitCue);

            dummy.HealthChanged += OnHealthChanged;
            hero.HealthChanged += OnHealthChanged;
        }

        /// <summary>
        /// 示例技能的冷却与 Cue 依赖示例标签；未注册时构造能力定义会抛异常，这里提前给出可操作的提示。
        /// </summary>
        private bool TagsRegistered()
        {
            var missing = new List<string>();
            try
            {
                foreach (var tag in SampleTags.All) if (!GameplayTagManager.RequestTag(tag, out _)) missing.Add(tag);
            }
            catch (InvalidOperationException) { missing.AddRange(SampleTags.All); }
            if (missing.Count == 0) return true;
            Debug.LogError("BasicCombat：以下示例标签尚未注册，训练场已停用：" + string.Join("，", missing) +
                "。请运行菜单 Tools → ZFramework GAS → 创建 BasicCombat 示例（会写入标签配置并重新生成注册表），等待编译完成后再进入 Play。", this);
            return false;
        }

        private void Tick(CombatUnit unit, float dt)
        {
            try { unit.AbilitySystem.Tick(dt); }
            catch (Exception error) {
                Debug.LogException(error, unit);
                var failed=unit.AbilitySystem.CurrentTask;
                if(failed?.Ability!=null && failed.ActivationVersion==failed.Ability.ActivationVersion)unit.AbilitySystem.EndAbility(failed.Ability);
            }
        }
        private void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;
            Tick(hero, dt);
            Tick(dummy, dt);
            for(int i=0;i<secondary.Length;i++){
                var u=secondary[i];Tick(u, dt);
                if(u.Get(CombatUnit.Health)<=0 && _resetAt[i]<0)_resetAt[i]=Time.time+1.2f;
                if(_resetAt[i]>=0 && Time.time>=_resetAt[i]){
                    _resetAt[i]=-1;_resetting=true;u.SetBase(CombatUnit.Health,u.Get(CombatUnit.MaxHealth));_resetting=false;
                }
            }
            hero.SetBase(CombatUnit.Stamina, Mathf.Min(hero.MaxStamina, hero.Get(CombatUnit.Stamina) + staminaRegen * dt));
            if (_dummyResetAt < 0 && dummy.Get(CombatUnit.Health) <= 0) { _dummyResetAt = Time.time + 1.2f; Toast("训练假人被击倒，即将复位"); }
            if (_dummyResetAt > 0 && Time.time >= _dummyResetAt)
            {
                _dummyResetAt = -1; _resetting = true;
                dummy.SetBase(CombatUnit.Health, dummy.Get(CombatUnit.MaxHealth));
                _resetting = false;
            }
        }

        private void LateUpdate()
        {
            float dt = Time.deltaTime;
            for (int i = _effects.Count - 1; i >= 0; i--)
            {
                var e = _effects[i]; e.Age += dt; float t = Mathf.Clamp01(e.Age / e.Life);
                if (e.Transform == null || t >= 1) { if (e.Transform != null) Destroy(e.Transform.gameObject); _effects.RemoveAt(i); continue; }
                e.Transform.localScale = Vector3.one * Mathf.Lerp(e.From, e.To, 1 - (1 - t) * (1 - t));
                float alpha = e.Fade ? 1 - t : Mathf.Clamp01((1 - t) * 3) * Mathf.Clamp01(t * 6);
                _block ??= new MaterialPropertyBlock();
                _block.SetColor("_TintColor", new Color(e.Color.r, e.Color.g, e.Color.b, e.Color.a * alpha));
                foreach (var r in e.Renderers) r.SetPropertyBlock(_block);
            }
            for (int i = _popups.Count - 1; i >= 0; i--)
            {
                var p = _popups[i]; p.Age += dt; p.Position += Vector3.up * dt * .7f; _popups[i] = p;
                if (p.Age > 1.1f) _popups.RemoveAt(i);
            }
            // 受击震屏：围绕初始机位抖动，不会累积漂移。
            if (_camera == null && Camera.main != null) { _camera = Camera.main.transform; _cameraHome = _camera.localPosition; }
            if (_camera != null)
            {
                _shake = Mathf.Max(0, _shake - dt * 4);
                _camera.localPosition = _cameraHome + _presentation.CameraOffset() + UnityEngine.Random.insideUnitSphere * (_shake * _shake * .03f);
            }
        }

        // ───────────── 施放 ─────────────

        private GameplayAbilityAsset Selected(Skill skill)=>skill.asset==boltSkill && !_fan?bounceSkill:skill.asset;
        public void Cast(int index) => TryCast(index);
        public void ToggleBoltMode(){_fan=!_fan;Toast("能量弹："+(_fan?"分叉闪电":"逐个弹射"));}
        private void OnApplicationFocus(bool focus){if(!focus)_qHeld=false;}
        public void ToggleWeaponMotion()
        {
            if(_equipped!=null){_equipped.PopOverride(axeOverride);_equipped=null;return;}
            if(axeOverride!=null && hero!=null){_equipped=hero.GetComponentInChildren<SkillAnimator>();_equipped?.PushOverride(axeOverride);}
        }
        public bool TryCast(int index)
        {
            if (index < 0 || index >= skills.Length) return false;
            var asset=Selected(skills[index]);
            AbilityActivationResult result;
            try { result = hero.SkillRunner.TryCast(asset, new SkillCastTarget(dummy.AbilitySystem, aimPoint)); }
            catch (Exception error) { Debug.LogException(error, this); hero.SkillRunner.Cancel(asset); return false; }
            if (result != AbilityActivationResult.Activated)
            {
                Toast(result switch
                {
                    AbilityActivationResult.OnCooldown => asset.DisplayName + " 冷却中",
                    AbilityActivationResult.CannotAfford => "体力不足",
                    AbilityActivationResult.AlreadyActive => asset.DisplayName + " 正在施放",
                    _ => "当前状态无法施放 " + asset.DisplayName,
                });
                return false;
            }
            return true;
        }

        private void OnHitCue(GameplayCueParameters cue)
        {
            if(!ReferenceEquals(cue.Target,dummy.AbilitySystem)){
                foreach(var u in secondary)if(ReferenceEquals(cue.Target,u.AbilitySystem)){
                    u.GetComponentInChildren<Animator>()?.Play("Hit",0,0);
                    SpawnBurst(u.transform.position+Vector3.up,Color.cyan,.15f,.8f,.22f);break;
                }
                return;
            }
            if (_dummyAnimator != null) _dummyAnimator.Play("Hit", 0, 0);
            SpawnBurst(aimPoint.position, new Color(1f, .82f, .45f, .95f), .15f, .9f, .28f);
            _shake = .8f;
        }

        private void OnHealthChanged(CombatUnit unit, float delta)
        {
            if (_resetting) { AddPopup(unit.HudPosition, "复位", new Color(.75f, .85f, 1f), 20); return; }
            bool heal = delta > 0;
            AddPopup(unit.HudPosition + UnityEngine.Random.insideUnitSphere * .15f, (heal ? "+" : "") + Mathf.RoundToInt(delta),
                heal ? new Color(.45f, 1f, .6f) : new Color(1f, .86f, .4f), heal ? 20 : 28);
        }

        private void SpawnBurst(Vector3 position, Color color, float from, float to, float life)
        {
            if (burstMesh == null || burstMaterial == null) return;
            var go = new GameObject("Burst");
            go.transform.position = position;
            go.AddComponent<MeshFilter>().sharedMesh = burstMesh;
            var renderer = go.AddComponent<MeshRenderer>(); renderer.sharedMaterial = burstMaterial;
            go.transform.rotation = UnityEngine.Random.rotation;
            _effects.Add(new Effect { Transform = go.transform, Renderers = new Renderer[] { renderer }, Life = life, From = from, To = to, Color = color, Fade = true });
        }

        private void AddPopup(Vector3 position, string text, Color color, float size)
            => _popups.Add(new Popup { Position = position, Text = text, Color = color, Size = size });

        private void Toast(string text) { _toast = text; _toastTime = Time.time; }

        // ───────────── HUD ─────────────

        private void HandleModeKey(Event e){
            if(e.keyCode==KeyCode.Q && (e.type==EventType.KeyDown || e.type==EventType.KeyUp)){
                if(e.type==EventType.KeyDown && !_qHeld)ToggleBoltMode();
                _qHeld=e.type==EventType.KeyDown;e.Use();
            }
            if(e.type==EventType.KeyDown && e.keyCode==KeyCode.E){ToggleWeaponMotion();e.Use();}

        }

        private void OnGUI()
        {
            EnsureStyles();
            var e = Event.current;
            // 键盘经 IMGUI 事件读取，新旧输入系统下都可用。
            if (e.type == EventType.KeyDown && e.keyCode >= KeyCode.Alpha1 && e.keyCode <= KeyCode.Alpha9)
            { Cast(e.keyCode - KeyCode.Alpha1); e.Use(); }
            HandleModeKey(e);

            var camera = Camera.main;
            float scale = Mathf.Max(1, Screen.height / 900f);
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));
            float width = Screen.width / scale, height = Screen.height / scale;

            // 标题
            Panel(new Rect(20, 18, 455, 64), new Color(.06f, .08f, .12f, .78f));
            GUI.Label(new Rect(34, 24, 320, 30), "BasicCombat · 训练场", _titleStyle);
            GUI.Label(new Rect(34, 52, 440, 22), "1–5 施放 · Q 能量弹："+(_fan?"分叉闪电":"逐个弹射")+" · E 武器："+(_equipped!=null?"斧劈":"剑斩"), _smallStyle);

            // 假人血条
            if(camera!=null){WorldBar(camera,dummy,scale);foreach(var u in secondary)WorldBar(camera,u,scale);}

            // 状态标签
            var chips = new List<(string, Color)>();
            void Chip(string tag, string label, Color color) { if (GameplayTagManager.RequestTag(tag, out var t) && hero.AbilitySystem.OwnedTags.HasTag(t)) chips.Add((label, color)); }
            Chip(SampleTags.SuperArmor, "霸体", new Color(1f, .75f, .3f));
            Chip(SampleTags.ComboWindow, "连招窗口", new Color(.5f, .85f, 1f));
            Chip(SampleTags.Channeling, "引导中", new Color(.4f, .95f, 1f));
            if (hero.Get(CombatUnit.Attack) > 20.5f) chips.Add(("战意 · 攻击 " + Mathf.RoundToInt(hero.Get(CombatUnit.Attack)), new Color(1f, .6f, .3f)));
            float chipX = width / 2 - chips.Count * 62;
            foreach (var (label, color) in chips)
            {
                Panel(new Rect(chipX, height - 196, 116, 26), new Color(color.r * .35f, color.g * .35f, color.b * .35f, .9f));
                Panel(new Rect(chipX, height - 196, 4, 26), color);
                GUI.Label(new Rect(chipX + 10, height - 194, 104, 22), label, _labelStyle);
                chipX += 124;
            }

            // 生命与体力
            float barWidth = 5 * 92 - 12, barX = width / 2 - barWidth / 2;
            Bar(new Rect(barX, height - 160, barWidth, 14), hero.Get(CombatUnit.Health) / hero.Get(CombatUnit.MaxHealth), new Color(.86f, .24f, .3f), $"生命 {Mathf.CeilToInt(hero.Get(CombatUnit.Health))}");
            Bar(new Rect(barX, height - 140, barWidth, 10), hero.Get(CombatUnit.Stamina) / hero.MaxStamina, new Color(.95f, .78f, .3f), null);

            // 技能栏
            for (int i = 0; i < skills.Length; i++)
            {
                var skill = skills[i];var asset=Selected(skill);
                var rect = new Rect(barX + i * 92, height - 120, 80, 80);
                Panel(rect, new Color(.07f, .09f, .14f, .92f));
                Panel(new Rect(rect.x, rect.y, rect.width, 4), skill.color);
                if (GUI.Button(rect, GUIContent.none, GUIStyle.none)) Cast(i);
                GUI.Label(new Rect(rect.x + 6, rect.y + 8, 30, 20), (i + 1).ToString(), _keyStyle);
                if (asset.icon != null) GUI.DrawTexture(new Rect(rect.x + 25, rect.y + 10, 30, 30), asset.icon.texture, ScaleMode.ScaleToFit);
                GUI.Label(new Rect(rect.x, rect.y + 46, rect.width, 22), new GUIContent(asset.DisplayName, asset.description), _labelStyle);
                float remaining = hero.SkillRunner.CooldownRemaining(asset), total = hero.SkillRunner.CooldownDuration(asset);
                if (remaining > 0 && total > 0)
                {
                    float fill = remaining / total;
                    Panel(new Rect(rect.x, rect.y + rect.height * (1 - fill), rect.width, rect.height * fill), new Color(0, 0, 0, .6f));
                    GUI.Label(new Rect(rect.x, rect.y + 20, rect.width, 26), remaining.ToString("0.0"), _countStyle);
                }
                if (hero.SkillRunner.IsActive(asset)) Panel(new Rect(rect.x, rect.yMax - 4, rect.width, 4), Color.white);
            }

            // 提示
            if (_toast != null && Time.time - _toastTime < 1.6f)
            {
                float a = Mathf.Clamp01((1.6f - (Time.time - _toastTime)) * 2);
                var old = GUI.color; GUI.color = new Color(1, 1, 1, a);
                Panel(new Rect(width / 2 - 150, 96, 300, 34), new Color(.06f, .08f, .12f, .85f));
                GUI.Label(new Rect(width / 2 - 150, 101, 300, 24), _toast, _labelStyle);
                GUI.color = old;
            }

            // 跳字
            if (camera != null)
                foreach (var p in _popups)
                {
                    Vector3 screen = camera.WorldToScreenPoint(p.Position);
                    if (screen.z < 0) continue;
                    float a = Mathf.Clamp01((1.1f - p.Age) * 3), pop = 1 + .4f * Mathf.Clamp01(1 - p.Age * 8);
                    _popupStyle.fontSize = Mathf.RoundToInt(p.Size * pop);
                    var r = new Rect(screen.x / scale - 80, (Screen.height - screen.y) / scale - 20, 160, 40);
                    _popupStyle.normal.textColor = new Color(0, 0, 0, a * .7f); GUI.Label(new Rect(r.x + 2, r.y + 2, r.width, r.height), p.Text, _popupStyle);
                    _popupStyle.normal.textColor = new Color(p.Color.r, p.Color.g, p.Color.b, a); GUI.Label(r, p.Text, _popupStyle);
                }
        }

        private void OnDestroy()
        {
            if(_equipped!=null)_equipped.PopOverride(axeOverride);
            if (hero != null) { hero.HealthChanged -= OnHealthChanged; hero.AbilitySystem?.Clear(); }
            if (dummy != null) dummy.HealthChanged -= OnHealthChanged;
            foreach(var u in secondary)if(u!=null)u.HealthChanged-=OnHealthChanged;
        }

        private void WorldBar(Camera camera, CombatUnit unit, float scale)
        {
            Vector3 screen = camera.WorldToScreenPoint(unit.HudPosition + Vector3.up * .2f);
            if (screen.z < 0) return;
            var rect = new Rect(screen.x / scale - 70, (Screen.height - screen.y) / scale - 10, 140, 12);
            GUI.Label(new Rect(rect.x, rect.y - 22, rect.width, 20), unit.DisplayName, _smallStyle);
            Bar(rect, unit.Get(CombatUnit.Health) / unit.Get(CombatUnit.MaxHealth), new Color(.9f, .3f, .3f), null);
        }

        private void Bar(Rect rect, float fill, Color color, string label)
        {
            Panel(new Rect(rect.x - 2, rect.y - 2, rect.width + 4, rect.height + 4), new Color(0, 0, 0, .65f));
            Panel(rect, new Color(color.r * .25f, color.g * .25f, color.b * .25f, 1));
            Panel(new Rect(rect.x, rect.y, rect.width * Mathf.Clamp01(fill), rect.height), color);
            Panel(new Rect(rect.x, rect.y, rect.width * Mathf.Clamp01(fill), rect.height * .4f), new Color(1, 1, 1, .18f));
            if (label != null) GUI.Label(new Rect(rect.x + 6, rect.y - 4, 200, 20), label, _smallStyle);
        }

        private void Panel(Rect rect, Color color)
        {
            var old = GUI.color; GUI.color = new Color(color.r, color.g, color.b, color.a * old.a);
            GUI.DrawTexture(rect, _white); GUI.color = old;
        }

        private void EnsureStyles()
        {
            if (_white != null) return;
            _white = Texture2D.whiteTexture;
            _titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
            _countStyle = new GUIStyle(_titleStyle) { alignment = TextAnchor.MiddleCenter };
            _labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };
            _smallStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, normal = { textColor = new Color(.78f, .82f, .9f) } };
            _keyStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold, normal = { textColor = new Color(.7f, .75f, .85f) } };
            _popupStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        }
    }
}
