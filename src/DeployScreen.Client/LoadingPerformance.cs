using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine;

namespace DeployScreen.Client
{
    // Lives on the plugin, not the screen: the transition after the screen disappears must
    // still be measured. A GameWorld marker plus the next Update is an explicit proxy, not
    // a claim that player input is already enabled.
    internal sealed class LoadingPerformance : MonoBehaviour
    {
        private static LoadingPerformance _instance;
        private static bool _showHook, _statusHook, _startHook, _playerHook, _bannerHook;
        private string _folder, _runId, _metadata, _presentation;
        private string _easeMetadata;
        private object _screen, _banners;
        private LoadingScreenMode _mode;
        private LoadTrace _trace;
        private readonly Stopwatch _clock = new Stopwatch();
        private bool _active, _started, _warned;
        private bool _previewSkipped, _bannersSkipped;
        private double _closedAt = -1, _nextMemorySample;
        private long _managedStart, _managedPeak, _workingStart, _workingPeak;
        private int _gc0, _gc1, _gc2;
        private Process _process;
        private MinimalScreen _minimal;
        private SceneDepth _depth;
        private StagingArea _staging;
        private ScreenLayout _layout;
        private CountdownScreen _countdown;

        /// <summary>
        /// True between the deploy screen closing and the final countdown being done with. While
        /// it is set the staging area is owed a restore rather than given one -- see ScreenClosed.
        /// </summary>
        private bool _holding;
        private bool _fading;
        private double _fadeAt;
        private LoadEase _ease;

        /// <summary>
        /// The map and the session this raid, kept from the Show arguments. The staging area needs
        /// the Location to build intel from and the session to reach the profile's quest list --
        /// the same two things the banner panel's own Show is handed.
        /// </summary>
        private object _location, _session;
        private string _mapId;
        private MapGrade.Weather _weather;

        internal static LoadingScreenMode Mode
        {
            get { return _instance != null && _instance._active ? _instance._mode : DeployScreenPlugin.ScreenMode.Value; }
        }

        internal void Initialize(string folder)
        {
            _instance = this;
            _folder = Path.Combine(folder, "diagnostics");
            enabled = false;
        }

        internal static void Install(Harmony harmony)
        {
            _showHook = Patch(harmony, GameTypes.Loading_Show, nameof(BeforeShow), nameof(AfterShow));
            _statusHook = Patch(harmony, GameTypes.Loading_Status, nameof(Status));
            _startHook = Patch(harmony, GameTypes.World_Started, null, nameof(Started));
            Patch(harmony, GameTypes.Loading_Abort, nameof(Aborted));
            _playerHook = Patch(harmony, GameTypes.Loading_ShowPlayer, nameof(SkipPlayer));
            if (GameTypes.BannersPanel_Show != null && GameTypes.BannersPanel_Show.ReturnType == typeof(Task))
                _bannerHook = Patch(harmony, GameTypes.BannersPanel_Show, nameof(SkipBanners));
            DeployScreenPlugin.Log.LogInfo("[DeployScreen] performance hooks: screen=" + _showHook
                + " phases=" + _statusHook + " raid-start=" + _startHook
                + " skip-preview=" + _playerHook + " skip-banners=" + _bannerHook);
        }

        private static bool Patch(Harmony harmony, MethodInfo method, string before, string after = null)
        {
            if (method == null) return false;
            try
            {
                harmony.Patch(method,
                    prefix: before == null ? null : new HarmonyMethod(typeof(LoadingPerformance), before) { priority = Priority.First },
                    postfix: after == null ? null : new HarmonyMethod(typeof(LoadingPerformance), after),
                    finalizer: method == GameTypes.Loading_Show
                        ? new HarmonyMethod(typeof(LoadingPerformance), nameof(ShowFailed)) : null);
                return true;
            }
            catch (Exception e)
            {
                DeployScreenPlugin.Log.LogWarning("[DeployScreen] optional performance hook " + method.Name + " unavailable: " + e.Message);
                return false;
            }
        }

        private static void BeforeShow(object __instance, object[] __args)
        {
            try { if (_instance != null) _instance.Begin(__instance, __args); }
            catch (Exception e) { if (_instance != null) _instance.Fail(e); }
        }

