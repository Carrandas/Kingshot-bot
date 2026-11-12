using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Bot.Core.Models
{
    /// <summary>
    /// One character's gear swap for the bear: in the prep the gear moves from the normal hero to the bear hero
    /// (Unequip All on one, Quick Equip on the other); once the bear is over it moves back.
    /// </summary>
    public class HeroGearSwap : INotifyPropertyChanged
    {
        private string _character = string.Empty;
        private string _normalHero = string.Empty;
        private string _bearHero = string.Empty;
        private bool _enabled = true;

        /// <summary>The character (roster name) whose heroes are swapped</summary>
        [JsonPropertyName("character")]
        public string Character { get => _character; set => Set(ref _character, value ?? string.Empty); }

        /// <summary>The hero that wears the gear normally</summary>
        [JsonPropertyName("normalHero")]
        public string NormalHero { get => _normalHero; set => Set(ref _normalHero, value ?? string.Empty); }

        /// <summary>The hero that wears the gear during the bear</summary>
        [JsonPropertyName("bearHero")]
        public string BearHero { get => _bearHero; set => Set(ref _bearHero, value ?? string.Empty); }

        [JsonPropertyName("enabled")]
        public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

        /// <summary>Both heroes are named and differ, so there is something to swap</summary>
        [JsonIgnore]
        public bool IsComplete => NormalHero.Length > 0 && BearHero.Length > 0 && !HeroCatalog.SameHero(NormalHero, BearHero);

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
