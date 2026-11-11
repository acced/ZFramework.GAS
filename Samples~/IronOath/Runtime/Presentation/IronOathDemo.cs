using System;
using IronOath.Campaign;
using IronOath.Combat;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace IronOath.Presentation
{
    /// <summary>One optional sample entry point. It never starts in unrelated scenes.</summary>
    [DisallowMultipleComponent]
    public sealed partial class IronOathDemo : MonoBehaviour
    {
        [Header("Art and typography")]
        [Tooltip("Assign a font containing Chinese glyphs for consistent player builds.")]
        [SerializeField] Font chineseFont;
        [Header("Optional world-space Spine / actor prefabs")]
        [SerializeField] IronOathActorSkin[] actors = Array.Empty<IronOathActorSkin>();
        [SerializeField] int campaignSeed = 1701;
        [Tooltip("Rendering layer used only by this battle stage. Choose an unused project layer.")]
        [SerializeField, Range(0, 31)] int battleLayer = 30;
        internal string SaveDirectoryOverride;
        IronOathSaveStore _saveStore;
        IronOathSaveStore SaveStore => _saveStore ??= new IronOathSaveStore(
            string.IsNullOrEmpty(SaveDirectoryOverride) ? Application.persistentDataPath : SaveDirectoryOverride);
        enum Page { Shop, Formation, Dungeon, Chronicle, Battle }
        Canvas _canvas;
        RectTransform _root, _screen;
        CampaignSession _campaign;
        BattleSimulation _battle;
        BattleStage _stage;
        ExpeditionTicket _ticket;
        GameObject _ownedEventSystem;
        Page _page;
        Text _toast;
        float _toastUntil, _nextAction;
        bool _autoplay, _practice, _loadedFromBackup;
        int _speed = 1, _selectedUnit, _selectedItem;
        bool _rotateItem;
        int[] _formation;
        readonly TacticRule[][] _savedTactics = new TacticRule[6][];
        readonly string[] _log = new string[6];
        int _logCount;
        Text _battleLog, _battleCounter, _inspectText;
        Button _playButton;

        void Start()
        {
            var backgroundCamera = new GameObject("Iron Oath • background camera", typeof(Camera));
            backgroundCamera.transform.SetParent(transform, false);
            Camera camera = backgroundCamera.GetComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = IronOathUi.Ink;
            camera.cullingMask = 0;
            camera.depth = -100;
            IronOathUi.Font = chineseFont != null ? chineseFont : Resources.Load<Font>("IronOath/Fonts/IronOathSerif");
            if (IronOathUi.Font == null)
                IronOathUi.Font = Font.CreateDynamicFontFromOSFont(
                    new[] { "Noto Serif CJK SC", "Microsoft YaHei", "PingFang SC", "SimHei", "Arial" }, 24);
            if (IronOathUi.Font == null) IronOathUi.Font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var canvasObject = new GameObject("Iron Oath • interface", typeof(RectTransform), typeof(Canvas),
                typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasObject.transform.SetParent(transform, false);
            _canvas = canvasObject.GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 100;
            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
            _root = IronOathUi.Rect(canvasObject.transform, "1920 × 1080 safe frame", 0, 0, 1920, 1080);
            _root.anchorMin = _root.anchorMax = new Vector2(.5f, .5f);
            _root.pivot = new Vector2(.5f, .5f);
            _root.anchoredPosition = Vector2.zero;
            if (EventSystem.current == null)
            {
                _ownedEventSystem = new GameObject("Iron Oath • input", typeof(EventSystem));
                _ownedEventSystem.transform.SetParent(transform, false);
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
                // Resolve an optional package once, without making this sample require the Input System assembly.
                Type moduleType = Type.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
                if (moduleType != null)
                {
                    Component module = _ownedEventSystem.AddComponent(moduleType);
                    moduleType.GetMethod("AssignDefaultActions", Type.EmptyTypes)?.Invoke(module, null);
                }
                else Debug.LogError("Iron Oath requires an EventSystem UI input module.", this);
#else
                _ownedEventSystem.AddComponent<StandaloneInputModule>();
#endif
            }
            if (TryLoad())
            {
                ShowPage(Page.Shop);
                if (_loadedFromBackup) Toast("已从备份恢复进度。", true);
            }
            else ShowProtagonists();
        }

        void Update()
        {
            if (_toast != null && Time.unscaledTime >= _toastUntil) _toast.transform.parent.gameObject.SetActive(false);
            if (_page != Page.Battle) return;
            _stage?.Tick();
            if (_resultPending && Time.unscaledTime >= _resultAt) ShowBattleResult();
            if (_autoplay && _battle != null && _battle.State == BattleState.Fighting && Time.unscaledTime >= _nextAction)
            {
                AdvanceBattle();
                _nextAction = Time.unscaledTime + 1.25f / _speed;
            }
        }

        void OnApplicationPause(bool pause) { if (pause) Save(); }
        void OnApplicationQuit() => Save();
        void OnEnable() => _stage?.SetVisible(true);
        void OnDisable()
        {
            _autoplay = false;
            if (_battle != null && _battle.State == BattleState.Fighting) SetPlayLabel("继续");
            _stage?.SetVisible(false);
        }
        void OnDestroy()
        {
            _stage?.Dispose();
            if (_ownedEventSystem != null) Destroy(_ownedEventSystem);
        }

        void ClearScreen()
        {
            _stage?.Dispose();
            _stage = null;
            _rewardDialog = null;
            if (_screen != null)
            {
                // Destroy is deferred; retire its raycasters immediately when rebuilding a page.
                _screen.gameObject.SetActive(false);
                Destroy(_screen.gameObject);
            }
            _screen = IronOathUi.Rect(_root, "Screen", 0, 0, 1920, 1080);
            _toast = null;
            _battleLog = _battleCounter = _inspectText = null;
        }

        void ShowPage(Page page)
        {
            _page = page;
            _autoplay = false;
            ClearScreen();
            if (page != Page.Battle)
            {
                IronOathUi.Fill(_screen, "Backdrop", 0, 0, 1920, 1080, IronOathUi.Ink);
                IronOathUi.Art(_screen, page == Page.Shop ? "IronOath/Art/PawnshopInterior" : page == Page.Dungeon ? "IronOath/Art/BlackstoneMap" : "IronOath/Art/BlackstoneCourtyard",
                    0, 0, 1920, 1080, new Color(.72f, .72f, .72f, 1));
                IronOathUi.Fill(_screen, "Atmosphere", 0, 0, 1920, 1080, new Color(.025f, .05f, .07f, page == Page.Shop ? .22f : .62f));
            }
            BuildHeader();
            switch (page)
            {
                case Page.Shop: BuildShop(); break;
                case Page.Formation: BuildFormation(); break;
                case Page.Dungeon: BuildDungeon(); break;
                case Page.Chronicle: BuildChronicle(); break;
                case Page.Battle: BuildBattle(); break;
            }
            BuildFooter();
            if (_campaign != null && _campaign.State == CampaignState.Evicted) ShowCampaignEnd();
        }

        void BuildHeader()
        {
            IronOathUi.Fill(_screen, "Header", 0, 0, 1920, 104, new Color32(13, 21, 27, 249));
            IronOathUi.Rule(_screen, 50, 103, 1820);
            IronOathUi.Label(_screen, "铁 誓 当 铺", 64, 18, 360, 45, 32, IronOathUi.PaleGold);
            IronOathUi.Label(_screen, "IRON OATH  /  BLACKSTONE HARBOR", 66, 64, 410, 24, 12, IronOathUi.Muted);
            string[] labels = { "当铺", "战前编排", "深渊探索", "家族纪事" };
            for (int i = 0; i < labels.Length; i++)
            {
                Page target = (Page)i;
                bool active = _page == target;
                IronOathUi.Button(_screen, labels[i], 520 + i * 158, 25, 145, 52,
                    () => ShowPage(target), active, _page != Page.Battle);
            }
            if (_campaign == null) return;
            IronOathUi.Label(_screen, "第 " + _campaign.Week + " 周 · " + _campaign.DayName,
                1190, 20, 260, 28, 20, IronOathUi.Ivory, TextAnchor.MiddleRight);
            IronOathUi.Label(_screen, "周日租金  " + _campaign.RentAmount + " G" + (_campaign.RentDue ? "  · 待缴" : ""),
                1160, 52, 290, 27, 15, _campaign.RentDue ? IronOathUi.PaleGold : IronOathUi.Muted, TextAnchor.MiddleRight);
            IronOathUi.Label(_screen, _campaign.Coins.ToString("N0") + "  G", 1480, 23, 205, 47,
                30, IronOathUi.PaleGold, TextAnchor.MiddleRight);
            IronOathUi.Button(_screen, "保存", 1730, 28, 118, 45, () => { bool saved = Save(); Toast(saved ? "进度已保存。" : "保存失败，当前游戏仍可继续。", saved); }, false, _page != Page.Battle);
        }

        void BuildFooter()
        {
            IronOathUi.Fill(_screen, "Footer", 0, 1028, 1920, 52, new Color32(13, 21, 27, 245));
            IronOathUi.Rule(_screen, 50, 1028, 1820);
            string partner = _campaign == null ? "一间当铺，一纸铁誓。" :
                (_campaign.Protagonist == Protagonist.Rayn ? "雷恩 · 少东家" : "克萝伊 · 少当家") + "    /    合伙人 " +
                (_campaign.Partner == Protagonist.Rayn ? "雷恩" : "克萝伊");
            IronOathUi.Label(_screen, partner, 64, 1040, 900, 26, 16, IronOathUi.Muted);
            IronOathUi.Label(_screen, "第一章  ·  黑石港的继承人", 1350, 1039, 500, 27, 16,
                IronOathUi.Gold, TextAnchor.MiddleRight);
        }

        void ShowProtagonists()
        {
            _page = Page.Shop;
            ClearScreen();
            IronOathUi.Fill(_screen, "Dark", 0, 0, 1920, 1080, IronOathUi.Ink);
            IronOathUi.Art(_screen, "IronOath/Art/PawnshopInterior", 0, 0, 1920, 1080, new Color(.48f, .48f, .48f, 1));
            IronOathUi.Label(_screen, "铁 誓 当 铺", 460, 83, 1000, 100, 68, IronOathUi.PaleGold, TextAnchor.MiddleCenter);
            IronOathUi.Label(_screen, "父母留下了一间店，也留下了一条通向深渊的路。", 460, 195, 1000, 50,
                24, IronOathUi.Ivory, TextAnchor.MiddleCenter);
            BuildProtagonistCard(Protagonist.Rayn, 330, "雷 恩", "RAYN · 铁誓统领", "守住这间店。然后，把他们带回家。",
                "剑盾 / 前中排指挥\n十字护甲强化 · 协同追击\n巧手翻新：战后修补护甲，去赃工时减半", false);
            BuildProtagonistCard(Protagonist.Chloe, 1010, "克 萝 伊", "CHLOE · 秘宝猎手", "再狡猾的生意人，也藏不住真相。",
                "刺剑 / 手弩 / 炼金\n开局识破甲缝 · 高穿甲终结\n慧眼识珠：看穿底价，更擅长讨价还价", true);
            IronOathUi.Label(_screen, "选择主控角色。另一位兄妹会作为合伙人与战斗伙伴全程同行。", 400, 944, 1120, 43,
                20, IronOathUi.Muted, TextAnchor.MiddleCenter);
        }

        void BuildProtagonistCard(Protagonist who, float x, string name, string subtitle, string quote, string details, bool light)
        {
            var panel = IronOathUi.Panel(_screen, name, x, 300, 580, 606);
            var figure = IronOathUi.Rect(panel, "Family crest", 205, 25, 170, 235).gameObject.AddComponent<IronOathFigure>();
            figure.LightArmor = light;
            figure.Cloak = light ? new Color32(123, 67, 79, 255) : new Color32(48, 95, 114, 255);
            figure.raycastTarget = false;
            IronOathUi.Label(panel, name, 40, 275, 500, 47, 34, IronOathUi.PaleGold, TextAnchor.MiddleCenter);
            IronOathUi.Label(panel, subtitle, 40, 324, 500, 25, 15, IronOathUi.Muted, TextAnchor.MiddleCenter);
            IronOathUi.Rule(panel, 70, 365, 440);
            IronOathUi.Label(panel, quote, 45, 385, 490, 38, 20, IronOathUi.Ivory, TextAnchor.MiddleCenter);
            IronOathUi.Label(panel, details, 60, 433, 460, 93, 18, IronOathUi.Muted, TextAnchor.MiddleCenter);
            IronOathUi.Button(panel, "以" + (who == Protagonist.Rayn ? "雷恩" : "克萝伊") + "的视角开始", 95, 535, 390, 48, () =>
            {
                _campaign = new CampaignSession(who, campaignSeed);
                _formation = null;
                _battle = null;
                for (int i = 0; i < _savedTactics.Length; i++) _savedTactics[i] = null;
                _selectedItem = 0;
                Save();
                ShowPage(Page.Shop);
                Toast("黑石港的早晨。今天，铁誓当铺重新开门。", true);
            }, true);
        }

        void Apply(CampaignResult result, bool redraw = true)
        {
            bool saved = Save();
            if (redraw) ShowPage(_page);
            Toast(result.Message + (saved ? "" : "（尚未保存）"), result.Succeeded);
        }

        void Toast(string message, bool success)
        {
            if (_toast == null)
            {
                var panel = IronOathUi.Panel(_screen, "Notification", 440, 949, 1040, 59, IronOathUi.PanelInk, false);
                _toast = IronOathUi.Label(panel, "", 20, 8, 1000, 43, 20, IronOathUi.Ivory, TextAnchor.MiddleCenter);
            }
            _toast.transform.parent.gameObject.SetActive(true);
            _toast.text = message;
            _toast.color = success ? IronOathUi.PaleGold : new Color32(241, 160, 132, 255);
            _toastUntil = Time.unscaledTime + 5;
            _toast.transform.parent.SetAsLastSibling();
        }

        bool Save()
        {
            if (_campaign == null) return false;
            if (SaveStore.TrySave(_campaign, _formation, _savedTactics, out string error)) return true;
            Debug.LogWarning("Iron Oath save failed; the previous save was kept: " + error, this);
            return false;
        }

        bool TryLoad()
        {
            if (!SaveStore.TryLoad(out IronOathLoadedSave loaded, out string error))
            {
                if (!string.IsNullOrEmpty(error))
                    Debug.LogWarning("Iron Oath ignored an unreadable save: " + error, this);
                return false;
            }
            _campaign = loaded.Campaign;
            _formation = loaded.Formation;
            for (int unit = 0; unit < _savedTactics.Length; unit++) _savedTactics[unit] = loaded.Tactics[unit];
            _loadedFromBackup = loaded.RecoveredFromBackup;
            return true;
        }

        void ShowCampaignEnd()
        {
            var blocker = IronOathUi.Fill(_screen, "Closure input shield", 0, 0, 1920, 1080, new Color(0, 0, 0, .5f));
            blocker.raycastTarget = true;
            var panel = IronOathUi.Panel(_screen, "Lease ended", 510, 300, 900, 400);
            IronOathUi.Label(panel, "店铺暂时歇业", 50, 55, 800, 60, 40, IronOathUi.PaleGold, TextAnchor.MiddleCenter);
            IronOathUi.Label(panel, "这一次，租金先一步压垮了我们。\n带着对行情与战利品的新认识，再经营一次。",
                90, 145, 720, 100, 24, IronOathUi.Ivory, TextAnchor.MiddleCenter);
            IronOathUi.Button(panel, "重新启程", 250, 290, 400, 58, () => { _campaign = null; ShowProtagonists(); }, true);
        }
    }
}
