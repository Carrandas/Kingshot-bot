using System;
using System.Globalization;
using System.Linq;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Bot.Core.Models
{
    /// <summary>
    /// Castle Battle (Bot tab > Castle Battle): the team flag for joining attack rallies (offensive) and for defending a
    /// tower or castle we hold (defensive). Each flag keeps its own troops and ratio in the game.
    /// Scheduled like Viking Vengeance: Read events saves the Task List's "Castle Battle" time in <see cref="NextBattleUtc"/>,
    /// and the mode starts <see cref="PrepMinutes"/> before it (recall, teleport near the castle), then runs <see cref="MaxHours"/>.
    /// </summary>
    /// <summary>A city's map spot (X, Y) before the teleport of the battle at <see cref="BattleUtc"/></summary>
    public sealed class HomeSpot
    {
        [JsonPropertyName("x")] public int X { get; set; }
        [JsonPropertyName("y")] public int Y { get; set; }
        [JsonPropertyName("battleUtc")] public DateTime BattleUtc { get; set; }

        public override string ToString() => $"X:{X} Y:{Y}";
    }

    public class CastleBattleSettings : INotifyPropertyChanged
    {
        private int _offensiveTeam = 1;
        private int _defensiveTeam = 2;
        private int _healAmount = 300;
        private int _reinforceIntervalMinutes = 2;
        private int _maxHours = 5;
        private bool _enabled = true;
        private bool _teleport = true;
        private int _prepMinutes = 15;
        private DateTime? _nextBattleUtc;

        /// <summary>Start the mode by itself before a Castle Battle the Task List showed</summary>
        [JsonPropertyName("enabled")]
        public bool Enabled { get => _enabled; set => SetFlag(ref _enabled, value); }

        /// <summary>In the prep, once the troops are home: teleport near the castle, next to the alliance</summary>
        [JsonPropertyName("teleport")]
        public bool Teleport { get => _teleport; set => SetFlag(ref _teleport, value); }

        /// <summary>Minutes before the start to stop the cycle, call the troops home and teleport</summary>
        [JsonPropertyName("prepMinutes")]
        public int PrepMinutes { get => _prepMinutes; set => Set(ref _prepMinutes, value); }

        /// <summary>Start of the next Castle Battle (UTC); null = not known</summary>
        [JsonPropertyName("nextBattleUtc")]
        public DateTime? NextBattleUtc
        {
            get => _nextBattleUtc;
            set
            {
                var utc = value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : (DateTime?)null;
                if (_nextBattleUtc == utc) return;
                _nextBattleUtc = utc;
                OnPropertyChanged(nameof(NextBattleUtc));
                OnPropertyChanged(nameof(NextBattleUtcText));
            }
        }

        /// <summary>The GUI's text box for <see cref="NextBattleUtc"/>: "yyyy-MM-dd HH:mm" in UTC, empty = not known</summary>
        [JsonIgnore]
        public string NextBattleUtcText
        {
            get => NextBattleUtc?.ToString(BearHuntSettings.TimeFormat, CultureInfo.InvariantCulture) ?? string.Empty;
            set
            {
                if (string.IsNullOrWhiteSpace(value)) NextBattleUtc = null;
                else if (DateTime.TryParseExact(value.Trim(), BearHuntSettings.TimeFormat, CultureInfo.InvariantCulture,
                             DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)) NextBattleUtc = parsed;
            }
        }

        [JsonIgnore] public TimeSpan Prep => TimeSpan.FromMinutes(Math.Max(0, PrepMinutes));
        [JsonIgnore] public TimeSpan EventLength => TimeSpan.FromHours(Math.Max(1, MaxHours));

        /// <summary>Team flag (1-12 at the top of the deploy screen) for joining rallies</summary>
        [JsonPropertyName("offensiveTeam")]
        public int OffensiveTeam { get => _offensiveTeam; set => Set(ref _offensiveTeam, value); }




        /// <summary>Team flag for the garrison and the reinforcements of a building we hold</summary>
        [JsonPropertyName("defensiveTeam")]
        public int DefensiveTeam { get => _defensiveTeam; set => Set(ref _defensiveTeam, value); }




        /// <summary>Troops per Heal tap; lowered for the rest of the battle when the game asks for speedups</summary>
        [JsonPropertyName("healAmount")]
        public int HealAmount { get => _healAmount; set => Set(ref _healAmount, value); }

        /// <summary>The heal batch per character (by its name key), learned in the battle; <see cref="HealAmount"/> until then</summary>
        [JsonPropertyName("healAmounts")]
        public System.Collections.Generic.Dictionary<string, int> HealAmounts { get; set; } = new();

        public int HealAmountFor(string character) =>
            HealAmounts.TryGetValue(KeyFor(character), out int amount) ? amount : HealAmount;

        public void SetHealAmountFor(string character, int amount)
        {
            string key = KeyFor(character);
            if (HealAmounts.TryGetValue(key, out int old) && old == amount) return;
            HealAmounts[key] = amount;
            OnPropertyChanged(nameof(HealAmounts));
        }

        /// <summary>The saved key of this character, matched like the roster ("Asi" and "Asli" are one), else a new one</summary>
        private string KeyFor(string character) => KeyIn(HealAmounts.Keys, character);

        private static string KeyIn(System.Collections.Generic.IEnumerable<string> keys, string character) =>
            keys.FirstOrDefault(k => Services.CharacterRoster.SameName(k, character)) ?? Services.CharacterRoster.NormalizeKey(character);

        /// <summary>Where a character's city stood before the teleport, per character: to move back to after the battle</summary>
        [JsonPropertyName("homeSpots")]
        public System.Collections.Generic.Dictionary<string, HomeSpot> HomeSpots { get; set; } = new();

        public HomeSpot? HomeSpotFor(string character) =>
            HomeSpots.TryGetValue(KeyIn(HomeSpots.Keys, character), out var spot) ? spot : null;

        /// <summary>
        /// Saves the city's spot before the teleport. False (kept) when this battle's spot is already saved: a prep run
        /// again after the teleport would save the spot next to the castle.
        /// </summary>
        public bool SetHomeSpotFor(string character, int x, int y, DateTime battleUtc)
        {
            string key = KeyIn(HomeSpots.Keys, character);
            if (HomeSpots.TryGetValue(key, out var old) && old.BattleUtc == battleUtc) return false;
            HomeSpots[key] = new HomeSpot { X = x, Y = y, BattleUtc = battleUtc };
            OnPropertyChanged(nameof(HomeSpots));
            return true;
        }

        [JsonPropertyName("reinforceIntervalMinutes")]
        public int ReinforceIntervalMinutes { get => _reinforceIntervalMinutes; set => Set(ref _reinforceIntervalMinutes, value); }

        /// <summary>The battle mode stops by itself after this many hours (the event lasts 5)</summary>
        [JsonPropertyName("maxHours")]
        public int MaxHours { get => _maxHours; set => Set(ref _maxHours, value); }

        /// <summary>The teams are flags 1-12</summary>
        [JsonIgnore]
        public bool IsValid => TeamFlags.IsFlag(OffensiveTeam) && TeamFlags.IsFlag(DefensiveTeam);

        public event PropertyChangedEventHandler? PropertyChanged;

        private void SetFlag(ref bool field, bool value, [CallerMemberName] string? name = null)
        {
            if (field == value) return;
            field = value;
            OnPropertyChanged(name);
        }

        private void Set(ref int field, int value, [CallerMemberName] string? name = null)
        {
            if (field == value) return;
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
