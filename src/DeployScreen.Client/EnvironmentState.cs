using System;
using System.Reflection;
using HarmonyLib;

namespace DeployScreen.Client
{
    /// <summary>
    /// Owns every change this mod makes to which backdrop is live, and guarantees it can be put
    /// back. Nothing else in this plugin may call SetEnvironmentAsync.
    ///
    /// Why this exists at all
    /// ----------------------
    /// The player's backdrop is a real game setting:
    /// GameSettingsGroup.EnvironmentUiType, a Bsg.GameSettings.GameSetting&lt;EEnvironmentUIType&gt;.
    /// EnvironmentUI.Awake() binds to it -- its IL reads
    /// SettingsManager.Instance.Game.Settings.EnvironmentUiType and calls
    /// CompositeDisposable.BindState(setting, CG_Awake), and CG_Awake calls SetEnvironmentAsync.
    ///
    /// So the setting drives the scene, one way. SetEnvironmentAsync writes
    /// _currentEnvironmentUiType and loads the scene; it does **not** write the setting back. A
    /// mod that calls it directly therefore leaves the setting saying one thing and the screen
    /// showing another, and since the binding only fires when the *setting* changes, nothing ever
    /// corrects it. That override then outlives the deploy screen -- which is exactly why the
    /// main menu came back wearing the wrong backdrop.
    ///
    /// The fix is not to write the setting (that would change what the player picked, on disk).
    /// It is to capture _currentEnvironmentUiType before touching anything and put that value
    /// back afterwards.
    ///
    /// Restoring is itself a scene load, so when the raid has actually started the restore is
    /// deferred rather than run: reloading a menu scene at the moment the map is loading is the
    /// worst possible time for it. The deferred restore is applied the next time the game makes
    /// the environment visible, which is the menu coming back.
    /// </summary>
    internal static class EnvironmentState
    {
        /// <summary>The backdrop that was live before this mod changed anything.</summary>
        private static int _original;

        private static bool _captured;

        /// <summary>Whether the live backdrop is currently one we asked for.</summary>
        private static bool _changed;

        /// <summary>
        /// The value we last asked for. If the live backdrop is no longer this, something else
        /// has taken over -- the player changing the setting in the options menu, which fires
        /// EnvironmentUI's binding, is the realistic case -- and restoring our captured value
        /// would undo their choice. So that is the moment to let go rather than to insist.
        /// </summary>
        private static int _applied = -1;

        /// <summary>A restore that was owed but deliberately not run yet -- see the class note.</summary>
        private static bool _pending;

        /// <summary>Set while we are inside our own SetEnvironmentAsync, so the hook cannot recurse.</summary>
        private static bool _applying;

        private static bool _warnedOnce;

        /// <summary>Whether this mod is currently holding the backdrop away from the player's choice.</summary>
        internal static bool Overridden { get { return _changed || _pending; } }

        internal static void Install(Harmony harmony)
        {
            if (GameTypes.EnvironmentUI_ShowEnvironment == null) return;

            // The menu bringing its environment back is exactly when a deferred restore is safe
            // and wanted. Cheap: it reads one bool and returns.
            harmony.Patch(
                GameTypes.EnvironmentUI_ShowEnvironment,
                postfix: new HarmonyMethod(AccessTools.Method(typeof(EnvironmentState), nameof(AfterShowEnvironment))));
        }

        private static bool _menuShown;

        /// <summary>
        /// Whether the menu has brought its environment up since the watch was armed.
        ///
        /// This is the end of the wait nobody had a name for. After the deploy screen closes there
        /// is a stretch where the raid is being torn down and the menu rebuilt -- quests
        /// re-requested, tabs re-added, the environment re-shown -- and the player called it a
        /// waiting room, which is exactly what it looks like. Finishing before it meant handing
        /// over to that instead of to the menu.
        ///
        /// ShowEnvironment(true) is the game saying the menu's own backdrop is up, and it is
        /// already patched here for the deferred restore, so this costs one bool.
        /// </summary>
        internal static bool MenuShown { get { return _menuShown; } }

        /// <summary>Starts the watch. Armed when the art begins waiting, not before.</summary>
        internal static void WatchForMenu() { _menuShown = false; }

        private static void AfterShowEnvironment(bool __0)
        {
            if (__0) _menuShown = true;

            if (!__0 || !_pending || _applying) return;

            _pending = false;
            Restore();
        }

