using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Bot.Core.Models
{
    /// <summary>
    /// Bear Hunt (Bot tab > Bear Hunt; the task's cycle switch turns the scheduled bear mode on): when the next bear is, and the team flags for our own rally on the trap and
    /// for joining the alliance's rallies. Each flag keeps its own troops and ratio in the game (own rally: 5/10/85).
    /// </summary>
    public class BearHuntSettings : INotifyPropertyChanged
    {
        private int _trap = 1;
        private int _ownTeam = 3;
        private int _joinTeam = 4;
        private int _maxJoins = 6;
        private string _firstCharacter = string.Empty;
        private DateTime? _nextBearUtc;
        private int _repeatDays = 2;
        private int _prepMinutes = 10;
        private int _lastSendSeconds = 24 * 60 + 30;
        private int _eventMinutes = 30;
        private int _rallyRoundSeconds = 5 * 60 + 30;
        private int _switchLeadSeconds = 60;

        /// <summary>Our alliance's trap: 1 or 2 (the event calendar lists "Bear Hunt - Trap 1/2")</summary>
        [JsonPropertyName("trap")]
        public int Trap { get => _trap; set => Set(ref _trap, value); }

        /// <summary>Team flag for our own rally on the bear (set to 5% infantry, 10% cavalry, 85% archers in the game)</summary>
        [JsonPropertyName("ownTeam")]
        public int OwnTeam { get => _ownTeam; set => Set(ref _ownTeam, value); }

        /// <summary>Team flag for joining the alliance's bear rallies</summary>
        [JsonPropertyName("joinTeam")]
        public int JoinTeam { get => _joinTeam; set => Set(ref _joinTeam, value); }

        /// <summary>Rallies joined at once per character (the top ones of the War list)</summary>
        [JsonPropertyName("maxJoins")]
        public int MaxJoins { get => _maxJoins; set => Set(ref _maxJoins, value); }

        /// <summary>The character that sends its rally first (empty: the roster's order)</summary>
        [JsonPropertyName("firstCharacter")]
        public string FirstCharacter
        {
            get => _firstCharacter;
            set
            {
                if (_firstCharacter == (value ?? string.Empty)) return;
                _firstCharacter = value ?? string.Empty;
                OnPropertyChanged(nameof(FirstCharacter));
            }
        }

        /// <summary>Start of the next bear (UTC); null = not known</summary>
        [JsonPropertyName("nextBearUtc")]
        public DateTime? NextBearUtc
        {
            get => _nextBearUtc;
            set
            {
                var utc = value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : (DateTime?)null;
                if (_nextBearUtc == utc) return;
                _nextBearUtc = utc;
                OnPropertyChanged(nameof(NextBearUtc));
                OnPropertyChanged(nameof(NextBearUtcText));
            }
        }

        /// <summary>The GUI's text box for <see cref="NextBearUtc"/>: "yyyy-MM-dd HH:mm" in UTC, empty = not known</summary>
        [JsonIgnore]
        public string NextBearUtcText
        {
            get => NextBearUtc?.ToString(TimeFormat, CultureInfo.InvariantCulture) ?? string.Empty;
            set
            {
                if (string.IsNullOrWhiteSpace(value)) NextBearUtc = null;
                else if (DateTime.TryParseExact(value.Trim(), TimeFormat, CultureInfo.InvariantCulture,
                             DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)) NextBearUtc = parsed;
            }
        }

        public const string TimeFormat = "yyyy-MM-dd HH:mm";

        /// <summary>After a bear, the next one is this many days later (0: enter it by hand)</summary>
        [JsonPropertyName("repeatDays")]
        public int RepeatDays { get => _repeatDays; set => Set(ref _repeatDays, value); }

        /// <summary>Minutes before the bear to stop the cycle, switch auto-join off and call the troops home</summary>
        [JsonPropertyName("prepMinutes")]
        public int PrepMinutes { get => _prepMinutes; set => Set(ref _prepMinutes, value); }

        /// <summary>No own rally after the bear's start + this (5 min rally + the walk must end inside the 30 min)</summary>
        [JsonPropertyName("lastSendSeconds")]
        public int LastSendSeconds { get => _lastSendSeconds; set => Set(ref _lastSendSeconds, value); }

        [JsonPropertyName("eventMinutes")]
        public int EventMinutes { get => _eventMinutes; set => Set(ref _eventMinutes, value); }

        /// <summary>Own rally away: 5 min rally timer + walk to the bear and back (~5:30 to 6:00)</summary>
        [JsonPropertyName("rallyRoundSeconds")]
        public int RallyRoundSeconds { get => _rallyRoundSeconds; set => Set(ref _rallyRoundSeconds, value); }

        /// <summary>Switch to a character this long before its own rally is due (a character switch takes ~30-60 s)</summary>
        [JsonPropertyName("switchLeadSeconds")]
        public int SwitchLeadSeconds { get => _switchLeadSeconds; set => Set(ref _switchLeadSeconds, value); }

        [JsonIgnore]
        public bool IsValid => OwnTeam is >= 1 and <= 8 && JoinTeam is >= 1 and <= 8 && Trap is 1 or 2 && MaxJoins >= 0;

        [JsonIgnore] public TimeSpan Prep => TimeSpan.FromMinutes(Math.Max(0, PrepMinutes));
        [JsonIgnore] public TimeSpan LastSend => TimeSpan.FromSeconds(Math.Max(0, LastSendSeconds));
        [JsonIgnore] public TimeSpan EventLength => TimeSpan.FromMinutes(Math.Max(1, EventMinutes));
        [JsonIgnore] public TimeSpan RallyRound => TimeSpan.FromSeconds(Math.Max(60, RallyRoundSeconds));
        [JsonIgnore] public TimeSpan SwitchLead => TimeSpan.FromSeconds(Math.Max(0, SwitchLeadSeconds));

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return;
            field = value;
            OnPropertyChanged(name);
            OnPropertyChanged(nameof(IsValid));
        }

        protected virtual void OnPropertyChanged(string? propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
