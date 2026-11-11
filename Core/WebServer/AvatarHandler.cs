using System.Collections.Concurrent;
using System.Text.Json;
using Dalamud.Plugin.Services;
using NetStone;
using NetStone.Search.Character;
using NetStoneCN;
using NetStoneCN.Model;
using WatsonWebserver.Core;

namespace ChatAnywhere.Core;

internal class AvatarHandler
{
    private readonly IPluginLog _log;
    private readonly AuthHandler _auth;
    private readonly Plugin _plugin;

    private static LodestoneClient? _lodestoneClient;
    private static readonly ConcurrentDictionary<string, string> AvatarCache = new();

    private static readonly IReadOnlyDictionary<string, string> RisingStonesDataCenters = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["陆行鸟"] = "拉诺西亚|幻影群岛|神意之地|红玉海|萌芽池|宇宙和音|沃仙曦染|晨曦王座",
        ["莫古力"] = "白银乡|白金幻象|神拳痕|潮风亭|拂晓之间|旅人栈桥|龙巢神殿|梦羽宝境",
        ["猫小胖"] = "紫水栈桥|延夏|静语庄园|摩杜纳|海猫茶屋|柔风海湾|琥珀原",
        ["豆豆柴"] = "水晶塔|银泪湖|太阳海岸|伊修加德|红茶川",
    }.SelectMany(pair => pair.Value.Split('|').Select(server => (server, pair.Key)))
     .ToDictionary(item => item.server, item => item.Key, StringComparer.Ordinal);

    internal AvatarHandler(Plugin plugin, IPluginLog log, AuthHandler auth)
    {
        _plugin = plugin;
        _log = log;
        _auth = auth;
    }

    internal async Task HandleGetAvatar(HttpContextBase ctx)
    {
        if (!await _auth.RequireAuth(ctx)) return;

        var nameRaw  = ctx.Request.Query.Elements["name"]  ?? string.Empty;
        var worldRaw = ctx.Request.Query.Elements["world"] ?? string.Empty;

        var name  = System.Net.WebUtility.UrlDecode(nameRaw);
        var world = System.Net.WebUtility.UrlDecode(worldRaw);

        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(world))
        {
            ctx.Response.StatusCode = 400;
            _auth.AddCorsHeaders(ctx);
            await ctx.Response.Send("{\"error\":\"name and world are required\"}");
            return;
        }

        var cacheKey = $"{name}@{world}";
        if (AvatarCache.TryGetValue(cacheKey, out var cachedUrl))
        {
            ctx.Response.StatusCode = 200;
            _auth.AddCorsHeaders(ctx);
            ctx.Response.Headers.Add("Content-Type", "application/json");
            await ctx.Response.Send(JsonSerializer.Serialize(new { avatarUrl = cachedUrl }));
            return;
        }

        try
        {
            string avatarUrl;
            if (_plugin.Config.EnableRisingStonesAvatar)
            {
                // Don't hit RisingStones search/profile APIs until the settings login check succeeded.
                if (!_plugin.IsRisingStonesLoggedIn)
                {
                    await SendAvatarResponse(ctx, string.Empty);
                    return;
                }
                avatarUrl = await GetRisingStonesAvatar(name, world);
            }
            else
            {
                _lodestoneClient ??= await LodestoneClient.GetClientAsync();
                var searchPage = await _lodestoneClient.SearchCharacter(new CharacterSearchQuery { CharacterName = name, World = world });
                var matchingEntry = searchPage?.Results?.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                var character = matchingEntry is null ? null : await matchingEntry.GetCharacter();
                avatarUrl = character?.Avatar?.ToString() ?? string.Empty;
            }

            avatarUrl = string.IsNullOrEmpty(avatarUrl) ? string.Empty : $"/avatar/image?url={Uri.EscapeDataString(avatarUrl)}";
            if (!string.IsNullOrEmpty(avatarUrl)) AvatarCache.TryAdd(cacheKey, avatarUrl);

            ctx.Response.StatusCode = 200;
            _auth.AddCorsHeaders(ctx);
            ctx.Response.Headers.Add("Content-Type", "application/json");
            await ctx.Response.Send(JsonSerializer.Serialize(new { avatarUrl }));
        }
        catch (Exception ex)
        {
            if (_plugin.Config.EnableRisingStonesAvatar && ex is RisingStonesException risingStonesException && risingStonesException.ApiCode is not null and not 10000)
            {
                _plugin.MarkRisingStonesLoginInvalid();
                if (_plugin.Config.AutoAskRelogin)
                    _plugin.RequestRisingStonesLogin();
            }
            _log.Error(ex, _plugin.Config.EnableRisingStonesAvatar
                ? "Failed to get character from Rising Stones"
                : "Failed to get character from Lodestone");
            await SendAvatarResponse(ctx, string.Empty);
        }
    }

    private async Task SendAvatarResponse(HttpContextBase ctx, string avatarUrl)
    {
        ctx.Response.StatusCode = 200;
        _auth.AddCorsHeaders(ctx);
        ctx.Response.Headers.Add("Content-Type", "application/json");
        await ctx.Response.Send(JsonSerializer.Serialize(new { avatarUrl }));
    }

    private async Task<string> GetRisingStonesAvatar(string name, string server)
    {
        if (!RisingStonesDataCenters.TryGetValue(server, out var dataCenter))
            return string.Empty;

        var uuid = await _plugin.RisingStonesClient.SearchCharacters(name, dataCenter, server);
        if (string.IsNullOrWhiteSpace(uuid)) return string.Empty;

        var character = await _plugin.RisingStonesClient.GetCharacter(uuid);
        return character?.Avatar?.ToString() ?? string.Empty;
    }
}
