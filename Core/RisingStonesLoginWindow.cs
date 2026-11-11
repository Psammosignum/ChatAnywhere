using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Windows.Forms;

namespace ChatAnywhere.Core;

internal sealed record RisingStonesWebSession(string Cookie, string UserAgent);

/// <summary>WebView2 登录窗口；通过登录接口验证会话，不以 Cookie 存在作为成功条件。</summary>
internal sealed class RisingStonesLoginWindow
{
    internal const string LoginUrl = "https://ff14risingstones.web.sdo.com/pc/index.html#/me/info";

    public Task<RisingStonesWebSession?> ShowAsync(Func<string, string, CancellationToken, Task<bool>> verifyLoginAsync, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Rising Stones embedded login requires the Windows WebView2 Runtime. On Wine/Proton, install the runtime into the same prefix used by XIVLauncher/Dalamud and retry.");

        var completion = new TaskCompletionSource<RisingStonesWebSession?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => RunWindow(completion, verifyLoginAsync, cancellationToken)) { IsBackground = true, Name = "ChatAnywhere RisingStones WebView2" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void RunWindow(TaskCompletionSource<RisingStonesWebSession?> completion, Func<string, string, CancellationToken, Task<bool>> verifyLoginAsync, CancellationToken cancellationToken)
    {
        var profilePath = Path.Combine(Path.GetTempPath(), "ChatAnywhere-WebView2-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var form = new Form { Text = "ChatAnywhere - Rising Stones Login", Width = 1000, Height = 760, StartPosition = FormStartPosition.CenterScreen };
            using var view = new WebView2 { Dock = DockStyle.Fill };
            var statusLabel = new Label { Dock = DockStyle.Bottom, Height = 28, TextAlign = System.Drawing.ContentAlignment.MiddleLeft, Text = "等待石之家页面加载…" };
            form.Controls.Add(view);
            form.Controls.Add(statusLabel);
            var pollTimer = new System.Windows.Forms.Timer { Interval = 10_000 };
            var checking = false;

            async Task<string?> ReadSessionCookieAsync()
            {
                if (view.CoreWebView2 is null) return null;
                var cookies = await view.CoreWebView2.CookieManager.GetCookiesAsync("https://apiff14risingstones.web.sdo.com/");
                var session = cookies.FirstOrDefault(c => string.Equals(c.Name, "ff14risingstones", StringComparison.Ordinal));
                return session is null || string.IsNullOrWhiteSpace(session.Value) ? null : $"{session.Name}={session.Value}";
            }

            async Task CheckLoginAsync()
            {
                if (checking || completion.Task.IsCompleted || cancellationToken.IsCancellationRequested || view.CoreWebView2 is null) return;
                checking = true;
                try
                {
                    statusLabel.Text = "正在检查石之家登录状态…";
                    var cookie = await ReadSessionCookieAsync();
                    if (cookie is null)
                    {
                        statusLabel.Text = "尚未检测到会话，登录后会自动检查；也可手动关闭。";
                        return;
                    }

                    var userAgent = view.CoreWebView2.Settings.UserAgent;
                    if (!await verifyLoginAsync(cookie, userAgent, cancellationToken)) return;

                    completion.TrySetResult(new RisingStonesWebSession(cookie, userAgent));
                    pollTimer.Stop();
                    form.Close();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    statusLabel.Text = $"登录校验暂不可用，将继续重试：{ex.Message}";
                }
                finally
                {
                    checking = false;
                    if (!completion.Task.IsCompleted && statusLabel.Text == "正在检查石之家登录状态…")
                        statusLabel.Text = "Cookie 已检测到但登录尚未确认，将继续等待并重试。";
                }
            }

            form.Shown += async (_, _) =>
            {
                try
                {
                    var environment = await CoreWebView2Environment.CreateAsync(null, profilePath);
                    await view.EnsureCoreWebView2Async(environment);
                    view.CoreWebView2.NavigationCompleted += async (_, args) =>
                    {
                        statusLabel.Text = args.IsSuccess ? "页面已跳转，立即检查登录状态…" : "页面加载未完成，等待后续跳转或定时重试…";
                        await CheckLoginAsync();
                    };
                    pollTimer.Tick += async (_, _) => await CheckLoginAsync();
                    view.Source = new Uri(LoginUrl);
                    pollTimer.Start();
                }
                catch (Exception ex)
                {
                    completion.TrySetException(new InvalidOperationException("WebView2 initialization failed. Ensure the Microsoft Edge WebView2 Runtime is installed in this Windows installation or Wine/Proton prefix.", ex));
                    form.Close();
                }
            };
            form.FormClosing += async (_, e) =>
            {
                if (completion.Task.IsCompleted || cancellationToken.IsCancellationRequested) return;

                // 用户手动关闭时，最后读取一次 WebView Cookie；SettingsWindow 会继续验证它。
                e.Cancel = true;
                pollTimer.Stop();
                try
                {
                    var cookie = await ReadSessionCookieAsync();
                    var userAgent = view.CoreWebView2?.Settings.UserAgent;
                    completion.TrySetResult(cookie is null || string.IsNullOrWhiteSpace(userAgent)
                        ? null
                        : new RisingStonesWebSession(cookie, userAgent));
                }
                catch { completion.TrySetResult(null); }
                form.BeginInvoke(new Action(form.Close));
            };
            form.FormClosed += async (_, _) =>
            {
                pollTimer.Stop();
                pollTimer.Dispose();
                if (!completion.Task.IsCompleted) completion.TrySetResult(null);
                try { if (view.CoreWebView2 is not null) await view.CoreWebView2.Profile.ClearBrowsingDataAsync(); } catch { }
            };
            using var registration = cancellationToken.Register(() =>
            {
                try { form.BeginInvoke(new Action(form.Close)); } catch { }
                completion.TrySetCanceled(cancellationToken);
            });
            Application.Run(form);
        }
        catch (Exception ex) { completion.TrySetException(ex); }
        finally
        {
            try { if (Directory.Exists(profilePath)) Directory.Delete(profilePath, true); } catch { }
        }
    }
}