        private static void AfterShow(object __instance)
        {
            try
            {
                if (_instance == null || !_instance._active || !ReferenceEquals(_instance._screen, __instance)) return;

                // Easing is about what the machine is doing, not about what the screen looks like,
                // so it applies in every mode -- Vanilla included, where it is the only thing this
                // mod is doing at all.
                _instance._ease = new LoadEase();
                _instance._ease.Begin(__instance as Component);
                _instance.Mark("ease: " + _instance._ease.Description);

                if (_instance._mode == LoadingScreenMode.Minimal)
                {
                    _instance._minimal = new MinimalScreen();
                    _instance._minimal.Begin(__instance as Component, _instance._banners,
                        _instance._previewSkipped, _instance._bannersSkipped);
                    _instance._presentation = _instance._minimal.Metadata;
                    _instance.Mark("minimal: " + _instance._minimal.Description);
                }
                else if (_instance._mode == LoadingScreenMode.Staging)
                {
                    // Order matters: the staging area hangs the art and hides the menu furniture,
                    // then depth drifts the camera across it. Depth alone on the stock scene is
                    // the Enhanced look; it is the two together that read as a place.
                    _instance._staging = new StagingArea();
                    _instance._staging.Begin(__instance as Component, _instance._mapId,
                        _instance._location, _instance._session, _instance._weather);
                    _instance._presentation = _instance._staging.Metadata;
                    _instance.Mark("staging: " + _instance._staging.Description);

                    // After the art is up and the panel is gone: the layout is arranged
                    // around what is left, and there is no point moving furniture for a
                    // staging area that failed to build.
                    if (DeployScreenPlugin.StagingRearrange.Value)
                    {
                        _instance._layout = new ScreenLayout();
                        _instance._layout.Apply(__instance as Component);
                    }

                    _instance._depth = new SceneDepth();
                    _instance._depth.Begin(__instance as Component);
                    if (_instance._depth.Running) _instance.Mark("depth: " + _instance._depth.Description);
                }
                else if (_instance._mode == LoadingScreenMode.Enhanced)
                {
                    // Depth rides this class's lifecycle on purpose: every way the deploy screen
                    // can end -- cancelled, raid started, timed out, Show threw, plugin destroyed
                    // -- already funnels through Restore(), so the effects cannot outlive it.
                    _instance._depth = new SceneDepth();
                    _instance._depth.Begin(__instance as Component);
                    if (_instance._depth.Running) _instance.Mark("depth: " + _instance._depth.Description);
                }
            }
            catch (Exception e) { if (_instance != null) _instance.Warn(e); }
        }

        private static void ShowFailed(Exception __exception)
        {
            // Observe failure without replacing or swallowing the game's exception.
            if (__exception != null && _instance != null) _instance.Finish("screen-show-failed");
        }

        private static void Status(object __instance, string __0)
        {
            if (_instance != null && _instance._trace != null && ReferenceEquals(_instance._screen, __instance))
                _instance._trace.SetPhase(_instance._clock.Elapsed.TotalSeconds, __0);
        }

        private static bool SkipPlayer(object __instance, ref Task __result)
        {
            if (_instance == null || !_instance._active || _instance._mode != LoadingScreenMode.Minimal
                || !ReferenceEquals(_instance._screen, __instance)) return true;
            _instance._previewSkipped = true;
            __result = Task.CompletedTask;
            return false;
        }

        private static bool SkipBanners(object __instance, ref Task __result)
        {
            if (_instance == null || !_instance._active || _instance._mode != LoadingScreenMode.Minimal
                || !ReferenceEquals(_instance._banners, __instance)) return true;
            _instance._bannersSkipped = true;
            __result = Task.CompletedTask;
            return false;
        }

        private static void Started()
        {
            if (_instance == null || !_instance._active) return;
            _instance._started = true;
            _instance.Mark("game-world-started");
        }

        private static void Aborted(object __instance)
        {
            if (_instance == null || !ReferenceEquals(_instance._screen, __instance)) return;

            // Back is pressed here, and the screen does not go away here. Restoring now is what
            // put the stock deploy screen on the display on the way out -- see HoldFade.
            if (_instance._active && _instance.FadeGroup() != null)
            {
                _instance._fading = true;
                _instance._fadeAt = _instance._clock.Elapsed.TotalSeconds;
                _instance.Mark("cancel-requested, holding the art while the screen goes");
                return;
            }

            _instance.Finish("cancel-requested");
        }

