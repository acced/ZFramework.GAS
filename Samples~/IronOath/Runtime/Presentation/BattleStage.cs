using System;
using IronOath.Combat;
using UnityEngine;
using UnityEngine.UI;

namespace IronOath.Presentation
{
    /// <summary>Mirrored front columns and bottom-center feet, independent of animation asset dimensions.</summary>
    internal sealed class BattleStage : IDisposable
    {
        sealed class ActorView
        {
            internal BattleUnit Unit;
            internal RectTransform Root, Foot, Hud, Effects;
            internal Image Armor, Health;
            internal Text Name, ActivePoints, PassivePoints, Status, Floating;
            internal Image[] Shards;
            internal float FloatUntil, BreakUntil;
            internal CanvasGroup Group, HudGroup;
            internal GameObject Model;
            internal Renderer[] Renderers;
            internal int[] RendererOrders;
            internal IIronOathBattlePresentation[] Receivers;
            internal Vector3 Offset, BaseScale;
            internal Vector2 Rest;
            internal float HitUntil, AttackUntil;
            internal int SortingOffset;
        }

        readonly GameObject _world;
        readonly Camera _camera;
        readonly Canvas _canvas;
        SpriteRenderer _backgroundRenderer;
        readonly RectTransform _ui, _artLayer, _hudLayer, _effectsLayer;
        readonly ActorView[] _actors;
        readonly Sprite _backdrop;
        readonly IronOathActorSkin[] _skins;
        readonly Action<int> _selected;
        int _lastWidth, _lastHeight;

        internal BattleStage(Transform parent, BattleSimulation battle, IronOathActorSkin[] skins, Action<int> selected)
        {
            _skins = skins;
            _canvas = parent.GetComponentInParent<Canvas>();
            _selected = selected;
            _ui = IronOathUi.Rect(parent, "Battle ground and actors", 0, 0, 1920, 1080);
            _world = new GameObject("Iron Oath • world stage");
            var cameraObject = new GameObject("Battle Camera", typeof(Camera));
            cameraObject.transform.SetParent(_world.transform, false);
            _camera = cameraObject.GetComponent<Camera>();
            _camera.transform.position = new Vector3(0, 0, -10);
            _camera.orthographic = true;
            _camera.orthographicSize = 5.4f;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color32(24, 35, 43, 255);
            _camera.nearClipPlane = .1f;
            _camera.farClipPlane = 100;
            _camera.depth = 0;
            var texture = Resources.Load<Texture2D>("IronOath/Art/BlackstoneCourtyard");
            if (texture != null)
            {
                _backdrop = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(.5f, .5f), 100);
                var backdrop = new GameObject("Painted courtyard", typeof(SpriteRenderer));
                backdrop.transform.SetParent(_world.transform, false);
                backdrop.transform.position = new Vector3(0, 0, 12);
                var renderer = backdrop.GetComponent<SpriteRenderer>();
                renderer.sprite = _backdrop;
                _backgroundRenderer = renderer;
                renderer.sortingOrder = -1000;
                float scale = Mathf.Max(19.2f / _backdrop.bounds.size.x, 10.8f / _backdrop.bounds.size.y);
                backdrop.transform.localScale = Vector3.one * scale;
            }
            // Ground markers are spatial guides, so the three empty spaces remain readable.
            for (int side = 0; side < 2; side++)
                for (int slot = 0; slot < 9; slot++)
                {
                    Vector2 point = SlotPoint((BattleSide)side, slot);
                    var marker = IronOathUi.Fill(_ui, "Ground slot", point.x - 67, point.y - 9, 134, 18,
                        side == 0 ? new Color(.35f, .64f, .67f, .20f) : new Color(.75f, .39f, .30f, .20f));
                    marker.transform.localEulerAngles = new Vector3(0, 0, side == 0 ? 6 : -6);
                    IronOathUi.Label(_ui, (slot + 1).ToString(), point.x - 12, point.y + 10, 24, 18,
                        12, new Color(1, 1, 1, .4f), TextAnchor.MiddleCenter);
                }
            _artLayer = IronOathUi.Rect(_ui, "Actor art • depth sorted", 0, 0, 1920, 1080);
            _hudLayer = IronOathUi.Rect(_ui, "Vitals • always above actors", 0, 0, 1920, 1080);
            _effectsLayer = IronOathUi.Rect(_ui, "Damage and status effects", 0, 0, 1920, 1080);
            _actors = new ActorView[battle.UnitCount];
            for (int i = 0; i < battle.UnitCount; i++) _actors[i] = CreateActor(battle.GetUnit(i));
            Refresh();
        }

