using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Workflow.Services;
using Workflow.Terminal;

namespace Workflow.ViewModels;

/// <summary>Hosts one xterm.js terminal in a WebView2 and drives one pseudo-console.</summary>
public sealed partial class TerminalViewModel : ObservableObject, ITerminalController, IDisposable
{
    private const string VirtualHost = "workflow.terminal";
    private const string CarriageReturn = "\r";
    private static readonly TimeSpan CoalesceInterval = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan SubmitDelay = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(10);

    private readonly IWebViewEnvironmentProvider _environmentProvider;
    private readonly ITerminalSessionFactory _sessionFactory;
    private readonly Dispatcher _dispatcher;
    private readonly List<byte> _pending = [];
    private readonly object _pendingGate = new();

    private DispatcherTimer? _flushTimer;
    private WebView2? _webView;
    // CA2213 cannot trace disposal through Interlocked.Exchange(ref field, ...) followed by
    // disposing the local it returns - but that is exactly how DisposeSession() disposes both
    // _session and _sessionLifetime below, and Dispose() always calls DisposeSession().
#pragma warning disable CA2213
    private ITerminalSession? _session;
    private TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Cancels a pending paste submit when the session it belonged to is replaced or disposed.
    private CancellationTokenSource _sessionLifetime = new();
#pragma warning restore CA2213
    private long _outputCount;
    private int _columns = 120;
    private int _rows = 30;
    private int _disposed;

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private bool _isTerminalAvailable;

    [ObservableProperty]
    private string? _terminalErrorMessage;

    /// <summary>Creates the view model.</summary>
    /// <param name="environmentProvider">Shared WebView2 environment.</param>
    /// <param name="sessionFactory">Creates pseudo-console sessions.</param>
    /// <param name="dispatcher">UI dispatcher.</param>
    public TerminalViewModel(
        IWebViewEnvironmentProvider environmentProvider,
        ITerminalSessionFactory sessionFactory,
        Dispatcher dispatcher)
    {
        _environmentProvider = environmentProvider;
        _sessionFactory = sessionFactory;
        _dispatcher = dispatcher;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Seeded to "now", never to <see cref="DateTimeOffset.MinValue"/>. With MinValue the settle
    /// loop would compute an enormous idle time on its first iteration, decide the terminal was
    /// quiet before the launcher had drawn anything, snapshot an empty buffer and send the prompt
    /// into a still-blocking confirmation dialog (spec section 7.3, Gate A).
    /// </remarks>
    public DateTimeOffset LastOutputUtc { get; private set; } = DateTimeOffset.UtcNow;

    /// <inheritdoc />
    public long OutputCount => Interlocked.Read(ref _outputCount);

    /// <inheritdoc />
    public Task WaitUntilReadyAsync(CancellationToken cancellationToken) =>
        _ready.Task.WaitAsync(cancellationToken);

    /// <summary>Initialises the WebView2 and navigates it to the terminal page.</summary>
    /// <param name="webView">The control from TerminalView.xaml.</param>
    public async Task AttachAsync(WebView2 webView)
    {
        ArgumentNullException.ThrowIfNull(webView);

        // Re-selecting a tab raises Loaded again on a still-attached view model (spec section
        // 12.4). Re-navigating here would blank a live terminal and start a second flush timer.
        if (ReferenceEquals(_webView, webView) && webView.CoreWebView2 is not null)
        {
            return;
        }

        _webView = webView;
        _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var environment = await _environmentProvider.GetAsync();
            await webView.EnsureCoreWebView2Async(environment);

            // CoreWebView2 is annotated nullable because it is null before EnsureCoreWebView2Async
            // completes; the WebView2 SDK does not carry a [MemberNotNull] attribute to tell the
            // compiler that, but a successful await above guarantees it is set.
            var core = webView.CoreWebView2!;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;

            core.SetVirtualHostNameToFolderMapping(
                VirtualHost,
                Path.Combine(AppContext.BaseDirectory, "Assets", "Terminal"),
                CoreWebView2HostResourceAccessKind.DenyCors);

            core.WebMessageReceived += OnWebMessageReceived;
            core.Navigate($"https://{VirtualHost}/terminal.html");

            _flushTimer = new DispatcherTimer(DispatcherPriority.Render, _dispatcher)
            {
                Interval = CoalesceInterval,
            };
            _flushTimer.Tick += OnFlushTick;
            _flushTimer.Start();

            // `ready` is a PRECONDITION, not a notification. Until terminal.js has installed its
            // message listener, `clear`, PTY output and snapshot requests are all dropped - and
            // the launcher startup screen is exactly what the auto-answer rules must match.
            // IsTerminalAvailable therefore flips AFTER the handshake, never after Navigate.
            await _ready.Task.WaitAsync(ReadyTimeout);

            IsTerminalAvailable = true;
        }
        catch (TimeoutException)
        {
            IsTerminalAvailable = false;
            TerminalErrorMessage =
                "Die Terminal-Oberflaeche hat sich nicht innerhalb von 10 Sekunden gemeldet. " +
                "Bitte den Tab schliessen und neu oeffnen.";
            _ready.TrySetResult();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            IsTerminalAvailable = false;
            TerminalErrorMessage =
                "Die WebView2-Laufzeit wurde nicht gefunden. Bitte den 'Evergreen WebView2 Runtime' " +
                "von https://developer.microsoft.com/microsoft-edge/webview2/ installieren.";
            _ready.TrySetResult();
        }
        catch (InvalidOperationException ex)
        {
            IsTerminalAvailable = false;
            TerminalErrorMessage = $"Das Terminal konnte nicht initialisiert werden: {ex.Message}";
            _ready.TrySetResult();
        }
    }

