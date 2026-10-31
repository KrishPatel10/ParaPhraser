namespace ParaPhraser.Core;

/// <summary>
/// The provider is fine, but this model cannot answer right now, so another
/// model may work.
/// </summary>
public sealed class ModelBusyException : InvalidOperationException
{
    public ModelBusyException(string message)
        : base(message)
    {
    }

    public ModelBusyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
