using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using BilibiliDM_PluginFramework;

namespace RestreamChatPlugin
{
    public class RestreamPlugin : DMPlugin
    {
        // 必须与 developers.restream.io/apps 里登记的 Redirect URI 完全一致。
        // 用本地 HTTP 监听器自动接收授权回调，用户无需手动复制 code。
        internal const string RedirectUri = "http://localhost:8989/callback";
        internal const int CallbackPort = 8989;

        private RestreamChatClient _client;
        private AdminWindow _admin;
        private HttpListener _listener;
        // 当前 WebSocket 是否已连上（连接成功置 true，断连/重连中置 false），用于重连循环判定是否恢复。
        private bool _connected;
        // 重连循环是否正在进行，避免多次断连触发多个并发重连循环。
        private bool _reconnecting;
        // 鉴权失败（401）后是否已尝试过一次 refresh 续期：避免重复续期与无限重连。
        private bool _authRetried;
        // 鉴权已彻底失败（续期也失败），重连循环据此直接停止，改提示用户重新授权。
        private bool _authFailed;
        // 连接流程并发守卫：原子标志，避免断连/重连间隙并发建出重复 WebSocket（同一聊天投两遍弹幕）。
        private int _connecting = 0;

        // 授权流程结束（成功或失败）时触发，供设置窗口刷新状态文案。
        // 在后台线程触发，订阅方需切回 UI 线程更新控件。参数为失败原因；成功传 null。
        internal event Action<string> AuthorizationCompleted;

        // 独立浮层窗口（用于正确渲染 emoji 与平台表情包）。
        private RestreamOverlayWindow _overlay;
        // 频道 emote 名称 -> 图片URL 映射，与浮层共享同一实例，拉取后即时生效。
        private readonly Dictionary<string, string> _emotes = new Dictionary<string, string>();
        // 是否使用独立浮层（false 则退回 bililive_dm 自带浮层，但无 emoji/表情包渲染）。
        private bool UseOwnOverlay = false;
        // 调试日志开关（默认关）。Trace 仅在开启时把运行细节写入插件内部目录的 调试日志.log。
        internal static bool DebugLogEnabled = false;

        public RestreamPlugin()
        {
            PluginName = L10n.T("Restream 聚合聊天集成", "Restream アグリゲートチャット統合", "Restream Aggregated Chat Integration");
            PluginAuth = "Elegy233";
            PluginCont = "HXDD233@qq.com";
            PluginVer = "v1.6.0";
            PluginDesc = L10n.T(
                "通过 Restream Chat API 把多平台的聊天集成至弹幕姬（无需连接 B 站）",
                "Restream Chat API で複数プラットフォームのチャットを弾幕姫に統合（B 站接続不要）",
                "Aggregate chats from multiple platforms into Bililive DM via the Restream Chat API (no Bilibili connection required)");

            try { UseOwnOverlay = Config.Load().UseOwnOverlay; } catch { }
            try { DebugLogEnabled = Config.Load().DebugLog; } catch { }

            // 调试日志开启时仅追加会话起始标记（不再清空历史，跨重启保留）；超过 1MB 由 Trace 自动截断。
            Trace("=== 会话开始 " + DateTime.Now + " ===");
            Trace("插件已加载 " + PluginVer);
        }

        // 把关键节点写到插件内部目录的“调试日志.log”，插件加载/启动/连接/收消息都会留痕。
        // 仅当 DebugLogEnabled 时写入（默认关）。internal 以便 RestreamChatClient 也能写原始 JSON。
        // 追加写入并限制文件不超过 1MB：超出时从头部截断、保留最近约 1MB，跨重启持续累积而不清空。
        internal static void Trace(string s)
        {
            if (!DebugLogEnabled) return;
            AppendDebugLog(DateTime.Now.ToString("HH:mm:ss") + " " + s);
        }

