using Bot.Core.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Bot.Core.Tasks
{
    /// <summary>
    /// Once per event per game account (an event that runs several days, like Champion's Way): a task marks itself done
    /// until the event's last UTC day and skips until the day after. Saved to data/event_gates.json.
    /// </summary>
    public static class EventRunGate
    {
        private static readonly string GateFile = Path.Combine(AppContext.BaseDirectory, "data", "event_gates.json");
        private static readonly object _fileLock = new();
        private static ConcurrentDictionary<string, DateTime>? _doneUntil;

        private static string Key(TaskType task, string accountKey) => $"{task}|{accountKey}";

        /// <summary>Done for the event that runs on <paramref name="nowUtc"/>'s day</summary>
        public static bool Done(TaskType task, string accountKey, DateTime nowUtc) =>
            Gates.TryGetValue(Key(task, accountKey), out var until) && nowUtc.Date <= until;

        /// <summary>Done until (and including) the UTC day <paramref name="lastDayUtc"/></summary>
        public static void MarkDone(TaskType task, string accountKey, DateTime lastDayUtc)
        {
            Gates[Key(task, accountKey)] = lastDayUtc.Date;
            Save();
        }

        public static void Clear(TaskType task, string accountKey)
        {
            if (Gates.TryRemove(Key(task, accountKey), out _)) Save();
        }

        /// <summary>For <see cref="IScheduledTask.GetNextRunUtc"/>: the day after the event's last day once done, else null</summary>
        public static DateTime? NextRunUtc(TaskType task, string accountKey, DateTime nowUtc) =>
            Done(task, accountKey, nowUtc) ? Gates[Key(task, accountKey)].AddDays(1) : null;

        private static ConcurrentDictionary<string, DateTime> Gates
        {
            get
            {
                if (_doneUntil != null) return _doneUntil;
                lock (_fileLock)
                {
                    if (_doneUntil != null) return _doneUntil;
                    var loaded = new ConcurrentDictionary<string, DateTime>();
                    try
                    {
                        if (File.Exists(GateFile))
                        {
                            var data = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(GateFile));
                            // gates of events that are over don't matter
                            if (data != null)
                                foreach (var kvp in data.Where(k => k.Value >= DateTime.UtcNow.Date)) loaded[kvp.Key] = kvp.Value;
                        }
                    }
                    catch
                    {
                        // a broken file means the tasks run once more for this event
                    }
                    _doneUntil = loaded;
                    return _doneUntil;
                }
            }
        }

        private static void Save()
        {
            lock (_fileLock)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(GateFile)!);
                    File.WriteAllText(GateFile, JsonSerializer.Serialize(Gates.ToDictionary(k => k.Key, k => k.Value)));
                }
                catch
                {
                    // the gate still holds in memory
                }
            }
        }
    }
}
