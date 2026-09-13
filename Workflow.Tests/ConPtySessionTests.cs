using System.IO;          // REQUIRED: UseWPF=true drops System.IO from the implicit usings,
                          // so Path.GetTempPath() below is CS0103 without it.
using System.Text;
using Workflow.Terminal;

namespace Workflow.Tests;

public sealed class ConPtySessionTests
{
    private static async Task<string> RunAndCaptureAsync(string command, TimeSpan timeout)
    {
        using var session = new ConPtySession();
        var buffer = new StringBuilder();
        using var gate = new SemaphoreSlim(0, 1);

        session.OutputReceived += (_, bytes) =>
        {
            lock (buffer)
            {
                buffer.Append(Encoding.UTF8.GetString(bytes.Span));
            }

            if (gate.CurrentCount == 0)
            {
                gate.Release();
            }
        };

        session.Start(
            ShellLocator.FindShellExecutable(),
            ShellLocator.ShellArguments,
            Path.GetTempPath(),
            columns: 120,
            rows: 30);

        session.Write(Encoding.UTF8.GetBytes(command + "\r"));

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await gate.WaitAsync(TimeSpan.FromMilliseconds(200));
            lock (buffer)
            {
                if (buffer.ToString().Contains("WF_MARKER_OK", StringComparison.Ordinal))
                {
                    return buffer.ToString();
                }
            }
        }

        lock (buffer)
        {
            return buffer.ToString();
        }
    }

    [Fact]
    public async Task Start_RunsACommandAndStreamsItsOutput()
    {
        var output = await RunAndCaptureAsync("Write-Output 'WF_MARKER_OK'", TimeSpan.FromSeconds(20));

        Assert.Contains("WF_MARKER_OK", output, StringComparison.Ordinal);
    }

    // Diagnostic companion to the test above. When the pseudo-console is mis-wired the plain
    // assertion only says "expected WF_MARKER_OK, got \"\"", which is exactly the unhelpful
    // signal the Step 0 probe had to work around. This one distinguishes the three cases:
    // the shell died, the shell lived but emitted nothing, or the shell emitted the wrong text.
    [Fact]
    public async Task Start_EmitsTheLauncherFrameBeforeAnyInput()
    {
        using var session = new ConPtySession();
        var bytes = 0;
        var exitCode = (int?)null;

        session.OutputReceived += (_, chunk) => Interlocked.Add(ref bytes, chunk.Length);
        session.Exited += (_, code) => exitCode = code;

        session.Start(
            ShellLocator.FindShellExecutable(),
            ShellLocator.ShellArguments,
            Path.GetTempPath(),
            columns: 120,
            rows: 30);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && Volatile.Read(ref bytes) == 0)
        {
            await Task.Delay(100);
        }

        // CA1508 statically sees exitCode as always null because it is only ever assigned from
        // the Exited event handler, which the analyzer's single-threaded dataflow cannot prove
        // runs before this point - but it genuinely can, concurrently, which is exactly the
        // race this diagnostic message exists to distinguish.
#pragma warning disable CA1508
        Assert.True(
            Volatile.Read(ref bytes) > 0,
            exitCode is null
                ? "The shell is still running but the pseudo-console produced no bytes at all. "
                  + "The PTY is attached to the wrong pipe or is not being read - see Step 0."
                : $"The shell exited with code {exitCode} before producing any output.");
#pragma warning restore CA1508
    }

    [Fact]
    public void IsRunning_IsFalseBeforeStartAndTrueAfter()
    {
        using var session = new ConPtySession();

        Assert.False(session.IsRunning);

        session.Start(
            ShellLocator.FindShellExecutable(),
            ShellLocator.ShellArguments,
            Path.GetTempPath(),
            columns: 80,
            rows: 24);

        Assert.True(session.IsRunning);
    }

    [Fact]
    public void Start_TwiceOnTheSameSessionThrows()
    {
        using var session = new ConPtySession();
        session.Start(ShellLocator.FindShellExecutable(), ShellLocator.ShellArguments, Path.GetTempPath(), 80, 24);

        Assert.Throws<InvalidOperationException>(
            () => session.Start(ShellLocator.FindShellExecutable(), ShellLocator.ShellArguments, Path.GetTempPath(), 80, 24));
    }

    [Fact]
    public void Resize_DoesNotThrowOnALiveSession()
    {
        using var session = new ConPtySession();
        session.Start(ShellLocator.FindShellExecutable(), ShellLocator.ShellArguments, Path.GetTempPath(), 80, 24);

        session.Resize(140, 40);
    }

    [Fact]
    public void Dispose_CompletesPromptlyAndIsIdempotent()
    {
        var session = new ConPtySession();
        session.Start(ShellLocator.FindShellExecutable(), ShellLocator.ShellArguments, Path.GetTempPath(), 80, 24);

        var started = DateTime.UtcNow;
        session.Dispose();
        session.Dispose();

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10), "Dispose took too long.");
        Assert.False(session.IsRunning);
    }

    [Fact]
    public void Write_BeforeStartThrows()
    {
        using var session = new ConPtySession();

        Assert.Throws<InvalidOperationException>(() => session.Write("x"u8));
    }
}
