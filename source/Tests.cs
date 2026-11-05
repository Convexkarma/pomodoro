using System;
using System.IO;
using System.Linq;

namespace QuietFocus
{
    public static class Tests
    {
        private static int passed;
        private static DateTimeOffset Start = new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);
        private static TimerEngine New() { return new TimerEngine(new SavedState(), TimeZoneInfo.Utc); }
        private static void Equal(double a, double b, string name)
        { if (Math.Abs(a - b) > .02) throw new Exception(name + ": expected " + b + ", got " + a); }
        private static void Check(bool value, string name) { if (!value) throw new Exception(name); }
        private static void Test(string name, Action run)
        { run(); passed++; Console.WriteLine("PASS " + name); }

        public static int Main()
        {
            try
            {
                Test("25 minutes focus -> automatically running 5-minute break", delegate
                {
                    var e = New(); e.Start(0); e.Advance(1500000, Start.AddMinutes(25));
                    Check(e.State.Phase == "Break" && e.State.Running, "Automatic break");
                    Equal(e.State.RemainingMs, 300000, "Break length");
                    Equal(e.Totals(true, Start).FocusMs, 1500000, "Focus total");
                    Equal(e.Totals(true, Start).Completed, 1, "Completion");
                });
                Test("5-minute break stops; total focus excludes breaks; ratio is 16.7%", delegate
                {
                    var e = New(); e.Start(0); e.Advance(1800000, Start.AddMinutes(30));
                    Check(e.State.Phase == "Focus" && !e.State.Running, "Wait for next focus");
                    var s = e.Totals(true, Start); Equal(s.FocusMs, 1500000, "Only focus");
                    Equal(s.BreakMs, 300000, "Break total"); Equal(s.BreakPercent, 100.0 / 6, "Ratio");
                    Equal(s.Interruptions, 0, "Automatic phase transitions are not interruptions");
                });
                Test("Delayed refresh credits remaining break precisely, without timer drift", delegate
                {
                    var e = New(); e.Start(0); e.Advance(1512345, Start.AddMilliseconds(1512345));
                    Equal(e.State.RemainingMs, 287655, "Break remainder");
                    Equal(e.Totals(true, Start).BreakMs, 12345, "Overrun transferred");
                    e.Advance(99999999, Start.AddDays(2));
                    Equal(e.Totals(true, Start).FocusMs, 1500000, "No auto focus loops");
                    Equal(e.Totals(true, Start).BreakMs, 300000, "Break capped");
                });
                Test("Pause excludes idle time; repeated pause counts once; resume continues", delegate
                {
                    var e = New(); e.Start(100); e.Pause(60100, Start.AddMinutes(1));
                    e.Pause(90000, Start.AddMinutes(2)); e.Advance(660100, Start.AddMinutes(11));
                    Equal(e.State.RemainingMs, 1440000, "Paused remaining");
                    Equal(e.Totals(true, Start).Interruptions, 1, "Only one interruption");
                    e.Start(660100); e.Advance(720100, Start.AddMinutes(12));
                    Equal(e.Totals(true, Start).FocusMs, 120000, "Only active time");
                });
                Test("Reset keeps accumulated focus and does not double-count a paused interruption", delegate
                {
                    var e = New(); e.Start(0); e.Reset(60000, Start);
                    Equal(e.State.RemainingMs, 1500000, "Reset duration");
                    Equal(e.Totals(true, Start).FocusMs, 60000, "Retain progress");
                    e.Reset(60000, Start); Equal(e.Totals(true, Start).Interruptions, 1, "No duplicate");
                });
                Test("Pausing and skipping breaks never increment focus interruptions", delegate
                {
                    var e = New(); e.Start(0); e.Advance(1500000, Start);
                    e.Pause(1530000, Start); e.SkipBreak(1530000, Start);
                    Check(e.State.Phase == "Focus" && !e.State.Running, "Skip break");
                    Equal(e.Totals(true, Start).Interruptions, 0, "No break interruption");
                    Equal(e.Totals(true, Start).BreakMs, 30000, "Partial break retained");
                });
                Test("Running focus recovered after crash is paused and counted once", delegate
                {
                    var e = New(); e.Start(0); e.Advance(10000, Start);
                    Check(e.Recover(Start.AddHours(10)), "Recover running state");
                    Check(!e.Recover(Start.AddHours(11)), "Idempotent recovery");
                    Equal(e.Totals(true, Start).FocusMs, 10000, "No closed time counted");
                    Equal(e.Totals(true, Start).Interruptions, 1, "Recovery count");
                });
                Test("Daily stats split exactly at midnight, including a sub-second boundary", delegate
                {
                    var e = New(); var end = new DateTimeOffset(2026, 11, 3, 0, 0, 1, TimeSpan.Zero).AddMilliseconds(250);
                    e.Start(0); e.Advance(2000, end);
                    Equal(e.State.Days.Single(x => x.Date == "2026-11-02").FocusMs, 750, "Before midnight");
                    Equal(e.Totals(false, end).FocusMs, 1250, "Today after midnight");
                    Equal(e.Totals(true, end).FocusMs, 2000, "All time");
                });
                Test("Local dates, not UTC dates, determine Today", delegate
                {
                    var zone = TimeZoneInfo.CreateCustomTimeZone("TestUTC3", TimeSpan.FromHours(3), "UTC3", "UTC3");
                    var e = new TimerEngine(new SavedState(), zone);
                    var end = new DateTimeOffset(2026, 11, 2, 21, 0, 1, TimeSpan.Zero);
                    e.Start(0); e.Advance(2000, end);
                    Equal(e.Totals(false, end).FocusMs, 1000, "Local new day");
                });
                Test("System wall-clock changes do not change elapsed focus", delegate
                {
                    var e = New(); e.Start(0); e.Advance(1000, Start.AddHours(-4));
                    e.Advance(2000, Start.AddHours(12)); Equal(e.Totals(true, Start).FocusMs, 2000, "Monotonic time");
                });
                Test("Double Start cannot discard elapsed time", delegate
                {
                    var e = New(); e.Start(0); e.Start(500); e.Advance(1000, Start);
                    Equal(e.Totals(true, Start).FocusMs, 1000, "Double start");
                });
                Test("Empty stats return zero, never NaN", delegate { Equal(New().Totals(true, Start).BreakPercent, 0, "Zero denominator"); });
                Test("Atomic persistence, valid backup recovery, unreadable data preserved", delegate
                {
                    string folder = Path.Combine(Path.GetTempPath(), "QuietFocus-tests-" + Guid.NewGuid());
                    Directory.CreateDirectory(folder);
                    try
                    {
                        var store = new StateStore(Path.Combine(folder, "state.json")); var e = New();
                        e.Start(0); e.Pause(15000, Start); store.Save(e.State);
                        e.Start(15000); e.Pause(30000, Start); store.Save(e.State);
                        Equal(new TimerEngine(store.Load(), TimeZoneInfo.Utc).Totals(true, Start).FocusMs, 30000, "Roundtrip");
                        File.WriteAllText(store.FilePath, "broken");
                        Equal(new TimerEngine(store.Load(), TimeZoneInfo.Utc).Totals(true, Start).FocusMs, 15000, "Recovered backup");
                        Check(Directory.GetFiles(folder, "*.damaged-*").Length == 1, "Damaged file retained");
                        File.WriteAllText(store.FilePath, "broken"); File.WriteAllText(store.FilePath + ".bak", "broken");
                        bool threw = false; try { store.Load(); } catch (IOException) { threw = true; }
                        Check(threw && File.ReadAllText(store.FilePath) == "broken", "Never silently reset corrupt data");
                    }
                    finally { Directory.Delete(folder, true); }
                });
                Test("Reject invalid stored numbers", delegate
                {
                    var s = new SavedState { RemainingMs = double.NaN }; bool rejected = false;
                    try { TimerEngine.Validate(s); } catch (InvalidDataException) { rejected = true; }
                    Check(rejected, "Reject NaN");
                });
                Console.WriteLine("ALL " + passed + " CORE TESTS PASSED");
                return 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        }
    }
}
