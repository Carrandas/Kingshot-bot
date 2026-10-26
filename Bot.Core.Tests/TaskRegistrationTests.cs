using Bot.Core.Logging;
using Bot.Core.Models;
using Bot.Core.Tasks;
using NUnit.Framework;

namespace Bot.Core.Tests
{
    /// <summary>
    /// A new task has to be registered in several places (see CLAUDE.md "Adding a task"); these catch the ones in Bot.Core.
    /// The GUI's display names are not checked here: Bot.GUI can't be referenced while the GUI runs (locked files).
    /// </summary>
    [TestFixture]
    public class TaskRegistrationTests
    {
        // Run by TaskManager itself, never put in a task list
        private static readonly TaskType[] SystemTasks = { TaskType.Startup, TaskType.Recovery };

        private static IEnumerable<TaskType> UserTasks() => Enum.GetValues<TaskType>().Except(SystemTasks);

        private sealed class NoServices : IServiceProvider
        {
            public object? GetService(Type serviceType) => null;
        }

        [TestCaseSource(nameof(UserTasks))]
        public void EveryTaskHasARunOrder(TaskType task)
        {
            Assert.That(TaskManager.GetRunOrder(task), Is.Not.EqualTo(998), "missing from the priority dictionary in TaskManager");
        }

        // Not new TaskManager(...): its InstanceManager looks for the LDPlayer install, which never finishes without one
        [Test]
        public void EveryTaskHasAModule()
        {
            var modules = TaskManager.CreateTaskModules(new LogService(0), new NoServices());

            Assert.Multiple(() =>
            {
                foreach (var task in Enum.GetValues<TaskType>().Where(t => t != TaskType.Startup))
                {
                    Assert.That(modules.ContainsKey(task), Is.True, $"{task} missing from TaskManager.CreateTaskModules");
                    if (modules.TryGetValue(task, out var module))
                        Assert.That(module.TaskType, Is.EqualTo(task), $"{task} registered with the wrong class");
                }
            });
        }

        [Test]
        public void RunOrder_FollowsTheTaskDependencies()
        {
            Assert.Multiple(() =>
            {
                Assert.That(UserTasks().Where(t => t != TaskType.AccountDetection).Select(TaskManager.GetRunOrder),
                    Has.All.GreaterThan(TaskManager.GetRunOrder(TaskType.AccountDetection)), "Account Detection first: tasks key their state on the account ID");
                Assert.That(TaskManager.GetRunOrder(TaskType.CollectOnlineRewards), Is.LessThan(TaskManager.GetRunOrder(TaskType.AutoHunt)),
                    "online rewards give the stamina for the Intel missions");
                Assert.That(TaskManager.GetRunOrder(TaskType.SuggestionBox), Is.LessThan(TaskManager.GetRunOrder(TaskType.ResidentWelcome)),
                    "Resident Welcome's zoom-out shrinks the Suggestion Box envelope");
                Assert.That(UserTasks().Where(t => t != TaskType.ChangeAccount).Select(TaskManager.GetRunOrder),
                    Has.All.LessThan(TaskManager.GetRunOrder(TaskType.ChangeAccount)), "Change Account last");
            });
        }
    }
}