        internal static Vector2 SlotPoint(BattleSide side, int slot)
        {
            int row = slot / 3, depth = slot % 3;
            float x = side == BattleSide.Allies ? 760 - depth * 200 : 1160 + depth * 200;
            return new Vector2(x + (row - 1) * (side == BattleSide.Allies ? -24 : 24), 390 + row * 130);
        }

        ActorView CreateActor(BattleUnit unit)
        {
            var view = new ActorView { Unit = unit };
            view.Root = IronOathUi.Rect(_artLayer, unit.Name, 0, 0, 170, 170);
            view.Hud = IronOathUi.Rect(_hudLayer, unit.Name + " vitals", 0, 0, 170, 71);
            view.HudGroup = view.Hud.gameObject.AddComponent<CanvasGroup>();
            view.Effects = IronOathUi.Rect(_effectsLayer, unit.Name + " feedback", 0, 0, 170, 250);
            view.Group = view.Root.gameObject.AddComponent<CanvasGroup>();
            view.Foot = IronOathUi.Rect(view.Root, "Foot pivot • attach model here", 85, 170, 0, 0);
            IronOathActorSkin skin = null;
            if (_skins != null)
                for (int i = 0; i < _skins.Length; i++)
                    if (_skins[i] != null && _skins[i].prefab != null &&
                        (_skins[i].unitDefinitionId == unit.Definition.Id ||
                         skin == null && string.IsNullOrEmpty(_skins[i].unitDefinitionId))) skin = _skins[i];
            if (skin == null)
            {
                var figureRect = IronOathUi.Rect(view.Root, "Heraldic stand-in", 26, 0, 118, 170);
                var figure = figureRect.gameObject.AddComponent<IronOathFigure>();
                figure.Cloak = unit.Side == BattleSide.Allies ? new Color32(51, 101, 116, 255) : new Color32(127, 53, 48, 255);
                figure.LightArmor = unit.Definition.MaxArmor < 65;
                figure.raycastTarget = false;
                if (unit.Side == BattleSide.Enemies)
                {
                    figureRect.pivot = new Vector2(.5f, 1);
                    figureRect.anchoredPosition = new Vector2(85, 0);
                    figureRect.localScale = new Vector3(-1, 1, 1);
                }
            }
            else
            {
                view.Model = UnityEngine.Object.Instantiate(skin.prefab, _world.transform);
                view.Model.name = unit.Name + " • Spine foot anchor";
                bool facesRight = unit.Side == BattleSide.Allies;
                view.Model.transform.localScale = new Vector3(skin.worldScale * (facesRight == skin.sourceFacesRight ? 1 : -1),
                    skin.worldScale, skin.worldScale);
                view.BaseScale = view.Model.transform.localScale;
                view.Offset = skin.footOffset;
                view.SortingOffset = skin.sortingOffset;
                view.Renderers = view.Model.GetComponentsInChildren<Renderer>(true);
                view.RendererOrders = new int[view.Renderers.Length];
                for (int i = 0; i < view.Renderers.Length; i++) view.RendererOrders[i] = view.Renderers[i].sortingOrder;
                var behaviours = view.Model.GetComponentsInChildren<MonoBehaviour>(true);
                int count = 0;
                for (int i = 0; i < behaviours.Length; i++) if (behaviours[i] is IIronOathBattlePresentation) count++;
                view.Receivers = new IIronOathBattlePresentation[count];
                count = 0;
                for (int i = 0; i < behaviours.Length; i++)
                    if (behaviours[i] is IIronOathBattlePresentation receiver)
                    {
                        view.Receivers[count++] = receiver;
                        receiver.Bind(unit);
                    }
            }
            IronOathUi.Fill(view.Hud, "Unit plate", 3, 0, 164, 71, new Color32(16, 24, 28, 232));
            view.Name = IronOathUi.Label(view.Hud, unit.Name, 8, 2, 154, 23, 17, IronOathUi.Ivory, TextAnchor.MiddleCenter);
            view.Armor = IronOathUi.Bar(view.Hud, "Armor", 13, 28, 144, 6, new Color32(224, 228, 221, 255));
            view.Health = IronOathUi.Bar(view.Hud, "Health", 13, 37, 144, 7, IronOathUi.Red);
            view.ActivePoints = IronOathUi.Label(view.Hud, "", 12, 48, 73, 20, 15, new Color32(236, 113, 94, 255), TextAnchor.MiddleRight);
            view.PassivePoints = IronOathUi.Label(view.Hud, "", 91, 48, 66, 20, 15, new Color32(119, 195, 231, 255), TextAnchor.MiddleLeft);
            view.Floating = IronOathUi.Label(view.Effects, "", -45, -70, 260, 56, 27, IronOathUi.PaleGold, TextAnchor.MiddleCenter, true);
            view.Floating.gameObject.SetActive(false);
            view.Shards = new Image[6];
            for (int i = 0; i < view.Shards.Length; i++)
            {
                view.Shards[i] = IronOathUi.Fill(view.Effects, "Armor shard", 85, 95, 7 + i % 3 * 3, 18,
                    new Color32(224, 228, 222, 255));
                view.Shards[i].gameObject.SetActive(false);
            }
            view.Status = IronOathUi.Label(view.Effects, "", -15, -26, 200, 24, 16, IronOathUi.PaleGold, TextAnchor.MiddleCenter);
            var hitTarget = IronOathUi.Rect(view.Root, "Inspect actor", 0, 0, 170, 170);
            var image = hitTarget.gameObject.AddComponent<Image>();
            image.color = Color.clear;
            var button = hitTarget.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            int unitId = unit.Id;
            button.onClick.AddListener(() => _selected(unitId));
            var hudHit = IronOathUi.Rect(view.Hud, "Inspect vitals", 0, 0, 170, 71);
            var hudImage = hudHit.gameObject.AddComponent<Image>();
            hudImage.color = Color.clear;
            var hudButton = hudHit.gameObject.AddComponent<Button>();
            hudButton.targetGraphic = hudImage;
            hudButton.onClick.AddListener(() => _selected(unitId));
            return view;
        }

