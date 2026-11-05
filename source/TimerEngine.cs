using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace QuietFocus
{
    [DataContract]
    public sealed class DayStats
    {
        [DataMember] public string Date;
        [DataMember] public double FocusMs;
        [DataMember] public double BreakMs;
        [DataMember] public int Completed;
        [DataMember] public int Interruptions;
        public double BreakPercent { get { return FocusMs + BreakMs > 0 ? 100 * BreakMs / (FocusMs + BreakMs) : 0; } }
    }

    [DataContract]
    public sealed class SavedState
    {
        [DataMember] public int Version = 1;
        [DataMember] public string Phase = "Focus";
        [DataMember] public double RemainingMs = TimerEngine.FocusDuration;
        [DataMember] public bool Running;
        [DataMember] public bool Sound = true;
        [DataMember] public bool OnTop;
        [DataMember] public List<DayStats> Days = new List<DayStats>();
    }

    public sealed class TimerEngine
    {
        public const double FocusDuration = 25 * 60 * 1000;
        public const double BreakDuration = 5 * 60 * 1000;
        public readonly SavedState State;
        private readonly TimeZoneInfo zone;
        private double lastMonotonic;
        public readonly List<string> Transitions = new List<string>();
        public double Duration { get { return State.Phase == "Focus" ? FocusDuration : BreakDuration; } }

        public TimerEngine(SavedState state, TimeZoneInfo timeZone)
        {
            Validate(state);
            State = state;
            zone = timeZone;
        }

        public static void Validate(SavedState s)
        {
            if (s == null || s.Version != 1 || (s.Phase != "Focus" && s.Phase != "Break") ||
                !Finite(s.RemainingMs) || s.RemainingMs <= 0 ||
                s.RemainingMs > (s.Phase == "Focus" ? FocusDuration : BreakDuration) || s.Days == null)
                throw new InvalidDataException("Invalid timer data.");
            var dates = new HashSet<string>();
            foreach (DayStats d in s.Days)
            {
                DateTime parsed;
                if (d == null || !DateTime.TryParseExact(d.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out parsed) || !dates.Add(d.Date) ||
                    !Finite(d.FocusMs) || !Finite(d.BreakMs) || d.FocusMs < 0 || d.BreakMs < 0 ||
                    d.Completed < 0 || d.Interruptions < 0) throw new InvalidDataException("Invalid statistics data.");
            }
        }
        private static bool Finite(double n) { return !double.IsNaN(n) && !double.IsInfinity(n); }
        private string Day(DateTimeOffset utc) { return TimeZoneInfo.ConvertTime(utc, zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
        private DayStats GetDay(DateTimeOffset utc)
        {
            string date = Day(utc);
            DayStats d = State.Days.FirstOrDefault(x => x.Date == date);
            if (d == null) { d = new DayStats { Date = date }; State.Days.Add(d); }
            return d;
        }

        // Split elapsed work at local date boundaries. One-second accounting slices
        // also handle DST transitions without assuming every day lasts 24 hours.
        private void Credit(string phase, DateTimeOffset start, double elapsed)
        {
            while (elapsed > .0001)
            {
                double slice = Math.Min(elapsed, 1000);
                DateTimeOffset end = start.AddMilliseconds(slice);
                if (Day(start) != Day(end.AddTicks(-1)))
                {
                    double lo = 0, hi = slice;
                    for (int i = 0; i < 32; i++)
                    {
                        double mid = (lo + hi) / 2;
                        if (Day(start.AddMilliseconds(mid)) == Day(start)) lo = mid; else hi = mid;
                    }
                    double before = Math.Round(hi); // DateTimeOffset.AddMilliseconds on .NET Framework rounds to milliseconds.
                    before = Math.Max(0, Math.Min(slice, before));
                    Add(GetDay(start), phase, before);
                    Add(GetDay(end), phase, slice - before);
                }
                else Add(GetDay(start), phase, slice);
                elapsed -= slice;
                start = end;
            }
        }
        private static void Add(DayStats d, string phase, double ms)
        {
            if (phase == "Focus") d.FocusMs += ms; else d.BreakMs += ms;
        }

        public void Start(double monotonic)
        {
            if (State.Running) return;
            lastMonotonic = monotonic;
            State.Running = true;
        }

        // The UI timer only schedules refreshes. Elapsed time comes from a monotonic
        // counter, so delayed callbacks and system-clock changes do not lose/add time.
        public void Advance(double monotonic, DateTimeOffset utc)
        {
            if (!State.Running) return;
            double delta = Math.Max(0, monotonic - lastMonotonic);
            lastMonotonic = monotonic;
            DateTimeOffset cursor = utc.AddMilliseconds(-delta);
            while (delta > .0001 && State.Running)
            {
                double use = Math.Min(delta, State.RemainingMs);
                Credit(State.Phase, cursor, use);
                cursor = cursor.AddMilliseconds(use);
                State.RemainingMs -= use;
                delta -= use;
                if (State.RemainingMs <= .0001)
                {
                    if (State.Phase == "Focus")
                    {
                        GetDay(cursor.AddTicks(-1)).Completed++;
                        State.Phase = "Break";
                        State.RemainingMs = BreakDuration;
                        Transitions.Add("Focus complete. Your 5-minute break has started.");
                    }
                    else
                    {
                        State.Phase = "Focus";
                        State.RemainingMs = FocusDuration;
                        State.Running = false;
                        Transitions.Add("Break complete. Start your next focus session when ready.");
                    }
                }
            }
        }

        public void Pause(double monotonic, DateTimeOffset utc)
        {
            Advance(monotonic, utc);
            if (!State.Running) return;
            if (State.Phase == "Focus") GetDay(utc).Interruptions++;
            State.Running = false;
        }

        public void Reset(double monotonic, DateTimeOffset utc)
        {
            Pause(monotonic, utc);
            State.RemainingMs = Duration;
        }

        public void SkipBreak(double monotonic, DateTimeOffset utc)
        {
            Advance(monotonic, utc);
            if (State.Phase != "Break") return;
            State.Running = false;
            State.Phase = "Focus";
            State.RemainingMs = FocusDuration;
        }

        public bool Recover(DateTimeOffset utc)
        {
            // Never credit time while the app was closed or a PC was powered off.
            if (!State.Running) return false;
            if (State.Phase == "Focus") GetDay(utc).Interruptions++;
            State.Running = false;
            return true;
        }

        public DayStats Totals(bool allTime, DateTimeOffset utc)
        {
            string today = Day(utc);
            var result = new DayStats { Date = today };
            foreach (DayStats d in State.Days.Where(x => allTime || x.Date == today))
            {
                result.FocusMs += d.FocusMs; result.BreakMs += d.BreakMs;
                result.Completed += d.Completed; result.Interruptions += d.Interruptions;
            }
            return result;
        }
    }

    public sealed class StateStore
    {
        public readonly string FilePath;
        public string Notice;
        public StateStore(string path) { FilePath = path; }
        private SavedState Read(string path)
        {
            using (var stream = File.OpenRead(path))
            {
                var s = (SavedState)new DataContractJsonSerializer(typeof(SavedState)).ReadObject(stream);
                TimerEngine.Validate(s);
                return s;
            }
        }
        public SavedState Load()
        {
            if (!File.Exists(FilePath) && !File.Exists(FilePath + ".bak")) return new SavedState();
            try { return Read(FilePath); }
            catch (Exception e)
            {
                if (!(e is IOException || e is SerializationException || e is UnauthorizedAccessException || e is System.Xml.XmlException)) throw;
                try
                {
                    SavedState backup = Read(FilePath + ".bak");
                    if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + ".damaged-" + DateTime.UtcNow.Ticks, false);
                    // Restore the good file before the next atomic save rotates the backup.
                    File.Copy(FilePath + ".bak", FilePath, true);
                    Notice = "Recovered your saved statistics from the backup.";
                    return backup;
                }
                catch (Exception backupError)
                {
                    throw new IOException("Saved data could not be read. Your files have been kept at " + FilePath +
                        ". Restore a valid backup before starting the app.", backupError);
                }
            }
        }
        public void Save(SavedState state)
        {
            TimerEngine.Validate(state);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            string temp = FilePath + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                new DataContractJsonSerializer(typeof(SavedState)).WriteObject(stream, state);
                stream.Flush(true);
            }
            if (File.Exists(FilePath)) File.Replace(temp, FilePath, FilePath + ".bak");
            else File.Move(temp, FilePath);
        }
    }
}
