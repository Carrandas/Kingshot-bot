using System;

namespace Bot.Core.Tasks
{
    /// <summary>
    /// A task that needs longer than the task runner's 15 minute timeout (Champion's Way: ten rallies of ~3-4 minutes in one run)
    /// </summary>
    public interface ILongRunningTask
    {
        TimeSpan RunTimeout { get; }
    }
}
