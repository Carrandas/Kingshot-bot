using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Bot.Core.Models; // Required for ResourceType and FarmingTarget

namespace Bot.GUI.ViewModels
{
    public class FarmingTargetViewModel : INotifyPropertyChanged
    {
        public FarmingTarget Model { get; }
        private readonly ConfigViewModel _parentViewModel;

        public FarmingTargetViewModel(FarmingTarget model, ConfigViewModel parentViewModel)
        {
            Model = model;
            _parentViewModel = parentViewModel;
        }

        public ResourceType ResourceType
        {
            get => Model.ResourceType;
            set
            {
                if (Model.ResourceType != value)
                {
                    Model.ResourceType = value;
                    OnPropertyChanged();
                    _parentViewModel.SaveConfigIfNotApplying();
                }
            }
        }

        public int Level
        {
            get => Model.Level;
            set
            {
                if (Model.Level != value)
                {
                    Model.Level = value;
                    OnPropertyChanged();
                    _parentViewModel.SaveConfigIfNotApplying();
                }
            }
        }

#pragma warning disable S2325 // bound from XAML, so it has to be an instance property
        public IEnumerable<ResourceType> AllResourceTypes =>
            Enum.GetValues(typeof(ResourceType)).Cast<ResourceType>();
#pragma warning restore S2325

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
} 