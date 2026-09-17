using System;
using UnityEngine;

namespace DeployScreen.Client
{
    /// <summary>
    /// Tries to leave the raid load more of the machine while the deploy screen is up.
    ///
    /// What the hitching on that screen actually is
    /// -------------------------------------------
    /// It is not the raid world being rendered. The raid world does not exist yet -- it is being
    /// built. What renders is the *menu*: the EnvironmentUIRoot scene, the PMC preview, and the
    /// canvas. Meanwhile the raid loads through
    /// `AssetsManager.LoadSceneOperation` / `LoadScenesFromPresetOperation` and `Streamer`, all of
    /// which call `SceneManager.LoadSceneAsync`.
    ///
    /// Unity's async scene load is only asynchronous in its reading. **Integration -- instantiating
    /// the objects, uploading the textures, warming the shaders -- happens on the main thread, in
    /// slices, every frame.** That is the hitching. No mod can move that work off the main thread,
    /// because Unity does not offer that.
    ///
    /// What the game already does, and we must not fight
    /// -------------------------------------------------
    /// EFT is not naive here. `LoadScenesFromPresetOperation` reads `asyncUploadTimeSlice` and
    /// `asyncUploadBufferSize`, **doubles both** for the duration of the load and restores them
    /// afterwards; `Streamer` drops `backgroundLoadingPriority` while it streams chunks. Blindly
    /// overriding any of that would be arguing with tuning done by people who could profile it.
    ///
    /// So this does not touch the loader. It reduces what the loader is *competing with*, and it
    /// stops this mod from adding a stall of its own -- everything here is off or conservative by
    /// default, captured before it is changed, and put back on every exit path.
    ///
    /// **None of it is measured.** The honest way to use this is one lever at a time with
    /// `Record loading` on, comparing reports. The settings are written into every report for
    /// exactly that reason.
    /// </summary>
    internal sealed class LoadEase
    {
        /// <summary>
        /// The slowest cap worth allowing. LoadTrace counts a stall at 100 ms, so a cap below
        /// 10 fps would make every ordinary frame look like a hitch in this mod's own reports --
        /// the setting would then flatter itself in the measurement meant to judge it.
        /// </summary>
        private const int MinimumCap = 10;

        private int _frameRateWas;
        private int _appliedCap;
        private bool _frameRateChanged;

        private ThreadPriority _priorityWas;
        private bool _priorityChanged;

        private Behaviour _poser;
        private bool _poserPaused;

        private bool _warnedOnce;

        internal string Description
        {
            get
            {
                return "fps-cap=" + (_frameRateChanged ? _appliedCap.ToString() : "off")
                    + "; load-priority=" + (_priorityChanged ? DeployScreenPlugin.EasePriority.Value.ToString() : "unchanged")
                    + "; character-ik-paused=" + _poserPaused;
            }
        }

        /// <summary>What was applied, for the report. Comparisons are meaningless without it.</summary>
        internal string Metadata
        {
            get
            {
                return ",\"easeFrameRateCap\":" + (_frameRateChanged ? _appliedCap : 0)
                    + ",\"easeLoadPriority\":" + LoadTrace.Quote(_priorityChanged
                        ? DeployScreenPlugin.EasePriority.Value.ToString() : "unchanged")
                    + ",\"easeCharacterIkPaused\":" + (_poserPaused ? "true" : "false")
                    + ",\"easePrewarmArt\":" + (DeployScreenPlugin.EasePrewarmArt.Value ? "true" : "false");
            }
        }

        internal static string MetadataFor()
        {
            return ",\"easeFrameRateCap\":0,\"easeLoadPriority\":\"unchanged\""
                + ",\"easeCharacterIkPaused\":false,\"easePrewarmArt\":false";
        }

        // ------------------------------------------------------------------ begin

        internal void Begin(Component screen)
        {
            try
            {
                CapFrameRate();
                RaisePriority();
                PauseCharacterIk(screen);
            }
            catch (Exception error)
            {
                WarnOnce(error);
                Restore();
            }
        }

