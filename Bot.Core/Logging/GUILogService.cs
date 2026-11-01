using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace Bot.Core.Logging
{
    public class GUILogService : LogService
    {
        private readonly Action<string>? _guiLogCallback;
        
        // Default constructor for global logs
        public GUILogService(Action<string>? guiLogCallback = null) : base()
        {
            _guiLogCallback = guiLogCallback;
        }
        
        // Constructor for per-instance logs
        public GUILogService(int instanceNumber, Action<string>? guiLogCallback = null) : base(instanceNumber)
        {
            _guiLogCallback = guiLogCallback;
        }
        
        // Constructor for custom log file path
        public GUILogService(string customLogPath, Action<string>? guiLogCallback = null) : base(customLogPath)
        {
            _guiLogCallback = guiLogCallback;
        }

        public override void LogInfo(string message, object? context = null, string? category = null, [CallerMemberName] string? method = null)
        {
            // Call base implementation for console and file logging (developer logs)
            base.LogInfo(message, context, category, method);
            
            // Developer logs don't appear in GUI - only user notifications do
        }
        
        public override void LogWarning(string message, object? context = null, string? category = null, [CallerMemberName] string? method = null)
        {
            // Call base implementation for console and file logging (developer logs)
            base.LogWarning(message, context, category, method);
            
            // Developer logs don't appear in GUI - only user notifications do
        }
        
        public override void LogError(string message, Exception? exception = null, object? context = null, string? category = null, [CallerMemberName] string? method = null)
        {
            // Call base implementation for console and file logging (developer logs)
            base.LogError(message, exception, context, category, method);
            
            // Developer logs don't appear in GUI - only user notifications do
        }
        
        /// <summary>
        /// Send a user notification to the GUI (separate from developer logs)
        /// </summary>
        public void SendUserNotification(string message)
        {
            if (_guiLogCallback != null)
            {
                _guiLogCallback(message);
            }
        }
        
        /// <summary>
        /// Purges logs and notifies GUI to clear its display
        /// </summary>
        public override void PurgeLogs()
        {
            // Call base implementation to purge the log file
            base.PurgeLogs();
            
            // Notify GUI that logs have been purged
            if (_guiLogCallback != null)
            {
                _guiLogCallback("🧹 LOGS PURGED - Fresh session started");
                _guiLogCallback("=====================================");
            }
        }
        
    }
} 