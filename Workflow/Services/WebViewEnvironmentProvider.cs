using System.IO;
using Microsoft.Web.WebView2.Core;

namespace Workflow.Services;

/// <inheritdoc cref="IWebViewEnvironmentProvider" />
// CA1001: owns a disposable field (_gate), so the class itself must be IDisposable.
public sealed class WebViewEnvironmentProvider : IWebViewEnvironmentProvider, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CoreWebView2Environment? _environment;

    /// <inheritdoc />
    public async Task<CoreWebView2Environment> GetAsync()
    {
        if (_environment is not null)
        {
            return _environment;
        }

        await _gate.WaitAsync();
        try
        {
            // Double-checked locking: CA1508 sees the outer "is not null" check above and
            // concludes _environment must still be null here, but a concurrent caller can have
            // set it while this call was waiting on _gate - the second check is not dead code.
#pragma warning disable CA1508
            _environment ??= await CoreWebView2Environment.CreateAsync(
#pragma warning restore CA1508
                browserExecutableFolder: null,
                userDataFolder: Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Workflow",
                    "WebView2"),
                options: null);

            return _environment;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();
}