        /// <summary>
        /// The CanvasGroup the screen fades itself out with, which is how it closes.
        ///
        /// Worth writing down, because five sessions assumed otherwise. The screen is never
        /// deactivated: LoadingScreenLifetime.OnDisable sits on its GameObject and has never once
        /// fired, so loading-screen-disabled is absent from every log this mod has ever written.
        /// Reading the game confirms it -- EFT.UI.Screens.UIScreen closes through
        /// SoftHide(CanvasGroup, Action), which runs VisualExtensions.SoftChange on the group and
        /// fades its alpha. The object stays active and fully present the whole time; only its
        /// alpha moves. HideGameObject, the one path that would deactivate it, logs
        /// "Closing screen: {0}" and that string appears in no log here at all.
        ///
        /// So ScreenClosed was watching for an event that this screen does not raise, and the
        /// alpha is the signal that was there all along.
        /// </summary>
        private CanvasGroup FadeGroup()
        {
            var screen = _screen as Component;
            return screen == null ? null : screen.GetComponent<CanvasGroup>();
        }

        /// <summary>
        /// Keeps the art up until the screen has actually gone.
        ///
        /// The complaint was that pressing Back reverts to the default loading screen, and that is
        /// exactly what was happening, for a reason that reads backwards until the paragraph above
        /// is in hand. The abort fired, Finish restored in its finally, and the staging area came
        /// down at once -- while the screen itself was still on the display at full alpha, because
        /// nothing had closed it yet. Taking our own art off a screen that is still up does not
        /// leave nothing, it leaves the game's own deploy screen, which is what was seen.
        ///
        /// So the restore is owed rather than run, the same bargain HoldCountdown already makes,
        /// and settled the same way in Update. Three ways out and every one of them ends at
        /// Finish, which restores in its finally: the screen finishes fading, the fade never
        /// happens, or the raid starts anyway through Update's own first line.
        ///
        /// A second and a half caps it. SoftChange takes well under that, and art left sitting
        /// over a menu nobody asked to look at is a worse failure than a visible cut.
        /// </summary>
        private void HoldFade(double now)
        {
            var group = FadeGroup();

            if (group == null)
            {
                Mark("no canvas group to wait on, letting the art go");
                Finish("cancel-requested", false);
                return;
            }

            if (group.alpha <= 0.02f)
            {
                Mark("screen faded out");
                Finish("cancel-requested", false);
                return;
            }

            if (now - _fadeAt < 1.5) return;

            Mark("screen did not fade, letting the art go");
            Finish("cancel-requested", false);
        }

        internal static void ScreenClosed(object screen)
        {
            if (_instance == null || !_instance._active || !ReferenceEquals(_instance._screen, screen)) return;
            if (_instance._closedAt >= 0) return;
            _instance._closedAt = _instance._clock.Elapsed.TotalSeconds;
            _instance.Mark("loading-screen-disabled");

            // The deploy screen going dark is not the end of the wait. The final countdown comes
            // up right after it and owns the last several seconds, and tearing the art down here
            // is what made the picture vanish for the walk-out. So the restore is owed rather
            // than run, and Update settles it when the countdown is done -- or when the raid
            // starts, or when the thirty-second cap runs out, both of which reach Finish, which
            // restores in a finally. Nothing can outlive the screen by holding here.
            if (_instance.HoldForCountdown())
            {
                _instance._holding = true;
                _instance.Mark("holding the art for the countdown");
                return;
            }

            _instance.Restore();
        }

        /// <summary>
        /// Whether there is anything worth holding. Only the staging area hangs art, and only a
        /// staging area that actually built any has something the countdown can stand on.
        /// </summary>
        private bool HoldForCountdown()
        {
            return _mode == LoadingScreenMode.Staging
                && _staging != null && _staging.Built
                && DeployScreenPlugin.StagingHoldCountdown.Value;
        }

