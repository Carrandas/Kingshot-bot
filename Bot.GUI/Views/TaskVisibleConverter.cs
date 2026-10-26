using System;
using System.Globalization;
using System.Linq;
using System.Windows.Data;
using Bot.Core.Models;

namespace Bot.GUI.Views
{
    /// <summary>Settings > Tasks tick: [task, account(, HiddenTasks.Count to re-evaluate)] → true when the task shows in the Bot tab's task list.</summary>
    public class TaskVisibleConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 2 || values[1] is not AccountSettings account) return false;
            TaskType task;
            if (values[0] is TaskType t) task = t;
            else if (values[0] is string s && Enum.TryParse(s, out TaskType parsed)) task = parsed;
            else return false;
            return account.HiddenTasks?.Contains(task) != true;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            targetTypes.Select(_ => System.Windows.Data.Binding.DoNothing).ToArray();
    }
}
