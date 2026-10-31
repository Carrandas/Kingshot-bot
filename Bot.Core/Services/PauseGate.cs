using System.Threading;
using System.Threading.Tasks;

namespace Bot.Core.Services
{
    /// <summary>
    /// State of the Pause button. Every emulator input (tap, swipe, key) waits here while paused,
    /// so a pause holds the bot at its next action instead of only before the next task.
    /// </summary>
    public static class PauseGate
    {
        private static readonly ManualResetEventSlim Open = new ManualResetEventSlim(true);

        public static bool IsPaused => !Open.IsSet;

        public static WaitHandle WaitHandle => Open.WaitHandle;

        public static void Pause() => Open.Reset();

        public static void Resume() => Open.Set();

        /// <summary>Returns at once when not paused; otherwise waits for Resume (or Stop, which resumes) or cancellation.</summary>
        public static Task WaitAsync(CancellationToken cancellationToken = default)
        {
            if (Open.IsSet) return Task.CompletedTask;
            return Task.Run(() => Open.Wait(cancellationToken), cancellationToken);
        }
    }
}
