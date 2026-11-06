using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Bot.Core.Models
{
    public class TroopTrainingSettings : INotifyPropertyChanged
    {
        private bool _trainSecondHighestTier = false;

        /// <summary>
        /// Train the tier below the highest unlocked one (e.g. IX when X is unlocked): lower-tier troops can be
        /// upgraded during some events for extra points.
        /// </summary>
        [JsonPropertyName("trainSecondHighestTier")]
        public bool TrainSecondHighestTier
        {
            get => _trainSecondHighestTier;
            set
            {
                if (_trainSecondHighestTier != value)
                {
                    _trainSecondHighestTier = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}