        /// <summary>
        /// The hold, one frame at a time.
        ///
        /// Three ways out, and every one of them gives the art back: the countdown finishes, the
        /// countdown never arrives, or the raid starts -- the last through Update's own first
        /// line, which reaches Finish, which restores in a finally.
        /// </summary>
        private void HoldCountdown(double now)
        {
            if (_countdown == null)
            {
                var root = CountdownRoot();

                if (root == null)
                {
                    // A second and a half is generous: on the run this was built from, the
                    // countdown was up on the frame after the deploy screen went dark. If it is
                    // not coming -- a game version that dropped it, a flow that skips it -- the
                    // art should not sit over a menu nobody asked to look at.
                    if (now - _closedAt >= 1.5)
                    {
                        Mark("no countdown screen, letting the art go");
                        Restore();
                    }

                    return;
                }

                var screen = new CountdownScreen();

                // The screen can be active a frame before its rect has a size. Arranging against
                // an empty rect would put everything in the corner of nothing, so it says so and
                // this comes back next frame.
                if (!screen.Apply(root, _screen as Component)) return;

                _countdown = screen;
                Mark("countdown: " + _countdown.Description);
                return;
            }

            if (!_countdown.Showing)
            {
                Mark("countdown-finished");
                Restore();
                return;
            }

            _countdown.Keep();
        }

        /// <summary>
        /// 'Matchmaker Final Countdown', a sibling of the deploy screen. Found by name under the
        /// same parent rather than patched: a Harmony hook would need a method name on a type
        /// that is only known by its GameObject, and this is one Find on one transform for a few
        /// seconds at the end of a load.
        /// </summary>
        private RectTransform CountdownRoot()
        {
            var screen = _screen as Component;
            if (screen == null) return null;

            var parent = screen.transform.parent;
            if (parent == null) return null;

            var found = parent.Find("Matchmaker Final Countdown") as RectTransform;
            if (found == null || !found.gameObject.activeInHierarchy) return null;

            return found;
        }

        private void Begin(object screen, object[] args)
        {
            if (_active) Finish("replaced-by-next-load");
            _mode = DeployScreenPlugin.ScreenMode.Value;
            _screen = screen;

            // The layout belongs to the screen, not to any one mode, and the feature being built
            // on top of it has to work whichever mode the player is in. Once per session.
            StagingArea.DumpScreen(screen as Component);
            _banners = GameTypes.Loading_Banners == null ? null : GameTypes.Loading_Banners.GetValue(screen);
            _started = false;
            _closedAt = -1;
            _previewSkipped = false;
            _bannersSkipped = false;
            _presentation = null;
            _easeMetadata = null;
            _holding = false;
            _fading = false;
            _fadeAt = 0;

            // Every raid gets its own warning budget. Kept for the session, the first failure
            // silences every later one, and reports that stop appearing leave no log line at all.
            _warned = false;

            _active = true;
            enabled = true;
            _clock.Restart();
            var component = screen as Component;
            if (component != null)
            {
                var lifetime = component.GetComponent<LoadingScreenLifetime>();
                if (lifetime == null) lifetime = component.gameObject.AddComponent<LoadingScreenLifetime>();
                lifetime.Screen = screen;
            }
            // Pulled out before the diagnostics gate: the staging area needs these whether or not
            // loading is being recorded, and Show's own arguments are the only place they appear.
            // MatchmakerTimeHasCome.Show(IEftSession, RaidSettings, MatchmakerPlayersController),
            // so the session is simply the argument that is not one of the other two.
            _location = null;
            _session = null;
            _weather = default(MapGrade.Weather);
            _mapId = null;
            foreach (var arg in args)
            {
                if (arg == null) continue;
                if (GameTypes.RaidSettings != null && GameTypes.RaidSettings.IsInstanceOfType(arg))
                {
                    _location = GameTypes.RaidSettings_SelectedLocation?.GetValue(arg, null);
                    if (_location != null) _mapId = GameTypes.Location_Id?.GetValue(_location) as string;

                    // Time of day and weather are settled before deploy, so the staging area can
                    // be lit for this raid rather than for the map in general.
                    _weather = MapGrade.ReadWeather(arg);
                    continue;
                }
                if (_session == null && !(arg is Component)) _session = arg;
            }

            if (!DeployScreenPlugin.RecordLoading.Value) return;

            var map = _mapId ?? "unknown";
            _runId = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            _trace = new LoadTrace(Application.isFocused);
            _gc0 = GC.CollectionCount(0); _gc1 = GC.CollectionCount(1); _gc2 = GC.CollectionCount(2);
            _managedStart = _managedPeak = GC.GetTotalMemory(false);
            _workingStart = _workingPeak = 0;
            try { _process = Process.GetCurrentProcess(); _workingStart = _workingPeak = _process.WorkingSet64; } catch { }
            _nextMemorySample = 5;
            _metadata = "\"schemaVersion\":1,\"version\":" + LoadTrace.Quote(DeployScreenPlugin.PluginVersion)
                + ",\"runId\":" + LoadTrace.Quote(_runId) + ",\"mode\":" + LoadTrace.Quote(_mode.ToString())
                + ",\"map\":" + LoadTrace.Quote(map) + ",\"label\":" + LoadTrace.Quote(DeployScreenPlugin.TestLabel.Value)
                + ",\"resolution\":" + LoadTrace.Quote(Screen.width + "x" + Screen.height)
                + ",\"phaseHook\":" + Bool(_statusHook) + ",\"raidStartHook\":" + Bool(_startHook)
                + ",\"previewSkipHook\":" + Bool(_playerHook) + ",\"bannerSkipHook\":" + Bool(_bannerHook)
                + ",\"configuredCustomArt\":" + Bool(DeployScreenPlugin.BannersEnabled.Value)
                + ",\"configuredMotion\":" + Bool(DeployScreenPlugin.MotionEnabled.Value)
                + ",\"configuredCaptions\":" + LoadTrace.Quote(DeployScreenPlugin.BannerCaptions.Value.ToString())
                + ",\"configuredBackdrop\":" + Bool(DeployScreenPlugin.MatchEnvironment.Value);
        }

