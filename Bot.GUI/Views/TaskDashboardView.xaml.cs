using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Bot.GUI.ViewModels;

namespace Bot.GUI.Views
{
    public partial class TaskDashboardView : System.Windows.Controls.UserControl
    {
        public TaskDashboardView()
        {
            InitializeComponent();
            DataContextChanged += (_, e) =>
            {
                if (e.OldValue is TaskDashboardViewModel oldVm) oldVm.PropertyChanged -= OnViewModelChanged;
                if (e.NewValue is TaskDashboardViewModel newVm) newVm.PropertyChanged += OnViewModelChanged;
            };
        }

        // Keep the running task's row in view
        private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(TaskDashboardViewModel.RunningRow) || sender is not TaskDashboardViewModel { RunningRow: { } row }) return;
            // after a character switch the rows are new: wait until they are laid out
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (TaskList.ItemContainerGenerator.ContainerFromItem(row) is FrameworkElement container)
                    container.BringIntoView();
            });
        }
    }
}
