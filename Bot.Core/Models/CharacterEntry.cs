using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Bot.Core.Models
{
    /// <summary>
    /// A character on the game login of an instance, as found on the Characters screen (Profile > Settings > Characters).
    /// Change Account only switches to enabled characters.
    /// </summary>
    public class CharacterEntry : INotifyPropertyChanged
    {
        private string _name = string.Empty;
        private int? _kingdom;
        private bool _enabled = true;

        [JsonPropertyName("name")]
        public string Name
        {
            get => _name;
            set { if (_name != value) { _name = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); } }
        }

        [JsonPropertyName("kingdom")]
        public int? Kingdom
        {
            get => _kingdom;
            set { if (_kingdom != value) { _kingdom = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); } }
        }

        [JsonPropertyName("enabled")]
        public bool Enabled
        {
            get => _enabled;
            set { if (_enabled != value) { _enabled = value; OnPropertyChanged(); } }
        }

        [JsonIgnore]
        public string DisplayName => Kingdom.HasValue ? $"{Name}  (Kingdom #{Kingdom})" : Name;

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
