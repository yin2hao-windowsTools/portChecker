using System;

namespace PortChecker.Services;

internal sealed class ProcessControlException : Exception
{
    public ProcessControlException(string message, bool canRetryElevated, Exception? innerException = null)
        : base(message, innerException)
    {
        CanRetryElevated = canRetryElevated;
    }

    public bool CanRetryElevated { get; }
}
