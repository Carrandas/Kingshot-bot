using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Bot.Core.Models
{
    /// <summary>
    /// Champion's Way: the deploy flag for the own rallies on the Elite Rebel (default 8: its hero lowers the stamina a rally
    /// costs, 20 instead of 25) and whether stamina items are used when the stamina runs short.
    /// </summary>
    public class ChampionsWaySettings : INotifyPropertyChanged
    {
        private int _rallyFlag = 8;
        private bool _useStaminaItems = true;

        /// <summary>Team flag 1-12 tapped on the deploy screen; 0 keeps the troops the game fills in</summary>
        [JsonPropertyName("rallyFlag")]
        public int RallyFlag
        {
            get => _rallyFlag;
            set => Set(ref _rallyFlag, value);
        }

        /// <summary>Short of stamina: use Governor Stamina items (+10 each) from the Get More popup after the free claim</summary>
        [JsonPropertyName("useStaminaItems")]
        public bool UseStaminaItems
        {
            get => _useStaminaItems;
            set => Set(ref _useStaminaItems, value);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
