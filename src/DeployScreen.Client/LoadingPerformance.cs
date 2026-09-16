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
        private object _screen, _banners;
        private LoadingScreenMode _mode;
        private LoadTrace _trace;
        private readonly Stopwatch _clock = new Stopwatch();
        private bool _active, _started, _warned;
        private double _closedAt = -1, _nextMemorySample;
        private long _managedStart, _managedPeak, _workingStart, _workingPeak;
        private int _gc0, _gc1, _gc2;
        private Process _process;
        private MinimalScreen _minimal;

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
                if (_instance._mode == LoadingScreenMode.Minimal)
                {
                    _instance._minimal = new MinimalScreen();
                    _instance._minimal.Begin(__instance as Component, _instance._banners,
                        _playerHook, _bannerHook);
                    _instance._presentation = _instance._minimal.Metadata;
                    _instance.Mark("minimal: " + _instance._minimal.Description);
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
            __result = Task.CompletedTask;
            return false;
        }

        private static bool SkipBanners(object __instance, ref Task __result)
        {
            if (_instance == null || !_instance._active || _instance._mode != LoadingScreenMode.Minimal
                || !ReferenceEquals(_instance._banners, __instance)) return true;
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
            _instance.Finish("cancel-requested");
        }

        internal static void ScreenClosed(object screen)
        {
            if (_instance == null || !_instance._active || !ReferenceEquals(_instance._screen, screen)) return;
            if (_instance._closedAt >= 0) return;
            _instance._closedAt = _instance._clock.Elapsed.TotalSeconds;
            _instance.Mark("loading-screen-disabled");
            _instance.Restore();
        }

        private void Begin(object screen, object[] args)
        {
            if (_active) Finish("replaced-by-next-load");
            _mode = DeployScreenPlugin.ScreenMode.Value;
            _screen = screen;
            _banners = GameTypes.Loading_Banners == null ? null : GameTypes.Loading_Banners.GetValue(screen);
            _started = false;
            _closedAt = -1;
            _presentation = new MinimalScreen().Metadata;
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
            if (!DeployScreenPlugin.RecordLoading.Value) return;

            var map = "unknown";
            foreach (var arg in args)
            {
                if (arg == null || GameTypes.RaidSettings == null || !GameTypes.RaidSettings.IsInstanceOfType(arg)) continue;
                var location = GameTypes.RaidSettings_SelectedLocation?.GetValue(arg, null);
                if (location != null) map = GameTypes.Location_Id?.GetValue(location) as string ?? map;
            }
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

        private void Update()
        {
            if (!_active) return;
            try
            {
                var now = _clock.Elapsed.TotalSeconds;
                _trace?.Frame(now, Application.isFocused);
                if (_started) { Finish("first-update-after-game-started", false); return; }
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
                    var metadata = _metadata + (_minimal == null ? _presentation : _minimal.Metadata)
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
