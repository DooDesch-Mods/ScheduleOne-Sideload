using Sideload.Host;
using Sideload.Paint;
using UnityEngine;
using UnityEngine.UI;
using Il2CppInterop.Runtime;
using Il2CppScheduleOne;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.UI;
using Il2CppScheduleOne.UI.Phone;
using Object = UnityEngine.Object;

namespace Sideload.Phone
{
    /// <summary>
    /// One registered app as a live phone app: a panel in the AppsCanvas, an icon on the home screen, and the
    /// open/close plumbing the vanilla apps use (active-app bookkeeping, orientation, camera offset, exit key).
    ///
    /// Sideload owns this instead of deriving from S1API's PhoneApp: S1API discovers apps by TYPE and instantiates
    /// each parameterless subclass once (Internal/Patches/HomeScreen.Start.cs), which cannot express "N apps declared
    /// at runtime by N different mods", and its SpawnUI/SpawnIcon are internal.
    ///
    /// Both halves are built rather than cloned from a vanilla app: the panel because cloning one runs the App
    /// component's Awake, the icon because it comes from the same prefab HomeScreen uses for its own.
    /// </summary>
    internal sealed class PhoneAppHost
    {
        private readonly AppRegistration _reg;

        private GameObject _panel;
        private GameObject _container;
        private GameObject _icon;
        private WebView _view;
        private Transform _appsCanvas;   // kept: the orientation templates are measured off it whenever the app turns
        private Transform _badge;        // the icon's unread badge, part of the vanilla prefab
        private Text _badgeText;

        private System.Action _closeAppsHandler;
        private GameInput.ExitDelegate _exitHandler;

        private PhoneAppHost(AppRegistration reg) { _reg = reg; }

        internal string Id => _reg.Id;

        /// <summary>True while this app is the one the phone has open - whether or not the phone itself is up.</summary>
        internal bool IsOpen => _panel != null && _panel.activeInHierarchy && Il2CppScheduleOne.UI.Phone.Phone.ActiveApp == _panel;

        /// <summary>
        /// True while the player can actually SEE this app. Closing the phone leaves the app panel active and still
        /// registered as the phone's ActiveApp - vanilla's SetIsOpen(false) only raises an event - so <see cref="IsOpen"/>
        /// stays true with the phone in the player's pocket. A mod asking "are they looking at this?" before raising a
        /// notification would then stay silent in exactly the case the notification exists for.
        /// </summary>
        internal bool IsShowing =>
            IsOpen && Il2CppScheduleOne.UI.Phone.Phone.InstanceExists && Il2CppScheduleOne.UI.Phone.Phone.Instance.IsOpen;

        /// <summary>
        /// Build panel and icon for one registration. Returns null (after logging) when the phone hierarchy does not
        /// look the way we expect, so a game update degrades to "app missing" instead of an exception storm.
        /// </summary>
        internal static PhoneAppHost Spawn(HomeScreen home, AppRegistration reg)
        {
            var host = new PhoneAppHost(reg);
            return host.Build(home) ? host : null;
        }

