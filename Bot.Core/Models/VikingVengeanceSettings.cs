using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Bot.Core.Models
{
    /// <summary>
    /// Viking Vengeance (Bot tab > Viking Vengeance): the Vikings attack the alliance's cities, and online members get
    /// our troops as reinforcement. The Bear Hunt task reads the time from the map's Task List; the mode starts
    /// <see cref="PrepMinutes"/> before it, every character recalls its marches and gives Squad Help to the online
    /// members, then the game stays open on <see cref="MainCharacter"/> until the event is over and the troops come home.
    /// </summary>
    public class VikingVengeanceSettings : INotifyPropertyChanged
    {
        public const string TimeFormat = BearHuntSettings.TimeFormat;

        private bool _enabled = true;
        private string _mainCharacter = string.Empty;
        private int _team = 3;
        private int _team2, _team3, _team4, _team5, _team6;
        private int _minFreeTroops = 100_000;
        private int _prepMinutes = 20;   // each character needs ~7 min (gatherers home ~5 min, 6 reinforcements); 10 left Asli 4 min late on 2026-11-19
        private int _eventMinutes = 35;
        private DateTime? _nextVikingUtc;

        /// <summary>Start the mode by itself before a Viking Vengeance the Task List showed</summary>
        [JsonPropertyName("enabled")]
        public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

        /// <summary>Sends last and keeps the game open until the event is over (empty: the last ticked character)</summary>
        [JsonPropertyName("mainCharacter")]
        public string MainCharacter { get => _mainCharacter; set => Set(ref _mainCharacter, value ?? string.Empty); }

        /// <summary>Reinforcement flag slot 1 (0 = not set; each flag keeps its saved troops and ratio)</summary>
        [JsonPropertyName("team")]
        public int Team { get => _team; set => Set(ref _team, value); }

        /// <summary>Reinforcement flag slots 2-6 (0 = not set); see <see cref="Flag"/></summary>
        [JsonPropertyName("team2")] public int Team2 { get => _team2; set => Set(ref _team2, value); }
        [JsonPropertyName("team3")] public int Team3 { get => _team3; set => Set(ref _team3, value); }
        [JsonPropertyName("team4")] public int Team4 { get => _team4; set => Set(ref _team4, value); }
        [JsonPropertyName("team5")] public int Team5 { get => _team5; set => Set(ref _team5, value); }
        [JsonPropertyName("team6")] public int Team6 { get => _team6; set => Set(ref _team6, value); }

        /// <summary>The set reinforcement flags in slot order</summary>
        [JsonIgnore]
        public System.Collections.Generic.List<int> Flags => TeamFlags.Set(new[] { Team, Team2, Team3, Team4, Team5, Team6 });

        /// <summary>Flag for the character's reinforcement <paramref name="index"/> (0-based): the set flags in turn, repeating from the first</summary>
        public int Flag(int index) => TeamFlags.ForMarch(Flags, index, 3);

        /// <summary>A member gets help only while at least this much of their Reinforce Capacity is free</summary>
        [JsonPropertyName("minFreeTroops")]
        public int MinFreeTroops { get => _minFreeTroops; set => Set(ref _minFreeTroops, value); }

        /// <summary>Minutes before the start to stop the cycle and call the troops home</summary>
        [JsonPropertyName("prepMinutes")]
        public int PrepMinutes { get => _prepMinutes; set => Set(ref _prepMinutes, value); }

        /// <summary>The event lasts a bit over half an hour; the troops are recalled after this</summary>
        [JsonPropertyName("eventMinutes")]
        public int EventMinutes { get => _eventMinutes; set => Set(ref _eventMinutes, value); }

        /// <summary>Start of the next Viking Vengeance (UTC); null = not known</summary>
        [JsonPropertyName("nextVikingUtc")]
        public DateTime? NextVikingUtc
        {
            get => _nextVikingUtc;
            set
            {
                var utc = value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : (DateTime?)null;
                if (_nextVikingUtc == utc) return;
                _nextVikingUtc = utc;
                OnPropertyChanged(nameof(NextVikingUtc));
                OnPropertyChanged(nameof(NextVikingUtcText));
            }
        }

        /// <summary>The GUI's text box for <see cref="NextVikingUtc"/>: "yyyy-MM-dd HH:mm" in UTC, empty = not known</summary>
        [JsonIgnore]
        public string NextVikingUtcText
        {
            get => NextVikingUtc?.ToString(TimeFormat, CultureInfo.InvariantCulture) ?? string.Empty;
            set
            {
                if (string.IsNullOrWhiteSpace(value)) NextVikingUtc = null;
                else if (DateTime.TryParseExact(value.Trim(), TimeFormat, CultureInfo.InvariantCulture,
                             DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)) NextVikingUtc = parsed;
            }
        }

        [JsonIgnore] public bool IsValid => Flags.Count > 0 && MinFreeTroops >= 0;
        [JsonIgnore] public TimeSpan Prep => TimeSpan.FromMinutes(Math.Max(0, PrepMinutes));
        [JsonIgnore] public TimeSpan EventLength => TimeSpan.FromMinutes(Math.Max(1, EventMinutes));

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return;
            field = value;
            OnPropertyChanged(name);
            OnPropertyChanged(nameof(IsValid));
        }

        protected virtual void OnPropertyChanged(string? propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
