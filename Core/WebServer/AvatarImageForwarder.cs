using System.Collections.Concurrent;
using System.Net;
using Dalamud.Plugin.Services;
using WatsonWebserver.Core;

namespace ChatAnywhere.Core;

/// <summary>Fetches trusted Lodestone/Rising Stones avatars on the plugin host and serves them to clients.</summary>
internal sealed class AvatarImageForwarder : IDisposable
{
    private const int MaxImageBytes = 10 * 1024 * 1024;
    private const int MaxCacheBytes = 32 * 1024 * 1024;
    private const int MaxCacheEntries = 100;
    private readonly IPluginLog _log;
    private readonly AuthHandler _auth;
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
    private readonly ConcurrentDictionary<string, (byte[] Bytes, string ContentType)> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _cacheOrder = new();
    private readonly object _cacheLock = new();
    private long _cacheBytes;

    internal AvatarImageForwarder(IPluginLog log, AuthHandler auth)
    {
        _log = log;
        _auth = auth;
    }

    internal async Task HandleGetImage(HttpContextBase ctx)
    {
        if (!await IsAuthenticated(ctx)) return;
        var rawUrl = ctx.Request.Query.Elements["url"]?.Trim().Trim('"');
        if (!TryParseAllowedUri(rawUrl, out var uri))
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.Send("Invalid avatar URL");
            return;
        }

        if (!_cache.TryGetValue(uri.AbsoluteUri, out var image))
        {
            try
            {
                using var request = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36");
                request.Headers.Referrer = uri.Host.EndsWith("gcloud.com.cn", StringComparison.OrdinalIgnoreCase)
                    ? new Uri("https://ff14risingstones.web.sdo.com/")
                    : new Uri("https://na.finalfantasyxiv.com/");
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxImageBytes)
                {
                    ctx.Response.StatusCode = (int)HttpStatusCode.BadGateway;
                    await ctx.Response.Send("Avatar source returned an error");
                    return;
                }

                var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
                if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Response.StatusCode = (int)HttpStatusCode.BadGateway;
                    await ctx.Response.Send("Avatar source did not return an image");
                    return;
                }

                await using var stream = await response.Content.ReadAsStreamAsync();
                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(chunk)) > 0)
                {
                    if (buffer.Length + read > MaxImageBytes)
                    {
                        ctx.Response.StatusCode = (int)HttpStatusCode.BadGateway;
                        await ctx.Response.Send("Avatar image is too large");
                        return;
                    }
                    buffer.Write(chunk, 0, read);
                }
                image = (buffer.ToArray(), contentType);
                AddToCache(uri.AbsoluteUri, image);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Failed to forward avatar image");
                ctx.Response.StatusCode = (int)HttpStatusCode.BadGateway;
                await ctx.Response.Send("Unable to retrieve avatar image");
                return;
            }
        }

        ctx.Response.StatusCode = 200;
        ctx.Response.Headers.Add("Content-Type", image.ContentType);
        ctx.Response.Headers.Add("Cache-Control", "private, max-age=86400");
        await ctx.Response.Send(image.Bytes);
    }

    private static bool IsAllowedHost(string host) =>
        string.Equals(host, "ff14risingstones.gcloud.com.cn", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".ff14risingstones.gcloud.com.cn", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "img.finalfantasyxiv.com", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "img2.finalfantasyxiv.com", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseAllowedUri(string? rawUrl, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(rawUrl)) return false;

        var candidates = new[] { rawUrl, Uri.UnescapeDataString(rawUrl) };
        foreach (var candidate in candidates)
        {
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var parsed)) continue;
            if (parsed.Scheme != Uri.UriSchemeHttps || !IsAllowedHost(parsed.Host)) continue;
            uri = parsed;
            return true;
        }
        return false;
    }

    private Task<bool> IsAuthenticated(HttpContextBase ctx) => _auth.RequireAuth(ctx);

    private void AddToCache(string key, (byte[] Bytes, string ContentType) image)
    {
        if (image.Bytes.Length > MaxCacheBytes) return;
        lock (_cacheLock)
        {
            if (_cache.ContainsKey(key)) return;
            while (_cache.Count >= MaxCacheEntries || _cacheBytes + image.Bytes.Length > MaxCacheBytes)
            {
                if (!_cacheOrder.TryDequeue(out var oldestKey)) break;
                if (_cache.TryRemove(oldestKey, out var oldest))
                    _cacheBytes -= oldest.Bytes.Length;
            }

            if (_cache.TryAdd(key, image))
            {
                _cacheOrder.Enqueue(key);
                _cacheBytes += image.Bytes.Length;
            }
        }
    }

    internal void ClearCache()
    {
        lock (_cacheLock)
        {
            _cache.Clear();
            _cacheOrder.Clear();
            _cacheBytes = 0;
        }
    }

    public void Dispose()
    {
        ClearCache();
        _http.Dispose();
    }
}
