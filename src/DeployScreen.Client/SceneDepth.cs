using System;
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

        private Lit[] _lights;

        private GameObject _shadow;
        private bool _shadowTurnedOn;

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
                    + "; ground-shadow=" + _shadowTurnedOn
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

                _running = _camera != null || _lights != null || _shadowTurnedOn || _patrolSet || _overlaySet;

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
            if (screen == null) return;
            if (GameTypes.Loading_PlayerModel == null || GameTypes.PlayerModelView_Poser == null) return;

            try
            {
                var view = GameTypes.Loading_PlayerModel.GetValue(screen);
                if (view == null) return;

                var poser = GameTypes.PlayerModelView_Poser.GetValue(view, null);
                if (poser == null) return;

                _poser = poser;

                if (DeployScreenPlugin.DepthGroundShadow.Value && GameTypes.MenuPoser_BottomShadow != null)
                {
                    _shadow = GameTypes.MenuPoser_BottomShadow.GetValue(poser) as GameObject;

                    if (_shadow != null && !_shadow.activeSelf)
                    {
                        _shadow.SetActive(true);
                        _shadowTurnedOn = true;
                    }
                }

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
            }
            catch (Exception error)
            {
                WarnOnce(error);
                Restore();
            }
        }

        /// <summary>
        /// Layered sines at frequencies that do not share a period, so the motion never settles
        /// into a visible loop. Slow on purpose: this should read as the room breathing, not as a
        /// camera move.
        /// </summary>
        private void DriveCamera(float t)
        {
            if (_camera == null) return;

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
                if (_shadowTurnedOn && _shadow != null) _shadow.SetActive(false);
            }
            catch (Exception error) { WarnOnce(error); }

            _shadow = null;
            _shadowTurnedOn = false;

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