        /// <summary>
        /// A frame the menu does not draw is a frame the loader gets more of.
        ///
        /// Deliberately a cap and not a floor: capping *below* 10 fps would start to look like
        /// stalling to the frame-gap recorder, whose threshold is 100 ms. At 30 or 60 the
        /// intervals stay well under that, so a capped run and an uncapped one are still
        /// comparable on gap count -- which matters, because otherwise this setting would flatter
        /// itself in its own measurements.
        /// </summary>
        private void CapFrameRate()
        {
            var cap = DeployScreenPlugin.EaseFrameRate.Value;
            if (cap <= 0) return;

            if (cap < MinimumCap)
            {
                DeployScreenPlugin.Log.LogWarning(
                    "[DeployScreen] a frame rate cap of " + cap + " would be slower than the "
                    + MinimumCap + " fps this mod counts a stall at, so its own reports would call "
                    + "it hitching. Using " + MinimumCap + ".");
                cap = MinimumCap;
            }

            _frameRateWas = Application.targetFrameRate;
            Application.targetFrameRate = cap;
            _frameRateChanged = true;
            _appliedCap = cap;
        }

        /// <summary>
        /// Unity's documented trade between frame rate and load speed: a higher
        /// backgroundLoadingPriority gives asset integration more milliseconds per frame.
        ///
        /// Off by default and worth being clear about why -- it makes each frame do *more* loading,
        /// so individual frames get longer even as the load finishes sooner. Whether that reads as
        /// smoother or worse is a judgement about your machine that only a measured comparison can
        /// settle. The game itself lowers this while streaming chunks in a raid, which is the
        /// opposite trade for the opposite reason.
        /// </summary>
        private void RaisePriority()
        {
            var wanted = DeployScreenPlugin.EasePriority.Value;
            if (wanted == LoadPriority.Unchanged) return;

            _priorityWas = Application.backgroundLoadingPriority;

            Application.backgroundLoadingPriority =
                wanted == LoadPriority.High ? ThreadPriority.High
                : wanted == LoadPriority.Normal ? ThreadPriority.Normal
                : ThreadPriority.BelowNormal;

            _priorityChanged = true;
        }

        /// <summary>
        /// Stops the character's inverse kinematics without stopping the character.
        ///
        /// MenuPlayerPoser runs FinalIK LimbIK solvers, twist relaxers and two hand posers in its
        /// LateUpdate, every frame, for one model. Disabling the component stops that; the model
        /// stays on screen and its Animator keeps playing, so the PMC still moves -- it just stops
        /// solving IK while the machine is busy.
        ///
        /// Off by default: it is the one thing here that changes what you see, and only a
        /// measurement can say whether it buys anything.
        /// </summary>
        private void PauseCharacterIk(Component screen)
        {
            if (!DeployScreenPlugin.EasePauseIk.Value) return;
            if (screen == null || GameTypes.Loading_PlayerModel == null || GameTypes.PlayerModelView_Poser == null) return;

            var view = GameTypes.Loading_PlayerModel.GetValue(screen);
            if (view == null) return;

            var poser = GameTypes.PlayerModelView_Poser.GetValue(view, null) as Behaviour;
            if (poser == null || !poser.enabled) return;

            poser.enabled = false;
            _poser = poser;
            _poserPaused = true;
        }

        // ---------------------------------------------------------------- restore

        internal void Restore()
        {
            try
            {
                if (_frameRateChanged) Application.targetFrameRate = _frameRateWas;
            }
            catch (Exception error) { WarnOnce(error); }
            _frameRateChanged = false;
            _appliedCap = 0;

            try
            {
                if (_priorityChanged) Application.backgroundLoadingPriority = _priorityWas;
            }
            catch (Exception error) { WarnOnce(error); }
            _priorityChanged = false;

            try
            {
                // The poser is destroyed with the screen; a destroyed Unity object compares null.
                if (_poserPaused && _poser != null) _poser.enabled = true;
            }
            catch (Exception error) { WarnOnce(error); }

            _poser = null;
            _poserPaused = false;
        }

        private void WarnOnce(Exception error)
        {
            if (_warnedOnce) return;
            _warnedOnce = true;

            DeployScreenPlugin.Log.LogWarning("[DeployScreen] load easing failed: " + error.Message);
        }
    }
}
