using System;
using System.Collections.Generic;
using UnityEngine;

namespace DeployScreen.Client
{
    /// <summary>
    /// Adds depth to the deploy screen by moving the backdrop's own camera and breathing its own
    /// lights. No new art, no new cameras, no post-processing.
    ///
    /// Why this works at all
    /// ---------------------
    /// The backdrop is not a picture. EFT.UI.EnvironmentUIRoot is the root of a real 3D scene and
    /// its fields are public:
    ///
    ///     Transform  CameraContainer
    ///     Light[]    MainScreenLights
    ///     GameObject[] MainScreenObjects
    ///     CanvasGroup Shading
    ///
    /// So the depth is already there -- geometry at different distances, lit by real lights. What
    /// the deploy screen never does is *move* anything, which is the whole reason it reads as a
    /// flat image with a man standing in front of it. Translating CameraContainer by a few
    /// centimetres produces true parallax for free, because near geometry sweeps across the frame
    /// faster than far geometry does. That is depth the engine computes, not a layered fake.
    ///
    /// Rotation alone would not do it: rotating a camera shifts everything by the same angle
    /// regardless of distance, so it gives motion without parallax. The translation is the part
    /// that sells depth; the small rotation on top just keeps the motion from looking like a
    /// slider.
    ///
    /// Composing rather than overwriting
    /// ---------------------------------
    /// The game moves CameraContainer itself -- EnvironmentUIRoot.RandomRotate/RotateBack, reached
    /// from EnvironmentUI.Rotate() when a screen stops being the main one. So this never assigns
    /// an absolute transform. It subtracts the offset it applied last frame, reads whatever the
    /// game left, and adds the new offset. If the game moves the container underneath us we ride
    /// it instead of fighting it, and Restore() takes off exactly what we put on.
    /// </summary>
    internal sealed class SceneDepth
    {
        /// <summary>Lights and their intensity when we found them.</summary>
        private struct Lit { internal Light Light; internal float Intensity; }

        private Transform _camera;
        private Vector3 _appliedOffset;
        private Quaternion _appliedRotation = Quaternion.identity;

        /// <summary>
        /// Where the character should be this frame, as a fraction of the frame's height, if he
        /// is standing in the place rather than printed on a picture of it.
        ///
        /// The backdrop is a real scene and the camera really moves across it, so everything in
        /// the scene parallaxes for free. The PMC does not: he is a preview rendered by a second
        /// camera and composited on top as a UI image, so while the world slides he is nailed to
        /// the screen. That is worse than not moving at all -- the nearest thing in the frame is
        /// the one moving least, which is the opposite of what an eye expects, and it reads as a
        /// sticker on a photograph.
        ///
        /// So he is given the shift he would have had. At the near plane -- the depth the haze
        /// hangs at, and the nearest thing the scene actually has -- that is the camera's own move
        /// over the frame height there, negated because a camera moving right sends the world
        /// left. Zero whenever there is no art up to be parallaxed against.
        /// </summary>
        internal Vector2 CharacterDrift { get; private set; }

        private Lit[] _lights;
        private Camera _lens;

        private GameObject _shadow;
        private bool _shadowSet;
        private bool _shadowWas;

        /// <summary>
        /// The PMC's poser, kept only so patrol can be turned off again. It is destroyed with the
        /// screen, so every use goes through a Unity null check rather than trusting the field.
        /// </summary>
        private object _poser;

        private bool _patrolSet;
        private bool _overlaySet;

        private float _seed;
        private bool _running;
        private bool _warnedOnce;

        internal bool Running { get { return _running; } }

        /// <summary>What was actually applied, for the diagnostic report.</summary>
        internal string Description
        {
            get
            {
                return "camera=" + (_camera != null)
                    + "; lights=" + (_lights == null ? 0 : _lights.Length)
                    + "; contact-shadow=" + (_shadowSet || _bootShadows.Count > 0 ? DeployScreenPlugin.DepthGroundShadow.Value + " on " + (_shadowSet ? 1 : 0) + "+" + _bootShadows.Count : "as found")
                    + "; patrol=" + _patrolSet
                    + "; overlay=" + _overlaySet;
            }
        }

        // ------------------------------------------------------------------ begin