        private static string Bool(bool value) { return value ? "true" : "false"; }
        private void Mark(string name) { _trace?.Event(_clock.Elapsed.TotalSeconds, name); }

        /// <summary>
        /// What minimal mode actually applied, for the report.
        ///
        /// The skip prefixes run inside Show; MinimalScreen is built in the postfix. So a Show
        /// that throws, or a screen that is not a Component, leaves no MinimalScreen even though
        /// the preview and banners were skipped. Reporting the installed hooks instead would let
        /// such a capture group with a complete one in compare-loading.ps1, which groups on
        /// exactly these fields.
        /// </summary>
        private string Presentation
        {
            get
            {
                if (_mode == LoadingScreenMode.Staging)
                {
                    if (_staging != null) return _staging.Metadata;
                    return _presentation ?? StagingArea.MetadataFor(false, 0, false, false, "unknown");
                }

                if (_minimal != null) return _minimal.Metadata;
                return _presentation ?? MinimalScreen.MetadataFor(_previewSkipped, _bannersSkipped, false, 0);
            }
        }

        /// <summary>What the load easing actually applied, for the report.</summary>
        private string Easing
        {
            get
            {
                if (_ease != null) return _ease.Metadata;
                return _easeMetadata ?? LoadEase.MetadataFor();
            }
        }

        private void Update()
        {
            if (!_active) return;
            try
            {
                var now = _clock.Elapsed.TotalSeconds;
                _trace?.Frame(now, Application.isFocused);
                if (_started) { Finish("first-update-after-game-started", false); return; }
                _depth?.Tick(now);
                _staging?.Tick(now);
                _layout?.Keep();

                // After Keep, not inside it: the character is walked with the scene rather than
                // held where he was put, and the drift is SceneDepth's number.
                _layout?.DriftCharacter(_depth == null ? Vector2.zero : _depth.CharacterDrift);
                StagingArea.WatchForCountdown(_screen as Component, now);
                StagingArea.WatchForPreview(_screen as Component, now);
                if (_holding) HoldCountdown(now);
                if (_fading) HoldFade(now);
                if (_closedAt >= 0 && now - _closedAt >= 30) { Finish("screen-closed-without-confirmed-start", false); return; }
                if (now >= 1800) { Finish("capture-timeout", false); return; }
                _minimal?.Tick(now);
                if (_trace != null && now >= _nextMemorySample)
                {
                    _nextMemorySample = now + 5;
                    SampleMemory();
                }
            }
            catch (Exception e) { Fail(e); }
        }

        private void SampleMemory()
        {
            _managedPeak = Math.Max(_managedPeak, GC.GetTotalMemory(false));
            try
            {
                if (_process == null) return;
                _process.Refresh();
                _workingPeak = Math.Max(_workingPeak, _process.WorkingSet64);
            }
            catch { } // Memory counters are optional, never a reason to fail a raid.
        }

