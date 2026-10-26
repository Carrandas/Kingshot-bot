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
    /// Once a day per game account: a task marks itself done after its run, and skips until the game's 00:00 UTC reset.
    /// Saved to data/daily_gates.json, so a restart doesn't repeat today's runs.
    /// </summary>
    public static class DailyGate
    {
        private static readonly string GateFile = Path.Combine(AppContext.BaseDirectory, "data", "daily_gates.json");
        private static readonly object _fileLock = new();
        private static ConcurrentDictionary<string, DateTime>? _doneUtcDate;

        private static string Key(TaskType task, string accountKey) => $"{task}|{accountKey}";

        public static bool DoneToday(TaskType task, string accountKey) =>
            Gates.TryGetValue(Key(task, accountKey), out var date) && date == DateTime.UtcNow.Date;

        public static void MarkDone(TaskType task, string accountKey)
        {
            Gates[Key(task, accountKey)] = DateTime.UtcNow.Date;
            Save();
        }

        public static void Clear(TaskType task, string accountKey)
        {
            if (Gates.TryRemove(Key(task, accountKey), out _)) Save();
        }

        /// <summary>For <see cref="IScheduledTask.GetNextRunUtc"/>: the next reset once done today, else null.</summary>
        public static DateTime? NextRunUtc(TaskType task, string accountKey) =>
            DoneToday(task, accountKey) ? Schedule.NextUtcMidnight : null;

        private static ConcurrentDictionary<string, DateTime> Gates
        {
            get
            {
                if (_doneUtcDate != null) return _doneUtcDate;
                lock (_fileLock)
                {
                    if (_doneUtcDate != null) return _doneUtcDate;
                    var loaded = new ConcurrentDictionary<string, DateTime>();
                    try
                    {
                        if (File.Exists(GateFile))
                        {
                            var data = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(GateFile));
                            // only today's entries matter
                            if (data != null)
                                foreach (var kvp in data.Where(k => k.Value == DateTime.UtcNow.Date)) loaded[kvp.Key] = kvp.Value;
                        }
                    }
                    catch
                    {
                        // a broken file means the tasks run once more today
                    }
                    _doneUtcDate = loaded;
                    return _doneUtcDate;
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
