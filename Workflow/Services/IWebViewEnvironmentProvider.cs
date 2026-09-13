using Microsoft.Web.WebView2.Core;

namespace Workflow.Services;

/// <summary>Supplies the single CoreWebView2Environment shared by every terminal.</summary>
public interface IWebViewEnvironmentProvider
{
    /// <summary>Creates the environment on first call and returns the same instance afterwards.</summary>
    /// <returns>The shared environment.</returns>
    public Task<CoreWebView2Environment> GetAsync();
}
