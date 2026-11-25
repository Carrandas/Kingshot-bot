using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Bot.Core.Models
{
    /// <summary>
    /// Alliance Mobilization: which of the character's own two missions (top row) to accept, and whether to refresh the
    /// open missions the alliance doesn't want (needs R4 or higher). An own mission that isn't kept is refreshed (free,
    /// a new one comes in 2 minutes). Open missions below the own row of a kept kind and worth MinPoints are accepted too
    /// (any rank, while attempts are left). With neither keep switch on, nothing is accepted and the own missions are left alone.
    /// </summary>
    public class AllianceMobilizationSettings : INotifyPropertyChanged
    {
        private bool _keepTerror = true;
        private bool _keepTroopTraining = true;
        private int _minPoints = 300;
        private bool _refreshOpenMissions = true;

        /// <summary>Accept an own "Defeat Terror" mission</summary>
        [JsonPropertyName("keepTerror")]
        public bool KeepTerror
        {
            get => _keepTerror;
            set => Set(ref _keepTerror, value);
        }

        /// <summary>Accept an own "Raise Power by training/promoting troops" mission</summary>
        [JsonPropertyName("keepTroopTraining")]
        public bool KeepTroopTraining
        {
            get => _keepTroopTraining;
            set => Set(ref _keepTroopTraining, value);
        }

        /// <summary>Only Terror / troop training missions worth at least this are accepted, own and open ones (default 300); a weaker own one is refreshed</summary>
        [JsonPropertyName("minPoints")]
        public int MinPoints
        {
            get => _minPoints;
            set => Set(ref _minPoints, value);
        }

        /// <summary>Refresh the open missions below the own row that aren't on the keep list (R4 and higher)</summary>
        [JsonPropertyName("refreshOpenMissions")]
        public bool RefreshOpenMissions
        {
            get => _refreshOpenMissions;
            set => Set(ref _refreshOpenMissions, value);
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