        private bool Build(HomeScreen home)
        {
            Transform appsCanvas = home.transform.parent != null ? home.transform.parent.Find("AppsCanvas") : null;
            if (appsCanvas == null) { Core.Log?.Error($"[Sideload] AppsCanvas not found - '{_reg.Id}' not spawned."); return false; }

            string panelName = "Sideload_" + _reg.Id;

            // A re-entered Main scene runs HomeScreen.Start again; drop the panel from last time rather than stacking
            // copies. Immediate, not deferred, so the name is free again before the replacement is created.
            Transform existing = appsCanvas.Find(panelName);
            if (existing != null) Object.DestroyImmediate(existing.gameObject);

            _appsCanvas = appsCanvas;

            // The player's own choice outranks the app's declared default, and is dropped silently if the app no
            // longer supports it - a mod update that removes portrait must not strand anyone in it.
            bool? remembered = OrientationStore.Remembered(_reg);
            if (remembered.HasValue) _reg.Portrait = remembered.Value;

            // Measured from a vanilla app rather than cloned from one. Instantiating that panel runs Awake on the
            // App component it carries, and App.OnStartClient generates a home-screen icon and adds itself to the
            // static app list - so every Sideload app used to put a second "Products" icon on the phone. A vanilla
            // panel root is a bare RectTransform plus that component, so copying the geometry loses nothing.
            _panel = new GameObject(panelName);
            RectTransform panelRect = _panel.AddComponent<RectTransform>();
            panelRect.SetParent(appsCanvas, false);

            if (!ApplyOrientation()) { Core.Log?.Error($"[Sideload] no vanilla app panel to measure - '{_reg.Id}' not spawned."); return false; }

            // The container fills the panel outright, in both orientations. A vanilla app insets its container to
            // leave room for its own chrome; a Sideload app has no chrome but the page, so it gets the whole screen.
            _container = new GameObject("Container");
            RectTransform containerRect = _container.AddComponent<RectTransform>();
            containerRect.SetParent(_panel.transform, false);
            containerRect.anchorMin = Vector2.zero;
            containerRect.anchorMax = Vector2.one;
            containerRect.pivot = new Vector2(0.5f, 0.5f);
            containerRect.anchoredPosition = Vector2.zero;
            containerRect.sizeDelta = Vector2.zero;

            _view = WebView.Mount(containerRect, _reg.Bundle, _reg.Id);

            // Assigned, not combined: HomeScreen.Start can run again for a fresh hierarchy, and a += would leave the
            // dead host of the previous one listening.
            _reg.OrientationChanged = OnOrientationChanged;

            _panel.SetActive(true);
            _container.SetActive(false);   // the panel stays alive; only the contents show while the app is open

            SpawnIcon(home);
            SubscribeShellEvents();

            Core.Log?.Msg($"[Sideload] '{_reg.Id}' spawned on the phone.");
            return true;
        }

        /// <summary>
        /// Give the panel the shape the app's current orientation calls for, measured off a vanilla app that already
        /// has it. Both shapes exist on every phone and they are not variations of one another:
        ///
        ///   landscape  1201 x 655   anchors 0,0-1,1 (stretch)   rotation 0
        ///   portrait    655 x 1201  anchors centred, fixed      rotation 90
        ///
        /// The portrait one is turned inside the canvas, which is why deriving it by swapping width and height alone
        /// would render the page on its side. Returns false only when the phone holds no app panel at all.
        /// </summary>
        private bool ApplyOrientation()
        {
            if (_panel == null || _appsCanvas == null) return false;

            Transform template = FindPanelTemplate(_appsCanvas, _reg.Portrait)
                              ?? FindPanelTemplate(_appsCanvas, !_reg.Portrait);
            if (template == null) return false;

            CopyRect(template.GetComponent<RectTransform>(), _panel.GetComponent<RectTransform>());
            return true;
        }

        /// <summary>
        /// A vanilla app panel of the requested shape. Selected by MEASURING rather than by name, because which app is
        /// portrait is a property of the game's scene, not something the decompiled source states.
        /// </summary>
        private static Transform FindPanelTemplate(Transform appsCanvas, bool portrait)
        {
            for (int i = 0; i < appsCanvas.childCount; i++)
            {
                Transform child = appsCanvas.GetChild(i);
                if (child.name.StartsWith("Sideload_", StringComparison.Ordinal)) continue;

                var rect = child.GetComponent<RectTransform>();
                if (rect == null) continue;

                Rect r = rect.rect;
                if (r.width < 1f || r.height < 1f) continue;
                if (r.height > r.width == portrait) return child;
            }
            return null;
        }

