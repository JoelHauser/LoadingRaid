namespace DeployScreen.Client
{
    public static class PerformanceChecks
    {
        private static void Require(bool value, string name)
        {
            if (!value) throw new System.Exception(name);
        }

        public static string[] Run()
        {
            var passed = new System.Collections.Generic.List<string>();
            var trace = new LoadTrace(true);
            trace.Frame(0.02, true);
            trace.SetPhase(0.04, "Loading map");
            trace.SetPhase(0.05, "Loading map");
            trace.Frame(0.52, true);
            Require(trace.GapCount == 1 && System.Math.Abs(trace.GapSeconds - 0.5) < 0.000001, "Gap accounting");
            Require(trace.Phases.Count == 2 && trace.Gaps[0].Phase == "screen-show", "Phase deduplication and spanning-gap attribution");
            Require(trace.Duration == 0.52 && trace.FrameCount == 2, "Duration and samples");
            passed.Add("frame gaps include blocking time, with previous-frame phase attribution");
            passed.Add("phase updates deduplicated independently of frame sampling");

            trace.Frame(2.52, false);
            trace.Frame(4.52, true);
            trace.Frame(4.54, true);
            Require(trace.GapCount == 1 && System.Math.Abs(trace.UnfocusedSeconds - 4) < 0.000001, "Focus exclusion");
            Require(trace.Events.Count == 2, "Focus markers");
            passed.Add("focus transitions excluded from stall totals and marked explicitly");

            var focusBetweenFrames = new LoadTrace(true);
            focusBetweenFrames.FocusChanged(0.2, false);
            focusBetweenFrames.FocusChanged(0.4, true);
            focusBetweenFrames.Frame(0.6, true);
            Require(focusBetweenFrames.GapCount == 0 && focusBetweenFrames.UnfocusedSeconds == 0.6,
                "Focus loss and regain between frames escaped exclusion");
            passed.Add("focus loss and regain between two frames still exclude that interval");

            var duration = trace.Duration;
            var frames = trace.FrameCount;
            trace.Frame(1, true);
            trace.Frame(double.NaN, true);
            trace.Frame(double.PositiveInfinity, true);
            Require(trace.Duration == duration && trace.FrameCount == frames, "Invalid timestamps changed accounting");
            passed.Add("invalid and backwards timestamps rejected");

            var capped = new LoadTrace(true);
            for (int i = 1; i <= 200; i++)
            {
                capped.SetPhase(i - 0.5, "phase " + i);
                capped.Event(i - 0.25, "event " + i);
                capped.Frame(i, true);
            }
            Require(capped.Gaps.Count == 128 && capped.DroppedGaps == 72 && capped.GapCount == 200
                && capped.GapSeconds == 200, "Gap cap lost aggregate totals");
            Require(capped.Phases.Count == 128 && capped.DroppedPhases == 73, "Phase cap");
            Require(capped.Events.Count == 128 && capped.DroppedEvents == 72, "Event cap");
            passed.Add("bounded detail buffers retain exact aggregate totals after overflow");

            var previousCulture = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("fr-FR");
                Require(LoadTrace.Number(1.25) == "1.250000", "Culture-sensitive JSON number");
                Require(LoadTrace.Quote("a\"\\\n\t") == "\"a\\\"\\\\\\u000a\\u0009\"", "JSON escaping");
                Require(trace.Json("\"schemaVersion\":1").Contains("\"durationSeconds\":4.540000"), "Report serialization");
            }
            finally { System.Threading.Thread.CurrentThread.CurrentCulture = previousCulture; }
            passed.Add("JSON numbers and control-character escaping are culture independent");

            var fresh = new LoadTrace(true);
            Require(fresh.GapCount == 0 && fresh.Duration == 0 && fresh.Phases.Count == 1, "Cross-run contamination");
            passed.Add("new captures start with independent state");
            return passed.ToArray();
        }
    }
}
