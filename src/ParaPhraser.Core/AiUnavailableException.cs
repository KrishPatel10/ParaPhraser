namespace ParaPhraser.Core;

public sealed class AiUnavailableException : InvalidOperationException
{
    public AiUnavailableException(string message)
        : base(message)
    {
    }

    public AiUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
