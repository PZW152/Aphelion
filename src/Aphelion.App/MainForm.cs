using System.Reflection;
using System.Runtime.InteropServices;
using Aphelion.App.Bridge;
using Aphelion.App.Platform;
using Aphelion.Core;
using Aphelion.Core.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Aphelion.App;

/// <summary>
/// The only window. The native caption is removed (WM_NCCALCSIZE) while the frame stays, so snapping, resizing,
/// shadows and rounded corners remain native; the page draws its own title bar and marks it with
/// <c>app-region: drag</c>, which WebView2 turns into a real caption.
/// </summary>
internal sealed class MainForm : Form, IHostWindow
{
    public const string Origin = "https://app.aphelion.internal";
    private static readonly Color Panel = Color.FromArgb(0x1A, 0x1F, 0x22);

    private readonly AppServices _services;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Panel };
    private readonly CancellationTokenSource _shutdown = new();
    private Api? _api;

    public MainForm(AppServices services)
    {
        _services = services;
        Text = AppInfo.Name;
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        BackColor = Panel;
        StartPosition = FormStartPosition.Manual;
        Controls.Add(_web);
    }

    /// <summary>Sizes are logical (96-DPI) pixels scaled to the monitor, then clamped to its work area and centred.</summary>
    private void ApplyInitialBounds()
    {
        var scale = DeviceDpi / 96.0;
        var area = Screen.FromHandle(Handle).WorkingArea;
        MinimumSize = new Size((int)(1040 * scale), (int)(680 * scale));
        var size = new Size(Math.Min((int)(1280 * scale), (int)(area.Width * 0.92)), Math.Min((int)(800 * scale), (int)(area.Height * 0.92)));
        Bounds = new Rectangle(area.Left + (area.Width - size.Width) / 2, area.Top + (area.Height - size.Height) / 2, size.Width, size.Height);
    }

    protected override async void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyInitialBounds();
        var dark = 1;
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        var border = 0x00393432; // COLORREF (BGR) of #323439: a hairline that matches the panel
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_BORDER_COLOR, ref border, sizeof(int));
        try
        {
            await InitializeWebViewAsync();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(this, "Aphelion 需要 Microsoft Edge WebView2 Runtime。\n請從 https://go.microsoft.com/fwlink/p/?LinkId=2124703 安裝後重新開啟。",
                AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
        catch (Exception ex)
        {
            Log.Error("WebView2 initialisation failed", ex);
            MessageBox.Show(this, "介面無法載入：" + ex.Message, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private async Task InitializeWebViewAsync()
    {
        WebView2Loader.Prepare(_services.Paths);
        var options = new CoreWebView2EnvironmentOptions
        {
            Language = System.Globalization.CultureInfo.CurrentUICulture.Name,
            AreBrowserExtensionsEnabled = false,
        };
        // Keep the browser profile inside Aphelion's cache instead of beside the executable.
        var env = await CoreWebView2Environment.CreateAsync(null, _services.Paths.WebViewData, options);
        await _web.EnsureCoreWebView2Async(env);
        var core = _web.CoreWebView2;
        var settings = core.Settings;
        settings.IsNonClientRegionSupportEnabled = true;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDevToolsEnabled = Environment.GetEnvironmentVariable("APHELION_DEVTOOLS") == "1";
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsPinchZoomEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsSwipeNavigationEnabled = false;

        core.AddWebResourceRequestedFilter(Origin + "/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnResourceRequested;
        core.NavigationStarting += (_, a) =>
        {
            if (a.Uri.StartsWith(Origin + "/", StringComparison.Ordinal)) return;
            a.Cancel = true; // the page never navigates away; external links open in the browser
            TryOpenExternal(a.Uri);
        };
        core.NewWindowRequested += (_, a) => { a.Handled = true; TryOpenExternal(a.Uri); };
        core.WebMessageReceived += OnWebMessage;
        core.PermissionRequested += (_, a) => a.State = CoreWebView2PermissionState.Deny;

        _api = new Api(_services, this);
        core.Navigate(Origin + "/index.html");
    }

    private static void TryOpenExternal(string uri)
    {
        try { Shell.OpenUrl(uri); }
        catch (Exception e) when (e is ArgumentException or System.ComponentModel.Win32Exception) { Log.Warn($"Refused to open {uri}"); }
    }

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!e.Source.StartsWith(Origin + "/", StringComparison.Ordinal) || _api is null) return;

        // Dropped files arrive as additional objects carrying their real paths.
        if (e.AdditionalObjects is { Count: > 0 } objects)
        {
            var paths = objects.OfType<CoreWebView2File>().Select(f => f.Path).ToList();
            string context;
            try { context = e.TryGetWebMessageAsString(); } catch (ArgumentException) { context = ""; }
            if (paths.Count > 0) _api.OnFilesDropped(context, paths);
            return;
        }

        string message;
        try { message = e.TryGetWebMessageAsString(); }
        catch (ArgumentException) { return; }
        var response = await Task.Run(() => _api.Router.HandleAsync(message, _shutdown.Token));
        Post(response);
    }

    private void OnResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var path = new Uri(e.Request.Uri).AbsolutePath.TrimStart('/');
        if (path.Length == 0) path = "index.html";
        var stream = UiResources.TryGetValue(path, out var resourceName) ? Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName) : null;
        var env = _web.CoreWebView2.Environment;
        if (stream is null)
        {
            e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", "Content-Type: text/plain");
            return;
        }
        var headers = $"Content-Type: {MimeType(path)}\r\n" +
                      "Cache-Control: no-cache\r\n" +
                      "X-Content-Type-Options: nosniff\r\n" +
                      "Content-Security-Policy: default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; " +
                      "img-src 'self' data: blob: https:; font-src 'self' data:; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-src 'none'";
        e.Response = env.CreateWebResourceResponse(stream, 200, "OK", headers);
    }

    /// <summary>Embedded UI files keyed by URL path (MSBuild may write subfolders with backslashes).</summary>
    private static readonly Dictionary<string, string> UiResources = Assembly.GetExecutingAssembly().GetManifestResourceNames()
        .Where(n => n.StartsWith("ui/", StringComparison.Ordinal))
        .ToDictionary(n => n[3..].Replace('\\', '/'), n => n, StringComparer.Ordinal);

    private static string MimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".woff2" => "font/woff2",
        ".woff" => "font/woff",
        ".ico" => "image/x-icon",
        _ => "application/octet-stream",
    };

    // ---- IHostWindow ----

    public void Post(string json)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(() => Post(json)); } catch (InvalidOperationException) { }
            return;
        }
        _web.CoreWebView2?.PostWebMessageAsJson(json);
    }

    public T OnUiThread<T>(Func<T> action) => InvokeRequired ? (T)Invoke(action) : action();

    public void Minimize() => WindowState = FormWindowState.Minimized;

    public void ToggleMaximize() => WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;

    public void CloseWindow() => Close();

    public bool IsMaximized => WindowState == FormWindowState.Maximized;

    void IHostWindow.BringToFront()
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        Native.SetForegroundWindow(Handle);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_api is not null) Post(RpcRouter.Event("window.state", new { maximized = IsMaximized }));
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _shutdown.Cancel();
        base.OnFormClosing(e);
    }

    // ---- Custom frame ----

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
        {
            // Let Windows compute the standard frame, then reclaim the caption area for the client.
            var p = Marshal.PtrToStructure<Native.NCCALCSIZE_PARAMS>(m.LParam);
            var top = p.Rect0.Top;
            base.WndProc(ref m);
            p = Marshal.PtrToStructure<Native.NCCALCSIZE_PARAMS>(m.LParam);
            p.Rect0.Top = top;
            if (Native.IsZoomed(Handle))
            {
                // A maximised window extends past the monitor by the frame thickness.
                var dpi = Native.GetDpiForWindow(Handle);
                p.Rect0.Top += Native.GetSystemMetricsForDpi(Native.SM_CYSIZEFRAME, dpi) + Native.GetSystemMetricsForDpi(Native.SM_CXPADDEDBORDER, dpi);
            }
            Marshal.StructureToPtr(p, m.LParam, false);
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _web.Dispose();
            _shutdown.Dispose();
        }
        base.Dispose(disposing);
    }
}