        // 调试日志写入：追加一行；文件超过 1MB 时从头部截断保留最近约 1MB（不影响本次写入）。
        // 加锁避免多线程（连接/接收/UI 线程）并发写与截断互相干扰。
        private static readonly object _logLock = new object();
        private const long MaxDebugLogBytes = 1 * 1024 * 1024;
        private static void AppendDebugLog(string line)
        {
            try
            {
                var p = System.IO.Path.Combine(Config.PluginRoot, "调试日志.log");
                lock (_logLock)
                {
                    if (File.Exists(p))
                    {
                        var len = new FileInfo(p).Length;
                        if (len > MaxDebugLogBytes)
                        {
                            // 读最近 1MB 内容并重写，丢弃更旧的部分。
                            using (var fs = new FileStream(p, FileMode.Open, FileAccess.ReadWrite))
                            {
                                fs.Seek(len - MaxDebugLogBytes, SeekOrigin.Begin);
                                var buf = new byte[MaxDebugLogBytes];
                                int total = 0, n;
                                while (total < MaxDebugLogBytes && (n = fs.Read(buf, total, (int)(MaxDebugLogBytes - total))) > 0)
                                    total += n;
                                fs.Seek(0, SeekOrigin.Begin);
                                fs.Write(buf, 0, total);
                                fs.SetLength(total);
                            }
                        }
                    }
                    File.AppendAllText(p, line + "\r\n");
                }
            }
            catch { }
        }

        public override void Inited()
        {
            Trace("Inited() 被调用");
            // 弹幕姬主程序不持久化插件启用/停用状态，重启后所有插件默认未启用。
            // 按上次保存的 AutoStart 标记自动恢复启用，使重启后仍保持启用（规避主程序限制）。
            try
            {
                if (Config.Load().AutoStart)
                {
                    Trace("按保存的启用状态自动启动插件");
                    Start();
                }
            }
            catch { }
        }

        public override void Start()
        {
            base.Start();
            Trace("Start() 已调用，准备连接聊天流");
            // 记录用户启用意图：弹幕姬不持久化启用态，写入配置供 Inited 自动恢复。
            try { var c = Config.Load(); c.AutoStart = true; Config.Save(c); } catch { }
            Connect();
        }

        public override void Stop()
        {
            base.Stop();
            Trace("Stop() 已调用，正在断开连接并停止监听器");
            // 记录用户停用意图，供 Inited 判断是否需要自动恢复启用。
            try { var c = Config.Load(); c.AutoStart = false; Config.Save(c); } catch { }
            _connected = false;
            _client?.Disconnect();
            _client = null;
            StopListener();
            CloseOverlay();
        }

        private void CloseOverlay()
        {
            if (_overlay == null) return;
            var ov = _overlay;
            _overlay = null;
            try { ov.Dispatcher.Invoke(new Action(() => ov.Close())); } catch { }
        }

        // 设置窗口切换“独立浮层”时调用：按当前开关与布局重建/收起浮层窗口。
        // 开启时立即按最新 config 重建，关闭时收掉窗口。保存配置时调用，使布局改动即时生效。
        internal void ApplyOverlayConfig()
        {
            Trace("套用浮层配置：UseOwnOverlay=" + UseOwnOverlay);
            if (UseOwnOverlay)
            {
                CloseOverlay();
                EnsureOverlay();
            }
            else
            {
                CloseOverlay();
            }
        }

        // 设置窗口写入“使用独立浮层”开关时调用：更新字段并即时套用（含布局）。
        internal void SetUseOwnOverlay(bool value)
        {
            Trace("设置窗口切换 使用独立浮层=" + value);
            UseOwnOverlay = value;
            ApplyOverlayConfig();
        }

        // 设置窗口切换“调试日志”时调用：更新开关（影响 Trace 是否写文件）。
        // 切换前先直接落盘记录本次动作——关闭后 Trace 不再写入，故不能依赖 Trace 记录自身。
        internal void SetDebugLog(bool value)
        {
            // 切换前先落盘记录本次动作（关闭后 Trace 不再写入，故不能依赖 Trace 记录自身）。
            AppendDebugLog(DateTime.Now.ToString("HH:mm:ss") + " 设置窗口切换调试日志开关为 " + value);
            DebugLogEnabled = value;
        }