        private void Finish(string outcome, bool finalFrame = true)
        {
            if (!_active) return;
            _active = false;
            enabled = false;
            var trace = _trace;
            _trace = null;
            try
            {
                if (trace != null)
                {
                    if (finalFrame) trace.Frame(_clock.Elapsed.TotalSeconds, Application.isFocused);
                    SampleMemory();
                    var metadata = _metadata + Presentation + Easing
                        + ",\"outcome\":" + LoadTrace.Quote(outcome)
                        + ",\"managedStartBytes\":" + _managedStart + ",\"managedSampledPeakBytes\":" + _managedPeak
                        + ",\"workingSetStartBytes\":" + _workingStart + ",\"workingSetSampledPeakBytes\":" + _workingPeak
                        + ",\"gcCollections\":[" + (GC.CollectionCount(0) - _gc0) + "," + (GC.CollectionCount(1) - _gc1)
                        + "," + (GC.CollectionCount(2) - _gc2) + "]";
                    var folder = _folder;
                    var id = _runId;
                    // Trace is detached before handing it to the writer. No Unity access here.
                    Task.Run(() =>
                    {
                        try
                        {
                            Directory.CreateDirectory(folder);
                            var path = Path.Combine(folder, id + ".json");
                            File.WriteAllText(path, trace.Json(metadata), new UTF8Encoding(false));
                            DeployScreenPlugin.Log.LogInfo("[DeployScreen] loading report (" + outcome + "): " + path);
                        }
                        catch (Exception e) { DeployScreenPlugin.Log.LogWarning("[DeployScreen] loading report could not be saved: " + e.Message); }
                    });
                }
            }
            catch (Exception e) { Warn(e); }
            finally
            {
                Restore();
                _screen = null; _banners = null;
                _process?.Dispose(); _process = null;
                _clock.Stop();
            }
        }

        private void Restore()
        {
            try
            {
                if (_minimal != null) _presentation = _minimal.Metadata;
                _minimal?.Restore();
            }
            catch (Exception e) { Warn(e); }
            _minimal = null;

            try { _depth?.Restore(); }
            catch (Exception e) { Warn(e); }
            _depth = null;

            // Before both of the below, for the same reason the layout goes before the staging
            // area: this screen was arranged around the art, so it goes back to its own shape
            // while the art it was arranged around is still there.
            _holding = false;
            _fading = false;
            try { _countdown?.Restore(); }
            catch (Exception e) { Warn(e); }
            _countdown = null;

            // Before the staging area: the layout is arranged around the art, so the screen
            // goes back to its own shape before the art it was arranged around disappears.
            try { _layout?.Restore(); }
            catch (Exception e) { Warn(e); }
            _layout = null;

            // After depth, so the camera is back at rest before the planes hung off it go away.
            if (_staging != null) _presentation = _staging.Metadata;
            try { _staging?.Restore(); }
            catch (Exception e) { Warn(e); }
            _staging = null;

            // Last, and always: a frame-rate cap or a loading priority left behind would follow
            // the player out of the menu and into everything else they do.
            if (_ease != null) _easeMetadata = _ease.Metadata;
            try { _ease?.Restore(); }
            catch (Exception e) { Warn(e); }
            _ease = null;

            _location = null;
            _session = null;

            // Putting the backdrop back is itself a scene load. Doing that the instant the raid
            // starts would drop it straight onto the frames the map load needs, for a menu nobody
            // is looking at -- so when the raid did start the restore is owed rather than run, and
            // settles when the menu next shows its environment.
            try
            {
                if (_started) EnvironmentState.RestoreWhenMenuReturns();
                else EnvironmentState.Restore();
            }
            catch (Exception e) { Warn(e); }
        }

        private void Fail(Exception e) { Warn(e); Finish("diagnostic-error"); }
        private void OnApplicationFocus(bool focused)
        {
            _trace?.FocusChanged(_clock.Elapsed.TotalSeconds, focused);
        }
        private void Warn(Exception e)
        {
            if (_warned) return;
            _warned = true;
            DeployScreenPlugin.Log.LogWarning("[DeployScreen] loading performance feature failed: " + e.Message);
        }
        private void OnDestroy()
        {
            Finish("plugin-destroyed");
            if (_instance == this) _instance = null;
        }
    }

    internal sealed class LoadingScreenLifetime : MonoBehaviour
    {
        internal object Screen;
        private void OnDisable() { LoadingPerformance.ScreenClosed(Screen); }
    }
}
