using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using KLink.Server.Data;
using KLink.Server.Model;
using KLink.Server.Util;
using KLink.Server.Ws;
using Microsoft.AspNetCore.Http;

namespace KLink.Server.Http;

/// <summary>
/// 游戏 API 处理器（ApiHandler）。
/// 路由与响应结构与 Java 版完全一致，保证官方客户端兼容。
/// </summary>
public sealed class ApiHandler
{
    /// <summary>JSON 序列化：与 Java org.json 一致(中文不转义为 \uXXXX, 紧凑无空格, 字符串内 / 转义为 \/)。</summary>
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        Encoder = new OrgJsonEncoder(),
        WriteIndented = false,
    };

    /// <summary>
    /// 模拟 org.json 的字符串转义规则：除默认行为外，ASCII 斜杠 '/' 转义为 "\/"（org.json 对 URL 也转义，
    /// 官方客户端已接受该格式）。System.Text.Json 默认不转义 '/'，导致响应字节与 Java 版不一致。
    /// </summary>
    private sealed class OrgJsonEncoder : System.Text.Encodings.Web.JavaScriptEncoder
    {
        private static readonly System.Text.Encodings.Web.JavaScriptEncoder Inner =
            System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

        public override int MaxOutputCharactersPerInputCharacter => Inner.MaxOutputCharactersPerInputCharacter + 1;

        public override bool WillEncode(int unicodeScalar)
            => unicodeScalar == '/' || Inner.WillEncode(unicodeScalar);

        public override unsafe int FindFirstCharacterToEncode(char* text, int textLength)
        {
            int inner = Inner.FindFirstCharacterToEncode(text, textLength);
            for (int i = 0; i < textLength; i++)
            {
                if (text[i] == '/')
                    return inner < 0 ? i : Math.Min(i, inner);
            }
            return inner;
        }

        public override unsafe bool TryEncodeUnicodeScalar(int unicodeScalar, char* buffer, int bufferLength, out int written)
        {
            if (unicodeScalar == '/')
            {
                if (bufferLength < 2)
                {
                    written = 0;
                    return false;
                }
                buffer[0] = '\\';
                buffer[1] = '/';
                written = 2;
                return true;
            }
            return Inner.TryEncodeUnicodeScalar(unicodeScalar, buffer, bufferLength, out written);
        }
    }

    private readonly ServerConfig _config;
    private readonly AppDatabase _database;
    private readonly AssetStore _assets;
    private readonly MatchManager _matches;
    private readonly GameWebSocketServer _webSockets;
    private readonly ActionCipher _actionCipher = new();
    private readonly Random _random = new();
    private readonly HashSet<string> _excluded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _nicknamesByAddress = new();

    public ApiHandler(ServerConfig config, AppDatabase database, AssetStore assets,
        MatchManager matches, GameWebSocketServer webSockets)
    {
        _config = config;
        _database = database;
        _assets = assets;
        _matches = matches;
        _webSockets = webSockets;
        foreach (var path in new[]
                 {
                     "/", "/session", "/.com/config", "/announcement", "/activity", "/broadcast",
                     "/ban-user", "/search-user", "/search-match", "/sendid", "/record/data",
                     "/api/check_update", "/api/check_update/pc", "/api/topbar_config",
                     "/download_config", "/clientfp", "/version", "/library",
                 })
        {
            _excluded.Add(path);
        }
    }

    // ==================== 入口 ====================

    public async Task Handle(HttpContext context)
    {
        // 缓存请求体（Kestrel 流只可读一次；Java 版同一 body 可多次解析）
        using (var ms = new MemoryStream())
        {
            await context.Request.Body.CopyToAsync(ms);
            context.Items["__klink_body"] = ms.ToArray();
        }

        string path = context.Request.Path.Value ?? "/";
        string method = context.Request.Method;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = Route(path, method, context);
            context.Response.StatusCode = result.Code;
            // 与 Java SimpleHttpServer 一致：json 响应用小写 "content-type" 头，text 用大写 "Content-Type"
            if (result.ContentType == "application/json")
                context.Response.Headers["content-type"] = "application/json";
            else
                context.Response.Headers["Content-Type"] = result.ContentType;
            if (result.Body.Length > 0)
            {
                // Content-Length 必须是 UTF-8 字节数（含中文时字节数 > 字符数，按字符数设置会被截断）
                var bytes = System.Text.Encoding.UTF8.GetBytes(result.Body);
                context.Response.ContentLength = bytes.Length;
                await context.Response.Body.WriteAsync(bytes);
            }
            ServerEvents.Log($"{method} {path} → {result.Code} ({sw.ElapsedMilliseconds}ms)");
        }
        catch (UnauthorizedException)
        {
            var error = new JsonObject
            {
                ["title"] = "401 Unauthorized",
                ["description"] = "Warning",
            };
            context.Response.StatusCode = 401;
            context.Response.Headers["content-type"] = "application/json";
            var errorBytes = System.Text.Encoding.UTF8.GetBytes(error.ToJsonString(JsonOptions));
            context.Response.ContentLength = errorBytes.Length;
            await context.Response.Body.WriteAsync(errorBytes);
            ServerEvents.Log($"{method} {path} → 401 Unauthorized（JWT 无效或过期）");
        }
        catch (NotFoundException)
        {
            context.Response.StatusCode = 404;
            context.Response.ContentType = "text/plain; charset=utf-8";
            context.Response.ContentLength = 9;
            await context.Response.WriteAsync("Not Found");
            ServerEvents.Log($"{method} {path} → 404 Not Found");
        }
        catch (Exception e)
        {
            var error = new JsonObject { ["error"] = e.ToString() };
            context.Response.StatusCode = 500;
            context.Response.Headers["content-type"] = "application/json";
            var errorBytes = System.Text.Encoding.UTF8.GetBytes(error.ToJsonString(JsonOptions));
            context.Response.ContentLength = errorBytes.Length;
            await context.Response.Body.WriteAsync(errorBytes);
            ServerEvents.Log($"{method} {path} → 500 ERROR：{e}");
        }
    }

    private (int Code, string ContentType, string Body) Route(string path, string method, HttpContext context)
    {
        if (method == "GET" && path == "/") return Json(200, Root(context));
        if (method == "GET" && path == "/.com/config") return Json(200, DotComConfig());
        if (method == "POST" && path == "/session") return Session(context);
        if (method == "GET" && path == "/library") return Json(200, _assets.Library());
        if (method == "GET" && path == "/announcement") return Json(200, Announcement());
        if (method == "GET" && path == "/activity") return Json(200, Activity());
        if (method == "GET" && (path == "/api/check_update" || path == "/api/check_update/pc")) return Json(200, CheckUpdate());
        if (method == "GET" && path == "/api/topbar_config") return Json(200, Topbar());
        if (method == "GET" && path == "/download_config") return Json(200, DownloadConfig());
        if (method == "GET" && path == "/clientfp") return Json(200, ClientFp());
        if (method == "GET" && path == "/fp/") return Json(200, FrontPage());
        if (method == "GET" && path == "/version") return Json(200, Version());
        if (method == "GET" && path == "/record/data") return Json(200, RecordData());
        // PC 客户端公开端点（照 Go 版）
        if (method == "GET" && path.StartsWith("/entitlements/")) return Json(200, new JsonObject { ["entitlements"] = new JsonArray() });
        if (method == "GET" && path == "/store/v2/") return Json(200, new JsonObject
        {
            ["items"] = new JsonArray(),
            ["provider"] = context.Request.Query["provider"].ToString(),
        });
        if (method == "POST" && path == "/broadcast") return Broadcast(context);
        if (method == "POST" && path == "/sendid") return SendId(context);
        if (method == "POST" && path == "/ban-user") return AdminNotImplemented(context);
        if (method == "POST" && (path == "/search-user" || path == "/search-match")) return EmptySearch(context);
        if (path.StartsWith("/launcher/room/")) return LauncherRoom(path, method, context);

        UserRecord user = Authenticate(path, context);
        string[] parts = Segments(path);
        if (parts.Length == 3 && parts[0] == "players"
            && (parts[2] == "library" || parts[2] == "librarynew") && method == "GET")
            return Json(200, _assets.Library());
        if (parts.Length == 3 && parts[0] == "players" && parts[2] == "packs" && method == "GET")
        {
            int playerId = ParseId(parts[1]);
            RequireSameUser(user, playerId);
            return Json(200, Packs());
        }
        if (parts.Length == 3 && parts[0] == "players" && parts[1] == "notifications" && method == "GET")
        {
            int playerId = ParseId(parts[2]);
            RequireSameUser(user, playerId);
            return Json(200, new JsonObject());
        }
        if (parts.Length == 2 && parts[0] == "items")
        {
            int playerId = ParseId(parts[1]);
            RequireSameUser(user, playerId);
            if (method == "GET") return Json(200, Items(user));
            if (method == "POST") return UpdateItem(context, user);
        }
        if (parts.Length == 2 && parts[0] == "players" && method == "PUT")
        {
            int playerId = ParseId(parts[1]);
            RequireSameUser(user, playerId);
            return UpdatePlayer(context, user);
        }
        if (parts.Length == 3 && parts[0] == "players" && parts[2] == "heartbeat" && method == "PUT")
        {
            int playerId = ParseId(parts[1]);
            RequireSameUser(user, playerId);
            return Json(200, new JsonObject());
        }
        if (parts.Length == 3 && parts[0] == "players" && parts[2] == "friends" && (method == "GET" || method == "POST"))
        {
            int playerId = ParseId(parts[1]);
            RequireSameUser(user, playerId);
            return Json(200, new JsonObject { ["friends"] = new JsonArray() });
        }
        if (parts.Length == 3 && parts[0] == "players" && parts[2] == "decks")
        {
            int playerId = ParseId(parts[1]);
            RequireSameUser(user, playerId);
            if (method == "GET") return Json(200, DecksArray(_database.ListDecks(playerId)));
            if (method == "POST") return CreateDeck(context, user);
            if (method == "PUT") return UpdateDeck(context);
        }
        if (parts.Length == 4 && parts[0] == "players" && parts[2] == "decks")
        {
            int playerId = ParseId(parts[1]);
            int deckId = ParseId(parts[3]);
            RequireSameUser(user, playerId);
            if (method == "PUT") return FillDeck(context, deckId);
            if (method == "DELETE")
            {
                _database.DeleteDeck(deckId);
                return Text(200, "OK");
            }
        }
        if ((method == "POST" || method == "DELETE")
            && (path == "/lobbyplayers" || path == "/singleplayerlobby"))
            return Lobby(context, user, method == "DELETE");
        if (parts.Length == 2 && parts[0] == "matches" && parts[1] == "v2" && method == "GET")
            return MatchesV2(context, user);
        // reconnect 必须在通用 matches/v2/{id} 分支之前（避免把 reconnect 当 matchId 解析）
        if (path == "/matches/v2/reconnect" && method == "GET")
            return Reconnect(context, user);
        if (parts.Length == 3 && parts[0] == "matches" && parts[1] == "v2")
        {
            int matchId = ParseId(parts[2]);
            if (method == "GET") return MatchStatus(user, matchId);
            if (method == "PUT") return EndMatch(context, user, matchId);
        }
        if (parts.Length == 4 && parts[0] == "matches" && parts[1] == "v2")
        {
            int matchId = ParseId(parts[2]);
            if (parts[3] == "actions")
            {
                if (method == "PUT") return PollActions(context, matchId);
                if (method == "POST") return PostAction(context, user, matchId);
                if (method == "GET") return PollActions(context, matchId);
            }
            if (parts[3] == "post" && method == "GET") return MatchPost(user, matchId);
            if (parts[3] == "mulligan" && method == "POST") return Mulligan(context, user, matchId);
        }
        if (parts.Length == 5 && parts[0] == "matches" && parts[1] == "v2" && parts[3] == "mulligan")
        {
            int matchId = ParseId(parts[2]);
            if (method == "GET" && parts[4] == "left") return MulliganSide(matchId, true);
            if (method == "GET" && parts[4] == "right") return MulliganSide(matchId, false);
        }
        throw new NotFoundException();
    }

    // ==================== 响应构造 ====================

    private static (int, string, string) Text(int code, string text)
        => (code, "text/plain; charset=utf-8", text ?? "");

    private static (int, string, string) Json(int code, JsonNode? node)
        => (code, "application/json", node?.ToJsonString(JsonOptions) ?? "{}");

    // ==================== 免认证端点 ====================

    private JsonObject Root(HttpContext context)
    {
        string baseUrl = BaseUrl(context.Request);
        var endpoints = new JsonObject
        {
            ["draft"] = baseUrl + "/draft/",
            ["email"] = baseUrl + "/email/set",
            ["lobbyplayers"] = baseUrl + "/lobbyplayers",
            ["matches"] = baseUrl + "/matches",
            ["matches2"] = baseUrl + "/matches/v2/",
            ["my_draft"] = null,
            ["my_items"] = null,
            ["my_player"] = null,
            ["players"] = baseUrl + "/players",
            ["purchase"] = baseUrl + "/store/v2/txn",
            ["root"] = baseUrl,
            ["session"] = baseUrl + "/session",
            ["store"] = baseUrl + "/store/",
            ["tourneys"] = baseUrl + "/tourney/",
            ["transactions"] = baseUrl + "/store/txn",
            ["view_offers"] = baseUrl + "/store/v2/",
        };

        UserRecord? user = OptionalUser(context.Request);
        var currentUser = new JsonObject();
        if (user is not null)
        {
            endpoints["my_draft"] = baseUrl + "/draft/" + user.Id;
            endpoints["my_items"] = baseUrl + "/items/" + user.Id;
            endpoints["my_player"] = baseUrl + "/players/" + user.Id;
            currentUser["client_id"] = user.Id;
            currentUser["exp"] = user.Id;
            currentUser["external_id"] = user.Username;
            currentUser["iat"] = TimeUtil.NowSeconds();
            currentUser["identity_id"] = user.Id;
            currentUser["iss"] = "cometkards";
            currentUser["jti"] = "";
            currentUser["language"] = "zh-Hans";
            currentUser["payment"] = "notavailable";
            currentUser["player_id"] = user.Id;
            currentUser["provider"] = "device";
            currentUser["roles"] = new JsonArray();
            currentUser["tier"] = "LIVE";
            currentUser["user_id"] = user.Id;
            currentUser["user_name"] = user.Username;
        }

        var buildInfo = new JsonObject
        {
            ["build_timestamp"] = "2025-10-13T17:31:40Z",
            ["commit_hash"] = "local-java",
            ["version"] = _config.GameVersion,
        };

        var hostInfo = new JsonObject
        {
            ["container_name"] = "kards-backend-LIVE",
            ["docker_image"] = "android-local-java",
            ["host_address"] = HostOnly(context.Request),
            ["host_name"] = "cometkards",
            ["instance_id"] = "android-local",
        };

        return new JsonObject
        {
            ["build_info"] = buildInfo,
            ["current_user"] = currentUser,
            ["endpoints"] = endpoints,
            ["host_info"] = hostInfo,
            ["server_time"] = TimeUtil.NowIso(),
            ["service_name"] = "kards-backend",
            ["tenant_name"] = "1939-kardslive",
            ["tier_name"] = "LIVE",
        };
    }

    private static JsonObject DotComConfig() => new()
    {
        ["xserver_closed"] = "",
        ["xserver_closed_header"] = "",
        ["forgot_password_url"] = "https://www.kards.com/auth/recovery?lang={lang}",
    };

    private (int, string, string) Session(HttpContext context)
    {
        var body = JsonBody(context.Request);
        string username = OptString(body, "username", "guest");
        string password = OptString(body, "password", "");
        var user = _database.GetOrCreateUser(username, password)
            ?? throw new InvalidOperationException("user not found / bad password");
        string preferredName = PreferredNameFor(context);
        if (preferredName.Length > 0 && preferredName != user.PlayerName)
            user = _database.SetPlayerName(user.Id, preferredName)!;
        string token = JwtUtil.Create(_config.JwtSecret, user.Id, user.Username, TimeUtil.NowSeconds() + 86400);
        _database.UpdateUserJwt(user.Id, token);
        user = _database.FindUserById(user.Id)!;
        return Json(200, PlayerSessionJson(context.Request, user, token));
    }

    // ==================== 房管端点 ====================

    private (int, string, string) LauncherRoom(string path, string method, HttpContext context)
    {
        if (method == "GET" && path == "/launcher/room/status")
            return Json(200, LauncherRoomStatus());
        if (method == "GET" && path == "/launcher/room/logs")
            return Json(200, ServerLog.Recent());
        if (method == "GET" && path == "/launcher/room/players")
            return Json(200, LauncherPlayers());
        if (method == "POST" && path == "/launcher/room/nickname")
        {
            var body = JsonBody(context.Request);
            string nickname = CleanNickname(OptString(body, "nickname"));
            if (nickname.Length > 0)
            {
                lock (_nicknamesByAddress)
                {
                    _nicknamesByAddress[RemoteKey(context)] = nickname;
                }
            }
            return Json(200, new JsonObject { ["ok"] = true, ["nickname"] = nickname });
        }
        if (method == "POST" && path == "/launcher/room/kick")
        {
            var body = JsonBody(context.Request);
            if (!IsAdminRequest(context.Request, body))
                return Json(403, new JsonObject { ["error"] = "forbidden" });
            int playerId = OptInt(body, "player_id", -1);
            if (playerId <= 0)
                return Json(400, new JsonObject { ["error"] = "missing player_id" });
            bool online = _webSockets.Kick(playerId);
            return Json(200, new JsonObject { ["ok"] = true, ["player_id"] = playerId, ["was_online"] = online });
        }
        throw new NotFoundException();
    }

    private JsonObject LauncherRoomStatus() => new()
    {
        ["room_name"] = _config.RoomName,
        ["host_name"] = _config.HostName,
        ["http_port"] = _config.HttpPort,
        ["ws_port"] = _config.WsPort,
        ["online_players"] = _webSockets.OnlineCount(),
        ["matches"] = _matches.MatchCount(),
        ["players"] = LauncherPlayers(),
        ["logs"] = ServerLog.Recent(),
    };

    private JsonArray LauncherPlayers()
    {
        var array = new JsonArray();
        foreach (var user in _database.ListUsers())
        {
            if (!user.IsOnline && !_matches.IsKicked(user.Id))
                continue;
            array.Add(new JsonObject
            {
                ["player_id"] = user.Id,
                ["player_name"] = user.PlayerName,
                ["player_tag"] = user.PlayerTag,
                ["username"] = user.Username,
                ["online"] = user.IsOnline,
                ["kicked"] = _matches.IsKicked(user.Id),
            });
        }
        return array;
    }

    private bool IsAdminRequest(HttpRequest request, JsonObject body)
    {
        string token = OptString(body, "admin_token");
        if (token.Length == 0)
            token = request.Headers["x-kards-room-admin"].ToString();
        return !string.IsNullOrEmpty(_config.AdminToken) && _config.AdminToken == token;
    }

    private string PreferredNameFor(HttpContext context)
    {
        string localName = CleanNickname(_config.PreferredPlayerName);
        if (IsLocalAddress(context.Connection.RemoteIpAddress) && localName.Length > 0)
            return localName;
        lock (_nicknamesByAddress)
        {
            return CleanNickname(_nicknamesByAddress.GetValueOrDefault(RemoteKey(context)) ?? "");
        }
    }

    private static string RemoteKey(HttpContext context)
    {
        string address = context.Connection.RemoteIpAddress?.ToString() ?? "";
        return address.Length == 0 ? "unknown" : address;
    }

    private static bool IsLocalAddress(IPAddress? address)
    {
        if (address is null) return false;
        return IPAddress.IsLoopback(address);
    }

    private static string CleanNickname(string? value)
    {
        string name = (value ?? "").Trim();
        if (name.Length > 24)
            name = name[..24];
        return name.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
    }

    // ==================== 会话响应 ====================

    private JsonObject PlayerSessionJson(HttpRequest request, UserRecord user, string token)
    {
        string baseUrl = BaseUrl(request);
        var json = new JsonObject
        {
            ["client_id"] = user.Id,
            ["user_id"] = user.Id,
            ["player_id"] = user.Id,
            ["jwt"] = token,
            ["jti"] = "1",
            ["player_name"] = user.PlayerName,
            ["player_tag"] = user.PlayerTag,
            ["server_time"] = CompactServerTime(),
            ["currency"] = "CNY",
            ["locale"] = "zh-hans",
            ["is_online"] = true,
            ["online_flag"] = true,
            ["is_officer"] = true,
            ["has_been_officer"] = true,
            ["stars"] = 120,
            ["gold"] = 78,
            ["diamonds"] = 91,
            ["dust"] = 0,
            ["draft_admissions"] = 1,
            ["claimable_crate_level"] = 0,
            ["email"] = null,
            ["email_reward_received"] = false,
            ["email_verified"] = false,
            ["extended_rewards"] = true,
            ["npc"] = false,
        };
        PutNationProgress(json);
        json["achievements_url"] = baseUrl + "/players/" + user.Id + "/achievements";
        json["dailymissions_url"] = baseUrl + "/players/" + user.Id + "/dailymissions";
        json["decks_url"] = baseUrl + "/players/" + user.Id + "/decks";
        json["heartbeat_url"] = baseUrl + "/players/" + user.Id + "/heartbeat";
        json["library_url"] = baseUrl + "/library";
        json["items_url"] = baseUrl + "/items/" + user.Id;
        json["packs_url"] = baseUrl + "/players/" + user.Id + "/packs";
        var decks = new JsonObject { ["headers"] = DecksArray(_database.ListDecks(user.Id)) };
        json["decks"] = decks;
        json["double_xp_end_date"] = "2026-07-03T12:13:36.889692Z";
        json["last_crate_claimed_date"] = "2026-02-24T03:12:20.011489Z";
        json["last_daily_mission_cancel"] = null;
        json["last_daily_mission_renewal"] = "2026-02-09T08:48:44.675532Z";
        json["last_logon_date"] = "2026-02-24T03:11:52.877909Z";
        json["linker_account"] = "";
        json["launch_messages"] = new JsonArray();
        json["tutorials_done"] = 0;
        json["tutorials_finished"] = TutorialsFinished();
        json["cards_blacklist"] = CardsBlacklist();
        json["new_cards"] = NewCards();
        json["new_player_login_reward"] = new JsonObject
        {
            ["day"] = 8,
            ["reset"] = "0001-01-01 00:00:00",
            ["seconds"] = 0,
        };
        json["misc"] = new JsonObject
        {
            ["createDate"] = "2026-02-09T08:48:18.482180Z",
            ["featuredAchievements"] = new JsonArray(),
        };
        json["rewards"] = new JsonObject
        {
            ["packs"] = 0,
            ["gold_max"] = 114,
            ["gold_min"] = 514,
        };
        json["season_end"] = "2027-01-01T00:00:00Z";
        json["season_id"] = 85;
        json["season_wins"] = 91;
        // 与 Java 版一致:server_options 必须是 JSON 字符串(Java serverOptions() 返回 String,
        // JSONObject.put 序列化为字符串;客户端按字符串再解析。.NET 若给对象会导致客户端解析不到 anzac 等选项)
        json["server_options"] = ServerOptions(request).ToJsonString(JsonOptions);
        json["all_knockout_tourneys"] = new JsonArray();
        json["current_knockout_tourney"] = new JsonObject();
        json["current_mini_sit_n_go"] = CurrentMiniSitNGo();
        return json;
    }

    // ==================== 玩家 ====================

    private (int, string, string) UpdatePlayer(HttpContext context, UserRecord user)
    {
        var body = JsonBody(context.Request);
        if (OptString(body, "action") == "set-name")
        {
            string value = OptString(body, "value").Trim();
            if (value.Length == 0 || value == "<anon>")
                return Json(400, new JsonObject { ["error"] = "name unavailable" });
            var updated = _database.SetPlayerName(user.Id, value)!;
            return Json(200, new JsonObject
            {
                ["player_name"] = updated.PlayerName,
                ["player_tag"] = updated.PlayerTag,
            });
        }
        return Text(200, "OK");
    }

    private JsonObject Items(UserRecord user)
    {
        var json = _assets.Items() as JsonObject ?? new JsonObject();
        var equipped = new JsonArray();
        string[] factions = { "Germany", "Britain", "Soviet", "USA", "Japan" };
        string[] slots = { "emote_1", "emote_2", "emote_3", "emote_4", "emote_5", "emote_6", "emote_7", "emote_8" };
        foreach (string slot in slots)
            foreach (string faction in factions)
                PutEquipped(equipped, user, slot, faction);
        foreach (string faction in factions)
            PutEquipped(equipped, user, "item_1", faction);
        equipped.Add(new JsonObject
        {
            ["item_id"] = ItemValue(user.Equipment.GetValueOrDefault("avatar")),
            ["slot"] = "avatar",
            ["faction"] = "NotAvailable",
        });
        json["equipped_items"] = equipped;
        return json;
    }

    private (int, string, string) UpdateItem(HttpContext context, UserRecord user)
    {
        var body = JsonBody(context.Request);
        _database.UpdateEquipment(user.Id, OptString(body, "slot"), OptString(body, "faction"), OptString(body, "item_id"));
        return Text(200, "OK");
    }

    private (int, string, string) CreateDeck(HttpContext context, UserRecord user)
    {
        var body = JsonBody(context.Request);
        string mainFaction = NormalizeFaction(body, "main_faction", "main_country", "main_nation");
        string allyFaction = OptString(body, "ally_faction");
        var deck = _database.CreateDeck(user.Id, OptString(body, "name"), mainFaction, allyFaction, OptString(body, "deck_code"))!;
        return Json(200, DeckJson(deck));
    }

    private (int, string, string) UpdateDeck(HttpContext context)
    {
        var body = JsonBody(context.Request);
        int deckId = OptInt(body, "id");
        string action = OptString(body, "action");
        if (action == "change_card_back") _database.UpdateCardBack(deckId, OptString(body, "name"));
        if (action == "rename") _database.RenameDeck(deckId, OptString(body, "name"));
        if (action == "make_favorite") _database.ToggleFavorite(deckId);
        return Text(200, "ok");
    }

    private (int, string, string) FillDeck(HttpContext context, int deckId)
    {
        var body = JsonBody(context.Request);
        if (OptString(body, "action") == "fill")
        {
            string deckCode = OptString(body, "deck_code");
            if (!IsPlayableDeckCode(deckCode))
                return Text(400, "invalid deck code");
            _database.UpdateDeckCode(deckId, deckCode);
        }
        return Text(200, "OK");
    }

    private (int, string, string) Lobby(HttpContext context, UserRecord user, bool delete)
    {
        var body = JsonBody(context.Request);
        int playerId = OptInt(body, "player_id");
        if (user is null || user.Id != playerId)
            return Text(403, "unauthorized");
        if (delete)
        {
            bool removed = _matches.RemoveFromQueues(playerId);
            return Text(removed ? 200 : 404, removed ? "OK" : "player not queued");
        }
        int deckId = OptInt(body, "deck_id");
        var deck = _database.FindDeckForUser(user.Id, deckId);
        if (deck is null)
        {
            ServerEvents.Log($"lobby 失败：玩家 {playerId} 的卡组 {deckId} 不存在");
            return Text(400, "deck not found");
        }
        if (!IsPlayableDeckCode(deck.DeckCode))
        {
            ServerEvents.Log($"lobby 失败：卡组 {deckId} 卡组代码不可用（deck_code='{deck.DeckCode}'）");
            return Text(400, "invalid deck code");
        }
        var extra = body?["extra_data"];
        string extraData = extra is JsonObject extraObj && OptString(extraObj, "match_type") == "training"
            ? "training"
            : (extra is null ? "" : extra.ToString());
        if (!_webSockets.IsOnline(playerId))
        {
            // Bot 训练模式不依赖 WS（动作走 HTTP），放行；多人匹配仍需 WS 在线（对局通知依赖它）
            if (extraData != "training")
            {
                ServerEvents.Log($"lobby 失败：玩家 {playerId} 的 WebSocket 未连接（非训练模式）");
                return Text(400, "WebSocket not connected");
            }
            ServerEvents.Log($"lobby 提示：玩家 {playerId} 的 WebSocket 未连接，训练模式放行");
        }
        bool matched = _matches.AddToQueue(playerId, deckId, extraData);
        bool waiting = _matches.IsWaiting(playerId);
        return Text(matched || waiting ? 200 : 402, matched || waiting ? "OK" : "match failed");
    }

    // ==================== 对局 ====================

    private (int, string, string) MatchesV2(HttpContext context, UserRecord user)
    {
        var match = _matches.MatchForPlayer(user.Id);
        if (match is null)
            return Text(200, "null");
        return Json(200, MatchAndStartingData(context.Request, match));
    }

    private (int, string, string) MatchStatus(UserRecord user, int matchId)
    {
        var match = _matches.MatchById(matchId);
        if (match is null) return Text(404, "missing");
        if (user.Id == match.PlayerLeft) match.LevelLoadedLeft = 1;
        if (user.Id == match.PlayerRight) match.LevelLoadedRight = 1;
        if (match.LevelLoadedLeft == 1 && match.LevelLoadedRight == 1) match.Status = "running";
        return Text(200, match.Status);
    }

    private (int, string, string) EndMatch(HttpContext context, UserRecord user, int matchId)
    {
        var match = _matches.MatchById(matchId);
        if (match is null) return Text(404, "missing");
        var body = JsonBody(context.Request);
        if (body?["a"] is not null)
        {
            var decoded = _actionCipher.Decode(body["a"]!.GetValue<string>());
            if (match.ActionSessionId == 0)
                match.ActionSessionId = decoded.ActionId;
            var payload = decoded.Payload;
            var value = payload["value"] as JsonObject;
            if (OptString(payload, "action") == "end-match" && value is not null)
            {
                match.WinnerSide = OptString(value, "winner_side");
                match.WinnerId = OptInt(value, "winner_id");
                match.CurrentTurn = 1;
                var action = new JsonObject
                {
                    ["action_id"] = match.CurrentActionId + 1,
                    ["action_type"] = "ActionEndMatch",
                    ["player_id"] = user.Id,
                    ["action_data"] = new JsonObject
                    {
                        ["winner_id"] = match.WinnerId,
                        ["reason"] = OptString(value, "result"),
                        ["winner_side"] = match.WinnerSide,
                    },
                    ["sub_actions"] = new JsonArray(),
                    ["turn_number"] = match.CurrentTurn,
                };
                int session = match.ActionSessionId == 0 ? decoded.ActionId : match.ActionSessionId;
                _matches.AppendAction(match, _actionCipher.Encode(session, action));
                match.Status = "finished";
            }
        }
        return Text(200, "OK");
    }

    private (int, string, string) PollActions(HttpContext context, int matchId)
    {
        var match = _matches.MatchById(matchId);
        if (match is null) return Text(404, "missing");
        _matches.TickBot(match);
        var body = JsonBody(context.Request);
        int minActionId = OptInt(body, "min_action_id", 1);
        var matchJson = new JsonObject
        {
            ["player_status_left"] = match.PlayerStatusLeft,
            ["player_status_right"] = match.PlayerStatusRight,
            ["status"] = match.Status,
        };
        var json = new JsonObject { ["match"] = matchJson };
        int opponentId = OptInt(body, "opponent_id", 0);
        bool opponentPolling = true;
        if (opponentId == match.PlayerLeft) opponentPolling = match.LeftOnline;
        if (opponentId == match.PlayerRight) opponentPolling = match.RightOnline;
        json["opponent_polling"] = opponentPolling;
        var actions = _matches.ActionsSince(match, Math.Max(1, minActionId));
        if (actions.Count > 0) json["actions"] = actions;
        return Json(200, json);
    }

    private (int, string, string) PostAction(HttpContext context, UserRecord user, int matchId)
    {
        var match = _matches.MatchById(matchId);
        if (match is null) return Text(404, "missing");
        var body = JsonBody(context.Request);
        if (body?["a"] is not null)
        {
            var decoded = _actionCipher.Decode(body["a"]!.GetValue<string>());
            string actionType = OptString(decoded.Payload, "action_type");
            if (actionType == "XStartOfGame" || match.ActionSessionId == 0)
                match.ActionSessionId = decoded.ActionId;
            if (actionType == "XActionStartOfTurn")
                match.ActionPlayerId = user.Id;
            var payload = ActionPayload(match, user.Id, decoded.Payload);
            _matches.AppendAction(match, _actionCipher.Encode(match.ActionSessionId, payload));
        }
        _matches.TickBot(match);
        return Text(201, "OK");
    }

    private JsonObject ActionPayload(MatchState match, int playerId, JsonObject payload)
    {
        string actionType = OptString(payload, "action_type");
        if (actionType == "XActionEndOfTurn")
            match.CurrentTurn++;
        return new JsonObject
        {
            ["action_id"] = match.CurrentActionId + 1,
            ["action_type"] = actionType,
            ["player_id"] = playerId,
            ["action_data"] = payload["action_data"]?.DeepClone(),
            ["sub_actions"] = new JsonArray(),
            ["turn_number"] = match.CurrentTurn,
        };
    }

    private (int, string, string) MatchPost(UserRecord user, int matchId)
    {
        var match = _matches.MatchById(matchId);
        if (match is null) return Text(404, "");
        if (match.Actions.Count == 0) return Text(404, "");
        string? encrypted = match.ActionsData.GetValueOrDefault(match.CurrentActionId);
        if (encrypted is null) return Text(404, "");
        var lastAction = _actionCipher.Decode(encrypted).Payload;
        if (OptString(lastAction, "action_type") != "ActionEndMatch")
            return Text(204, "");
        bool left = user.Id == match.PlayerLeft;
        bool right = user.Id == match.PlayerRight;
        if (!left && !right)
            return Text(403, "");
        var actionData = lastAction["action_data"] as JsonObject;
        string winnerSide = actionData is null
            ? match.WinnerSide
            : OptString(actionData, "winner_side", match.WinnerSide);
        bool winner = (left && winnerSide == "left") || (right && winnerSide == "right");
        var deckData = left ? match.LeftDeckData : match.RightDeckData;
        var json = new JsonObject
        {
            ["faction"] = OptString(deckData, "main_country"),
            ["is_double_xp"] = true,
            ["new_level"] = 500,
            ["new_xp"] = 0,
            ["old_level"] = 500,
            ["old_xp"] = 0,
            ["winner"] = winner,
        };
        match.EndConfirmCount++;
        if (match.EndConfirmCount >= 2)
            _matches.CleanupMatch(matchId);
        return Json(200, json);
    }

    private JsonObject MatchJson(HttpRequest request, MatchState match)
    {
        string baseUrl = BaseUrl(request);
        int actionPlayer = match.ActionPlayerId == 0 ? match.PlayerLeft : match.ActionPlayerId;
        return new JsonObject
        {
            ["action_player_id"] = actionPlayer,
            ["action_side"] = actionPlayer == match.PlayerRight ? "right" : "left",
            ["actions"] = match.Actions.DeepClone(),
            ["actions_url"] = baseUrl + "/matches/v2/" + match.MatchId + "/actions",
            ["current_action_id"] = match.CurrentActionId,
            ["current_turn"] = match.CurrentTurn,
            ["deck_id_left"] = match.DeckIdLeft,
            ["deck_id_right"] = match.DeckIdRight,
            ["left_is_online"] = match.LeftOnline,
            ["right_is_online"] = match.RightOnline,
            ["match_id"] = match.MatchId,
            ["match_type"] = match.MatchType,
            ["match_url"] = baseUrl + "/matches/v2/" + match.MatchId,
            ["modify_date"] = TimeUtil.NowIso(),
            ["notifications"] = match.Notifications.DeepClone(),
            ["player_id_left"] = match.PlayerLeft,
            ["player_id_right"] = match.PlayerRight,
            ["player_status_left"] = match.PlayerStatusLeft,
            ["player_status_right"] = match.PlayerStatusRight,
            ["start_side"] = "left",
            ["status"] = match.Status,
            ["winner_id"] = match.WinnerId,
            ["winner_side"] = match.WinnerSide,
        };
    }

    private JsonObject StartingData(MatchState match)
    {
        var left = _database.FindUserById(match.PlayerLeft);
        var right = _database.FindUserById(match.PlayerRight);
        var leftDeck = _database.FindDeckById(match.DeckIdLeft);
        var rightDeck = _database.FindDeckById(match.DeckIdRight);
        return new JsonObject
        {
            ["ally_faction_left"] = OptString(match.LeftDeckData, "ally_country"),
            ["ally_faction_right"] = OptString(match.RightDeckData, "ally_country"),
            ["card_back_left"] = leftDeck?.CardBack ?? "",
            ["card_back_right"] = rightDeck?.CardBack ?? "",
            ["starting_hand_left"] = match.LeftHandCards.DeepClone(),
            ["starting_hand_right"] = match.RightHandCards.DeepClone(),
            ["deck_left"] = match.LeftDeckCards.DeepClone(),
            ["deck_right"] = match.RightDeckCards.DeepClone(),
            ["equipment_left"] = EquipmentList(left, OptString(match.LeftDeckData, "main_country")),
            ["equipment_right"] = EquipmentList(right, OptString(match.RightDeckData, "main_country")),
            ["is_ai_match"] = false,
            ["left_player_name"] = left?.PlayerName ?? "Player",
            ["right_player_name"] = right?.PlayerName ?? "Player",
            ["left_player_tag"] = left?.PlayerTag ?? 0,
            ["right_player_tag"] = right?.PlayerTag ?? 0,
            ["left_player_officer"] = true,
            ["right_player_officer"] = true,
            ["location_card_left"] = match.LeftCardsData.Count > 0 ? match.LeftCardsData[0]!.DeepClone() : new JsonObject(),
            ["location_card_right"] = match.RightCardsData.Count > 0 ? match.RightCardsData[0]!.DeepClone() : new JsonObject(),
            ["player_id_left"] = match.PlayerLeft,
            ["player_id_right"] = match.PlayerRight,
            ["player_stars_left"] = 120,
            ["player_stars_right"] = 120,
        };
    }

    private JsonObject MatchAndStartingData(HttpRequest request, MatchState match) => new()
    {
        ["local_subactions"] = true,
        ["match_and_starting_data"] = new JsonObject
        {
            ["match"] = MatchJson(request, match),
            ["starting_data"] = StartingData(match),
        },
        ["action_player_id"] = match.PlayerRight,
        ["action_side"] = "right",
    };

    private JsonArray EquipmentList(UserRecord? user, string faction)
    {
        var items = new JsonArray();
        if (user is null) return items;
        string[] columns =
        {
            "item_" + faction,
            "emote_Start_" + faction, "emote_End_" + faction, "emote_Good_" + faction, "emote_Bad_" + faction,
            "emote_Cheer_" + faction, "emote_Taunt_" + faction, "emote_Poke_" + faction, "emote_Proclaim_" + faction,
            "avatar"
        };
        foreach (string column in columns)
            items.Add(user.Equipment.GetValueOrDefault(column));
        return items;
    }

    private (int, string, string) Mulligan(HttpContext context, UserRecord user, int matchId)
    {
        var match = _matches.MatchById(matchId);
        if (match is null) return Text(404, "missing");
        var body = JsonBody(context.Request);
        var discardedIds = body?["discarded_card_ids"] as JsonArray ?? new JsonArray();
        bool left = user.Id == match.PlayerLeft;
        var hand = left ? match.LeftHandCards : match.RightHandCards;
        var deck = left ? match.LeftDeckCards : match.RightDeckCards;
        var discarded = new JsonArray();
        var replacements = new JsonArray();
        for (int i = 0; i < hand.Count; i++)
        {
            var card = hand[i] as JsonObject;
            if (card is null || !ContainsInt(discardedIds, OptInt(card, "card_id")))
                continue;
            discarded.Add(card.DeepClone());
            if (deck.Count > 0)
            {
                int deckIndex = _random.Next(deck.Count);
                var replacement = deck[deckIndex] as JsonObject;
                if (replacement is not null)
                {
                    replacement = (JsonObject)replacement.DeepClone();
                    var oldCard = (JsonObject)card.DeepClone();
                    int oldLocationNumber = OptInt(card, "location_number", i);
                    int replacementLocationNumber = OptInt(replacement, "location_number", deckIndex);
                    replacement["location"] = left ? "hand_left" : "hand_right";
                    replacement["location_number"] = oldLocationNumber;
                    hand[i] = replacement;
                    replacements.Add(replacement.DeepClone());
                    oldCard["location"] = left ? "deck_left" : "deck_right";
                    oldCard["location_number"] = replacementLocationNumber;
                    deck[deckIndex] = oldCard;
                }
            }
        }
        if (left)
        {
            match.PlayerStatusLeft = "mulligan_done";
            match.LeftDiscardedCards = discarded;
            match.LeftReplacementCards = replacements;
        }
        else
        {
            match.PlayerStatusRight = "mulligan_done";
            match.RightDiscardedCards = discarded;
            match.RightReplacementCards = replacements;
        }
        return Json(200, new JsonObject
        {
            ["deck"] = deck.DeepClone(),
            ["replacement_cards"] = replacements.DeepClone(),
        });
    }

    private (int, string, string) MulliganSide(int matchId, bool left)
    {
        var match = _matches.MatchById(matchId);
        if (match is null) return Text(404, "missing");
        return Json(200, new JsonObject
        {
            ["deck"] = (left ? match.LeftDeckCards : match.RightDeckCards).DeepClone(),
            ["replacement_cards"] = (left ? match.LeftReplacementCards : match.RightReplacementCards).DeepClone(),
        });
    }

    private (int, string, string) Reconnect(HttpContext context, UserRecord user)
    {
        var match = _matches.MatchForPlayer(user.Id);
        if (match is null)
            return Json(200, new JsonObject());
        if (user.Id == match.PlayerLeft) match.LeftOnline = true;
        if (user.Id == match.PlayerRight) match.RightOnline = true;
        var actions = new JsonArray();
        for (int i = 0; i < match.Actions.Count; i++)
        {
            string? data = match.ActionsData.GetValueOrDefault(match.Actions[i]!.GetValue<int>());
            if (data is not null) actions.Add(data);
        }
        var json = new JsonObject
        {
            ["actions"] = actions,
            ["local_subactions"] = true,
            ["match"] = MatchJson(context.Request, match),
            ["mulligan_left"] = new JsonObject
            {
                ["deck"] = match.LeftDeckCards.DeepClone(),
                ["discarded_cards"] = match.LeftDiscardedCards.DeepClone(),
                ["replacement_cards"] = match.LeftReplacementCards.DeepClone(),
            },
            ["mulligan_right"] = new JsonObject
            {
                ["deck"] = match.RightDeckCards.DeepClone(),
                ["discarded_cards"] = match.RightDiscardedCards.DeepClone(),
                ["replacement_cards"] = match.RightReplacementCards.DeepClone(),
            },
            ["same_turn"] = true,
            ["starting_data"] = StartingData(match),
            ["time_since_start_of_turn"] = 60,
            ["unranked"] = false,
            ["waiting_for_sit_n_go_match"] = false,
        };
        return Json(200, json);
    }

    private static bool ContainsInt(JsonArray array, int value)
    {
        for (int i = 0; i < array.Count; i++)
        {
            if (array[i]?.GetValue<int>() == value) return true;
        }
        return false;
    }

    private void PutEquipped(JsonArray array, UserRecord user, string slot, string faction)
    {
        string? column = AppDatabase.EquipmentColumn(slot, faction);
        array.Add(new JsonObject
        {
            ["item_id"] = ItemValue(column is null ? null : user.Equipment.GetValueOrDefault(column)),
            ["slot"] = slot,
            ["faction"] = faction,
        });
    }

    private static JsonNode? ItemValue(string? value)
        => string.IsNullOrEmpty(value) ? null : value;

    // ==================== 杂项端点 ====================

    private static JsonObject Announcement() => new()
    {
        ["code"] = 200,
        ["message"] = "success",
        ["data"] = new JsonArray
        {
            new JsonObject
            {
                ["title"] = "KARDS Local Server",
                ["content"] = "Local Java backend is running.",
                ["date"] = "2026-05-24",
                ["type"] = "info",
            },
        },
    };

    private static JsonObject Activity() => new()
    {
        ["code"] = 200,
        ["message"] = "success",
        ["data"] = new JsonArray(),
    };

    private static JsonObject CheckUpdate() => new()
    {
        ["code"] = 200,
        ["message"] = "success",
        ["data"] = new JsonObject { ["mods"] = new JsonArray() },
    };

    private static JsonObject Topbar() => new()
    {
        ["code"] = 200,
        ["data"] = new JsonObject
        {
            ["title"] = "",
            ["subtitle"] = "",
            ["link"] = "",
            ["link_type"] = "web",
            ["fallback_url"] = null,
            ["package_name"] = null,
        },
    };

    private static JsonObject DownloadConfig() => new()
    {
        ["code"] = 200,
        ["data"] = new JsonObject
        {
            ["title"] = "",
            ["message"] = "",
            ["links"] = new JsonArray(),
        },
    };

    private static JsonObject ClientFp() => new()
    {
        ["above_left_message"] = new JsonObject
        {
            ["title"] = "KARDS Local Server",
            ["text"] = "Local Java backend",
            ["link"] = "",
        },
        ["activites"] = new JsonArray(),
        ["pop_up"] = new JsonObject
        {
            ["num"] = 0,
            ["title"] = "",
            ["text"] = "",
            ["link"] = "",
        },
    };

    private static JsonObject FrontPage() => new()
    {
        ["changed"] = true,
        ["elements"] = new JsonArray(),
        ["message"] = "OK",
        ["status_code"] = 200,
        ["targeted"] = new JsonArray(),
    };

    private static JsonObject Version() => new()
    {
        ["pak_version"] = "1.52.25476",
        ["game_version"] = "1.52.25476",
        ["pak_md5"] = "05e128fea79e2a85b387b430bdbfec6e",
        ["game_updata"] = "",
    };

    private static JsonArray Packs()
    {
        var packs = new JsonArray();
        for (int i = 0; i < 40; i++)
        {
            packs.Add(new JsonObject { ["card_set"] = "5|Core", ["id"] = 0 });
            packs.Add(new JsonObject { ["card_set"] = "7|Core", ["id"] = 1 });
        }
        return packs;
    }

    private JsonObject RecordData()
    {
        var stats = new JsonObject
        {
            ["online_players"] = _webSockets.OnlineCount(),
            ["waiting_ranked"] = 0,
            ["waiting_casual"] = 0,
            ["waiting_total"] = 0,
            ["wait_code_players"] = 0,
            ["playing_players"] = 0,
            ["playing_decks"] = 0,
            ["matches"] = _matches.MatchCount(),
            ["match_id"] = 0,
            ["status"] = _webSockets.Running,
            ["battle_codes"] = 0,
        };
        var queues = new JsonObject
        {
            ["ranked"] = new JsonArray(),
            ["casual"] = new JsonArray(),
            ["ranked_count"] = 0,
            ["casual_count"] = 0,
            ["total"] = 0,
        };
        return new JsonObject
        {
            ["statistics"] = stats,
            ["queues"] = queues,
            ["battle_code_queues"] = new JsonObject(),
            ["battle_code_count"] = 0,
            ["player_mappings"] = new JsonObject
            {
                ["playing"] = new JsonObject(),
                ["decks"] = new JsonObject(),
                ["codes"] = new JsonObject(),
            },
            ["matches_detail"] = new JsonObject(),
            ["matches_count"] = _matches.MatchCount(),
            ["online_players_detail"] = new JsonObject(),
            ["online_count"] = _webSockets.OnlineCount(),
            ["system"] = new JsonObject
            {
                ["message"] = null,
                ["timestamp"] = TimeUtil.NowIso(),
            },
        };
    }

    private (int, string, string) Broadcast(HttpContext context)
    {
        var body = JsonBody(context.Request);
        if (OptString(body, "password") != "CometServer1234567890@ABC")
            return Json(401, new JsonObject { ["message"] = "unauthorized" });
        var message = body?["message"] as JsonObject;
        int sent = _webSockets.Broadcast(message ?? new JsonObject());
        return Json(200, new JsonObject { ["status"] = "OK", ["sent"] = sent });
    }

    private (int, string, string) SendId(HttpContext context)
    {
        var body = JsonBody(context.Request);
        if (OptString(body, "password") != "CometServer1234567890@ABC")
            return Json(401, new JsonObject { ["message"] = "unauthorized" });
        var message = body?["message"] as JsonObject;
        bool sent = _webSockets.SendToId(OptInt(body, "id"), message ?? new JsonObject());
        return Json(200, new JsonObject { ["status"] = sent ? "OK" : "offline" });
    }

    private (int, string, string) AdminNotImplemented(HttpContext context)
    {
        var body = JsonBody(context.Request);
        if (OptString(body, "password") != "CometServer123@BanUserID")
            return Json(401, new JsonObject { ["message"] = "unauthorized" });
        return Json(501, new JsonObject
        {
            ["error"] = "not_implemented",
            ["message"] = "Android v1 does not implement ban persistence yet",
        });
    }

    private (int, string, string) EmptySearch(HttpContext context)
    {
        var body = JsonBody(context.Request);
        if (OptString(body, "password") != "CometServer123@BanUserID")
            return Json(401, new JsonObject { ["message"] = "unauthorized" });
        int page = Math.Max(1, OptInt(body, "page", 1));
        int limit = Math.Max(1, OptInt(body, "limit", 50));
        return Json(200, new JsonObject
        {
            ["status"] = "success",
            ["data"] = new JsonArray(),
            ["page"] = page,
            ["limit"] = limit,
            ["total_count"] = 0,
            ["total_pages"] = 0,
        });
    }

    // ==================== 认证 ====================

    private UserRecord Authenticate(string path, HttpContext context)
    {
        if (_excluded.Contains(path) || path.StartsWith("/static/"))
            return null!;
        string? token = NormalizeAuthToken(context.Request.Headers["authorization"].ToString());
        if (token is null)
            throw new UnauthorizedException();
        var payload = JwtUtil.Verify(_config.JwtSecret, token);
        int userId = payload["user_id"]?.GetValue<int>() ?? 0;
        var user = _database.FindUserById(userId);
        if (user is null || !TokenMatchesUserSession(token, payload, user))
            throw new UnauthorizedException();
        return user;
    }

    private UserRecord? OptionalUser(HttpRequest request)
    {
        try
        {
            string? token = NormalizeAuthToken(request.Headers["authorization"].ToString());
            if (token is null)
                return null;
            var user = _database.FindUserByJwt(token);
            if (user is not null)
                return user;
            var payload = JwtUtil.Verify(_config.JwtSecret, token);
            user = _database.FindUserById(payload["user_id"]?.GetValue<int>() ?? 0);
            return user is not null && TokenMatchesUserSession(token, payload, user) ? user : null;
        }
        catch
        {
            return null;
        }
    }

    private bool TokenMatchesUserSession(string token, JsonObject payload, UserRecord user)
    {
        try
        {
            if (string.IsNullOrEmpty(user.PlayerJwt))
                return false;
            if (token == user.PlayerJwt)
                return true;
            var serverPayload = JwtUtil.Verify(_config.JwtSecret, user.PlayerJwt);
            long clientExp = payload["exp"]?.GetValue<long>() ?? 0;
            long serverExp = serverPayload["exp"]?.GetValue<long>() ?? 0;
            return clientExp > 0 && serverExp > 0 && Math.Abs(clientExp - serverExp) < 86400;
        }
        catch
        {
            return false;
        }
    }

    private static string? NormalizeAuthToken(string? value)
    {
        if (value is null)
            return null;
        string token = value.Trim();
        if (token.StartsWith("JWT ", StringComparison.OrdinalIgnoreCase))
            token = token[4..].Trim();
        else if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            token = token[7..].Trim();
        return token.Length == 0 ? null : token;
    }

    private static void RequireSameUser(UserRecord user, int playerId)
    {
        if (user is null || user.Id != playerId)
            throw new UnauthorizedException();
    }

    // ==================== 卡组/工具 ====================

    private JsonArray DecksArray(List<DeckRecord> decks)
    {
        var array = new JsonArray();
        foreach (var deck in decks)
            array.Add(DeckJson(deck));
        return array;
    }

    private JsonObject DeckJson(DeckRecord deck)
    {
        string mainFaction = deck.MainFaction;
        string allyFaction = deck.AllyFaction;
        // 数据库阵营为空时从卡组代码恢复
        if (string.IsNullOrEmpty(mainFaction) && deck.DeckCode is not null && deck.DeckCode.StartsWith("%%") && deck.DeckCode.Length >= 4)
        {
            string country = deck.DeckCode.Substring(2, 2);
            mainFaction = CountryName(country.Substring(0, 1));
            allyFaction = CountryName(country.Substring(1, 1));
        }
        return new JsonObject
        {
            ["name"] = deck.Name,
            ["main_faction"] = mainFaction,
            ["main_country"] = mainFaction,
            ["main_nation"] = mainFaction,
            ["ally_faction"] = allyFaction,
            ["card_back"] = deck.CardBack,
            ["deck_code"] = deck.DeckCode,
            ["favorite"] = deck.Favorite,
            ["id"] = deck.Id,
            ["player_id"] = deck.UserId,
            ["last_played"] = deck.LastPlayed,
            ["create_date"] = deck.CreateDate,
            ["modify_date"] = deck.ModifyDate,
        };
    }

    private static void PutNationProgress(JsonObject json)
    {
        string[] nations = { "britain", "germany", "japan", "soviet", "usa" };
        foreach (string nation in nations)
        {
            json[nation + "_level"] = 500;
            json[nation + "_level_claimed"] = 500;
            json[nation + "_xp"] = 0;
        }
    }

    private static JsonArray TutorialsFinished()
    {
        var tutorials = new JsonArray();
        string[] values =
        {
            "unlocking_germany_1", "unlocking_germany_2", "unlocking_germany_0",
            "germany_cards_rewarded", "unlocking_usa_8", "recruit_missions_done",
            "draft_1", "draft_ally", "draft_kredits", "unlocking_japan_0",
            "japan_cards_rewarded", "unlocking_soviet_0", "soviet_cards_rewarded",
            "unlocking_usa_0", "usa_cards_rewarded", "unlocking_britain_0",
            "britain_cards_rewarded"
        };
        foreach (string value in values) tutorials.Add(value);
        return tutorials;
    }

    private static JsonArray NewCards()
    {
        var array = new JsonArray();
        string[] values =
        {
            "card_unit_panzer_i_dak", "card_unit_fw189", "card_event_dive_bombing",
            "card_unit_welsh_guards", "card_unit_ms_460", "card_event_reich_defenders",
            "card_unit_panzer_iii_h", "card_event_u_375", "card_event_careless_talk",
            "card_unit_panzergrenadier", "card_unit_fw_190", "card_unit_kv_1",
            "card_unit_42nd_rifles", "card_event_the_hammer", "card_unit_89th_infantry",
            "card_event_sturmovik", "card_unit_43_infantry_regiment"
        };
        foreach (string value in values) array.Add(value);
        return array;
    }

    private static JsonArray CardsBlacklist()
    {
        var array = new JsonArray();
        array.Add(new JsonObject
        {
            ["card_type"] = "card_unit_8th_cavalry_regiment",
            ["end_date"] = "2026-03-08T11:00:00",
        });
        return array;
    }

    private static JsonObject CurrentMiniSitNGo()
    {
        var rules = new JsonObject
        {
            ["reward"] = new JsonObject { ["type"] = "random_pack" },
            ["name"] = "Skirmish #45",
            ["hq_starting_defense"] = 90,
        };
        return new JsonObject
        {
            ["end_date"] = "2026-04-29T18:00:00.000000Z",
            ["hasWon"] = false,
            ["id"] = 254937,
            ["name"] = "Skirmish #45",
            ["rules_json_str"] = rules.ToJsonString(JsonOptions),
            ["start_date"] = "2026-03-27T12:00:00.000000Z",
        };
    }

    private JsonNode ServerOptions(HttpRequest request)
    {
        try
        {
            var options = new JsonObject
            {
                ["christmas_music"] = 0,
                ["nui_mobile"] = 1,
                ["beta_expiry_date"] = "2023-10-01 00:00:00",
            };
            var console = new JsonObject { ["console_commands"] = StringArray("r.Screenpercentage 100") };
            var scalability = new JsonObject
            {
                ["Android_Low"] = console.DeepClone(),
                ["Android_Mid"] = console.DeepClone(),
                ["Android_High"] = console.DeepClone(),
            };
            options["scalability_override"] = scalability;
            options["appscale_desktop_default"] = 1.0;
            options["appscale_desktop_max"] = 1.4;
            options["appscale_mobile_default"] = 1.4;
            options["appscale_mobile_max"] = 1.4;
            options["appscale_mobile_min"] = 1.0;
            options["appscale_tablet_min"] = 1.0;
            options["battle_wait_time"] = 60;
            options["brothers_in_arms_date"] = "2023.06.18-09.30.00";
            options["covert_ops_date"] = "2024.06.11-11.00.00";
            options["naval_warfare_date"] = "2025.05.22-12.00.00";
            options["first_purchase_bonus_date"] = "2025.07.24-11.10.00";
            options["reconnect"] = 1;
            options["logger_disabled"] = 0;
            options["new_rewards"] = 1;
            options["most_popular_products"] = "304;238;318;319;320;321;322;324;11;270;1;52;45;18;56;72;7;149;70;143;9;53;57;75;151;228;153;79";
            options["winter_war_date"] = "2023.11.29-09.00.00";
            options["websocketurl"] = WebSocketUrl(request);
            options["homefront_date"] = "2025.11.27-09.00.00";
            options["finland"] = 0;
            options["achievements"] = 0;
            options["avatars"] = 0;
            options["broken_deck_check"] = 0;
            options["key_encryption"] = 0;
            options["show_full_image"] = true;
            options["new_effect_bar"] = 1;
            options["new_effect_bar_pc"] = 1;
            options["new_effect_icons"] = 1;
            options["feature_socketerror_popup_enabled"] = 1;
            options["versions"] = StringArray("Kards 1.47", "Kards 1.49", "Kards 1.50", "Kards 1.52",
                "Kards 1.52.25476.launcher", "Kards 1.53", "Kards 1.54", "Kards 1.54.26471.APK", "Kards 1.56",
                "KLink 29452.29452"); // 与 Java 版一致:VersionPakManager.DEFAULT_VERSION(版本补丁默认值)
            var locked = new JsonArray();
            locked.Add(new JsonObject
            {
                ["cards"] = StringArray(
                    "card_unit_whirlwind", "card_event_pound", "card_unit_tiger_moth",
                    "card_event_harass", "card_unit_salamander", "card_unit_henschel_he_129",
                    "card_unit_ilyushin_10", "card_unit_p_39_airacobra", "card_event_out_with_the_old",
                    "card_unit_tigercat", "card_unit_seahawk", "card_event_screening_force",
                    "card_unit_n1k1_kyofu", "card_event_flight_to_oblivion", "card_unit_kyushu_j7w3",
                    "card_event_sally", "card_event_air_strips", "card_event_pilot_escape",
                    "card_unit_fokker_finland"),
                ["unlock_date"] = "2025-09-23T12:10:00",
            });
            options["locked_cards"] = locked;
            options["reserve_changes"] = new JsonArray();
            options["draft_card_limits"] = new JsonObject
            {
                ["blacklist"] = StringArray(
                    "card_event_tactical_withdrawal", "card_event_hms_spectre", "card_event_lure",
                    "card_event_naval_power", "card_event_fortification", "card_event_for_the_king",
                    "card_event_overrun", "card_event_creeping_barrage", "card_unit_qf_40mm_mk_iii",
                    "card_unit_no43_commando", "card_event_bpf", "card_event_hms_formidable"),
                ["whitelist"] = StringArray(
                    "card_unit_baluch_regiment", "card_unit_the_glamour_boys",
                    "card_unit_east_surray_regiment", "card_unit_3rd_canadian_division",
                    "card_unit_spitfire_v", "card_unit_rnzaf_kittyhawk"),
            };
            options["give_guest_name"] = 0;
            options["anzac"] = 1;
            options["oceania_storm_date"] = "2026.06.11-08.00.00";
            return options;
        }
        catch
        {
            return new JsonObject();
        }
    }

    private static JsonArray StringArray(params string[] values)
    {
        var array = new JsonArray();
        foreach (string value in values)
            array.Add(value);
        return array;
    }

    private static string CompactServerTime()
        => DateTime.UtcNow.ToString("yyyy.MM.dd-HH.mm.ss");

    private string BaseUrl(HttpRequest request)
    {
        string host = _config.AdvertisedHost ?? "";
        if (string.IsNullOrWhiteSpace(host))
            host = request.Host.Value ?? "";
        if (string.IsNullOrWhiteSpace(host))
            host = "127.0.0.1:" + _config.HttpPort;
        // AdvertisedHost 为纯 IP 时补 HTTP 端口（如 192.168.1.10 → 192.168.1.10:5231）
        if (!host.Contains(':'))
            host += ":" + _config.HttpPort;
        return "http://" + host;
    }

    private string HostOnly(HttpRequest request)
    {
        string host = _config.AdvertisedHost ?? "";
        if (string.IsNullOrWhiteSpace(host))
            host = request.Host.Value ?? "";
        if (string.IsNullOrWhiteSpace(host))
            return "127.0.0.1";
        int colon = host.IndexOf(':');
        return colon >= 0 ? host[..colon] : host;
    }

    private string WebSocketUrl(HttpRequest request)
    {
        string host = _config.AdvertisedHost ?? "";
        if (string.IsNullOrWhiteSpace(host))
        {
            host = request.Host.Value ?? "";
            int colon = host.IndexOf(':');
            if (colon >= 0)
                host = host[..colon] + ":" + _config.WsPort;
        }
        if (string.IsNullOrWhiteSpace(host))
            host = "127.0.0.1:" + _config.WsPort;
        // AdvertisedHost 为纯 IP 时补 WS 端口（如 192.168.1.10 → 192.168.1.10:5232）
        if (!host.Contains(':'))
            host += ":" + _config.WsPort;
        return "ws://" + host + "/ws";
    }

    private static string[] Segments(string path)
    {
        string clean = path.StartsWith('/') ? path[1..] : path;
        if (clean.Length == 0)
            return Array.Empty<string>();
        var parts = clean.Split('/');
        // Java String.split 丢弃尾部空串，C# Split 保留 —— 手动去掉尾部空元素以保持一致
        int end = parts.Length;
        while (end > 0 && parts[end - 1].Length == 0)
            end--;
        return parts[..end];
    }

    private static int ParseId(string value) => int.Parse(value);

    private static bool IsPlayableDeckCode(string? deckCode)
    {
        if (deckCode is null || !deckCode.StartsWith("%%")) return false;
        string code = deckCode[2..];
        string[] parts = code.Split('|');
        if (parts.Length < 2) return false;
        string country = parts[0];
        string cards = parts[1];
        if (country.Length < 2) return false;
        int tilde = cards.IndexOf('~');
        if (tilde >= 0) cards = cards[..tilde];
        string[] groups = cards.Split(';');
        if (groups.Length != 4) return false;
        // 不能是空卡组
        foreach (string group in groups)
        {
            if (group.Length > 0) return true;
        }
        return false;
    }

    private static string NormalizeFaction(JsonObject body, params string[] keys)
    {
        foreach (string key in keys)
        {
            string value = OptString(body, key);
            if (value.Length > 0) return value;
        }
        return "";
    }

    private static string CountryName(string code) => code switch
    {
        "1" => "Germany",
        "2" => "Britain",
        "3" => "Japan",
        "4" => "Soviet",
        "5" => "USA",
        "6" => "France",
        "7" => "Italy",
        "8" => "Poland",
        "9" => "Finland",
        _ => "Unknown",
    };

    // ==================== JSON 读取 ====================

    private static JsonObject JsonBody(HttpRequest request)
    {
        byte[] body = request.HttpContext.Items["__klink_body"] as byte[]
            ?? Array.Empty<byte>();
        if (body.Length == 0)
            return new JsonObject();
        return JsonNode.Parse(Encoding.UTF8.GetString(body)) as JsonObject ?? new JsonObject();
    }

    /// <summary>Java JSONObject.optString 语义：null → 默认值；数字/布尔 → 字符串表示。</summary>
    private static string OptString(JsonObject? obj, string key, string def = "")
    {
        if (obj is null || obj[key] is not JsonValue value)
            return def;
        try { return value.GetValue<string>(); }
        catch { return value.ToString() ?? def; }
    }

    /// <summary>Java JSONObject.optInt 语义：字符串数字也转换。</summary>
    private static int OptInt(JsonObject? obj, string key, int def = 0)
    {
        if (obj is null || obj[key] is not JsonValue value)
            return def;
        try { return value.GetValue<int>(); }
        catch
        {
            return int.TryParse(value.ToString(), out int parsed) ? parsed : def;
        }
    }
}
