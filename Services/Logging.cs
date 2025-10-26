using System;

namespace GoombaCast.Services
{
    public static class Logging
    {
        private static ILoggingService? _loggingService;

        public static void Initialize(ILoggingService loggingService)
        {
            _loggingService = loggingService ?? throw new ArgumentNullException(nameof(loggingService));
        }

        //TODO: consider file backing?

        public static void WriteLine(string message) 
            => _loggingService?.WriteLine(message);

        public static void Log(string message) 
            => WriteLine($"{message}");
        public static void LogError(string message) 
            => WriteLine($"[ERROR] {message}");
        public static void LogWarning(string message) 
            => WriteLine($"[WARNING] {message}");
        public static void LogDebug(string message) 
            => WriteLine($"[DEBUG] {message}");
    }
}