# Quiet Focus

Open **Quiet Focus** from your Desktop shortcut, then click **Start focus**.

- Focus lasts 25 minutes. A 5-minute break starts automatically afterward.
- After the break, the next focus session waits for you to press Start.
- Pause and resume whenever needed. Reset restarts the current phase without deleting accumulated statistics. Skip break returns to a fresh, stopped focus timer.
- **Total focus time** includes only time spent running focus sessions, including partial sessions. Breaks and pauses are excluded.
- **Break percentage** is break time divided by (focus time + break time), multiplied by 100.
- **Interruptions** count pausing, resetting, closing, or putting the PC to sleep while focus is running. Actions while already paused do not add another interruption. Break interruptions do not count.
- **Completed pomodoros** counts finished 25-minute focus sessions.
- Today uses your local calendar date; All time includes all recorded days.

Minimizing keeps the timer running. Closing pauses it and exits. Sleep pauses the timer; sleeping time is excluded. If the app stops unexpectedly, reopening pauses the saved timer and counts one interruption for an active focus session. No time while closed is credited. A crash may lose up to approximately five seconds since the last checkpoint.

Sound enables the Windows notification sound at phase transitions. Windows sound and notification settings may affect alerts. On top keeps the window above other apps. Export stats saves daily totals to CSV (focus and break durations are in seconds). Space starts/pauses when a button or checkbox does not have keyboard focus; focused controls use their usual Space action.

## Saved data

Statistics are stored locally in `%LOCALAPPDATA%\QuietFocus\state.json`, with `state.json.bak` as a backup. Saves happen every five seconds while running and immediately after actions. Keep both files when backing up your data. No account or internet connection is needed.

## Source and checks

Source code is in `source`. To rebuild, close the app and run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "$env:USERPROFILE\Desktop\Quiet Focus\build.ps1"
```

This uses the installed Windows .NET Framework compiler and runs 14 core tests. `CoreTests.exe` can also be run separately. The app requires .NET Framework 4.8.

For isolated UI checks, run `QuietFocus.exe --ui-test <new-test-directory>` with a fresh, writable directory. This produces three screenshots and `ui-test-result.txt` without changing normal statistics.
