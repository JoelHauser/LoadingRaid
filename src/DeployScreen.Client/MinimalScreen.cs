using System;
using System.Collections.Generic;
using UnityEngine;

namespace DeployScreen.Client
{
    // Only owns presentation objects. Never disables the loading screen, its UI camera,
    // input handling, status updates, cancel button, party controls or any raid object.
    internal sealed class MinimalScreen
    {
        private struct Hidden { internal GameObject Object; internal bool Environment; }
        private readonly List<Hidden> _hidden = new List<Hidden>();
        private GameObject _background;
        private Component _screen;
        private object _environment;
        private double _nextCheck;
        private bool _playerSkipped, _bannersSkipped, _backgroundCreated;
        private int _environmentRootsSuspended;
        internal string Description
        {
            get { return "preview-skipped=" + _playerSkipped + "; banners-skipped=" + _bannersSkipped
                + "; plain-background=" + _backgroundCreated + "; environment-roots-suspended=" + _environmentRootsSuspended; }
        }
        internal string Metadata
        {
            get { return MetadataFor(_playerSkipped, _bannersSkipped, _backgroundCreated, _environmentRootsSuspended); }
        }

        /// <summary>
        /// The same fields when there is no MinimalScreen to ask -- a Show that threw before the
        /// postfix could build one, though its prefixes had already skipped the preview and the
        /// banners. See LoadingPerformance.Presentation.
        /// </summary>
        internal static string MetadataFor(bool preview, bool banners, bool background, int roots)
        {
            return ",\"minimalPreviewSkipped\":" + Bool(preview)
                + ",\"minimalBannersSkipped\":" + Bool(banners)
                + ",\"minimalBackgroundCreated\":" + Bool(background)
                + ",\"minimalEnvironmentSuspended\":" + Bool(roots > 0)
                + ",\"minimalEnvironmentRootsSuspended\":" + roots;
        }

        private static string Bool(bool value) { return value ? "true" : "false"; }

        internal void Begin(Component screen, object banners, bool skipPlayer, bool skipBanners)
        {
            _screen = screen;
            if (screen == null) return;
            // Both arguments are what the prefixes actually skipped this raid, not which hooks
            // installed -- the report must not credit minimal with work it did not do.
            _playerSkipped = skipPlayer;
            _bannersSkipped = skipBanners;
            var player = GameTypes.Loading_PlayerModel?.GetValue(screen) as Component;
            if (skipPlayer) Hide(player, false);
            if (skipBanners) Hide(banners as Component, false);
            CreateBackground();
            SuspendEnvironment();
            DeployScreenPlugin.Log.LogInfo("[DeployScreen] minimal screen: " + Description);
        }

        private void CreateBackground()
        {
            if (GameTypes.BackgroundImage == null || GameTypes.Background_Color == null
                || GameTypes.Background_Raycast == null) return;
            GameObject background = null;
            try
            {
                background = new GameObject("DeployScreen Minimal Background", typeof(RectTransform));
                var rect = (RectTransform)background.transform;
                rect.SetParent(_screen.transform, false);
                rect.SetAsFirstSibling();
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = rect.offsetMax = Vector2.zero;
                var image = background.AddComponent(GameTypes.BackgroundImage);
                GameTypes.Background_Color.SetValue(image, new Color(0.025f, 0.03f, 0.035f, 1f), null);
                GameTypes.Background_Raycast.SetValue(image, false, null);
                _background = background;
                _backgroundCreated = true;
            }
            catch
            {
                if (background != null) UnityEngine.Object.Destroy(background);
                throw;
            }
        }

        internal void Tick(double now)
        {
            if (now < _nextCheck) return;
            _nextCheck = now + 0.5;
            // An environment scene already loading when deployment began can arrive later.
            SuspendEnvironment();
        }

        private void SuspendEnvironment()
        {
            // Without an opaque replacement, keep the existing environment visible.
            if (_background == null || GameTypes.Environment_Current == null
                || GameTypes.Environment_Visible == null || GameTypes.EnvironmentUI_Instance == null) return;
            if (GameTypes.EnvironmentUI_Instantiated != null
                && !(bool)GameTypes.EnvironmentUI_Instantiated.GetValue(null, null)) return;
            _environment = GameTypes.EnvironmentUI_Instance.GetValue(null, null);
            if (_environment == null) return;
            var root = GameTypes.Environment_Current.GetValue(_environment) as Component;
            // A prefab/version that shares the environment camera with the UI must retain it.
            var canvas = _screen.GetComponentInParent<Canvas>();
            if (canvas == null || root == null) return;
            canvas = canvas.rootCanvas;
            if (canvas.renderMode != RenderMode.ScreenSpaceOverlay)
            {
                if (canvas.worldCamera == null || canvas.worldCamera.transform.IsChildOf(root.transform)) return;
            }
            Hide(root, true);
        }

        private void Hide(Component component, bool environment)
        {
            if (component == null || _screen == null) return;
            var target = component.gameObject;
            // A hierarchy change must not accidentally turn off the screen or its ancestors.
            if (target == _screen.gameObject || _screen.transform.IsChildOf(target.transform)) return;
            if (!target.activeSelf) return;
            var tracked = false;
            foreach (var item in _hidden) if (item.Object == target) { tracked = true; break; }
            if (!tracked)
            {
                _hidden.Add(new Hidden { Object = target, Environment = environment });
                if (environment) _environmentRootsSuspended++;
            }
            target.SetActive(false);
        }

        /// <summary>
        /// Puts back everything that was switched off, then lets the game reassert what it wants
        /// visible through its own code path.
        ///
        /// This used to skip re-enabling an environment root whenever EFT no longer wanted the
        /// environment shown -- which meant a GameObject we had disabled stayed disabled for the
        /// rest of the session. The menu came back with no environment at all, and nothing in the
        /// game would ever switch it on again, because as far as EFT was concerned it had never
        /// been switched off. Leaving a foreign object disabled is not deference to the game's
        /// state; it is a hole in it.
        ///
        /// So: always undo our own SetActive(false) -- we only ever disabled objects that were
        /// active when we found them -- and then call ShowEnvironment with whatever EFT currently
        /// wants, so the game's own visibility logic has the last word and its state and the scene
        /// agree again.
        /// </summary>
        internal void Restore()
        {
            foreach (var item in _hidden)
            {
                if (item.Object == null) continue;
                try { item.Object.SetActive(true); }
                catch (Exception e) { DeployScreenPlugin.Log.LogWarning("[DeployScreen] could not restore presentation object: " + e.Message); }
            }
            _hidden.Clear();

            // Hand visibility back to the game. If it wants the environment hidden for a raid
            // transition it hides it here, through the same method it always uses -- and crucially
            // _lastVisibleStateEnvironment and the scene end up describing the same thing.
            try
            {
                if (_environment != null && GameTypes.Environment_Visible != null
                    && GameTypes.EnvironmentUI_ShowEnvironment != null)
                {
                    var wanted = (bool)GameTypes.Environment_Visible.GetValue(_environment);
                    GameTypes.EnvironmentUI_ShowEnvironment.Invoke(_environment, new object[] { wanted });
                }
            }
            catch (Exception e)
            {
                DeployScreenPlugin.Log.LogWarning("[DeployScreen] could not hand the environment back: " + e.Message);
            }

            _environment = null;

            if (_background != null) UnityEngine.Object.Destroy(_background);
            _background = null;
        }
    }
}
