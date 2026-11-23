using System;
using System.Collections.Generic;
using IronOath.Combat;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace IronOath.Presentation
{
    /// <summary>World-space feet own placement. The overlay only presents vitals, selection and feedback.</summary>
    internal sealed class BattleStage : IDisposable
    {
        sealed class ActorView
        {
            internal BattleUnit Unit;
            internal Transform Anchor, Visual;
            internal SortingGroup Sorting;
            internal RectTransform HitArea, Hud, Effects;
            internal Image Armor, Health;
            internal Text ActivePoints, PassivePoints, Status, Floating;
            internal Image[] Shards;
            internal float FloatUntil, BreakUntil, HitUntil, AttackUntil;
            internal CanvasGroup HudGroup;
            internal MeshRenderer StandIn;
            internal MaterialPropertyBlock Tint;
            internal IIronOathBattlePresentation[] Receivers;
            internal Vector3 Offset;
            internal int SortingOffset;
        }

        static readonly int TintId = Shader.PropertyToID("_Color");
        readonly GameObject _world;
        readonly Camera _camera;
        readonly Canvas _canvas;
        readonly RectTransform _ui, _hitLayer, _hudLayer, _effectsLayer;
        readonly ActorView[] _actors;
        readonly Transform[] _markers = new Transform[18];
        readonly Text[] _slotLabels = new Text[18];
        readonly List<Mesh> _meshes = new List<Mesh>(14);
        Mesh _backdrop;
        readonly Material _material;
        readonly Mesh _groundMesh;
        readonly IronOathActorSkin[] _skins;
        readonly Action<int> _selected;
        readonly int _layer;
        MeshRenderer _backgroundRenderer;
        int _lastWidth, _lastHeight;
        bool _disposed;

        internal Transform WorldRoot => _world != null ? _world.transform : null;
        internal Camera WorldCamera => _camera;

        internal Transform GetActorAnchor(int unitId)
        {
            for (int i = 0; i < _actors.Length; i++)
                if (_actors[i].Unit.Id == unitId) return _actors[i].Anchor;
            return null;
        }

        internal BattleStage(Transform owner, Transform uiParent, BattleSimulation battle,
            IronOathActorSkin[] skins, Action<int> selected, int layer)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            if (uiParent == null) throw new ArgumentNullException(nameof(uiParent));
            if (battle == null) throw new ArgumentNullException(nameof(battle));
            if (owner.GetComponentInParent<Canvas>() != null)
                throw new ArgumentException("The battle world owner must be outside a Canvas.", nameof(owner));
            if ((uint)layer > 31) throw new ArgumentOutOfRangeException(nameof(layer));
            Shader shader = Resources.Load<Shader>("IronOath/WorldUnlit");
            if (shader == null) throw new InvalidOperationException("Iron Oath world shader is missing. Import the complete sample.");
            _skins = skins;
            _selected = selected;
            _layer = layer;
            _canvas = uiParent.GetComponentInParent<Canvas>();
            _material = new Material(shader) { name = "Iron Oath world material" };
            _groundMesh = IronOathWorldGeometry.Ground();
            _meshes.Add(_groundMesh);
            _ui = IronOathUi.Rect(uiParent, "Battle HUD", 0, 0, 1920, 1080);
            _world = new GameObject("Iron Oath • world stage") { layer = layer };
            _world.transform.SetParent(owner, false);
            WarnLayerInUse();
            var cameraObject = new GameObject("Battle Camera", typeof(Camera)) { layer = layer };
            cameraObject.transform.SetParent(_world.transform, false);
            _camera = cameraObject.GetComponent<Camera>();
            _camera.transform.localPosition = new Vector3(0, 0, -10);
            _camera.orthographic = true;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color32(24, 35, 43, 255);
            _camera.nearClipPlane = .1f;
            _camera.farClipPlane = 100;
            _camera.depth = 10;
            _camera.cullingMask = 1 << layer;
            var texture = Resources.Load<Texture2D>("IronOath/Art/BlackstoneCourtyard");
            if (texture != null)
            {
                _backdrop = IronOathWorldGeometry.Quad(texture.width / 100f, texture.height / 100f);
                _meshes.Add(_backdrop);
                _backgroundRenderer = CreateMesh("Painted courtyard", _world.transform, _backdrop, Color.white, -1000);
                _backgroundRenderer.transform.localPosition = new Vector3(0, 0, 12);
                var block = new MaterialPropertyBlock();
                block.SetTexture("_MainTex", texture);
                block.SetColor(TintId, Color.white);
                _backgroundRenderer.SetPropertyBlock(block);
            }
            for (int side = 0; side < 2; side++)
                for (int slot = 0; slot < Formation.SlotCount; slot++)
                {
                    int index = side * Formation.SlotCount + slot;
                    Color color = side == 0 ? new Color(.35f, .64f, .67f, .28f) : new Color(.75f, .39f, .30f, .28f);
                    var marker = CreateMesh("Ground slot " + side + ":" + slot, _world.transform, _groundMesh, color, -500);
                    marker.transform.localPosition = BattleStageLayout.GetFootPoint((BattleSide)side, slot);
                    marker.transform.localRotation = Quaternion.Euler(0, 0, side == 0 ? 6 : -6);
                    _markers[index] = marker.transform;
                    _slotLabels[index] = IronOathUi.Label(_ui, (slot + 1).ToString(), 0, 0, 24, 18,
                        12, new Color(1, 1, 1, .4f), TextAnchor.MiddleCenter);
                }
            _hitLayer = IronOathUi.Rect(_ui, "Actor inspection targets", 0, 0, 1920, 1080);
            _hudLayer = IronOathUi.Rect(_ui, "Vitals", 0, 0, 1920, 1080);
            _effectsLayer = IronOathUi.Rect(_ui, "Damage and status effects", 0, 0, 1920, 1080);
            _actors = new ActorView[battle.UnitCount];
            for (int i = 0; i < battle.UnitCount; i++) _actors[i] = CreateActor(battle.GetUnit(i));
            // Projection requires the actual Canvas scaler and safe-frame transform on the first frame.
            Canvas.ForceUpdateCanvases();
            Refresh();
        }

        void WarnLayerInUse()
        {
            Renderer[] renderers = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None);
            for (int i = 0; i < renderers.Length; i++)
                if (renderers[i].gameObject.layer == _layer && !renderers[i].transform.IsChildOf(_world.transform))
                {
                    Debug.LogWarning("Iron Oath battle layer " + _layer + " is also used by '" + renderers[i].name +
                        "'. Choose an unused Battle Layer on IronOathDemo for an isolated stage.", _world);
                    return;
                }
        }

        MeshRenderer CreateMesh(string name, Transform parent, Mesh mesh, Color tint, int order)
        {
            var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer)) { layer = _layer };
            go.transform.SetParent(parent, false);
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = _material;
            renderer.sortingOrder = order;
            var block = new MaterialPropertyBlock();
            block.SetColor(TintId, tint);
            renderer.SetPropertyBlock(block);
            return renderer;
        }

        IronOathActorSkin FindSkin(string unitId)
        {
            IronOathActorSkin fallback = null;
            if (_skins == null) return null;
            for (int i = 0; i < _skins.Length; i++)
            {
                IronOathActorSkin skin = _skins[i];
                if (skin == null || skin.prefab == null) continue;
                if (skin.unitDefinitionId == unitId) return skin;
                if (fallback == null && string.IsNullOrEmpty(skin.unitDefinitionId)) fallback = skin;
            }
            return fallback;
        }

        static void AssignLayer(Transform root, int layer)
        {
            root.gameObject.layer = layer;
            for (int i = 0; i < root.childCount; i++) AssignLayer(root.GetChild(i), layer);
        }

        ActorView CreateActor(BattleUnit unit)
        {
            var view = new ActorView { Unit = unit };
            var anchor = new GameObject("Actor " + unit.Id + " • " + unit.Name, typeof(SortingGroup)) { layer = _layer };
            anchor.transform.SetParent(_world.transform, false);
            view.Anchor = anchor.transform;
            view.Sorting = anchor.GetComponent<SortingGroup>();
            var visual = new GameObject("Facing and foot offset") { layer = _layer };
            visual.transform.SetParent(view.Anchor, false);
            view.Visual = visual.transform;
            var shadow = CreateMesh("Foot shadow", view.Anchor, _groundMesh, new Color(0, 0, 0, .34f), -10);
            shadow.transform.localScale = new Vector3(.8f, .65f, 1);
            IronOathActorSkin skin = FindSkin(unit.Definition.Id);
            bool facesRight = unit.Side == BattleSide.Allies;
            if (skin == null)
            {
                Color cloak = facesRight ? new Color32(51, 101, 116, 255) : new Color32(127, 53, 48, 255);
                Mesh mesh = IronOathWorldGeometry.Figure(unit.Definition.MaxArmor < 65, cloak);
                _meshes.Add(mesh);
                view.StandIn = CreateMesh("Heraldic stand-in", view.Visual, mesh, Color.white, 0);
                view.Tint = new MaterialPropertyBlock();
                view.Visual.localScale = new Vector3(facesRight ? 1 : -1, 1, 1);
            }
            else
            {
                float scale = float.IsFinite(skin.worldScale) && skin.worldScale > 0 ? skin.worldScale : 1;
                view.Visual.localScale = new Vector3(scale * (facesRight == skin.sourceFacesRight ? 1 : -1), scale, scale);
                view.Offset = skin.footOffset;
                view.SortingOffset = skin.sortingOffset;
                // A wrapper applies facing and calibration; the prefab's authored localScale is preserved.
                GameObject model = UnityEngine.Object.Instantiate(skin.prefab, view.Visual, false);
                model.name = unit.Name + " • actor model";
                model.transform.localPosition = Vector3.zero;
                AssignLayer(model.transform, _layer);
                // Nested groups keep their authored order, but cannot escape their actor's row group.
                SortingGroup[] groups = model.GetComponentsInChildren<SortingGroup>(true);
                for (int i = 0; i < groups.Length; i++) groups[i].sortAtRoot = false;
                model.SetActive(true);
                var behaviours = model.GetComponentsInChildren<MonoBehaviour>(true);
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
            view.Visual.localPosition = view.Offset;
            view.HitArea = IronOathUi.Rect(_hitLayer, unit.Name + " inspection", 0, 0, 170, 170);
            AddInspectButton(view.HitArea, unit.Id);
            view.Hud = IronOathUi.Rect(_hudLayer, unit.Name + " vitals", 0, 0, 170, 71);
            view.HudGroup = view.Hud.gameObject.AddComponent<CanvasGroup>();
            view.Effects = IronOathUi.Rect(_effectsLayer, unit.Name + " feedback", 0, 0, 170, 250);
            IronOathUi.Fill(view.Hud, "Unit plate", 3, 0, 164, 71, new Color32(16, 24, 28, 232));
            IronOathUi.Label(view.Hud, unit.Name, 8, 2, 154, 23, 17, IronOathUi.Ivory, TextAnchor.MiddleCenter);
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
            AddInspectButton(IronOathUi.Rect(view.Hud, "Inspect vitals", 0, 0, 170, 71), unit.Id);
            return view;
        }

        void AddInspectButton(RectTransform parent, int unitId)
        {
            var image = parent.gameObject.AddComponent<Image>();
            image.color = Color.clear;
            var button = parent.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(() => _selected?.Invoke(unitId));
        }

        internal void Refresh()
        {
            if (_disposed) return;
            UpdateCamera();
            for (int i = 0; i < _actors.Length; i++)
            {
                ActorView actor = _actors[i];
                BattleUnit unit = actor.Unit;
                actor.Anchor.localPosition = BattleStageLayout.GetFootPoint(unit.Side, unit.Slot);
                actor.Sorting.sortingOrder = BattleStageLayout.GetRowOrder(unit.Slot, actor.SortingOffset);
                actor.HudGroup.alpha = unit.IsAlive ? 1 : .7f;
                IronOathUi.SetBar(actor.Armor, unit.Armor, unit.MaxArmor, 142);
                IronOathUi.SetBar(actor.Health, unit.Health, unit.MaxHealth, 142);
                actor.ActivePoints.text = new string('●', Math.Min(unit.AP, 8));
                actor.PassivePoints.text = new string('●', Math.Min(unit.PP, 8));
                actor.Status.text = unit.IsAlive ? StatusName(unit.Status.ToString()) : "已倒下";
                if (actor.StandIn != null)
                {
                    actor.Tint.SetColor(TintId, unit.IsAlive ? Color.white : new Color(.42f, .42f, .42f, .48f));
                    actor.StandIn.SetPropertyBlock(actor.Tint);
                    actor.Visual.localRotation = unit.IsAlive ? Quaternion.identity : Quaternion.Euler(0, 0,
                        unit.Side == BattleSide.Allies ? -75 : 75);
                }
                PositionHud(actor);
            }
            // Selection hit targets use the same row order as world actors.
            for (int row = 0; row < 3; row++)
                for (int i = 0; i < _actors.Length; i++)
                    if (Formation.Row(_actors[i].Unit.Slot) == row) _actors[i].HitArea.SetAsLastSibling();
        }

        void UpdateCamera()
        {
            if (Screen.width != _lastWidth || Screen.height != _lastHeight) Canvas.ForceUpdateCanvases();
            _camera.orthographicSize = BattleStageLayout.GetOrthographicSize(_camera.aspect);
            if (_backgroundRenderer != null)
            {
                float height = _camera.orthographicSize * 2;
                float scale = Mathf.Max(height * _camera.aspect / _backdrop.bounds.size.x, height / _backdrop.bounds.size.y);
                _backgroundRenderer.transform.localScale = Vector3.one * scale;
            }
            for (int i = 0; i < _markers.Length; i++)
                _slotLabels[i].rectTransform.anchoredPosition = Project(_markers[i].position) + new Vector2(-12, -10);
            _lastWidth = Screen.width;
            _lastHeight = Screen.height;
        }

        Vector2 Project(Vector3 worldPoint)
        {
            Vector3 screen = _camera.WorldToScreenPoint(worldPoint);
            Camera uiCamera = _canvas != null && _canvas.renderMode != RenderMode.ScreenSpaceOverlay ? _canvas.worldCamera : null;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(_ui, screen, uiCamera, out Vector2 point);
            return point;
        }

        void PositionHud(ActorView actor)
        {
            Vector2 foot = Project(actor.Anchor.position);
            actor.Hud.anchoredPosition = foot + new Vector2(-85, -6);
            actor.HitArea.anchoredPosition = foot + new Vector2(-85, 170);
            actor.Effects.anchoredPosition = Project(actor.Visual.position) + new Vector2(-85, 170);
        }

        internal void Present(in BattleEvent evt)
        {
            if (_disposed) return;
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
            if (_disposed) return;
            if (Screen.width != _lastWidth || Screen.height != _lastHeight) UpdateCamera();
            for (int i = 0; i < _actors.Length; i++)
            {
                ActorView actor = _actors[i];
                float lunge = Mathf.Clamp01((actor.AttackUntil - Time.unscaledTime) / .36f);
                float shake = actor.HitUntil > Time.unscaledTime ? Mathf.Sin(Time.unscaledTime * 65) * .04f : 0;
                float direction = actor.Unit.Side == BattleSide.Allies ? 1 : -1;
                actor.Visual.localPosition = actor.Offset + new Vector3(Mathf.Sin(lunge * Mathf.PI) * direction * .24f + shake, 0, 0);
                PositionHud(actor);
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

        internal void SetVisible(bool visible)
        {
            if (_disposed) return;
            if (_world != null) _world.SetActive(visible);
            if (_ui != null) _ui.gameObject.SetActive(visible);
            if (visible) { Canvas.ForceUpdateCanvases(); Refresh(); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // Destroy is deferred by Unity. Deactivate now so old cameras, input and actors cannot survive a page switch.
            if (_ui != null) { _ui.gameObject.SetActive(false); UnityEngine.Object.Destroy(_ui.gameObject); }
            if (_world != null) { _world.SetActive(false); UnityEngine.Object.Destroy(_world); }
            for (int i = 0; i < _meshes.Count; i++) UnityEngine.Object.Destroy(_meshes[i]);
            if (_material != null) UnityEngine.Object.Destroy(_material);
        }
    }
}
