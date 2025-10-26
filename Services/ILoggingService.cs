using System;

namespace GoombaCast.Services
{
    public interface ILoggingService
    {
        void WriteLine(string message);
        event EventHandler<string>? LogLineAdded;
    }
}