        /// <summary>
        /// The app was told to turn. Reshape the panel, turn the phone with it if the app is on screen, and let the
        /// page lay itself out again against the new viewport - document and script intact, so nothing on screen is
        /// lost and `@media (orientation: ...)` decides what changes.
        /// </summary>
        private void OnOrientationChanged()
        {
            try
            {
                if (!ApplyOrientation()) return;

                OrientationStore.Remember(_reg.Id, _reg.Portrait);

                if (IsOpen && Il2CppScheduleOne.UI.Phone.Phone.InstanceExists)
                {
                    Il2CppScheduleOne.UI.Phone.Phone.Instance.SetIsHorizontal(!_reg.Portrait);
                    Il2CppScheduleOne.UI.Phone.Phone.Instance.SetLookOffsetMultiplier(_reg.Portrait ? 1f : 0.6f);
                }

                Canvas.ForceUpdateCanvases();
                _view?.QueueResize();
            }
            catch (Exception e) { Core.Log?.Error($"[Sideload] turning '{_reg.Id}' failed: {e.Message}"); }
        }

        /// <summary>
        /// Reproduce a RectTransform's placement on another one. GetComponent, not a managed cast: under Il2CppInterop
        /// the `as` operator tests the WRAPPER type, which is Transform here, so a cast silently yields null.
        /// </summary>
        private static void CopyRect(RectTransform from, RectTransform to)
        {
            if (from == null || to == null) return;

            to.anchorMin = from.anchorMin;
            to.anchorMax = from.anchorMax;
            to.pivot = from.pivot;
            to.anchoredPosition3D = from.anchoredPosition3D;
            to.sizeDelta = from.sizeDelta;
            to.localScale = from.localScale;
            to.localRotation = from.localRotation;
        }

        /// <summary>
        /// Build this app's home-screen icon from the same prefab the game uses for its own, and dress it the way
        /// <c>HomeScreen.GenerateAppIcon</c> does: sprite into <c>Mask/Image</c>, caption into <c>Label</c>.
        ///
        /// The prefab matters. Cloning a neighbouring icon instead - which is what this did first - inherits that
        /// app's sprite and caption, so every Sideload app looked like whichever app happened to sit at index 0.
        /// The caption is the other half of it: it is a legacy <see cref="Text"/>, so a TextMeshPro-first search
        /// reported success while nothing on screen changed.
        /// </summary>
        private void SpawnIcon(HomeScreen home)
        {
            Transform icons = home.appIconContainer;
            GameObject prefab = home.appIconPrefab;
            if (icons == null || prefab == null)
            {
                Core.Log?.Warning($"[Sideload] HomeScreen exposes no icon prefab or container - '{_reg.Id}' has no home-screen icon.");
                return;
            }

            // HomeScreen.Start can run twice for one world load, so drop a previous icon before making a new one -
            // otherwise the app collects a second one on every re-entry.
            string iconName = "SideloadIcon_" + _reg.Id;
            Transform stale = icons.Find(iconName);
            if (stale != null) Object.DestroyImmediate(stale.gameObject);

            _icon = Object.Instantiate(prefab, icons);
            _icon.name = iconName;
            _icon.transform.SetAsLastSibling();
            _icon.SetActive(true);

            Dress(_icon.transform, AppIconSprite.For(_reg), _reg.IconLabel, _reg.Id);

            var button = _icon.GetComponent<Button>();
            if (button != null)
            {
                button.onClick.RemoveAllListeners();
                button.onClick.AddListener((UnityEngine.Events.UnityAction)(Open));
            }
            else Core.Log?.Warning($"[Sideload] the icon prefab has no Button component - '{_reg.Id}' cannot be opened from the home screen.");

            // Without this the icon exists but controller and arrow-key navigation walks straight past it.
            var selectable = _icon.GetComponent<Il2CppScheduleOne.UISelectable>();
            if (selectable != null && home.uiPanel != null) home.uiPanel.AddSelectable(selectable);

            // The unread badge is part of the prefab; the vanilla apps drive it through App.SetNotificationCount and
            // nothing else. Re-applied rather than reset, because a phone rebuilt on a scene change must not silently
            // drop a count the mod set before it.
            _badge = _icon.transform.Find("Notifications");
            _badgeText = _badge != null ? _badge.Find("Text")?.GetComponent<Text>() : null;
            if (_badge == null) Core.Log?.Warning($"[Sideload] the icon prefab has no Notifications badge - '{_reg.Id}' cannot show a count.");

            ApplyBadge();
        }