        internal void Begin(Component screen)
        {
            if (!GameTypes.DepthReady) return;
            if (!DeployScreenPlugin.DepthEnabled.Value) return;

            try
            {
                // A different starting point per raid, so two raids in a row do not drift
                // identically. Time-based rather than Random so it is continuous.
                _seed = UnityEngine.Random.Range(0f, 1000f);

                var root = CurrentRoot();
                if (root != null)
                {
                    TakeCamera(root);
                    TakeLights(root);
                }

                TakePlayerModel(screen);
                TakeOverlay();

                // The shadow rig counts even though nothing has been found yet: it cannot be found
                // until the character loads, and Tick is what goes looking.
                _running = _camera != null || _lights != null || _shadowSet || _patrolSet || _overlaySet
                    || (_playerView != null
                        && DeployScreenPlugin.DepthGroundShadow.Value != ContactShadow.AsFound);

                if (_running) DeployScreenPlugin.Log.LogInfo("[DeployScreen] scene depth: " + Description);
            }
            catch (Exception error)
            {
                WarnOnce(error);
                Restore();
            }
        }

        /// <summary>The EnvironmentUIRoot that is live now, or null.</summary>
        private static Component CurrentRoot()
        {
            if (GameTypes.Environment_Current == null || GameTypes.EnvironmentUI_Instance == null) return null;

            try
            {
                if (GameTypes.EnvironmentUI_Instantiated != null)
                {
                    var live = GameTypes.EnvironmentUI_Instantiated.GetValue(null, null) as bool?;
                    if (live != true) return null;
                }

                var environmentUI = GameTypes.EnvironmentUI_Instance.GetValue(null, null);
                if (environmentUI == null) return null;

                return GameTypes.Environment_Current.GetValue(environmentUI) as Component;
            }
            catch
            {
                return null;
            }
        }

        private void TakeCamera(Component root)
        {
            if (GameTypes.EnvRoot_CameraContainer == null) return;
            if (DeployScreenPlugin.DepthDrift.Value <= 0f && DeployScreenPlugin.DepthSway.Value <= 0f) return;

            _camera = GameTypes.EnvRoot_CameraContainer.GetValue(root) as Transform;

            // The container is what moves; the camera inside it is what decides which way that
            // move is on screen. They are not the same transform and there is no promise they
            // share an orientation, so the drift is turned into a direction through this one
            // rather than by assuming the container's x is the screen's x.
            _lens = _camera == null ? null : _camera.GetComponentInChildren<Camera>();
        }

        private void TakeLights(Component root)
        {
            if (GameTypes.EnvRoot_MainScreenLights == null) return;
            if (DeployScreenPlugin.DepthLight.Value <= 0f) return;

            var array = GameTypes.EnvRoot_MainScreenLights.GetValue(root) as Array;
            if (array == null || array.Length == 0) return;

            var taken = new Lit[array.Length];
            var count = 0;

            foreach (var entry in array)
            {
                var light = entry as Light;
                if (light == null) continue;

                taken[count++] = new Lit { Light = light, Intensity = light.intensity };
            }

            if (count == 0) return;

            _lights = new Lit[count];
            Array.Copy(taken, _lights, count);
        }

        /// <summary>
        /// The PMC's ground contact. MenuPlayerPoser.BottomShadow is a public GameObject that
        /// already exists -- grounding the character needs no new asset, only for it to be on.
        /// </summary>
        private void TakePlayerModel(Component screen)
        {
            if (screen == null || GameTypes.Loading_PlayerModel == null) return;

            try
            {
                var view = GameTypes.Loading_PlayerModel.GetValue(screen);
                if (view == null) return;

                // Kept first, and before anything that can fail. The character is loaded
                // asynchronously, so at screen-show MenuPlayer -- and the shadow rig hanging off
                // it -- does not exist yet, and neither necessarily does the poser. Tick goes
                // looking once they do, and it can only do that if this was recorded. See
                // TakeBootShadow.
                _playerView = view as Component;

                var poser = GameTypes.PlayerModelView_Poser == null
                    ? null
                    : GameTypes.PlayerModelView_Poser.GetValue(view, null);

                if (poser == null) return;

                _poser = poser;

                TakeContactShadow(poser);

                // Patrol is a write-only property -- there is no readable backing field -- so what
                // it was before cannot be recovered. That is why it is opt-in and why Restore
                // sets it false rather than "back".
                if (DeployScreenPlugin.DepthPatrol.Value && GameTypes.MenuPoser_SetPatrol != null)
                {
                    GameTypes.MenuPoser_SetPatrol.Invoke(poser, new object[] { true });
                    _patrolSet = true;
                }
            }
            catch (Exception error)
            {
                WarnOnce(error);
            }
        }

