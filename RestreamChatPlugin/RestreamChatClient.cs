using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RestreamChatPlugin
{
    // 连接 Restream Chat API 的实时 WebSocket，把聚合聊天转成弹幕事件。
    // 文档：https://developers.restream.io/chat
    // 端点：wss://chat.api.restream.io/ws?accessToken={oauth_access_token}
    // 该连接为单向（仅服务器 -> 客户端），用于接收聊天事件。
    //
    // 事件结构（对照官方 events / event-sources 文档与真实抓包）：
    //   { "action": "event", "payload": { "eventPayload": { "author": {...}, "text": "..." }, "eventSourceId": 2, "eventTypeId": 4 }, ... }
    //   顶层字段是 "action"（不是 "type"）；观众聊天的文本/作者嵌套在 payload.eventPayload 内；
    //   eventTypeId 标识事件类型，eventSourceId 标识平台来源（见 SourceNames）。
    //   其余 action：heartbeat / connection_info / reply_created（主播在 Restream 发的回复）。
    // 一条表情在聊天文本中的范围与图片地址（来自 Restream 的 replaces 字段，权威覆盖 Twitch 原生表情）。
    public class EmoteRange
    {
        public int Start;   // 起始字符索引（含）
        public int End;     // 结束字符索引（含）
        public string Url;  // 表情图片地址
    }

    public class RestreamChatClient
    {
        // eventSourceId -> 平台名（来自官方 event-sources 文档）。用于 event / reply_created / connection_info 等动作。
        private static readonly Dictionary<int, string> SourceNames = new Dictionary<int, string>
        {
            { 1, "Restream" }, { 2, "Twitch" }, { 13, "YouTube" }, { 19, "Facebook" },
            { 20, "Facebook" }, { 24, "DLive" }, { 25, "Discord" }, { 26, "LinkedIn" },
            { 27, "Trovo" }, { 28, "X" }, { 29, "Kick" }, { 33, "Rumble" }
        };

        private readonly string _accessToken;
        private readonly string _proxyMode;
        private readonly string _proxyUrl;
        private ClientWebSocket _ws;
        private CancellationTokenSource _cts;
        // 标记本端主动关闭（用户停用 / 重连时先断开旧连接），用于区分意外断连与主动关闭。
        private bool _closedByUs;

        public event Action<string, string, string, List<EmoteRange>> OnMessage; // platform, user, text, emotes
        public event Action<string> OnStatus;
        public event Action<string, int> OnChannelInfo; // targetId, eventSourceId（用于拉取 Twitch emote）
        // 意外断连（服务器关闭 / 网络异常，非用户主动停用）时触发，供上层自动重连。
        public event Action OnUnexpectedDisconnect;
        // 鉴权失败（401/403，token 被服务器拒绝）时触发，供上层提示重新授权，不触发自动重连。
        public event Action OnAuthFailure;

        public RestreamChatClient(string accessToken, string proxyMode, string proxyUrl = "")
        {
            _accessToken = accessToken;
            _proxyMode = proxyMode;
            _proxyUrl = proxyUrl;
        }

        public void Connect()
        {
            _cts = new CancellationTokenSource();
            // Start() 在 UI 线程调用，连接与接收循环必须放到后台，避免阻塞。
            Task.Run(async () => await RunAsync(_cts.Token));
        }

        public void Disconnect()
        {
            _closedByUs = true;
            try { _cts?.Cancel(); } catch { }
            try
            {
                if (_ws != null && _ws.State == WebSocketState.Open)
                    _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).Wait();
            }
            catch { }
        }

        private async Task RunAsync(CancellationToken token)
        {
            bool unexpected = false;
            bool authFail = false;
            _ws = new ClientWebSocket();
            // 代理：直连 / 系统代理 / 自定义 三选一，由 Config 解析（ClientWebSocket 不会自动读取系统代理，须显式赋值）。
            _ws.Options.Proxy = Config.ResolveWebSocketProxy(_proxyMode, _proxyUrl);
            var url = "wss://chat.api.restream.io/ws?accessToken=" + Uri.EscapeDataString(_accessToken);
            RestreamPlugin.Trace("代理设置：" + Config.ProxyDescription(_proxyMode, _proxyUrl));
            try
            {
                OnStatus?.Invoke("正在连接 Restream Chat API ...");
                RestreamPlugin.Trace("正在建立 WebSocket 连接（accessToken 随 URL 发送）");
                await _ws.ConnectAsync(new Uri(url), token);
                OnStatus?.Invoke("Restream 已连接，等待聊天事件");
                RestreamPlugin.Trace("WebSocket 已连接，等待 Restream 推送帧（若长时间只有本行、无 RAW，说明 Restream 未向本连接推送任何帧）");

                var buf = new byte[8192];
                while (_ws.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    var sb = new StringBuilder();
                    WebSocketReceiveResult res;
                    do
                    {
                        res = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), token);
                        if (res.MessageType == WebSocketMessageType.Close)
                        {
                            // 服务器主动关闭连接：记录原因后退出接收循环。
                            // 非本端主动关闭（_closedByUs）即视为意外断连，触发自动重连。
                            RestreamPlugin.Trace("收到 Close 帧：status=" + res.CloseStatus + " desc=" + (res.CloseStatusDescription ?? ""));
                            if (!_closedByUs) unexpected = true;
                            break;
                        }
                        if (res.MessageType != WebSocketMessageType.Text)
                        {
                            // Ping/Pong/二进制：ClientWebSocket 通常自动回 Ping，这里只记录，避免误吞真实数据。
                            RestreamPlugin.Trace("收到非文本帧：type=" + res.MessageType);
                            continue;
                        }
                        sb.Append(Encoding.UTF8.GetString(buf, 0, res.Count));
                    } while (!res.EndOfMessage);

                    if (res.MessageType == WebSocketMessageType.Close) break;
                    if (sb.Length > 0) Handle(sb.ToString());
                }
            }
            catch (OperationCanceledException)
            {
                OnStatus?.Invoke("Restream 连接已停止");
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException?.Message;
                OnStatus?.Invoke("Restream 连接异常: " + ex.Message + (inner != null ? " | " + inner : ""));
                // 鉴权失败（401/403）属永久错误，重试无效：交给上层提示重新授权，不触发自动重连。
                // 其余（DNS/网络不通、连接被重置等瞬断）才视为意外断连走重连。
                if (IsAuthFailure(ex)) authFail = true;
                else unexpected = true;
            }
            finally
            {
                try
                {
                    if (_ws.State == WebSocketState.Open || _ws.State == WebSocketState.CloseReceived)
                        _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).Wait(500, token);
                }
                catch { }
                RestreamPlugin.Trace("接收循环结束");
                // 鉴权失败触发 OnAuthFailure（上层提示重新授权，不重连）；
                // 其余意外断连（非本端主动关闭）触发 OnUnexpectedDisconnect（走重连）。
                if (authFail) OnAuthFailure?.Invoke();
                else if (unexpected) OnUnexpectedDisconnect?.Invoke();
            }
        }

        // 判断连接异常是否由鉴权失败引起（401/403）。ConnectAsync 失败时内部异常通常是
        // WebException（含 HttpWebResponse.StatusCode）；个别环境异常信息里直接带状态码文本，作为兜底。
        private static bool IsAuthFailure(Exception ex)
        {
            var we = ex.InnerException as WebException ?? ex as WebException;
            if (we?.Response is HttpWebResponse resp)
                return resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden;
            var msg = ex.Message + " | " + (ex.InnerException?.Message ?? "");
            return msg.Contains("(401)") || msg.Contains("(403)");
        }

        // 解析单条原始帧，返回结构化结果（message / channelInfo / ignore / error）。
        // 抽成独立方法便于单元测试覆盖各 action 与边缘情况，Handle 只负责据此触发事件。
        internal class ParsedFrame
        {
            public string Kind;        // message / channelInfo / ignore / error
            public string Platform;
            public string User;
            public string Text;
            public string TargetId;
            public int EventSourceId;
            public string Error;
            public List<EmoteRange> Emotes = new List<EmoteRange>(); // 表情范围（来自 replaces）
        }

        internal static ParsedFrame ParseFrame(string json)
        {
            try
            {
                var obj = JObject.Parse(json);
                var action = (string)obj["action"];

                // connection_info：连接建立后服务器先推一条，内含当前频道与平台来源，
                // 用它拿到 Twitch 频道 ID 以便拉取 emote（Kappa 等显示为图片）。
                if (action == "connection_info")
                {
                    var payload = obj["payload"] as JObject;
                    if (payload != null)
                    {
                        var target = payload["target"] as JObject;
                        var owner = target?["owner"] as JObject;
                        var targetId = (string)(target?["id"] ?? owner?["id"]);
                        var eventSourceId = (int?)payload["eventSourceId"] ?? 0;
                        if (!string.IsNullOrEmpty(targetId))
                            return new ParsedFrame { Kind = "channelInfo", TargetId = targetId, EventSourceId = eventSourceId };
                    }
                    return new ParsedFrame { Kind = "ignore" };
                }

                // Restream 用顶层 "action" 区分消息类型（不是 "type"）：
                //   event         = 来自观众的聊天消息（eventTypeId 标识平台与事件类型）
                //   reply_created = 主播自己在 Restream 发出的回复（也回显，便于自测）
                // 其它（heartbeat / connection_info / reply_accepted / reply_confirmed）忽略。
                if (action != "event" && action != "reply_created") return new ParsedFrame { Kind = "ignore" };

                string platform;
                string user;
                string text;
                var emotes = new List<EmoteRange>();

                if (action == "event")
                {
                    var payload = obj["payload"] as JObject;
                    if (payload == null) return new ParsedFrame { Kind = "ignore" };

                    // 观众聊天的文本与作者嵌套在 payload.eventPayload 内（不在 payload 直接层级）。
                    var eventPayload = payload["eventPayload"] as JObject;
                    if (eventPayload == null) return new ParsedFrame { Kind = "ignore" };

                    // 平台来源用 eventSourceId（来自官方 event-sources 文档，更准确）。
                    var eventSourceId = (int?)payload["eventSourceId"] ?? 0;
                    platform = SourceNames.TryGetValue(eventSourceId, out var p) ? p : ("src" + eventSourceId);

                    var eventTypeId = (int?)payload["eventTypeId"] ?? 0;
                    text = (string)eventPayload["text"];

                    // 表情范围：Restream 在 replaces 字段给出每条表情在文本中的起止与图片地址，
                    // 这是权威来源，覆盖 Kappa 等 Twitch 原生表情（不依赖第三方表情包接口）。
                    var replaces = eventPayload["replaces"] as JArray;
                    if (replaces != null)
                    {
                        // 表情范围相对 text 字符索引；畸形（from<0、to<from、to 越界）会在
                        // AppendMessage 的 Substring 触发越界异常并被外层吞掉导致整条消息丢失，
                        // 故在此过滤掉不可信范围，仅丢弃该表情而非整条消息。
                        var textLen = text == null ? 0 : text.Length;
                        foreach (var r in replaces)
                        {
                            var from = (int?)r["from"];
                            var to = (int?)r["to"];
                            var url = (string)r["payload"]?["url"];
                            if (from != null && to != null && !string.IsNullOrEmpty(url)
                                && from.Value >= 0 && to.Value >= from.Value && to.Value < textLen)
                                emotes.Add(new EmoteRange { Start = from.Value, End = to.Value, Url = url });
                        }
                    }

                    // 打赏/订阅/贴图等无文本事件给可读兜底描述，避免整条被丢弃。
                    if (string.IsNullOrEmpty(text))
                    {
                        if (eventTypeId == 3 || eventTypeId == 12 || eventTypeId == 14)
                            text = "[贴图]";
                        else if (eventTypeId == 26)
                            text = "订阅了频道";
                        else if (eventTypeId == 7 || eventTypeId == 8)
                            text = "[SuperChat]";
                        else
                            return new ParsedFrame { Kind = "ignore" };
                    }

                    var author = eventPayload["author"] as JObject;
                    user = author == null ? "?" :
                        (string)author["displayName"] ?? (string)author["username"] ??
                        (string)author["name"] ?? (string)author["nickname"] ?? "?";
                }
                else // reply_created：主播自己的回复
                {
                    var payload = obj["payload"] as JObject;
                    if (payload == null) return new ParsedFrame { Kind = "ignore" };
                    var eventSourceId = (int?)payload["eventSourceId"] ?? 0;
                    platform = SourceNames.TryGetValue(eventSourceId, out var p) ? p : ("src" + eventSourceId);
                    user = "我";
                    text = (string)payload["text"];
                }

                if (string.IsNullOrEmpty(text)) return new ParsedFrame { Kind = "ignore" };
                return new ParsedFrame { Kind = "message", Platform = platform, User = user, Text = text, Emotes = emotes };
            }
            catch (Exception ex)
            {
                return new ParsedFrame { Kind = "error", Error = ex.Message };
            }
        }

        private void Handle(string json)
        {
            // 把收到的原始帧写到插件调试日志（仅 DebugLog 开启时），用于核对 Restream 下发结构与解析是否一致。
            RestreamPlugin.Trace("RAW: " + json);
            var f = ParseFrame(json);
            if (f.Kind == "channelInfo")
            {
                RestreamPlugin.Trace("收到频道信息 targetId=" + f.TargetId + " eventSourceId=" + f.EventSourceId + "（用于拉取 Twitch 表情）");
                OnChannelInfo?.Invoke(f.TargetId, f.EventSourceId);
                return;
            }
            if (f.Kind == "error")
            {
                OnStatus?.Invoke("Restream 解析失败: " + f.Error + " | raw=" + json);
                return;
            }
            if (f.Kind == "ignore") return;
            OnMessage?.Invoke(f.Platform, f.User, f.Text, f.Emotes);
        }

        // ===== OAuth token 获取 / 续期 =====

        public class TokenResult
        {
            public string access_token { get; set; }
            public string refresh_token { get; set; }
            public int expires_in { get; set; }
            public string scope { get; set; }
            public string token_type { get; set; }
        }

        // 用授权码换取 access_token + refresh_token（OAuth 授权码流程第④步）。
        public static async Task<TokenResult> ExchangeCodeAsync(string clientId, string clientSecret, string code, string redirectUri, string proxyMode, string proxyUrl)
        {
            using (var handler = new HttpClientHandler())
            {
                Config.ApplyHttpClientProxy(handler, proxyMode, proxyUrl);
                using (var http = new HttpClient(handler))
                {
                    var auth = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(clientId + ":" + clientSecret));
                    http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", auth);
                    var body = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["grant_type"] = "authorization_code",
                        ["redirect_uri"] = redirectUri,
                        ["code"] = code
                    });
                    RestreamPlugin.Trace("OAuth 授权码兑换请求已发出");
                    var resp = await http.PostAsync("https://api.restream.io/oauth/token", body);
                    var text = await resp.Content.ReadAsStringAsync();
                    RestreamPlugin.Trace("OAuth 授权码兑换响应 HTTP " + (int)resp.StatusCode);
                    if (!resp.IsSuccessStatusCode)
                        throw new Exception("HTTP " + (int)resp.StatusCode + " " + text);
                    return JsonConvert.DeserializeObject<TokenResult>(text);
                }
            }
        }

        // 用 refresh_token 换取新的 access_token（access token 每小时过期，靠它自动续期）。
        public static async Task<TokenResult> RefreshAsync(string clientId, string clientSecret, string refreshToken, string proxyMode, string proxyUrl)
        {
            using (var handler = new HttpClientHandler())
            {
                Config.ApplyHttpClientProxy(handler, proxyMode, proxyUrl);
                using (var http = new HttpClient(handler))
                {
                    var auth = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(clientId + ":" + clientSecret));
                    http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", auth);
                    var body = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["grant_type"] = "refresh_token",
                        ["refresh_token"] = refreshToken
                    });
                    RestreamPlugin.Trace("OAuth refresh_token 续期请求已发出");
                    var resp = await http.PostAsync("https://api.restream.io/oauth/token", body);
                    var text = await resp.Content.ReadAsStringAsync();
                    RestreamPlugin.Trace("OAuth 续期响应 HTTP " + (int)resp.StatusCode);
                    if (!resp.IsSuccessStatusCode)
                        throw new Exception("HTTP " + (int)resp.StatusCode + " " + text);
                    return JsonConvert.DeserializeObject<TokenResult>(text);
                }
            }
        }
    }
}
