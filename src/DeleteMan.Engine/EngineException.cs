namespace DeleteMan.Engine;

/// <summary>
/// A rejected <see cref="EngineHost"/> command (bad id, unknown job, a deletion already running...). The message is
/// meant for the user and is shown verbatim.
/// </summary>
public sealed class EngineException : Exception
{
    /// <summary>Creates the exception with a user-facing message.</summary>
    public EngineException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a user-facing message and the failure behind it.</summary>
    public EngineException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