        /// <summary>
        /// Notes the backdrop that is live now, if it has not been noted already. Called before
        /// anything is changed, and harmless to call repeatedly.
        /// </summary>
        internal static bool Capture()
        {
            if (_captured) return true;
            if (!GameTypes.EnvironmentRestoreReady) return false;

            var instance = Instance();
            if (instance == null) return false;

            try
            {
                var value = GameTypes.EnvironmentUI_CurrentEnvType.GetValue(instance);
                if (value == null) return false;

                _original = Convert.ToInt32(value);
                _captured = true;
                return true;
            }
            catch (Exception error)
            {
                WarnOnce(error);
                return false;
            }
        }

        /// <summary>
        /// Switches the live backdrop, remembering what to put back. Refuses outright if the
        /// current value could not be captured: a change that cannot be undone is the bug.
        /// </summary>
        internal static bool Apply(int environmentType)
        {
            if (!GameTypes.EnvironmentRestoreReady) return false;
            if (!Capture()) return false;
            if (Current() == environmentType)
            {
                // Already showing it. Still counts as ours if we are the reason.
                return true;
            }

            return Set(environmentType, "override");
        }

        /// <summary>
        /// Puts the player's backdrop back, if we moved it. Safe to call from anywhere, any
        /// number of times, including when nothing was ever changed.
        /// </summary>
        internal static void Restore()
        {
            if (!_changed) { _pending = false; return; }
            if (!_captured) { _changed = false; _pending = false; return; }

            var live = Current();

            if (live == _original)
            {
                // Already back where it started -- the menu was rebuilt, or the player set it
                // there themselves. Nothing to do.
                _changed = false;
                _pending = false;
                _applied = -1;
                return;
            }

            if (live != _applied)
            {
                // The backdrop moved to something that is neither ours nor theirs, so someone
                // else is driving it now. Forcing our captured value back would overwrite a
                // choice made after ours.
                DeployScreenPlugin.Log.LogInfo(
                    "[DeployScreen] the backdrop changed under us -- leaving it alone");
                _changed = false;
                _pending = false;
                _applied = -1;
                return;
            }

            if (Set(_original, "restore"))
            {
                _changed = false;
                _pending = false;
            }
        }

        /// <summary>
        /// Owes a restore without running one. Used when the raid has started: the scene load a
        /// restore costs would land on the frame budget the raid needs, and the menu backdrop is
        /// not visible anyway. AfterShowEnvironment settles it when the menu returns.
        /// </summary>
        internal static void RestoreWhenMenuReturns()
        {
            if (!_changed) return;
            _pending = true;
        }

        /// <summary>The backdrop that is live right now, or -1 if it cannot be read.</summary>
        internal static int Current()
        {
            if (!GameTypes.EnvironmentRestoreReady) return -1;

            var instance = Instance();
            if (instance == null) return -1;

            try
            {
                var value = GameTypes.EnvironmentUI_CurrentEnvType.GetValue(instance);
                return value == null ? -1 : Convert.ToInt32(value);
            }
            catch
            {
                return -1;
            }
        }

        private static bool Set(int environmentType, string why)
        {
            var instance = Instance();
            if (instance == null) return false;

            try
            {
                _applying = true;

                var typed = Enum.ToObject(GameTypes.EEnvironmentUIType, environmentType);
                var task = GameTypes.EnvironmentUI_SetEnvironmentAsync.Invoke(instance, new[] { typed });

                Observe(task);

                _changed = why == "override";
                _applied = _changed ? environmentType : -1;

                DeployScreenPlugin.Log.LogInfo(
                    "[DeployScreen] backdrop " + why + " -> " + typed
                    + (why == "restore" ? " (the player's own choice)" : ""));

                return true;
            }
            catch (Exception error)
            {
                WarnOnce(error);
                return false;
            }
            finally
            {
                _applying = false;
            }
        }

        /// <summary>
        /// A faulted scene load must not surface as an unhandled task exception, and must not
        /// leave us believing we own a backdrop we failed to set.
        /// </summary>
        private static System.Threading.Tasks.Task _settling;

        /// <summary>
        /// Whether the backdrop is mid-swap.
        ///
        /// Putting the player's own backdrop back is a scene load, and a scene load takes as long
        /// as it takes. Nothing used to wait on it: the art faded out, the menu behind it was
        /// still the map's, and the swap then happened in full view -- the player's background
        /// arriving, hanging, and the main menu turning up after it. Three things where there
        /// should have been one.
        ///
        /// So the dissolve waits on this. The swap is started while the art is still solid and
        /// hiding everything, and the art only begins to thin once the menu underneath is the one
        /// the player is going back to.
        /// </summary>
        internal static bool Settling
        {
            get { return _settling != null && !_settling.IsCompleted; }
        }