    /// <inheritdoc />
    public void StartSession(string executable, string arguments, string workingDirectory)
    {
        DisposeSession();

        // A fresh session has produced nothing yet, and its idle clock starts now. Both are read
        // by the settle loop's Gate A.
        Interlocked.Exchange(ref _outputCount, 0);
        LastOutputUtc = DateTimeOffset.UtcNow;

        var session = _sessionFactory.Create();
        session.OutputReceived += OnOutputReceived;
        session.Start(executable, arguments, workingDirectory, _columns, _rows);
        _session = session;
    }

    /// <inheritdoc />
    public void ClearScreen() => PostToPage(new { type = "clear" });

    /// <inheritdoc />
    public async Task<string> SnapshotAsync(int lines, CancellationToken cancellationToken)
    {
        if (_webView?.CoreWebView2 is null)
        {
            return string.Empty;
        }

        // ExecuteScriptAsync is only used here: snapshots are low-frequency, output is not.
        var json = await _dispatcher.InvokeAsync(
            () => _webView.CoreWebView2.ExecuteScriptAsync($"window.wfSnapshot({lines})"),
            DispatcherPriority.Normal,
            cancellationToken).Task.Unwrap();

        if (string.IsNullOrEmpty(json) || json == "null")
        {
            return string.Empty;
        }

        try
        {
            return JsonSerializer.Deserialize<string>(json) ?? string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    /// <inheritdoc />
    public void Send(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        _session?.Write(Encoding.UTF8.GetBytes(text));
    }

    /// <inheritdoc />
    public async Task SendPasteAsync(string body, CancellationToken cancellationToken)
    {
        // Capture the session this paste belongs to. The orchestrator can advance inside the
        // submit delay whenever a reused task folder already satisfies a watcher (spec section
        // 8.3); the carriage return must then be dropped rather than written into the NEXT
        // launcher, where it would accept the preselected "No, exit".
        var target = _session;
        if (target is null)
        {
            return;
        }

        Write(target, BracketedPaste.Wrap(body));

        using var scope =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _sessionLifetime.Token);

        try
        {
            // A separate write: inside the paste block the TUI input box would treat the
            // carriage return as a literal newline instead of as submit.
            await Task.Delay(SubmitDelay, scope.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The session was replaced while we waited. Dropping the submit is the point.
            return;
        }

        if (!ReferenceEquals(_session, target))
        {
            return;
        }

        Write(target, CarriageReturn);
    }

    private static void Write(ITerminalSession session, string text) =>
        session.Write(Encoding.UTF8.GetBytes(text));

    /// <inheritdoc />
    public void DisposeSession()
    {
        // Cancel any paste submit still waiting out its delay for the outgoing session, so its
        // carriage return can never reach the session that replaces it (spec section 6.5).
        var lifetime = Interlocked.Exchange(ref _sessionLifetime, new CancellationTokenSource());
        lifetime.Cancel();
        lifetime.Dispose();

        var session = Interlocked.Exchange(ref _session, null);
        if (session is null)
        {
            return;
        }

        session.OutputReceived -= OnOutputReceived;
        session.Dispose();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_flushTimer is not null)
        {
            _flushTimer.Tick -= OnFlushTick;
            _flushTimer.Stop();
            _flushTimer = null;
        }

        DisposeSession();

        if (_webView?.CoreWebView2 is not null)
        {
            _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
        }

        _webView?.Dispose();
        _webView = null;
    }

    [RelayCommand]
    private void SendInput()
    {
        if (string.IsNullOrEmpty(InputText))
        {
            return;
        }

        Send(InputText + "\r");
        InputText = string.Empty;
    }

    private void OnOutputReceived(object? sender, ReadOnlyMemory<byte> bytes)
    {
        LastOutputUtc = DateTimeOffset.UtcNow;

        // Gate A in the settle loop needs to tell "the launcher has not drawn yet" apart from
        // "the screen has gone quiet"; a counter is the cheapest way to say so.
        Interlocked.Increment(ref _outputCount);

        lock (_pendingGate)
        {
            _pending.AddRange(bytes.Span);
        }
    }

    private void OnFlushTick(object? sender, EventArgs e)
    {
        byte[] payload;

        lock (_pendingGate)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            payload = [.. _pending];
            _pending.Clear();
        }

        PostToPage(new { type = "out", b64 = Convert.ToBase64String(payload) });
    }

    private void PostToPage(object message)
    {
        if (_webView?.CoreWebView2 is null)
        {
            return;
        }

        _webView.CoreWebView2.PostWebMessageAsString(JsonSerializer.Serialize(message));
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(e.TryGetWebMessageAsString());
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("type", out var typeElement))
            {
                return;
            }

            switch (typeElement.GetString())
            {
                case "ready":
                    // Non-nullable: a null-conditional here would be dead code (CA1508).
                    _ready.TrySetResult();
                    break;

                case "in":
                    if (document.RootElement.TryGetProperty("b64", out var input))
                    {
                        _session?.Write(Convert.FromBase64String(input.GetString() ?? string.Empty));
                    }

                    break;

                case "resize":
                    if (document.RootElement.TryGetProperty("cols", out var cols) &&
                        document.RootElement.TryGetProperty("rows", out var rows))
                    {
                        _columns = Math.Max(1, cols.GetInt32());
                        _rows = Math.Max(1, rows.GetInt32());
                        _session?.Resize(_columns, _rows);
                    }

                    break;

                default:
                    break;
            }
        }
    }
}
