using System;
using System.Threading;
using System.Threading.Tasks;
using Bot.Core.Logging;

namespace Bot.Core.Services
{
    /// <summary>What <see cref="ExclusiveModeRunner"/> needs from the app that owns the cycle (the GUI)</summary>
    public interface IModeHost
    {
        bool IsBotRunning { get; }

        /// <summary>The Run on schedule cycle (not run once) is going</summary>
        bool IsOnSchedule { get; }

        Task StopCycleAsync();

        /// <summary>The Stop token; replaced by a fresh one first when it was cancelled</summary>
        CancellationToken FreshToken();

        void SetBusy(bool busy);

        void RestartSchedule();
    }

    /// <summary>
    /// Runs one exclusive mode (Castle Battle, Bear Hunt): refuses while another runs or is starting, stops the cycle,
    /// runs the mode and starts a schedule that was running again once the mode ended by itself (not after Stop).
    /// </summary>
    public sealed class ExclusiveModeRunner
    {
        private readonly IModeHost _host;
        private readonly LogService _logger;
        private bool _starting;

        public ExclusiveModeRunner(IModeHost host, LogService logger)
        {
            _host = host;
            _logger = logger;
        }

        /// <summary>A mode is starting or running. Stopping the cycle can take longer than the 30 s schedule tick.</summary>
        public bool IsBusy => _starting || ModeStatus.AnyRunning;

        /// <param name="mode">Display name, e.g. "Castle Battle"</param>
        /// <param name="icon">Log prefix, e.g. "⚔"</param>
        /// <param name="begin">Publishes the mode's status, runs after the cycle stopped</param>
        /// <param name="body">The mode's loop; ends by itself or throws OperationCanceledException on Stop</param>
        /// <param name="end">Clears the status; runs after the body, whatever the outcome</param>
        /// <returns>True when the mode ran to its end (not Stop, no error)</returns>
        public async Task<bool> RunAsync(string mode, string icon, Action begin, Func<CancellationToken, Task> body, Func<Task> end)
        {
            if (IsBusy) return false;
            _starting = true;
            try
            {
                bool wasOnSchedule = _host.IsOnSchedule;
                if (!await StopCycleAsync(mode)) return false;
                var token = _host.FreshToken();
                begin();
                _host.SetBusy(true);
                bool finished = await RunBodyAsync(mode, icon, body, token);
                _host.SetBusy(false);
                await end();
                if (finished && wasOnSchedule)
                {
                    _logger.LogInfo($"{icon} {mode} over: back to Run on schedule");
                    _host.RestartSchedule();
                }
                return finished;
            }
            finally
            {
                _starting = false;
            }
        }

        /// <summary>Stops a running cycle first. False when the bot still runs.</summary>
        private async Task<bool> StopCycleAsync(string mode)
        {
            if (_host.IsBotRunning)
            {
                _logger.LogInfo($"{mode}: stopping the cycle first", category: LogCategories.UserAction);
                await _host.StopCycleAsync();
            }
            return !_host.IsBotRunning;
        }

        private async Task<bool> RunBodyAsync(string mode, string icon, Func<CancellationToken, Task> body, CancellationToken token)
        {
            try
            {
                await Task.Run(() => body(token), token);
                return !token.IsCancellationRequested;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInfo($"{icon} {mode} stopped");
            }
            catch (Exception ex)
            {
                _logger.LogError($"{mode} failed: {ex}", category: LogCategories.TaskExecution);
            }
            return false;
        }
    }
}
