namespace Workflow.Terminal;

/// <inheritdoc cref="ITerminalSessionFactory" />
public sealed class ConPtySessionFactory : ITerminalSessionFactory
{
    /// <inheritdoc />
    public ITerminalSession Create() => new ConPtySession();
}