        internal void Refresh()
        {
            if (_backgroundRenderer != null)
            {
                float worldHeight = _camera.orthographicSize * 2;
                float scale = Mathf.Max(worldHeight * _camera.aspect / _backdrop.bounds.size.x,
                    worldHeight / _backdrop.bounds.size.y);
                _backgroundRenderer.transform.localScale = Vector3.one * scale;
            }
            for (int i = 0; i < _actors.Length; i++)
            {
                ActorView actor = _actors[i];
                BattleUnit unit = actor.Unit;
                Vector2 point = SlotPoint(unit.Side, unit.Slot);
                actor.Rest = new Vector2(point.x - 85, -point.y + 170);
                actor.Root.anchoredPosition = actor.Rest;
                actor.Hud.anchoredPosition = new Vector2(point.x - 85, -point.y - 6);
                actor.Effects.anchoredPosition = actor.Rest;
                actor.HudGroup.alpha = unit.IsAlive ? 1 : .7f;
                actor.Group.alpha = unit.IsAlive ? 1 : .32f;
                IronOathUi.SetBar(actor.Armor, unit.Armor, unit.MaxArmor, 142);
                IronOathUi.SetBar(actor.Health, unit.Health, unit.MaxHealth, 142);
                actor.ActivePoints.text = new string('●', Math.Min(unit.AP, 8));
                actor.PassivePoints.text = new string('●', Math.Min(unit.PP, 8));
                actor.Status.text = unit.IsAlive ? StatusName(unit.Status.ToString()) : "已倒下";
                PositionModel(actor);
            }
            // Back-row art is always drawn before front-row art, independent of unit creation order.
            for (int row = 0; row < 3; row++)
                for (int i = 0; i < _actors.Length; i++)
                    if (_actors[i].Unit.Slot / 3 == row) _actors[i].Root.SetAsLastSibling();
            _lastWidth = Screen.width;
            _lastHeight = Screen.height;
        }

        void PositionModel(ActorView actor)
        {
            if (actor.Model == null) return;
            Vector2 screenPoint = RectTransformUtility.WorldToScreenPoint(null, actor.Foot.position);
            float pixelsPerWorldUnit = Screen.height / (_camera.orthographicSize * 2);
            float fit = _canvas != null ? _canvas.scaleFactor * 100 / pixelsPerWorldUnit : 1;
            actor.Model.transform.position = _camera.ScreenToWorldPoint(new Vector3(screenPoint.x, screenPoint.y, 10)) + actor.Offset * fit;
            actor.Model.transform.localScale = actor.BaseScale * fit;
            int order = actor.Unit.Slot / 3 * 100 + actor.SortingOffset;
            for (int i = 0; i < actor.Renderers.Length; i++) actor.Renderers[i].sortingOrder = order + actor.RendererOrders[i];
        }