        private static void Observe(object task)
        {
            _settling = task as System.Threading.Tasks.Task;

            var asTask = task as System.Threading.Tasks.Task;
            if (asTask == null) return;

            asTask.ContinueWith(finished =>
            {
                if (finished.Exception == null) return;

                WarnOnce(finished.Exception);
                _changed = false;
                _pending = false;
                _applied = -1;
            });
        }

        private static object Instance()
        {
            try
            {
                if (GameTypes.EnvironmentUI_Instantiated != null)
                {
                    var live = GameTypes.EnvironmentUI_Instantiated.GetValue(null, null) as bool?;
                    if (live != true) return null;
                }

                return GameTypes.EnvironmentUI_Instance == null
                    ? null
                    : GameTypes.EnvironmentUI_Instance.GetValue(null, null);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// The environments the player can actually be shown: the ones the build ships
        /// (EnvironmentUI._environments) intersected with the ones they have unlocked
        /// (CustomizationSolver.GetAvailableEnvironmentUIs, which is the list the game's own
        /// GetRandomEnvironment draws from).
        ///
        /// Fail-open on both counts. "Unreadable" is not "absent", and refusing to show a
        /// backdrop we simply could not verify would be worse than showing it.
        /// </summary>
        internal static bool IsAvailable(int environmentType)
        {
            return InShippedScenes(environmentType) && InPlayerCustomizations(environmentType);
        }

        private static bool InShippedScenes(int environmentType)
        {
            if (GameTypes.EnvironmentUI_Environments == null || GameTypes.EnvironmentData_Type == null)
                return true;

            try
            {
                var instance = Instance();
                if (instance == null) return true;

                var environments =
                    GameTypes.EnvironmentUI_Environments.GetValue(instance) as System.Collections.IEnumerable;
                if (environments == null) return true;

                foreach (var environment in environments)
                {
                    if (environment == null) continue;

                    var type = GameTypes.EnvironmentData_Type.GetValue(environment);
                    if (type != null && Convert.ToInt32(type) == environmentType) return true;
                }

                return false;
            }
            catch
            {
                return true;
            }
        }

        private static bool InPlayerCustomizations(int environmentType)
        {
            if (GameTypes.CustomizationSolver_Instance == null
                || GameTypes.CustomizationSolver_GetAvailable == null
                || GameTypes.CustomizationEnvironment_Type == null)
            {
                return true;
            }

            try
            {
                if (GameTypes.CustomizationSolver_Instantiated != null)
                {
                    var live = GameTypes.CustomizationSolver_Instantiated.GetValue(null, null) as bool?;
                    if (live != true) return true;
                }

                var solver = GameTypes.CustomizationSolver_Instance.GetValue(null, null);
                if (solver == null) return true;

                var parameters = GameTypes.CustomizationSolver_GetAvailable.GetParameters();
                if (parameters.Length != 1) return true;

                // EPlayerSide. The game passes 2 (Bear) here; the environment list is not
                // side-specific in practice, but the parameter is required.
                var side = Enum.ToObject(parameters[0].ParameterType, 2);

                var available =
                    GameTypes.CustomizationSolver_GetAvailable.Invoke(solver, new[] { side })
                        as System.Collections.IEnumerable;

                if (available == null) return true;

                var any = false;
                foreach (var entry in available)
                {
                    if (entry == null) continue;
                    any = true;

                    var type = GameTypes.CustomizationEnvironment_Type.GetValue(entry);
                    if (type != null && Convert.ToInt32(type) == environmentType) return true;
                }

                // An empty list is far more likely to mean "could not be read" than "the player
                // owns no backdrops at all", so it must not veto.
                return !any;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>Forgets everything, for a session where the menu was rebuilt under us.</summary>
        internal static void Forget()
        {
            _captured = false;
            _changed = false;
            _pending = false;
            _applied = -1;
        }

        private static void WarnOnce(Exception error)
        {
            if (_warnedOnce) return;
            _warnedOnce = true;

            DeployScreenPlugin.Log.LogWarning("[DeployScreen] backdrop state handling failed: " + error);
        }
    }
}
