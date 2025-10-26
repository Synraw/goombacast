using System;
using System.Collections.Concurrent;

namespace GoombaCast.Services
{
    public class LoggingService : ILoggingService
    {
        private readonly ConcurrentQueue<string> _logLines = new();
        public event EventHandler<string>? LogLineAdded;

        public void WriteLine(string message)
        {
            _logLines.Enqueue(message);
            LogLineAdded?.Invoke(this, message);
        }
    }
}