        /// <summary>
        /// Put an unread count on the app's home-screen icon, or clear it with zero. Exactly what
        /// <c>App.SetNotificationCount</c> does for a vanilla app, on the same prefab child.
        /// </summary>
        internal void SetBadge(int count)
        {
            _reg.Badge = Math.Max(0, count);
            ApplyBadge();
        }

        private void ApplyBadge()
        {
            if (_badge == null) return;

            if (_badgeText != null) _badgeText.text = _reg.Badge > 99 ? "99+" : _reg.Badge.ToString();
            _badge.gameObject.SetActive(_reg.Badge > 0);
        }

        /// <summary>
        /// Raise one of the game's own phone notifications for this app - the slide-in the vanilla apps use, with
        /// this app's icon on it, so a message arriving while the phone is closed reads like any other.
        /// </summary>
        internal void Notify(string title, string subtitle)
        {
            try
            {
                if (!Il2CppScheduleOne.DevUtilities.Singleton<NotificationsManager>.InstanceExists) return;

                Il2CppScheduleOne.DevUtilities.Singleton<NotificationsManager>.Instance
                    .SendNotification(title ?? "", subtitle ?? "", AppIconSprite.For(_reg), 5f, true);
            }
            catch (Exception e) { Core.Log?.Error($"[Sideload] notification from '{_reg.Id}' failed: {e.Message}"); }
        }

        /// <summary>
        /// Put the picture and the caption on a fresh icon. The paths are the ones the game itself uses, with a
        /// component-wide search as the fallback so a renamed child degrades to a wrong-looking icon, not an exception.
        /// </summary>
        private static void Dress(Transform icon, Sprite sprite, string caption, string appId)
        {
            Transform image = icon.Find("Mask/Image");
            var picture = image != null ? image.GetComponent<Image>() : icon.GetComponentInChildren<Image>(true);
            if (picture != null && sprite != null) picture.sprite = sprite;
            else if (picture == null) Core.Log?.Warning($"[Sideload] no Image under the icon for '{appId}' - it keeps the prefab's picture.");

            if (!SetIconLabel(icon.gameObject, caption))
                Core.Log?.Warning($"[Sideload] no label found on the icon for '{appId}' - it stays unnamed.");
        }

        /// <summary>
        /// Write the caption. "Label" first, because that is where <c>GenerateAppIcon</c> writes and it is a legacy
        /// <see cref="Text"/>; the subtree sweep afterwards is only there to survive a renamed child in a game update.
        /// </summary>
        private static bool SetIconLabel(GameObject icon, string caption)
        {
            Transform label = icon.transform.Find("Label");
            if (label != null)
            {
                var text = label.GetComponent<Text>();
                if (text != null) { text.text = caption; return true; }

                var tmp = label.GetComponent<Il2CppTMPro.TextMeshProUGUI>();
                if (tmp != null) { tmp.text = caption; return true; }
            }

            foreach (Text legacy in icon.GetComponentsInChildren<Text>(true))
            {
                // "Notifications/Text" is the unread badge, not the caption - writing the app name into it would put
                // the name inside the little red circle.
                if (legacy.transform.parent != null && legacy.transform.parent.name == "Notifications") continue;
                legacy.text = caption;
                return true;
            }

            foreach (Il2CppTMPro.TextMeshProUGUI tmp in icon.GetComponentsInChildren<Il2CppTMPro.TextMeshProUGUI>(true))
            {
                tmp.text = caption;
                return true;
            }

            return false;
        }

        private void SubscribeShellEvents()
        {
            if (!Il2CppScheduleOne.UI.Phone.Phone.InstanceExists) return;

            _closeAppsHandler = Close;
            Il2CppScheduleOne.UI.Phone.Phone.Instance.closeApps += _closeAppsHandler;

            _exitHandler = DelegateSupport.ConvertDelegate<GameInput.ExitDelegate>(new System.Action<ExitAction>(OnExit));
            GameInput.RegisterExitListener(_exitHandler, 1);
        }

