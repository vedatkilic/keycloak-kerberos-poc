// IBrowser implementation: embedded WebView2, first runs off-screen (invisible);
// if the flow cannot reach the redirect (password expired, OTP enrollment, etc.) the window is shown.
using System.Windows;
using IdentityModel.OidcClient.Browser;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace WpfClient.Sample;

public class LayeredBrowser : IBrowser
{
    public async Task<BrowserResult> InvokeAsync(BrowserOptions options, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<BrowserResult>();

        var window = new Window
        {
            Width = 500, Height = 620,
            Title = "Signing In...",
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000,      // off-screen: starts invisible
            ShowInTaskbar = false,
            ShowActivated = false
        };
        var webView = new WebView2();
        window.Content = webView;
        window.Closing += (_, _) =>
            tcs.TrySetResult(new BrowserResult { ResultType = BrowserResultType.UserCancel });

        // Kerberos permission: the app grants it itself, no GPO needed
        var env = await CoreWebView2Environment.CreateAsync(null, null,
            new CoreWebView2EnvironmentOptions("--auth-server-allowlist=keycloak.bank.local"));

        window.Show();
        await webView.EnsureCoreWebView2Async(env);
        // CAUTION: NO cookie cleanup - the Keycloak SSO cookie must survive.

        webView.NavigationStarting += (_, e) =>
        {
            if (e.Uri.StartsWith(options.EndUrl, StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;             // no request is ever sent to the redirect
                tcs.TrySetResult(new BrowserResult
                {
                    ResultType = BrowserResultType.Success,
                    Response   = e.Uri       // the code is read out of the URL
                });
                window.Close();
            }
        };

        webView.NavigationCompleted += (_, _) =>
        {
            if (tcs.Task.IsCompleted) return;
            // The page loaded but no redirect came -> user interaction is required.
            window.Left = (SystemParameters.WorkArea.Width  - window.Width)  / 2;
            window.Top  = (SystemParameters.WorkArea.Height - window.Height) / 2;
            window.ShowInTaskbar = true;
            window.Activate();               // show the window ONLY now
        };

        webView.CoreWebView2.Navigate(options.StartUrl);
        return await tcs.Task;
    }
}