        /// <summary>
        /// The PMC's contact shadow, set to whichever of the three states was asked for.
        ///
        /// It used to be a bool that could only switch the thing on, which meant there was no way
        /// to say "and I do not want it" -- and over a photograph that is a real thing to want.
        /// A blob authored to sit under a character in a dim menu room does not necessarily sit
        /// under him over a picture: the framing is different, the camera is at a different
        /// height, and what grounded him in the room can read as a smear behind him instead.
        ///
        /// Whatever it was is recorded and put back, in both directions.
        /// </summary>
        private void TakeContactShadow(object poser)
        {
            var wanted = DeployScreenPlugin.DepthGroundShadow.Value;

            if (wanted == ContactShadow.AsFound || GameTypes.MenuPoser_BottomShadow == null) return;

            var shadow = GameTypes.MenuPoser_BottomShadow.GetValue(poser) as GameObject;
            if (shadow == null) return;

            var show = wanted == ContactShadow.Show;
            if (shadow.activeSelf == show) return;

            _shadow = shadow;
            _shadowWas = shadow.activeSelf;
            _shadowSet = true;

            shadow.SetActive(show);

            DeployScreenPlugin.Log.LogInfo(
                "[DeployScreen] contact shadow '" + shadow.name + "' "
                + (show ? "switched on" : "switched off") + " (was " + _shadowWas + ")");
        }

