namespace ParaPhraser.Core;

public sealed class LocalAiUnavailableException : InvalidOperationException
{
    public LocalAiUnavailableException(string message)
        : base(message)
    {
    }

    public LocalAiUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
