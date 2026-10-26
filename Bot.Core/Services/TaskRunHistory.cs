using Bot.Core.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Bot.Core.Services
{
    /// <summary>
    /// When each task last ran per game account, for the task dashboard. Saved to data/task_history.json so it survives a restart
    /// (the tasks' own gates live in memory). A run the task's gate skipped does not replace the last real run.
    /// </summary>
    public static class TaskRunHistory
    {
        public sealed class Record
        {
            /// <summary>Last run that did work (or failed)</summary>
            public DateTime? LastRunUtc { get; set; }
            public bool LastRunOk { get; set; }
            public string? LastRunMessage { get; set; }
            /// <summary>Last time the task came up at all, a gate skip included</summary>
            public DateTime? LastCheckUtc { get; set; }
            public string? LastCheckMessage { get; set; }
        }

        private static readonly string HistoryFile = Path.Combine(AppContext.BaseDirectory, "data", "task_history.json");
        private static readonly object _fileLock = new();
        private static ConcurrentDictionary<string, Record>? _records;

        /// <summary>Raised after a run was recorded (on the task's thread).</summary>
        public static event Action? Changed;

        // Task running right now on each instance
        private static readonly ConcurrentDictionary<int, TaskType> _current = new();

        public static TaskType? GetCurrent(int instanceNumber) => _current.TryGetValue(instanceNumber, out var task) ? task : null;

        public static void SetCurrent(int instanceNumber, TaskType? task)
        {
            if (task.HasValue) _current[instanceNumber] = task.Value;
            else if (!_current.TryRemove(instanceNumber, out _)) return;
            Changed?.Invoke();
        }

        private static string Key(string accountKey, TaskType task) => $"{accountKey}|{task}";

        public static Record? Get(string accountKey, TaskType task) =>
            Records.TryGetValue(Key(accountKey, task), out var record) ? record : null;

        public static void RecordRun(string accountKey, TaskType task, TaskExecutionDetails result)
        {
            var now = DateTime.UtcNow;
            var record = Records.GetOrAdd(Key(accountKey, task), _ => new Record());
            lock (record)
            {
                record.LastCheckUtc = now;
                record.LastCheckMessage = result.Message;
                if (!result.Skipped)
                {
                    record.LastRunUtc = now;
                    record.LastRunOk = result.Success;
                    record.LastRunMessage = result.Message;
                }
            }
            Save();
            Changed?.Invoke();
        }

        private static ConcurrentDictionary<string, Record> Records
        {
            get
            {
                if (_records != null) return _records;
                lock (_fileLock)
                {
                    if (_records != null) return _records;
                    var loaded = new ConcurrentDictionary<string, Record>();
                    try
                    {
                        if (File.Exists(HistoryFile))
                        {
                            var data = JsonSerializer.Deserialize<Dictionary<string, Record>>(File.ReadAllText(HistoryFile));
                            if (data != null) foreach (var kvp in data) loaded[kvp.Key] = kvp.Value;
                        }
                    }
                    catch
                    {
                        // a broken file only loses the history display
                    }
                    _records = loaded;
                    return _records;
                }
            }
        }

        private static void Save()
        {
            lock (_fileLock)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(HistoryFile)!);
                    var snapshot = Records.ToDictionary(k => k.Key, k => k.Value);
                    File.WriteAllText(HistoryFile, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch
                {
                    // history is display only
                }
            }
        }
    }
}