        internal void Present(in BattleEvent evt)
        {
            for (int i = 0; i < _actors.Length; i++)
            {
                ActorView actor = _actors[i];
                if (actor.Unit.Id == evt.SourceId && evt.Kind == BattleEventKind.Action) actor.AttackUntil = Time.unscaledTime + .36f;
                if (actor.Unit.Id == evt.TargetId && evt.Kind == BattleEventKind.Hit)
                {
                    actor.HitUntil = Time.unscaledTime + .32f;
                    actor.FloatUntil = Time.unscaledTime + 1.1f;
                    actor.Floating.text = "甲 −" + evt.ArmorDamage.ToString("0") + "  血 −" + evt.HealthDamage.ToString("0");
                    actor.Floating.color = IronOathUi.Ivory;
                }
                if (actor.Unit.Id == evt.TargetId && evt.Kind == BattleEventKind.ArmorBroken)
                {
                    actor.BreakUntil = Time.unscaledTime + .65f;
                    actor.FloatUntil = Time.unscaledTime + 1.25f;
                    actor.Floating.text = "破甲崩溃 · PP −1";
                    actor.Floating.color = IronOathUi.PaleGold;
                }
                if (actor.Unit.Id == evt.TargetId && evt.Kind == BattleEventKind.StatusApplied)
                {
                    actor.FloatUntil = Time.unscaledTime + 1.2f;
                    actor.Floating.text = StatusName(evt.Status.ToString());
                    actor.Floating.color = new Color32(140, 218, 233, 255);
                }
                if (actor.Receivers != null && (actor.Unit.Id == evt.SourceId || actor.Unit.Id == evt.TargetId))
                    for (int j = 0; j < actor.Receivers.Length; j++) actor.Receivers[j].Present(evt);
            }
        }

        internal void Tick()
        {
            if (Screen.width != _lastWidth || Screen.height != _lastHeight) Refresh();
            for (int i = 0; i < _actors.Length; i++)
            {
                ActorView actor = _actors[i];
                float lunge = Mathf.Clamp01((actor.AttackUntil - Time.unscaledTime) / .36f);
                float shake = actor.HitUntil > Time.unscaledTime ? Mathf.Sin(Time.unscaledTime * 65) * 4 : 0;
                float direction = actor.Unit.Side == BattleSide.Allies ? 1 : -1;
                actor.Root.anchoredPosition = actor.Rest + new Vector2(Mathf.Sin(lunge * Mathf.PI) * direction * 24 + shake, 0);
                actor.Effects.anchoredPosition = actor.Root.anchoredPosition;
                if (actor.Model != null) PositionModel(actor);
                float floating = Mathf.Clamp01((actor.FloatUntil - Time.unscaledTime) / 1.2f);
                actor.Floating.gameObject.SetActive(floating > 0);
                if (floating > 0)
                {
                    actor.Floating.rectTransform.anchoredPosition = new Vector2(-45, 55 + (1 - floating) * 40);
                    Color color = actor.Floating.color; color.a = Mathf.Min(1, floating * 4); actor.Floating.color = color;
                }
                float broken = Mathf.Clamp01((actor.BreakUntil - Time.unscaledTime) / .65f);
                for (int shard = 0; shard < actor.Shards.Length; shard++)
                {
                    Image image = actor.Shards[shard];
                    image.gameObject.SetActive(broken > 0);
                    if (broken <= 0) continue;
                    float t = 1 - broken, directionShard = shard % 2 == 0 ? -1 : 1;
                    image.rectTransform.anchoredPosition = new Vector2(85 + directionShard * t * (45 + shard * 12), -75 + t * 90 - t * t * 150);
                    image.rectTransform.localEulerAngles = new Vector3(0, 0, t * (160 + shard * 65));
                    image.color = new Color(.88f, .91f, .9f, broken);
                }
            }
        }

        static string StatusName(string status)
        {
            if (status == "None") return "";
            return status.Replace("Unbalanced", "失衡").Replace("KnockedDown", "击倒").Replace("Knockdown", "击倒")
                .Replace("Wet", "潮湿").Replace("Frozen", "冰封").Replace("Paralyzed", "麻痹")
                .Replace("Rooted", "定身").Replace("Slipped", "冰面滑倒").Replace("Oiled", "油污").Replace("Burning", "燃烧").Replace("Chilled", "寒冷")
                .Replace("Exposed", "甲缝破绽").Replace(", ", " · ");
        }

        public void Dispose()
        {
            if (_ui != null) UnityEngine.Object.Destroy(_ui.gameObject);
            if (_world != null) UnityEngine.Object.Destroy(_world);
            if (_backdrop != null) UnityEngine.Object.Destroy(_backdrop);
        }
    }
}