        /// <summary>
        /// Escape or right-click - the game raises both through the same chain (GameInput.cs:219-235), and the vanilla
        /// apps treat them alike: right-click steps back out of a conversation before it closes Messages.
        ///
        /// So the page gets first refusal. A page that navigated somewhere calls preventDefault() and the app stays
        /// open one level up; a page that did not, or that never listened, closes exactly as it always did. The press
        /// is marked used either way, otherwise the same press would also open the pause menu.
        /// </summary>
        private void OnExit(ExitAction exit)
        {
            if (exit.Used || !IsShowing) return;

            exit.Used = true;

            try
            {
                if (_view != null && _view.DispatchBack(exit.exitType == ExitType.RightClick ? "rightClick" : "escape"))
                    return;
            }
            catch (Exception e) { Core.Log?.Error($"[Sideload] the back handler of '{_reg.Id}' threw: {e.Message}"); }

            Close();
        }

        /// <summary>True while this host's panel still exists - a destroyed one means the phone was rebuilt.</summary>
        internal bool IsAlive => _panel != null;

        /// <summary>Whether the app declared both orientations, and so may be turned by the player.</summary>
        internal bool CanTurn => _reg.CanTurn;

        /// <summary>Turn the phone the other way round. The player's half of the orientation.</summary>
        internal void Turn() => Registry.SetOrientation(_reg.Id, _reg.Portrait ? "landscape" : "portrait");

        internal void Open()
        {
            try
            {
                var phone = Il2CppScheduleOne.UI.Phone.Phone.Instance;
                if (Il2CppScheduleOne.UI.Phone.Phone.ActiveApp != null && Il2CppScheduleOne.UI.Phone.Phone.ActiveApp != _panel)
                    phone.RequestCloseApp();

                SetOpen(true);

                // The page is built on first open, not on spawn: a panel that has never been shown has no laid-out
                // rect, and measuring text against a zero-width viewport would wrap everything to nothing.
                Canvas.ForceUpdateCanvases();
                _view?.EnsureBuilt();

#if DEBUG
                // The viewport constant in the design doc rests on this number, so measure it where it is real:
                // after the app is open and the canvas has laid the container out.
                Devtools.Probe.LogRect(_container != null ? _container.GetComponent<RectTransform>() : null, $"app container '{_reg.Id}'");
#endif
            }
            catch (Exception e) { Core.Log?.Error($"[Sideload] opening '{_reg.Id}' failed: {e.Message}"); }
        }

        internal void Close()
        {
            try { if (IsOpen) SetOpen(false); }
            catch (Exception e) { Core.Log?.Error($"[Sideload] closing '{_reg.Id}' failed: {e.Message}"); }
        }

        /// <summary>
        /// The open/close dance the vanilla App class performs: swap which canvas is live, record the active app, and
        /// rotate the phone plus pull the camera in for landscape.
        /// </summary>
        private void SetOpen(bool open)
        {
            if (open && Il2CppScheduleOne.UI.Phone.Phone.ActiveApp != null && Il2CppScheduleOne.UI.Phone.Phone.ActiveApp != _panel) return;

            if (AppsCanvas.InstanceExists) AppsCanvas.Instance.SetIsOpen(open);
            if (HomeScreen.InstanceExists) HomeScreen.Instance.SetIsOpen(!open);

            if (Il2CppScheduleOne.UI.Phone.Phone.InstanceExists)
            {
                // A portrait app leaves the phone upright, exactly as a vanilla Vertical app does - and the camera
                // only pulls in for the wide ones, which is where the extra width has to come from.
                bool horizontal = open && !_reg.Portrait;
                Il2CppScheduleOne.UI.Phone.Phone.Instance.SetIsHorizontal(horizontal);
                Il2CppScheduleOne.UI.Phone.Phone.Instance.SetLookOffsetMultiplier(horizontal ? 0.6f : 1f);
            }

            Il2CppScheduleOne.UI.Phone.Phone.ActiveApp = open ? _panel : null;
            if (_container != null) _container.SetActive(open);
        }
    }
}
