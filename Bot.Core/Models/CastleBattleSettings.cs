using System.Linq;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Bot.Core.Models
{
    /// <summary>
    /// Castle Battle (Bot tab > Castle Battle): the team flag for joining attack rallies (offensive) and for defending a
    /// tower or castle we hold (defensive). Each flag keeps its own troops and ratio in the game.
    /// </summary>
    public class CastleBattleSettings : INotifyPropertyChanged
    {
        private int _offensiveTeam = 1;
        private int _defensiveTeam = 2;
        private int _healAmount = 300;
        private int _reinforceIntervalMinutes = 2;
        private int _maxHours = 5;

        /// <summary>Team flag (1-8 at the top of the deploy screen) for joining rallies</summary>
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
        private string KeyFor(string character) =>
            HealAmounts.Keys.FirstOrDefault(k => Services.CharacterRoster.SameName(k, character)) ?? Services.CharacterRoster.NormalizeKey(character);

        [JsonPropertyName("reinforceIntervalMinutes")]
        public int ReinforceIntervalMinutes { get => _reinforceIntervalMinutes; set => Set(ref _reinforceIntervalMinutes, value); }

        /// <summary>The battle mode stops by itself after this many hours (the event lasts 5)</summary>
        [JsonPropertyName("maxHours")]
        public int MaxHours { get => _maxHours; set => Set(ref _maxHours, value); }

        /// <summary>The teams are flags 1-8</summary>
        [JsonIgnore]
        public bool IsValid => OffensiveTeam is >= 1 and <= 8 && DefensiveTeam is >= 1 and <= 8;

        public event PropertyChangedEventHandler? PropertyChanged;

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
