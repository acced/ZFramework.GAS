using System;
using System.Collections;
using IronOath.Combat;
using IronOath.Presentation;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace IronOath.UnityTests
{
    public sealed class IronOathWorldStageTests
    {
        GameObject _owner, _prefab;
        BattleStage _stage;
        RectTransform _ui;

        [SetUp]
        public void SetUp()
        {
            _owner = new GameObject("Iron Oath acceptance owner");
            var canvasObject = new GameObject("Acceptance UI", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            canvasObject.transform.SetParent(_owner.transform, false);
            Canvas canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
            _ui = IronOathUi.Rect(canvasObject.transform, "Acceptance safe frame", 0, 0, 1920, 1080);
            _ui.anchorMin = _ui.anchorMax = _ui.pivot = new Vector2(.5f, .5f);
            _ui.anchoredPosition = Vector2.zero;
            IronOathUi.Font = Resources.Load<Font>("IronOath/Fonts/IronOathSerif");
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            _stage?.Dispose();
            _stage = null;
            if (_owner != null) Object.Destroy(_owner);
            if (_prefab != null) Object.Destroy(_prefab);
            yield return null;
        }

        [UnityTest]
        public IEnumerator DefaultActorsUseWorldFeetAndDedicatedCameraAndFollowFormationChanges()
        {
            BattleSimulation battle = BattleCatalog.CreateDemo();
            _stage = new BattleStage(_owner.transform, _ui, battle, Array.Empty<IronOathActorSkin>(), _ => { }, 30);
            AssertHudTracksFoot(battle.GetUnit(0)); // Before the first Tick: no stale Canvas placement.
            yield return null;
            Assert.IsNull(_stage.WorldRoot.GetComponentInParent<Canvas>());
            Assert.AreEqual(1 << 30, _stage.WorldCamera.cullingMask);
            Assert.AreEqual(_stage.WorldRoot, _stage.WorldCamera.transform.parent);
            Assert.IsTrue(_stage.WorldRoot.IsChildOf(_owner.transform));
            for (int i = 0; i < battle.UnitCount; i++)
            {
                BattleUnit unit = battle.GetUnit(i);
                Transform anchor = _stage.GetActorAnchor(unit.Id);
                Assert.IsNotNull(anchor);
                Assert.AreEqual(BattleStageLayout.GetFootPoint(unit.Side, unit.Slot), anchor.localPosition);
                Assert.IsNull(anchor.GetComponentInParent<Canvas>());
                Assert.IsEmpty(anchor.GetComponentsInChildren<Graphic>(true));
                Renderer[] renderers = anchor.GetComponentsInChildren<Renderer>();
                Assert.IsNotEmpty(renderers, "An unassigned actor must still be visible in world space.");
                foreach (Renderer renderer in renderers) Assert.AreEqual(30, renderer.gameObject.layer);
                Assert.AreEqual(BattleStageLayout.GetRowOrder(unit.Slot), anchor.GetComponent<SortingGroup>().sortingOrder);
                Vector3 viewport = _stage.WorldCamera.WorldToViewportPoint(anchor.position);
                Assert.That(viewport.x, Is.InRange(0f, 1f));
                Assert.That(viewport.y, Is.InRange(0f, 1f));
                Assert.Greater(viewport.z, 0);
            }
            Assert.IsTrue(battle.MoveUnit(0, 8));
            _stage.Refresh();
            Assert.AreEqual(BattleStageLayout.GetFootPoint(BattleSide.Allies, 8), _stage.GetActorAnchor(0).localPosition);
            Assert.AreEqual(BattleStageLayout.GetRowOrder(8), _stage.GetActorAnchor(0).GetComponent<SortingGroup>().sortingOrder);
            foreach (float aspect in new[] { 4f / 3, 16f / 9, 21f / 9 })
            {
                _stage.WorldCamera.aspect = aspect;
                _stage.Refresh();
                AssertHudTracksFoot(battle.GetUnit(0));
                AssertHudTracksFoot(battle.GetUnit(6));
            }
        }

        [UnityTest]
        public IEnumerator InactivePrefabKeepsAuthoredScaleAndRendererOrderAndReceivesResolvedEvents()
        {
            _prefab = new GameObject("Acceptance inactive actor");
            _prefab.SetActive(false);
            _prefab.transform.localScale = new Vector3(2, 3, 1);
            _prefab.AddComponent<IronOathPresentationProbe>();
            var rear = new GameObject("Rear", typeof(SpriteRenderer));
            rear.transform.SetParent(_prefab.transform, false);
            rear.GetComponent<SpriteRenderer>().sortingOrder = 7;
            rear.AddComponent<SortingGroup>().sortAtRoot = true;
            var front = new GameObject("Front", typeof(SpriteRenderer));
            front.transform.SetParent(_prefab.transform, false);
            front.GetComponent<SpriteRenderer>().sortingOrder = 11;
            BattleSimulation battle = BattleCatalog.CreateDemo();
            var skin = new IronOathActorSkin { prefab = _prefab, sourceFacesRight = true, worldScale = .7f,
                footOffset = new Vector3(.1f, .2f, 0), sortingOffset = 4 };
            _stage = new BattleStage(_owner.transform, _ui, battle, new[] { skin }, _ => { }, 30);
            yield return null;
            for (int i = 0; i < battle.UnitCount; i++)
            {
                BattleUnit unit = battle.GetUnit(i);
                Transform anchor = _stage.GetActorAnchor(unit.Id);
                IronOathPresentationProbe probe = anchor.GetComponentInChildren<IronOathPresentationProbe>(true);
                Assert.IsNotNull(probe);
                Assert.IsTrue(probe.gameObject.activeInHierarchy, "An inactive prefab asset must produce an active actor instance.");
                Assert.AreSame(unit, probe.Unit);
                Assert.AreEqual(_prefab.transform.localScale, probe.transform.localScale);
                Assert.AreEqual(unit.Side == BattleSide.Allies ? 1 : -1, Mathf.Sign(probe.transform.lossyScale.x));
                Renderer[] renderers = probe.GetComponentsInChildren<Renderer>();
                Assert.AreEqual(7, renderers[0].sortingOrder);
                Assert.AreEqual(11, renderers[1].sortingOrder);
                Assert.IsFalse(renderers[0].GetComponent<SortingGroup>().sortAtRoot,
                    "Nested prefab groups must stay inside the board row sorting group.");
                Assert.AreEqual(BattleStageLayout.GetRowOrder(unit.Slot, 4), anchor.GetComponent<SortingGroup>().sortingOrder);
            }
            Assert.IsTrue(battle.Start());
            battle.Step();
            for (int i = 0; i < battle.EventCount; i++) _stage.Present(battle.GetEvent(i));
            int received = 0;
            foreach (IronOathPresentationProbe probe in _stage.WorldRoot.GetComponentsInChildren<IronOathPresentationProbe>())
                received += probe.Events;
            Assert.Greater(received, 0);
            Assert.IsFalse(_prefab.activeSelf, "The source prefab itself must remain unchanged.");
            Assert.IsTrue(rear.GetComponent<SortingGroup>().sortAtRoot);
        }

        [UnityTest]
        public IEnumerator DisposalImmediatelyHidesWorldAndDestroysItOnNextFrame()
        {
            _stage = new BattleStage(_owner.transform, _ui, BattleCatalog.CreateDemo(), null, _ => { }, 30);
            GameObject world = _stage.WorldRoot.gameObject;
            Camera camera = _stage.WorldCamera;
            _stage.Dispose();
            Assert.IsFalse(world.activeSelf);
            Assert.IsFalse(camera.isActiveAndEnabled);
            _stage.Dispose(); // Repeated teardown must be harmless.
            yield return null;
            Assert.IsTrue(world == null);
            Assert.IsTrue(camera == null);
        }

        void AssertHudTracksFoot(BattleUnit unit)
        {
            Text name = null;
            foreach (Text candidate in _ui.GetComponentsInChildren<Text>())
                if (candidate.text == unit.Name) { name = candidate; break; }
            Assert.IsNotNull(name);
            var hud = (RectTransform)name.transform.parent;
            Vector2 hudTop = RectTransformUtility.WorldToScreenPoint(null,
                hud.TransformPoint(new Vector3(hud.rect.width * .5f, 0, 0)));
            Vector3 foot = _stage.WorldCamera.WorldToScreenPoint(_stage.GetActorAnchor(unit.Id).position);
            Assert.AreEqual(foot.x, hudTop.x, 1f);
            Assert.AreEqual(foot.y - 6 * _ui.GetComponentInParent<Canvas>().scaleFactor, hudTop.y, 1f);
        }
    }
}
