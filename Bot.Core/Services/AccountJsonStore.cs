using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Bot.Core.Services
{
    /// <summary>A small per-account value saved as JSON under data/ next to the exe, loaded on first use</summary>
    public sealed class AccountJsonStore<T>
    {
        private readonly string _path;
        private readonly object _lock = new();
        private ConcurrentDictionary<string, T>? _values;

        public AccountJsonStore(string fileName)
        {
            _path = Path.Combine(AppContext.BaseDirectory, "data", fileName);
        }

        public bool TryGet(string key, out T value)
        {
            if (Values.TryGetValue(key, out var found))
            {
                value = found;
                return true;
            }
            value = default!;
            return false;
        }

        public void Set(string key, T value)
        {
            Values[key] = value;
            Save();
        }

        public void Remove(string key)
        {
            if (Values.TryRemove(key, out _)) Save();
        }

        private ConcurrentDictionary<string, T> Values
        {
            get
            {
                if (_values != null) return _values;
                lock (_lock)
                {
                    _values ??= Load();
                    return _values;
                }
            }
        }

        private ConcurrentDictionary<string, T> Load()
        {
            try
            {
                if (File.Exists(_path))
                {
                    var data = JsonSerializer.Deserialize<Dictionary<string, T>>(File.ReadAllText(_path));
                    if (data != null) return new ConcurrentDictionary<string, T>(data);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AccountJsonStore] Error loading {_path}: {ex.Message}");
            }
            return new ConcurrentDictionary<string, T>();
        }

        private void Save()
        {
            lock (_lock)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    File.WriteAllText(_path, JsonSerializer.Serialize(Values.ToDictionary(k => k.Key, k => k.Value)));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AccountJsonStore] Error saving {_path}: {ex.Message}");
                }
            }
        }
    }
}