        /// <summary>
        /// The shadow rig that is actually on the character, as opposed to the one the field
        /// name promised.
        ///
        /// `MenuPlayerPoser.BottomShadow` -- the field this mod has been reaching for since the
        /// beginning -- points at a GameObject that is **inactive** on this build. That is why
        /// setting the contact shadow to Hide changed nothing and the log said "as found": there
        /// was nothing there to hide.
        ///
        /// What is actually drawing is `MenuPlayer/BootShadow`, five quads -- Over, Left, Right,
        /// Over (1), Over (2) -- all active, all `Unlit/Transparent Colored`, the largest of them
        /// 2.83 units across against a character about 1.8 tall. The probe found them the moment
        /// it stopped filtering by path and started filtering by shader; they had been in front
        /// of it the whole time, under MenuPlayer, which the old filter skipped.
        ///
        /// Found by name rather than by field, because the field lied. Whole subtrees are toggled
        /// -- the root of each run of shadow-named objects, not each child -- and each one is
        /// recorded with what it was so Restore can put it back either way.
        ///
        /// Driven from Tick rather than from Begin, and that is the whole reason the first attempt
        /// did nothing: `ShowPlayerModel` is async, so at screen-show there is no MenuPlayer yet
        /// and nothing named shadow to find. The log said so -- no rig line at all, and
        /// `contact-shadow=as found`. The probe only ever saw these because it runs four seconds
        /// in, which is exactly the trap it was moved late to avoid, and then this walked into it.
        ///
        /// Searching stops as soon as anything is found; after that the objects it holds are
        /// re-asserted instead, which costs an activeSelf check each and no allocation.
        /// </summary>
        private void TakeBootShadow(Component view)
        {
            var wanted = DeployScreenPlugin.DepthGroundShadow.Value;

            if (view == null || wanted == ContactShadow.AsFound) return;

            var show = wanted == ContactShadow.Show;

            foreach (var child in view.GetComponentsInChildren<Transform>(true))
            {
                if (child == null) continue;
                if (child.name.IndexOf("shadow", StringComparison.OrdinalIgnoreCase) < 0) continue;

                // The root of the run only. BootShadow's children are all called shadow-something
                // too, and toggling the parent takes them with it.
                var parent = child.parent;
                if (parent != null && parent.name.IndexOf("shadow", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                if (child.gameObject.activeSelf == show) continue;

                _bootShadows.Add(child.gameObject);
                _bootShadowWas.Add(child.gameObject.activeSelf);

                child.gameObject.SetActive(show);

                DeployScreenPlugin.Log.LogInfo(
                    "[DeployScreen] shadow rig '" + child.name + "' "
                    + (show ? "switched on" : "switched off") + " (was " + !show + ")");
            }
        }

        private readonly List<GameObject> _bootShadows = new List<GameObject>();
        private readonly List<bool> _bootShadowWas = new List<bool>();
        private Component _playerView;
        private double _bootShadowNext;

        /// <summary>
        /// Looks for the shadow rig until it exists, then holds it where it was put.
        /// </summary>
        private void WatchBootShadow(double now)
        {
            var wanted = DeployScreenPlugin.DepthGroundShadow.Value;
            if (wanted == ContactShadow.AsFound || _playerView == null) return;

            var show = wanted == ContactShadow.Show;

            if (_bootShadows.Count == 0)
            {
                if (now < _bootShadowNext) return;

                // Four times a second while the character is still loading. Once anything is
                // found this branch is never taken again, so the walk is not a running cost.
                _bootShadowNext = now + 0.25;
                TakeBootShadow(_playerView);
                return;
            }

            for (var i = 0; i < _bootShadows.Count; i++)
            {
                var one = _bootShadows[i];
                if (one == null || one.activeSelf == show) continue;

                one.SetActive(show);

                DeployScreenPlugin.Log.LogInfo(
                    "[DeployScreen] shadow rig '" + one.name + "' came back, switching it off again");
            }
        }

        private void GiveBackBootShadow()
        {
            for (var i = _bootShadows.Count - 1; i >= 0; i--)
            {
                try { if (_bootShadows[i] != null) _bootShadows[i].SetActive(_bootShadowWas[i]); }
                catch { }
            }

            _bootShadows.Clear();
            _bootShadowWas.Clear();
        }

        /// <summary>
        /// The game's own readability scrim -- EnableOverlay(true) is
        /// EnvironmentShading.SetShadingVisibility(true, 0.4f). Using it rather than adding our
        /// own panel means the loading text sits on the same treatment the rest of the UI uses.
        /// </summary>
        private void TakeOverlay()
        {
            if (!DeployScreenPlugin.DepthOverlay.Value) return;
            if (GameTypes.EnvironmentUI_EnableOverlay == null) return;

            try
            {
                if (GameTypes.EnvironmentUI_Instantiated != null)
                {
                    var live = GameTypes.EnvironmentUI_Instantiated.GetValue(null, null) as bool?;
                    if (live != true) return;
                }

                var environmentUI = GameTypes.EnvironmentUI_Instance.GetValue(null, null);
                if (environmentUI == null) return;

                GameTypes.EnvironmentUI_EnableOverlay.Invoke(environmentUI, new object[] { true });
                _overlaySet = true;
            }
            catch (Exception error)
            {
                WarnOnce(error);
            }
        }

        // ------------------------------------------------------------------- tick

        internal void Tick(double now)
        {
            if (!_running) return;

            try
            {
                var t = (float)now + _seed;

                DriveCamera(t);
                DriveLights(t);
                WatchBootShadow(now);
            }
            catch (Exception error)
            {
                WarnOnce(error);
                Restore();
            }
        }

        /// <summary>
        /// Layered sines at frequencies that do not share a period, so the motion never settles
        /// into a visible loop.
        ///
        /// These were slow on purpose -- the room breathing rather than a camera move -- and that
        /// went too far. At speed 1 the components run from a 44-second period to a 170-second
        /// one, so a one-minute load shows less than a single cycle of the slowest of them and the
        /// screen reads as a still photograph. The player's word for it was "static", which is
        /// exactly right.
        ///
        /// Speed is the lever rather than drift, and the difference matters. RequiredOverscan
        /// grows the art planes to cover whatever sweep the drift asks for, so a bigger drift is
        /// paid for in picture: the plane is built larger and you see a smaller part of it. The
        /// same sweep played faster costs nothing at all.
        /// </summary>
        private void DriveCamera(float t)
        {
            if (_camera == null) return;

            t *= Mathf.Max(0.01f, DeployScreenPlugin.DepthSpeed.Value);

            var drift = DeployScreenPlugin.DepthDrift.Value;
            var sway = DeployScreenPlugin.DepthSway.Value;

            var offset = new Vector3(
                (Mathf.Sin(t * 0.081f) * 0.7f + Mathf.Sin(t * 0.143f + 1.7f) * 0.3f) * drift,
                (Mathf.Sin(t * 0.063f + 2.3f) * 0.6f + Mathf.Sin(t * 0.117f) * 0.4f) * drift * 0.55f,
                Mathf.Sin(t * 0.049f + 0.9f) * drift * 0.8f);

            var rotation = Quaternion.Euler(
                Mathf.Sin(t * 0.055f + 1.1f) * sway * 0.6f,
                Mathf.Sin(t * 0.071f) * sway,
                Mathf.Sin(t * 0.037f + 2.7f) * sway * 0.25f);

            // Take last frame's contribution off before adding this one, so whatever the game did
            // to the transform in between survives.
            _camera.localPosition = _camera.localPosition - _appliedOffset + offset;
            _camera.localRotation = _camera.localRotation * Quaternion.Inverse(_appliedRotation) * rotation;

            _appliedOffset = offset;
            _appliedRotation = rotation;

            // The sway is not compensated for. A rotation shifts near and far by the same angle,
            // so it is not parallax and the character is no more wrong for it than the scene is
            // -- and at the default 0.12 degrees it is worth about a tenth of what the drift is.
            var frame = StagingArea.CharacterFrameHeight;
            var taste = Mathf.Clamp(DeployScreenPlugin.DepthCharacter.Value, 0f, 2f);

            if (frame <= 0.0001f || taste <= 0f)
            {
                CharacterDrift = Vector2.zero;
                return;
            }

            // The offset is written in the container's parent space. What the character needs is
            // right and up as the lens sees them, so it goes out to world and back in through the
            // camera. Negated on the way: a camera moving right sends the world left.
            var world = _camera.parent == null ? offset : _camera.parent.TransformVector(offset);
            var seen = _lens == null ? offset : _lens.transform.InverseTransformVector(world);

            CharacterDrift = new Vector2(-seen.x, -seen.y) * (taste / frame);
        }

        /// <summary>
        /// A slow, shallow intensity wander per light, each on its own phase. Relative to the
        /// intensity the scene shipped with, so it works whatever the scene's exposure is.
        /// </summary>
        private void DriveLights(float t)
        {
            if (_lights == null) return;

            var depth = DeployScreenPlugin.DepthLight.Value;

            for (var i = 0; i < _lights.Length; i++)
            {
                var light = _lights[i].Light;
                if (light == null) continue;

                var phase = i * 1.7f;
                var wander = Mathf.Sin(t * 0.091f + phase) * 0.6f + Mathf.Sin(t * 0.167f + phase * 2f) * 0.4f;

                light.intensity = _lights[i].Intensity * (1f + wander * depth);
            }
        }

        // ---------------------------------------------------------------- restore

        /// <summary>
        /// Takes off exactly what was put on. Safe to call more than once and safe to call when
        /// Begin never ran.
        /// </summary>
        internal void Restore()
        {
            CharacterDrift = Vector2.zero;
            _lens = null;

            _running = false;

            try
            {
                if (_camera != null)
                {
                    _camera.localPosition -= _appliedOffset;
                    _camera.localRotation = _camera.localRotation * Quaternion.Inverse(_appliedRotation);
                }
            }
            catch (Exception error) { WarnOnce(error); }

            _camera = null;
            _appliedOffset = Vector3.zero;
            _appliedRotation = Quaternion.identity;

            try
            {
                if (_lights != null)
                {
                    foreach (var lit in _lights)
                    {
                        if (lit.Light != null) lit.Light.intensity = lit.Intensity;
                    }
                }
            }
            catch (Exception error) { WarnOnce(error); }

            _lights = null;

            try
            {
                if (_shadowSet && _shadow != null) _shadow.SetActive(_shadowWas);
                GiveBackBootShadow();
                _playerView = null;
                _bootShadowNext = 0;
            }
            catch (Exception error) { WarnOnce(error); }

            _shadow = null;
            _shadowSet = false;

            try
            {
                // The poser goes away with the screen; a destroyed Unity object compares equal to
                // null through the Component cast, which is the check that matters here.
                var poser = _poser as Component;
                if (_patrolSet && GameTypes.MenuPoser_SetPatrol != null && poser != null)
                {
                    GameTypes.MenuPoser_SetPatrol.Invoke(poser, new object[] { false });
                }
            }
            catch (Exception error) { WarnOnce(error); }

            _patrolSet = false;
            _poser = null;

            try
            {
                if (_overlaySet && GameTypes.EnvironmentUI_EnableOverlay != null)
                {
                    if (GameTypes.EnvironmentUI_Instance != null)
                    {
                        var environmentUI = GameTypes.EnvironmentUI_Instance.GetValue(null, null);
                        if (environmentUI != null)
                            GameTypes.EnvironmentUI_EnableOverlay.Invoke(environmentUI, new object[] { false });
                    }
                }
            }
            catch (Exception error) { WarnOnce(error); }

            _overlaySet = false;
        }

        private void WarnOnce(Exception error)
        {
            if (_warnedOnce) return;
            _warnedOnce = true;

            DeployScreenPlugin.Log.LogWarning("[DeployScreen] scene depth failed: " + error);
        }
    }
}
