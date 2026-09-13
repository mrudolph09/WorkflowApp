namespace Workflow.Terminal;

/// <summary>An interactive shell running inside a Windows pseudo-console.</summary>
public interface ITerminalSession : IDisposable
{
    /// <summary>Raised for every chunk of raw bytes read from the pseudo-console.</summary>
    public event EventHandler<ReadOnlyMemory<byte>>? OutputReceived;

    /// <summary>Raised once when the hosted process ends, carrying its exit code.</summary>
    public event EventHandler<int>? Exited;

    /// <summary>True between a successful Start and Dispose.</summary>
    public bool IsRunning { get; }

    /// <summary>Starts the shell.</summary>
    /// <param name="executable">Full path to the shell executable.</param>
    /// <param name="arguments">Command-line arguments for the shell.</param>
    /// <param name="workingDirectory">Initial current directory.</param>
    /// <param name="columns">Initial pseudo-console width.</param>
    /// <param name="rows">Initial pseudo-console height.</param>
    public void Start(string executable, string arguments, string workingDirectory, int columns, int rows);

    /// <summary>Writes bytes to the pseudo-console input.</summary>
    /// <param name="data">Raw bytes, normally UTF-8 encoded keystrokes.</param>
    public void Write(ReadOnlySpan<byte> data);

    /// <summary>Resizes the pseudo-console.</summary>
    /// <param name="columns">New width in character cells.</param>
    /// <param name="rows">New height in character cells.</param>
    public void Resize(int columns, int rows);
}

/// <summary>Creates terminal sessions.</summary>
public interface ITerminalSessionFactory
{
    /// <summary>Creates a new, unstarted session.</summary>
    /// <returns>The session.</returns>
    public ITerminalSession Create();
}
