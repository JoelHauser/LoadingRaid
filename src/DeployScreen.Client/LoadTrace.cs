using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DeployScreen.Client
{
    // Pure managed accounting. Times are monotonic seconds supplied by the caller.
    // Detailed buffers are capped; aggregate counts continue after the buffers fill.
    internal sealed class LoadTrace
    {
        internal const int DetailLimit = 128;
        internal const double GapThreshold = 0.1;
        internal readonly List<Phase> Phases = new List<Phase>();
        internal readonly List<Gap> Gaps = new List<Gap>();
        internal readonly List<Phase> Events = new List<Phase>();
        internal double Duration, LongestGap, GapSeconds, UnfocusedSeconds;
        internal int GapCount, FrameCount, DroppedPhases, DroppedGaps, DroppedEvents;
        private double _lastFrame;
        private bool _focused;
        private bool _unfocusedSinceFrame;
        private string _phase = "screen-show";
        private string _framePhase = "screen-show";

        internal struct Phase { internal double At; internal string Name; }
        internal struct Gap { internal double At, Seconds; internal string Phase; }

        internal LoadTrace(bool focused)
        {
            _focused = focused;
            _unfocusedSinceFrame = !focused;
            SetPhase(0, _phase);
        }

        internal void Frame(double now, bool focused)
        {
            if (now < _lastFrame || double.IsNaN(now) || double.IsInfinity(now)) return;
            var delta = now - _lastFrame;
            // A focus transition makes the whole spanning interval unsuitable for comparison.
            if (_unfocusedSinceFrame || !_focused || !focused) UnfocusedSeconds += delta;
            else
            {
                LongestGap = Math.Max(LongestGap, delta);
                if (delta >= GapThreshold)
                {
                    GapCount++;
                    GapSeconds += delta;
                    if (Gaps.Count < DetailLimit)
                        Gaps.Add(new Gap { At = _lastFrame, Seconds = delta, Phase = _framePhase });
                    else DroppedGaps++;
                }
            }
            FocusChanged(now, focused);
            _unfocusedSinceFrame = !focused;
            _lastFrame = now;
            _framePhase = _phase;
            Duration = now;
            FrameCount++;
        }

        internal void FocusChanged(double now, bool focused)
        {
            if (_focused != focused) Event(now, focused ? "focus-gained" : "focus-lost");
            _focused = focused;
            if (!focused) _unfocusedSinceFrame = true;
        }

        internal void SetPhase(double now, string name)
        {
            name = name ?? "unknown";
            if (Phases.Count > 0 && name == _phase) return;
            _phase = name;
            if (Phases.Count < DetailLimit) Phases.Add(new Phase { At = now, Name = name });
            else DroppedPhases++;
        }

        internal void Event(double now, string name)
        {
            if (Events.Count < DetailLimit) Events.Add(new Phase { At = now, Name = name });
            else DroppedEvents++;
        }

        internal string Json(string metadata)
        {
            var b = new StringBuilder(8192);
            b.Append('{').Append(metadata)
                .Append(",\"durationSeconds\":").Append(Number(Duration))
                .Append(",\"frameSamples\":").Append(FrameCount)
                .Append(",\"gapThresholdSeconds\":").Append(Number(GapThreshold))
                .Append(",\"longestFocusedFrameGapSeconds\":").Append(Number(LongestGap))
                .Append(",\"focusedGapCount\":").Append(GapCount)
                .Append(",\"focusedGapSeconds\":").Append(Number(GapSeconds))
                .Append(",\"unfocusedSeconds\":").Append(Number(UnfocusedSeconds))
                .Append(",\"droppedPhases\":").Append(DroppedPhases)
                .Append(",\"droppedGaps\":").Append(DroppedGaps)
                .Append(",\"droppedEvents\":").Append(DroppedEvents);
            AppendPhases(b, "phases", Phases);
            AppendPhases(b, "events", Events);
            b.Append(",\"gaps\":[");
            for (var i = 0; i < Gaps.Count; i++)
            {
                if (i > 0) b.Append(',');
                var gap = Gaps[i];
                b.Append("{\"atSeconds\":").Append(Number(gap.At))
                    .Append(",\"seconds\":").Append(Number(gap.Seconds))
                    .Append(",\"phaseAtPreviousFrame\":").Append(Quote(gap.Phase)).Append('}');
            }
            return b.Append("]}").ToString();
        }

        private static void AppendPhases(StringBuilder b, string key, List<Phase> phases)
        {
            b.Append(',').Append(Quote(key)).Append(":[");
            for (var i = 0; i < phases.Count; i++)
            {
                if (i > 0) b.Append(',');
                b.Append("{\"atSeconds\":").Append(Number(phases[i].At))
                    .Append(",\"name\":").Append(Quote(phases[i].Name)).Append('}');
            }
            b.Append(']');
        }

        internal static string Number(double value) { return value.ToString("0.000000", CultureInfo.InvariantCulture); }

        internal static string Quote(string value)
        {
            var b = new StringBuilder("\"");
            foreach (var c in value ?? "")
            {
                if (c == '"' || c == '\\') b.Append('\\').Append(c);
                else if (c < 32) b.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else b.Append(c);
            }
            return b.Append('"').ToString();
        }
    }
}
