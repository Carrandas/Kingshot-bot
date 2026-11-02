using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Bot.Core.Models
{
    public class ArenaSettings : INotifyPropertyChanged
    {
        private bool _runAfterEnabled;
        private string _runAfterUtc = "21:30";

        /// <summary>
        /// Off: Arena runs in the first cycle of the day. On: only in cycles from <see cref="RunAfterUtc"/> on,
        /// so the defence lineup is attacked as little as possible before the 00:00 UTC reset.
        /// </summary>
        [JsonPropertyName("runAfterEnabled")]
        public bool RunAfterEnabled
        {
            get => _runAfterEnabled;
            set
            {
                if (_runAfterEnabled != value)
                {
                    _runAfterEnabled = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>UTC time of day ("HH:mm"), default 21:30: two and a half hours before the reset.</summary>
        [JsonPropertyName("runAfterUtc")]
        public string RunAfterUtc
        {
            get => _runAfterUtc;
            set
            {
                if (_runAfterUtc != value)
                {
                    _runAfterUtc = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>The configured time of day, or null when the setting is off or the time doesn't parse.</summary>
        public TimeSpan? RunAfterTime()
        {
            if (!RunAfterEnabled) return null;
            return TimeSpan.TryParseExact(RunAfterUtc?.Trim(), @"h\:mm", CultureInfo.InvariantCulture, out var time) && time < TimeSpan.FromDays(1)
                ? time
                : null;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