        public override void Admin()
        {
            base.Admin();
            Trace("Admin() 打开设置窗口");
            _admin?.Close();
            _admin = new AdminWindow(this);
            _admin.Closed += (s, e) =>
            {
                Trace("设置窗口已关闭");
                _admin = null;
            };
            _admin.Show();
        }

        // 生成 OAuth 授权链接（官方端点为 api.restream.io/login）。
        internal string BuildAuthorizeUrl()
        {
            var cfg = Config.Load();
            var ru = Uri.EscapeDataString(RedirectUri);
            return "https://api.restream.io/login?response_type=code&client_id="
                + Uri.EscapeDataString(cfg.ClientId)
                + "&redirect_uri=" + ru
                + "&scope=chat.read&state=restream_plugin";
        }

        // 一键登录：本地监听器自动接收回调 code 并兑换，无需手动复制。
        internal void StartLoginFlow()
        {
            var cfg = Config.Load();
            if (string.IsNullOrWhiteSpace(cfg.ClientId) || string.IsNullOrWhiteSpace(cfg.ClientSecret))
            {
                Log("请先在设置窗填写 Client ID 和 Client Secret");
                return;
            }

            Task.Run(async () =>
            {
                StopListener();
                Trace("开始登录流程");
                try
                {
                    _listener = new HttpListener();
                    _listener.Prefixes.Add("http://localhost:" + CallbackPort + "/");
                    _listener.Start();
                    Trace("本地回调监听器已在端口 " + CallbackPort + " 启动，等待浏览器授权");
                }
                catch (HttpListenerException ex)
                {
                    Log("本地回调监听器启动失败（多为权限问题）：" + ex.Message
                        + "。可尝试：以管理员身份运行弹幕姬，或在命令提示符执行"
                        + " netsh http add urlacl url=http://localhost:8989/ user=Everyone 后重试；"
                        + "也可改用下方「手动粘贴 code」方式授权。");
                    AuthorizationCompleted?.Invoke("本地回调监听器启动失败（多为权限问题），可改用「手动粘贴 code」方式授权");
                    _listener = null;
                    return;
                }

                try
                {
                    var url = BuildAuthorizeUrl();
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true });
                        Trace("已尝试打开浏览器访问授权页");
                    }
                    catch (Exception ex)
                    {
                        Log("无法自动打开浏览器，请手动访问：\n" + url + "\n(" + ex.Message + ")");
                        Trace("无法自动打开浏览器: " + ex.Message);
                    }

                    Log("等待浏览器授权回调（请在浏览器完成登录与同意）...");
                    Trace("已发送授权请求，等待浏览器回调");
                    // 等待回调设上限：用户放弃授权（关闭页面/未同意）时，GetContextAsync 会无限阻塞，
                    // 既泄漏后台线程也长期占用回调端口，直到插件停用才释放。超时即主动撤销并提示重试。
                    var ctxTask = _listener.GetContextAsync();
                    var timeout = Task.Delay(TimeSpan.FromMinutes(5));
                    var completed = await Task.WhenAny(ctxTask, timeout);
                    if (completed != ctxTask)
                    {
                        Log("等待授权回调超时（5 分钟未收到浏览器回调），已取消本次登录，可重新点击「登录并授权」。");
                        Trace("登录流程超时（未收到回调）");
                        AuthorizationCompleted?.Invoke("等待授权回调超时，请重新点击「登录并授权」。");
                        return;
                    }
                    var ctx = await ctxTask;
                    Trace("已收到浏览器授权回调");
                    var code = ctx.Request.QueryString["code"];

                    // 回一个友好的“授权成功”页面，然后关闭连接。
                    var resp = ctx.Response;
                    var html = "<html><body style='font-family:sans-serif'><h3>Restream 授权成功</h3>"
                        + "<p>可以关闭此页面，弹幕姬将自动连接。</p></body></html>";
                    var buf = System.Text.Encoding.UTF8.GetBytes(html);
                    resp.ContentType = "text/html; charset=utf-8";
                    resp.OutputStream.Write(buf, 0, buf.Length);
                    resp.OutputStream.Close();

                    if (string.IsNullOrWhiteSpace(code))
                    {
                        Log("授权回调未包含 code（可能你在 Restream 页面拒绝了授权）");
                        Trace("授权回调未包含 code（可能拒绝了授权）");
                        AuthorizationCompleted?.Invoke("授权回调未包含 code（可能你在 Restream 页面拒绝了授权）");
                        return;
                    }
                    await ExchangeAndConnect(code);
                }
                catch (Exception ex)
                {
                    Log("登录流程异常: " + ex.Message);
                    AuthorizationCompleted?.Invoke("登录流程异常: " + ex.Message);
                }
                finally
                {
                    StopListener();
                }
            });
        }

        // 从用户粘贴的内容中提取授权码：支持完整回调地址
        //（http://localhost:8989/callback?code=xxx&state=yyy）、单独的 ?code= 片段，或直接的 code 文本。
        // 仅取首个 code 参数值，自动截断到下一个 & 或 #。
        internal static string ExtractAuthCode(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;
            var s = input.Trim();
            var idx = s.IndexOf("code=", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return s; // 不含 code=，按原始 code 处理
            var after = s.Substring(idx + 5);
            var amp = after.IndexOf('&');
            var hash = after.IndexOf('#');
            var end = after.Length;
            if (amp >= 0 && (hash < 0 || amp < hash)) end = amp;
            else if (hash >= 0) end = hash;
            return after.Substring(0, end).Trim();
        }

        // 授权码换 token（设置窗“用 code 兑换”按钮 / 自动回调共用）。成功则保存并连接。
        internal async Task ExchangeAndConnect(string code)
        {
            Trace("开始用授权码兑换 token");
            var cfg = Config.Load();
            if (string.IsNullOrWhiteSpace(cfg.ClientId) || string.IsNullOrWhiteSpace(cfg.ClientSecret))
            {
                Log("请先填写 Client ID 和 Client Secret");
                AuthorizationCompleted?.Invoke("请先在第 ② 步填写 Client ID 和 Client Secret");
                return;
            }
            code = ExtractAuthCode(code);
            if (string.IsNullOrWhiteSpace(code))
            {
                Log("请粘贴授权回调地址（含 ?code=），或直接粘贴 code 值");
                AuthorizationCompleted?.Invoke("请粘贴授权回调地址（含 ?code=），或直接粘贴 code 值");
                return;
            }
            try
            {
                var tr = await RestreamChatClient.ExchangeCodeAsync(cfg.ClientId, cfg.ClientSecret, code.Trim(), RedirectUri, cfg.ProxyMode, cfg.ProxyUrl);
                ApplyToken(tr, cfg);
                Config.Save(cfg);
                Log("OAuth token 获取成功，access token 有效期 1 小时（插件会自动续期）");
                Trace("OAuth 兑换成功，准备连接");
                AuthorizationCompleted?.Invoke(null);
            }
            catch (Exception ex)
            {
                Log("授权码兑换失败: " + ex.Message);
                Trace("授权码兑换失败: " + ex.Message);
                AuthorizationCompleted?.Invoke(ex.Message);
                return;
            }
            Start();
        }

        // 确保有可用的 access token：缺失或过期则用 refresh_token 续期。
        private async Task<bool> EnsureTokenAsync()
        {
            var cfg = Config.Load();
            if (!string.IsNullOrWhiteSpace(cfg.AccessToken) && !IsExpired(cfg))
            {
                Trace("token 仍有效，直接连接");
                return true;
            }

            Trace("token 缺失或已过期，准备续期");
            if (string.IsNullOrWhiteSpace(cfg.RefreshToken))
            {
                Log("未配置 Restream token：请打开插件“设置”点击「登录并授权」");
                AddDM("Restream 未授权");
                Trace("无 token 也无 refresh_token，终止连接");
                return false;
            }

            return await RefreshTokenWithRetryAsync(cfg, maxAttempts: 5);
        }

        // 用 refresh_token 续期，网络/瞬断错误自动重试（默认 5 次，指数退避），
        // 吸收启动瞬间系统代理/网络未就绪、DNS 抖动等瞬时故障（日志中的
        // “An error occurred while sending the request.” 即此类传输层错误）。
        // 仅当服务器明确拒绝（4xx，refresh token 已失效/无效）才不再重试、直接提示重新授权，避免无谓等待。
        private async Task<bool> RefreshTokenWithRetryAsync(PluginConfig cfg, int maxAttempts)
        {
            Exception lastEx = null;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    var tr = await RestreamChatClient.RefreshAsync(cfg.ClientId, cfg.ClientSecret, cfg.RefreshToken, cfg.ProxyMode, cfg.ProxyUrl);
                    ApplyToken(tr, cfg);
                    Config.Save(cfg);
                    Log("Restream token 已用 refresh_token 自动续期" + (attempt > 1 ? "（第 " + attempt + " 次重试成功）" : ""));
                    Trace("refresh_token 续期成功" + (attempt > 1 ? "（第 " + attempt + " 次重试）" : ""));
                    return true;
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    // 4xx 属永久错误（refresh token 失效/被拒），重试无意义，立即终止。
                    if (IsAuthHttpError(ex))
                    {
                        Trace("refresh_token 续期失败（服务器明确拒绝，不再重试）：" + ex.Message);
                        break;
                    }
                    Trace("refresh_token 续期失败（第 " + attempt + "/" + maxAttempts + " 次，将重试）：" + ex.Message);
                    if (attempt < maxAttempts)
                    {
                        // 指数退避：1s, 2s, 4s, 8s（封顶 8s），给网络/代理恢复时间。
                        var delay = Math.Min(8000, (int)Math.Pow(2, attempt - 1) * 1000);
                        await Task.Delay(delay);
                    }
                }
            }
            // 区分鉴权失败（refresh token 被服务器拒绝，确属 token 失效）与网络/传输层瞬断：
            // 后者并非 token 失效，不应误报“需重新授权”，交由重连循环自动重试即可。
            if (IsAuthHttpError(lastEx))
            {
                _authFailed = true;
                Log("Restream token 续期失败（refresh token 已被服务器拒绝），请重新授权: " + lastEx?.Message);
                AddDM("Restream token 失效，请重新授权");
                Trace("refresh_token 续期失败（已重试 " + maxAttempts + " 次，服务器明确拒绝）：" + lastEx?.Message);
            }
            else
            {
                Log("Restream token 续期失败（网络/传输层错误，非 token 失效，将自动重试）: " + lastEx?.Message);
                Trace("refresh_token 续期失败（已重试 " + maxAttempts + " 次，网络/传输层错误）：" + lastEx?.Message);
            }
            return false;
        }

        // 判断异常是否为服务器明确拒绝（4xx）。此类错误表示 refresh token 已失效，重试无效。
        // token 端点以 "HTTP 401 ..." 形式抛出（无括号），故从消息中提取状态码，
        // 避免依赖具体括号格式；429 限流属瞬时错误，应走重试而非判为鉴权失败。
        internal static bool IsAuthHttpError(Exception ex)
        {
            var msg = ex?.Message ?? "";
            var m = System.Text.RegularExpressions.Regex.Match(msg, @"\bHTTP\s+(\d{3})\b");
            if (m.Success)
            {
                var code = int.Parse(m.Groups[1].Value);
                return code >= 400 && code < 500 && code != 429;
            }
            var we = ex?.InnerException as WebException ?? ex as WebException;
            if (we?.Response is HttpWebResponse resp)
            {
                var code = (int)resp.StatusCode;
                return code >= 400 && code < 500 && code != 429;
            }
            return false;
        }

        internal static bool IsExpired(PluginConfig cfg)
        {
            // 提前 5 分钟视为过期，留出续期缓冲
            return cfg.AccessTokenExpiresAt > 0 &&
                   cfg.AccessTokenExpiresAt - 300 <= DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        private static void ApplyToken(RestreamChatClient.TokenResult tr, PluginConfig cfg)
        {
            if (!string.IsNullOrEmpty(tr.access_token))
                cfg.AccessToken = tr.access_token;
            if (!string.IsNullOrEmpty(tr.refresh_token))
                cfg.RefreshToken = tr.refresh_token;
            if (tr.expires_in > 0)
            {
                cfg.AccessTokenExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + tr.expires_in;
                Trace("token 有效期：expires_in=" + tr.expires_in + " 秒（约 " + (tr.expires_in / 60) + " 分钟），到期前插件自动续期");
            }
            else
                Trace("token 响应未包含 expires_in，无法计算过期时间");
        }

        private void StopListener()
        {
            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }
            _listener = null;
        }

        // 由 Start() 与设置窗口的“保存并连接”按钮共用
        internal void Connect()
        {
            // token 续期/获取是异步的，放到后台执行，避免阻塞 UI 线程。
            Task.Run(async () =>
            {
                try
                {
                    Trace("连接流程启动（后台线程）");
                    await ConnectAsync();
                }
                catch (Exception ex)
                {
                    Trace("Connect 异常: " + ex);
                    Log("连接启动异常: " + ex.Message);
                }
            });
        }

        // 确保 token 可用并建立 WebSocket 连接。供首次连接与断线重连共用：
        // 每次都重建 client 并重新订阅事件（含 OnUnexpectedDisconnect 触发重连）。
        private async Task ConnectAsync()
        {
            // 已通过当前 token 建立连接时跳过重复建连：同一 token 的第二个并发 WebSocket
            // 会被 Restream 以 401 拒绝（会话限制），而浮层/布局等配置改动已由
            // SetUseOwnOverlay 即时套用，无需为保存配置而重建连接。
            if (_connected && _client != null)
            {
                Trace("WebSocket 已连接，跳过重复建连（保存配置不会重建连接）");
                Log("连接已在运行，配置已即时套用（浮层布局等），未重建连接。");
                return;
            }
            // 并发守卫：Connect（保存并连接/自动恢复）与 ScheduleReconnect 可能在断连/重连间隙
            // 并发进入，用原子标志丢弃冗余调用，避免建出重复 WebSocket 导致同一聊天投两遍弹幕。
            if (System.Threading.Interlocked.Exchange(ref _connecting, 1) == 1) return;
            try
            {
                if (!await EnsureTokenAsync())
                {
                    Trace("Connect 终止：token 不可用");
                    // 非鉴权类失败（多为启动瞬间网络/代理未就绪的瞬断）交由重连循环自动重试，
                    // 避免一次性误报后停滞；确属 token 失效（_authFailed）则不重试，仅提示重新授权。
                    if (!_authFailed && this.Status) ScheduleReconnect();
                    return;
                }

                Trace("token 可用，准备建立 WebSocket 连接");
                _connected = false;
                var cfg = Config.Load();
                _client?.Disconnect();
                _client = new RestreamChatClient(cfg.AccessToken, cfg.ProxyMode, cfg.ProxyUrl);
                // 连接信息里带当前频道与平台，用于拉取 Twitch emote（Kappa 等显示为图片）。
                _client.OnChannelInfo += (targetId, eventSourceId) =>
                {
                    if (eventSourceId != 2) return; // 仅 Twitch 有 BTTV/FFZ/7TV 表情生态
                    Task.Run(async () =>
                    {
                        try
                        {
                            var em = await EmoteProvider.FetchTwitchAsync(targetId, cfg.ProxyMode, cfg.ProxyUrl);
                            lock (_emotes)
                            {
                                foreach (var kv in em)
                                    if (!_emotes.ContainsKey(kv.Key)) _emotes[kv.Key] = kv.Value;
                            }
                            Trace("Twitch emote 已拉取：" + em.Count + " 个");
                        }
                        catch (Exception ex) { Trace("emote 拉取失败: " + ex.Message); }
                    });
                };
                _client.OnMessage += (platform, user, text, emotes) =>
                {
                    // 一条聚合聊天 -> 一条弹幕。平台名做前缀便于区分来源。
                    // 同时写入弹幕姬官方日志面板，便于在面板里直接看到收到的聊天；
                    // 详细链路（含表情范围）另走调试日志 Trace。
                    Trace("[收到聊天] " + platform + " " + user + ": " + text + " | 表情 " + (emotes == null ? 0 : emotes.Count) + " 个");
                    this.Log("[" + platform + "] " + user + ": " + text);
                    PushDanmaku(platform, user, text, emotes);
                };
                _client.OnStatus += msg =>
                {
                    Log(msg);
                    Trace("[状态] " + msg);
                    // 连接成功置 true、异常/停止置 false，供重连循环判定是否恢复。
                    // 连上即清掉鉴权失败标记，使后续若真断连可正常重连。
                    if (msg.Contains("已连接")) { _connected = true; _authRetried = false; _authFailed = false; }
                    else if (msg.Contains("异常") || msg.Contains("已停止")) _connected = false;
                    // 重连提示不必滚成弹幕（避免反复刷屏），仅连上/断开等关键状态进弹幕列表。
                    if (!msg.Contains("重连")) PushDanmaku("Restream", "", msg, null);
                };
                _client.OnUnexpectedDisconnect += ScheduleReconnect;
                // 鉴权失败（401）不重连：尝试 refresh 续期一次，失败则提示重新授权。
                _client.OnAuthFailure += () => Task.Run(async () => await HandleAuthFailureAsync());
                _client.Connect();
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _connecting, 0);
            }
        }

        // 鉴权失败（401）处理：不进入 5 分钟重连循环。先尝试用 refresh token 续期一次；
        // 续期成功则重连一次，续期失败（或无 refresh token）则明确提示用户重新授权。
        private async Task HandleAuthFailureAsync()
        {
            _connected = false;
            Trace("处理鉴权失败（401）：准备尝试 refresh 续期");
            Log("Restream 授权已失效（服务器返回 401，token 被拒绝）。");
            if (_authRetried)
            {
                _authFailed = true;
                Log("已尝试用 refresh token 续期仍然失败，请重新授权：打开插件「设置」点击「登录并授权」，或在第 ④ 步手动填入新的 access token。");
                return;
            }
            _authRetried = true;

            // 强制走续期路径：清掉本地 access token，使 EnsureTokenAsync 用 refresh token 续期而非直接复用旧 token。
            var cfg = Config.Load();
            cfg.AccessToken = "";
            Config.Save(cfg);
            if (await EnsureTokenAsync())
            {
                Log("已用 refresh token 续期，正在重连...");
                await ConnectAsync();
            }
            else
            {
                // 仅当刷新被服务器明确拒绝才认定 token 失效；否则（网络瞬断）交由重连循环重试。
                // RefreshTokenWithRetryAsync 已在「服务器明确拒绝(4xx)」时置 _authFailed=true 并提示重新授权，
                // 此处复用该判定，不重复覆盖（避免网络瞬断被误判为永久失效而锁死重连）。
                if (!_authFailed && this.Status)
                {
                    Log("续期暂未成功（网络/传输层错误，非 token 失效），将自动重试。");
                    ScheduleReconnect();
                }
            }
        }

        // 意外断连后的自动重连：指数退避（3s→30s 封顶），至少持续重试 5 分钟。
        // 期间任一连接恢复（_connected 置 true）即停止；插件被停用、或鉴权已彻底失败也立即停止，不会无限占用。
        private void ScheduleReconnect()
        {
            if (_reconnecting) return;
            if (!this.Status) return;
            Trace("触发重连流程");
            _reconnecting = true;
            Task.Run(async () =>
            {
                try
                {
                    // 进入重连即认定当前连接已失效：服务器优雅 Close 帧只置 unexpected 标记、
                    // 不触发 OnStatus，_connected 仍可能为上次成功连接遗留的 true，若不在此清零，
                    // 下方循环会误判“连接已恢复”而直接退出，导致 WS 已死却永不重连。
                    _connected = false;
                    // 鉴权失败（token 已彻底失效）不需要重试连接，直接提示重新授权。
                    if (_authFailed)
                    {
                        Log("Restream token 已失效，需重新授权后才能重连（见上方提示）。");
                        return;
                    }
                    var deadline = DateTimeOffset.Now.AddMinutes(5);
                    var attempt = 0;
                    var delayMs = 3000;
                    while (DateTimeOffset.Now < deadline)
                    {
                        if (!this.Status) { Log("Restream 重连已取消（插件已停用）"); return; }
                        if (_connected) { Log("Restream 连接已恢复"); return; }
                        attempt++;
                        Log("Restream 连接已断开，正在重连（第 " + attempt + " 次）...");
                        Trace("重连尝试 #" + attempt);
                        await Task.Delay(delayMs);
                        if (!this.Status) return;
                        try { await ConnectAsync(); }
                        catch (Exception ex) { Trace("重连异常: " + ex.Message); }
                        // 给新连接一点时间建立（OnStatus “已连接” 会置 _connected=true），避免反复重建。
                        await Task.Delay(2500);
                        delayMs = Math.Min(30000, delayMs * 2);
                    }
                    Log("Restream 重连失败（已持续重试约 5 分钟），请检查网络或重新授权。");
                    Trace("重连放弃：超过 5 分钟仍未恢复");
                }
                finally
                {
                    _reconnecting = false;
                }
            });
        }

        // 组合弹幕的“来源名”：平台用方括号，紧跟观众名（若有）。
        // 退回弹幕姬自带浮层时整体作为 AddDMText 的 name 传入，正文单独传 text，
        // 由框架渲染成 “name : text”，避免出现多余前缀冒号。
        internal static string BuildDanmakuName(string platform, string user)
        {
            return "[" + platform + "]" + (string.IsNullOrEmpty(user) ? "" : " " + user);
        }

        // 把一条聊天投到显示层。
        // 开启独立浮层时走 RestreamOverlayWindow（emoji/表情包正确渲染）；
        // 否则退回 bililive_dm 自带浮层（AddDMText，warn=false 避免被“显示错误”开关丢弃）。
        // 仅在插件启用（Status==true）时推送，停用后不再注入弹幕。
        private void PushDanmaku(string platform, string user, string text, List<EmoteRange> emotes)
        {
            if (!this.Status) return;
            try
            {
                if (UseOwnOverlay)
                {
                    EnsureOverlay();
                    if (_overlay != null)
                    {
                        _overlay.ShowDanmaku(platform, user, text, emotes);
                        return;
                    }
                }
                // 退回路径：用框架原生的“用户名 : 正文”分离渲染。name 为空时弹幕姬会补默认“ : ”前缀，
                // 因此把平台与观众名合并进 name，正文单独作为 text 传入。
                // AddDMText 是弹幕姬主窗口的方法（DMPlugin 基类仅暴露 AddDM），框架自身也以
                // dynamic 调 MainWindow.AddDMText 完成跨程序集互操作，此处沿用同一方式。
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        dynamic mw = System.Windows.Application.Current.MainWindow;
                        mw.AddDMText(BuildDanmakuName(platform, user), text, false, false);
                    }
                    catch (Exception ex)
                    {
                        Trace("PushDanmaku 注入失败: " + ex.Message);
                    }
                }));
            }
            catch (Exception ex)
            {
                Trace("PushDanmaku 调度失败: " + ex.Message);
            }
        }

        // 在 UI 线程创建独立浮层窗口（Window 必须在 UI 线程构造与显示）。
        private void EnsureOverlay()
        {
            if (_overlay != null) return;
            try
            {
                Dispatcher.Invoke(new Action(() =>
                {
                    if (_overlay != null) return;
                    var ocfg = Config.Load();
                    // Mode/Side 必须在构造时传入：构造函数据此决定建 _sidebarRoot 还是 _scrollRoot，
                    // 否则 Loaded 时 PositionToScreen 会访问到 null 的根而崩溃。
                    _overlay = new RestreamOverlayWindow(
                        _emotes,
                        ocfg.OverlayMode == "sidebar" ? "sidebar" : "scroll",
                        ocfg.OverlaySide == "left" ? "left" : "right")
                    {
                        FontSize = 22,
                        BackgroundOpacity = 0.55
                    };
                    _overlay.Show();
                    Trace("独立浮层窗口已创建");
                }));
            }
            catch (Exception ex)
            {
                Trace("独立浮层创建失败: " + ex.Message);
            }
        }
    }
}
