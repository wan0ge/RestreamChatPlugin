using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RestreamChatPlugin.Tests
{
    // 覆盖解析（ParseFrame）、弹幕命名（BuildDanmakuName）、配置根目录（Config.PluginRoot）、
    // 内嵌资源（Newtonsoft.Json）的常规与边缘情况。
    // 说明：ParseFrame 内部使用 Newtonsoft.Json，运行时会触发插件自带的 AssemblyResolve 从内嵌资源加载，
    // 因此这些测试同时验证了“单文件嵌入”是否真正生效（若嵌入失败，JObject.Parse 会抛 FileNotFoundException）。
    [TestClass]
    public class PluginTests
    {
        // ===== ParseFrame：观众聊天（event） =====

        [TestMethod]
        public void ParseFrame_Event_TwitchNormalChat()
        {
            var json = "{\"action\":\"event\",\"payload\":{\"eventPayload\":{\"author\":{\"displayName\":\"xdqt5555\"},\"text\":\"哇，好精彩的直播\"},\"eventSourceId\":2,\"eventTypeId\":4}}";
            var f = RestreamChatClient.ParseFrame(json);
            Assert.AreEqual("message", f.Kind);
            Assert.AreEqual("Twitch", f.Platform);
            Assert.AreEqual("xdqt5555", f.User);
            Assert.AreEqual("哇，好精彩的直播", f.Text);
        }

        [TestMethod]
        public void ParseFrame_Event_UnknownSourceId_UseSrcPrefix()
        {
            var json = "{\"action\":\"event\",\"payload\":{\"eventPayload\":{\"text\":\"x\"},\"eventSourceId\":999}}";
            var f = RestreamChatClient.ParseFrame(json);
            Assert.AreEqual("message", f.Kind);
            Assert.AreEqual("src999", f.Platform);
        }

        [TestMethod]
        public void ParseFrame_Event_AuthorFallbackToUsername()
        {
            var json = "{\"action\":\"event\",\"payload\":{\"eventPayload\":{\"author\":{\"username\":\"foo\"},\"text\":\"x\"},\"eventSourceId\":4}}";
            var f = RestreamChatClient.ParseFrame(json);
            Assert.AreEqual("foo", f.User);
        }

        [TestMethod]
        public void ParseFrame_Event_AuthorMissing_DefaultQuestion()
        {
            var json = "{\"action\":\"event\",\"payload\":{\"eventPayload\":{\"text\":\"x\"},\"eventSourceId\":4}}";
            var f = RestreamChatClient.ParseFrame(json);
            Assert.AreEqual("?", f.User);
        }

        // ===== ParseFrame：表情范围（replaces，权威覆盖 Twitch 原生表情） =====

        [TestMethod]
        public void ParseFrame_Event_WithReplaces_ParsesRanges()
        {
            // 真实抓包结构：SirPrise 是 Twitch 原生表情，replaces 给出其在文本中的起止与图片地址。
            var json = "{\"action\":\"event\",\"payload\":{\"eventPayload\":{\"author\":{\"displayName\":\"elegy2333\"},\"text\":\"SirPrise\",\"replaces\":[{\"from\":0,\"payload\":{\"url\":\"https://static-cdn.jtvnw.net/emoticons/v1/301544926/3.0\"},\"to\":7,\"type\":\"imageUrl\"}]},\"eventSourceId\":2,\"eventTypeId\":4}}";
            var f = RestreamChatClient.ParseFrame(json);
            Assert.AreEqual("message", f.Kind);
            Assert.AreEqual("SirPrise", f.Text);
            Assert.AreEqual(1, f.Emotes.Count, "应解析出 1 条表情范围");
            Assert.AreEqual(0, f.Emotes[0].Start);
            Assert.AreEqual(7, f.Emotes[0].End);
            Assert.IsTrue(f.Emotes[0].Url.Contains("301544926"), "表情图片地址应来自 replaces");
        }

        [TestMethod]
        public void ParseFrame_Event_NoReplaces_EmotesEmpty()
        {
            var json = "{\"action\":\"event\",\"payload\":{\"eventPayload\":{\"author\":{\"displayName\":\"a\"},\"text\":\"hi\"},\"eventSourceId\":4}}";
            var f = RestreamChatClient.ParseFrame(json);
            Assert.IsNotNull(f.Emotes, "Emotes 不应为 null");
            Assert.AreEqual(0, f.Emotes.Count, "无 replaces 时表情范围为空列表");
        }

        // ===== ParseFrame：无文本事件的可读兜底 =====

        [TestMethod]
        public void ParseFrame_Event_NoText_Subscribe()
        {
            var json = "{\"action\":\"event\",\"payload\":{\"eventPayload\":{\"author\":{\"displayName\":\"a\"}},\"eventSourceId\":4,\"eventTypeId\":26}}";
            var f = RestreamChatClient.ParseFrame(json);
            Assert.AreEqual("message", f.Kind);
            Assert.AreEqual("订阅了频道", f.Text);
        }

        [TestMethod]
        public void ParseFrame_Event_NoText_SuperChat()
        {
            var json = "{\"action\":\"event\",\"payload\":{\"eventPayload\":{\"author\":{\"displayName\":\"a\"}},\"eventSourceId\":4,\"eventTypeId\":7}}";
            var f = RestreamChatClient.ParseFrame(json);
            Assert.AreEqual("[SuperChat]", f.Text);
        }

        [TestMethod]
        public void ParseFrame_Event_NoText_Sticker()
        {
            var json = "{\"action\":\"event\",\"payload\":{\"eventPayload\":{\"author\":{\"displayName\":\"a\"}},\"eventSourceId\":4,\"eventTypeId\":3}}";
            var f = RestreamChatClient.ParseFrame(json);
            Assert.AreEqual("[贴图]", f.Text);
        }

        [TestMethod]
        public void ParseFrame_Event_NoText_OtherTypeId_Ignored()
        {
            var json = "{\"action\":\"event\",\"payload\":{\"eventPayload\":{\"author\":{\"displayName\":\"a\"}},\"eventSourceId\":4,\"eventTypeId\":99}}";
            var f = RestreamChatClient.ParseFrame(json);
            Assert.AreEqual("ignore", f.Kind);
        }

        // ===== ParseFrame：结构缺失 / 边缘 =====

        [TestMethod]
        public void ParseFrame_Event_PayloadNull_Ignored()
        {
            var f = RestreamChatClient.ParseFrame("{\"action\":\"event\",\"payload\":null}");
            Assert.AreEqual("ignore", f.Kind);
        }

        [TestMethod]
        public void ParseFrame_Event_EventPayloadNull_Ignored()
        {
            var f = RestreamChatClient.ParseFrame("{\"action\":\"event\",\"payload\":{\"eventSourceId\":2}}");
            Assert.AreEqual("ignore", f.Kind);
        }

        // ===== ParseFrame：主播回复 / 频道信息 / 其它 action =====

        [TestMethod]
        public void ParseFrame_ReplyCreated()
        {
            var json = "{\"action\":\"reply_created\",\"payload\":{\"eventSourceId\":2,\"text\":\"hello\"}}";
            var f = RestreamChatClient.ParseFrame(json);
            Assert.AreEqual("message", f.Kind);
            Assert.AreEqual("Twitch", f.Platform);
            Assert.AreEqual("我", f.User);
            Assert.AreEqual("hello", f.Text);
        }

        [TestMethod]
        public void ParseFrame_ConnectionInfo_WithTarget()
        {
            var json = "{\"action\":\"connection_info\",\"payload\":{\"target\":{\"id\":\"12345\"},\"eventSourceId\":2}}";
            var f = RestreamChatClient.ParseFrame(json);
            Assert.AreEqual("channelInfo", f.Kind);
            Assert.AreEqual("12345", f.TargetId);
            Assert.AreEqual(2, f.EventSourceId);
        }

        [TestMethod]
        public void ParseFrame_ConnectionInfo_NoTarget_Ignored()
        {
            var f = RestreamChatClient.ParseFrame("{\"action\":\"connection_info\",\"payload\":{}}");
            Assert.AreEqual("ignore", f.Kind);
        }

        [TestMethod]
        public void ParseFrame_Heartbeat_Ignored()
        {
            var f = RestreamChatClient.ParseFrame("{\"action\":\"heartbeat\"}");
            Assert.AreEqual("ignore", f.Kind);
        }

        [TestMethod]
        public void ParseFrame_UnknownAction_Ignored()
        {
            var f = RestreamChatClient.ParseFrame("{\"action\":\"reply_accepted\"}");
            Assert.AreEqual("ignore", f.Kind);
        }

        [TestMethod]
        public void ParseFrame_MalformedJson_Error()
        {
            var f = RestreamChatClient.ParseFrame("not a json");
            Assert.AreEqual("error", f.Kind);
            Assert.IsFalse(string.IsNullOrEmpty(f.Error));
        }

        // ===== BuildDanmakuName：弹幕命名（避免开头多余冒号） =====

        [TestMethod]
        public void BuildDanmakuName_WithUser()
        {
            Assert.AreEqual("[Twitch] alice", RestreamPlugin.BuildDanmakuName("Twitch", "alice"));
        }

        [TestMethod]
        public void BuildDanmakuName_EmptyUser_NoLeadingColon()
        {
            var name = RestreamPlugin.BuildDanmakuName("Restream", "");
            Assert.AreEqual("[Restream]", name);
            Assert.IsFalse(name.StartsWith(":"), "来源名不应以冒号开头（避免弹幕姬补默认“ : ”前缀）");
        }

        [TestMethod]
        public void BuildDanmakuName_NullUser_NoLeadingColon()
        {
            var name = RestreamPlugin.BuildDanmakuName("YouTube", null);
            Assert.AreEqual("[YouTube]", name);
            Assert.IsFalse(name.StartsWith(":"));
        }

        // ===== Config.PluginRoot：单文件部署路径（与 DLL 同名子目录） =====

        [TestMethod]
        public void PluginRoot_NamedAfterAssembly()
        {
            var pluginAsm = typeof(Config).Assembly;
            var pluginDir = Path.GetDirectoryName(pluginAsm.Location);
            var name = Path.GetFileNameWithoutExtension(pluginAsm.Location); // 如 RestreamChatPlugin
            Assert.IsFalse(string.IsNullOrEmpty(Config.PluginRoot), "PluginRoot 不应为空");
            // 单文件部署时数据目录为 DLL 同级、以 DLL 命名的子目录（与 点歌姬 同构）。
            Assert.AreEqual(Path.Combine(pluginDir, name), Config.PluginRoot,
                "单文件部署时数据目录应为 DLL 同级的“<DLL名>”子目录");
        }

        // ===== 内嵌资源：Newtonsoft.Json 已打进 DLL（单文件部署） =====

        [TestMethod]
        public void EmbeddedResource_NewtonsoftJson_Present()
        {
            var names = typeof(RestreamChatClient).Assembly.GetManifestResourceNames();
            Assert.IsTrue(names.Contains("Newtonsoft.Json.dll"),
                "Newtonsoft.Json.dll 应作为内嵌资源存在，使产物为单文件 DLL。实际资源：" + string.Join(", ", names));
        }

        // ===== emoji -> Twemoji 码点映射（把 emoji 渲染成高清图片，规避字体降级为单色） =====

        [TestMethod]
        public void EmojiCodepoints_Pray_Stripped()
        {
            // 🙏 = U+1F64F，去掉变体选择符/ZWJ 后应为 1f64f（Twemoji 资源名）。
            Assert.AreEqual("1f64f", RestreamOverlayWindow.EmojiCodepoints("🙏", false, false));
        }

        [TestMethod]
        public void EmojiCodepoints_Heart_StripsVariationSelector()
        {
            // ❤️ = U+2764 U+FE0F：去掉变体选择符后为 2764；保留变体时为 2764-fe0f。
            Assert.AreEqual("2764", RestreamOverlayWindow.EmojiCodepoints("❤️", false, false));
            Assert.AreEqual("2764-fe0f", RestreamOverlayWindow.EmojiCodepoints("❤️", true, true));
        }

        [TestMethod]
        public void EmojiCodepoints_Family_KeepsZwj()
        {
            // 家庭 emoji 含 ZWJ：保留 ZWJ（keepZwj=true）时文件名应含 200d 且以 man 码点开头；
            // 精简（keepZwj=false）时应去掉 ZWJ 与变体选择符。用结构断言避免受具体成员（男/女/孩）变体影响。
            var full = RestreamOverlayWindow.EmojiCodepoints("👨‍👩‍👦", true, true);
            Assert.IsTrue(full.Contains("200d"), "家庭 emoji 应保留 ZWJ 连接符（Twemoji 资源名含 200d）");
            Assert.IsTrue(full.StartsWith("1f468-"), "应以 man 码点开头");
            var stripped = RestreamOverlayWindow.EmojiCodepoints("👨‍👩‍👦", false, false);
            Assert.IsFalse(stripped.Contains("200d"), "精简模式应去掉 ZWJ");
            Assert.IsFalse(stripped.Contains("fe0f"), "精简模式应去掉变体选择符");
        }

        [TestMethod]
        public void EmojiCodepoints_ThumbsUp_NoVariation()
        {
            // 👍 = U+1F44D，无变体选择符/ZWJ，完整与精简一致。
            Assert.AreEqual("1f44d", RestreamOverlayWindow.EmojiCodepoints("👍", false, false));
            Assert.AreEqual("1f44d", RestreamOverlayWindow.EmojiCodepoints("👍", true, true));
        }

        [TestMethod]
        public void EmojiCdnUrls_ContainsWorkingUrlForPray()
        {
            // 🙏 生成的候选 URL 应包含 1f64f.png，且按镜像顺序展开。
            var urls = RestreamOverlayWindow.EmojiCdnUrls("🙏").ToList();
            Assert.IsTrue(urls.Any(u => u.EndsWith("1f64f.png")), "应生成 🙏 的 Twemoji 图片 URL");
            Assert.IsTrue(urls[0].StartsWith("https://cdn.jsdelivr.net/"), "首个候选应使用主镜像 cdn.jsdelivr.net");
        }

        [TestMethod]
        public void EmojiCdnUrls_Heart_TriesStrippedAfterFull()
        {
            // ❤️：完整候选 2764-fe0f.png 之后应追加精简候选 2764.png（覆盖资源实际命名）。
            var urls = RestreamOverlayWindow.EmojiCdnUrls("❤️").ToList();
            Assert.IsTrue(urls.Any(u => u.EndsWith("2764.png")), "应生成红心的精简候选 2764.png");
        }

        // ===== emoji 分段：相邻多个 emoji 各自成图（避免拼成无效 Twemoji 文件名） =====

        [TestMethod]
        public void EmojiTokenEnd_SplitsAdjacentEmojis()
        {
            // 🥰😇 是两个独立 emoji，应拆成两个片段，而非拼成 1f970-1f607 这种无效文件名。
            var s = "🥰😇";
            var j1 = RestreamOverlayWindow.EmojiTokenEnd(s, 0);
            Assert.AreEqual("🥰", s.Substring(0, j1), "第一个片段应为单个 emoji 🥰");
            var j2 = RestreamOverlayWindow.EmojiTokenEnd(s, j1);
            Assert.AreEqual("😇", s.Substring(j1, j2 - j1), "第二个片段应为单个 emoji 😇");
        }

        [TestMethod]
        public void EmojiTokenEnd_KeepsZwjSequenceAsOneToken()
        {
            // 家庭 emoji（含 ZWJ）应作为一个片段，供 Twemoji 生成单张图片。
            var s = "👨‍👩‍👦";
            var j = RestreamOverlayWindow.EmojiTokenEnd(s, 0);
            Assert.AreEqual(s.Length, j, "ZWJ 序列应整体作为一个片段（长度等于原串）");
            Assert.AreEqual(s, s.Substring(0, j));
        }

        [TestMethod]
        public void EmojiTokenEnd_HeartWithVariationSelector_OneToken()
        {
            // ❤️ = U+2764 U+FE0F 应作为一个片段。
            var s = "❤️";
            var j = RestreamOverlayWindow.EmojiTokenEnd(s, 0);
            Assert.AreEqual(s.Length, j);
        }

        [TestMethod]
        public void EmojiTokenEnd_MixedText_EmojisSeparated()
        {
            // 文本与 emoji 交替：应只把 emoji 段识别为片段，文本不混入。
            var s = "hi🥰ok😇";
            var j1 = RestreamOverlayWindow.EmojiTokenEnd(s, 2); // 🥰 起始于索引 2
            Assert.AreEqual("🥰", s.Substring(2, j1 - 2), "🥰 应单独成段");
            var j2 = RestreamOverlayWindow.EmojiTokenEnd(s, 6); // 😇 起始于索引 6
            Assert.AreEqual("😇", s.Substring(6, j2 - 6), "😇 应单独成段");
        }

        [TestMethod]
        public void EmojiTokenEnd_SingleEmoji_OneToken()
        {
            var s = "🙏";
            var j = RestreamOverlayWindow.EmojiTokenEnd(s, 0);
            Assert.AreEqual(s.Length, j, "单个 emoji 应作为一个片段");
        }

        // ===== emoji 本地缓存：文件名与落盘路径 =====

        [TestMethod]
        public void EmojiCacheFile_UsesStrippedCodepoint()
        {
            // 缓存文件名以去变体选择符、保留 ZWJ 的码点命名，与 Twemoji 资源一致且稳定。
            Assert.AreEqual("1f970.png", RestreamOverlayWindow.EmojiCodepoints("🥰", false, true) + ".png");
            Assert.AreEqual("2764.png", RestreamOverlayWindow.EmojiCodepoints("❤️", false, true) + ".png");
        }

        // ===== 表情包本地缓存：URL 哈希文件名 =====

        [TestMethod]
        public void EmoteCacheFile_StableForSameUrl()
        {
            // 同一 URL 必须始终得到同一缓存文件名，保证“下载一次、反复命中本地”。
            var url = "https://static-cdn.jtvnw.net/emoticons/x/1/1-1.png";
            var a = RestreamOverlayWindow.EmoteCacheFile(url);
            var b = RestreamOverlayWindow.EmoteCacheFile(url);
            Assert.AreEqual(a, b, "相同 URL 必须得到相同缓存文件名");
        }

        [TestMethod]
        public void EmoteCacheFile_KeepsOriginalExtension()
        {
            // 扩展名沿用原 URL 末段，保留图片格式（Twitch/BTTV/FFZ 为 PNG，7TV 可能为 WebP）。
            Assert.IsTrue(RestreamOverlayWindow.EmoteCacheFile("https://static-cdn.jtvnw.net/emoticons/x/1/1-1.png").EndsWith(".png"));
            Assert.IsTrue(RestreamOverlayWindow.EmoteCacheFile("https://cdn.7tv.app/emote/abc123/1.webp").EndsWith(".webp"));
        }

        [TestMethod]
        public void EmoteCacheFile_NoCollisionBetweenUrls()
        {
            // 不同 URL 必须映射到不同文件名，避免互相覆盖。
            var png = RestreamOverlayWindow.EmoteCacheFile("https://static-cdn.jtvnw.net/emoticons/x/1/1-1.png");
            var webp = RestreamOverlayWindow.EmoteCacheFile("https://cdn.7tv.app/emote/abc123/1.webp");
            Assert.AreNotEqual(png, webp, "不同 URL 不应冲突");
        }

        [TestMethod]
        public void EmoteCacheFile_NoExtensionFallsBackToPng()
        {
            // URL 无扩展名（如 BTTV 的 /emote/xxx/1x）时默认按 PNG 落盘，WPF 仍按内容解码。
            Assert.IsTrue(RestreamOverlayWindow.EmoteCacheFile("https://cdn.betterttv.net/emote/abc/1x").EndsWith(".png"));
        }

        // ===== ExtractAuthCode：从用户粘贴内容中提取授权码（覆盖完整回调整址 / 独立片段 / 直接 code / 截断边界） =====
        [TestMethod]
        public void ExtractAuthCode_FullCallbackUrl_StopsAtAmp()
        {
            // 完整回调地址：取到首个 code 参数值，遇到 & 即截断（state 不会污染）。
            var s = "http://localhost:8989/callback?code=ABC123&state=restream_plugin";
            Assert.AreEqual("ABC123", RestreamPlugin.ExtractAuthCode(s));
        }

        [TestMethod]
        public void ExtractAuthCode_StandaloneCodeFragment()
        {
            // 用户只粘贴 ?code= 片段时同样提取到 code 值。
            Assert.AreEqual("ABC123", RestreamPlugin.ExtractAuthCode("?code=ABC123"));
        }

        [TestMethod]
        public void ExtractAuthCode_DirectCode_ReturnsAsIs()
        {
            // 不含 code= 时按原始 code 文本原样返回（用户直接粘贴了 code 值）。
            Assert.AreEqual("ABC123", RestreamPlugin.ExtractAuthCode("ABC123"));
        }

        [TestMethod]
        public void ExtractAuthCode_StopsAtHashFragment()
        {
            // 授权回调带 #fragment 时遇到 # 即截断，不把 fragment 并入 code。
            var s = "http://localhost:8989/callback?code=ABC123#_=abc";
            Assert.AreEqual("ABC123", RestreamPlugin.ExtractAuthCode(s));
        }

        [TestMethod]
        public void ExtractAuthCode_CodeEmbeddedBetweenAmpersands()
        {
            // code 前后都有其它参数（&code=...&）时仍只取 code 值。
            Assert.AreEqual("ABC123", RestreamPlugin.ExtractAuthCode("x&code=ABC123&y"));
        }

        // ===== 代理模式：IsValidProxyMode / ApplyHttpClientProxy / ResolveWebSocketProxy / ProxyDescription / 旧字段迁移 =====

        [TestMethod]
        public void ProxyMode_ValidModes_Accepted()
        {
            // 三种合法模式均可识别。
            Assert.IsTrue(Config.IsValidProxyMode(Config.ProxyModeNone));
            Assert.IsTrue(Config.IsValidProxyMode(Config.ProxyModeSystem));
            Assert.IsTrue(Config.IsValidProxyMode(Config.ProxyModeCustom));
        }

        [TestMethod]
        public void ProxyMode_InvalidModes_Rejected()
        {
            // 大小写敏感：None/auto/空/空格均不合法，避免误用未定义模式导致行为不确定。
            Assert.IsFalse(Config.IsValidProxyMode("None"));
            Assert.IsFalse(Config.IsValidProxyMode("SYSTEM"));
            Assert.IsFalse(Config.IsValidProxyMode("auto"));
            Assert.IsFalse(Config.IsValidProxyMode(""));
            Assert.IsFalse(Config.IsValidProxyMode(null));
            Assert.IsFalse(Config.IsValidProxyMode("   "));
        }

        [TestMethod]
        public void ApplyHttpClientProxy_None_DisablesProxy()
        {
            // 直连（默认）：禁用 HttpClient 代理，不走系统代理。
            var h = new HttpClientHandler();
            Config.ApplyHttpClientProxy(h, Config.ProxyModeNone, "http://127.0.0.1:7890");
            Assert.IsFalse(h.UseProxy);
        }

        [TestMethod]
        public void ApplyHttpClientProxy_System_UsesSystemProxy()
        {
            // 系统代理：启用代理并指向系统代理对象（与 ClientWebSocket 共享同一来源）。
            var h = new HttpClientHandler();
            Config.ApplyHttpClientProxy(h, Config.ProxyModeSystem, "");
            Assert.IsTrue(h.UseProxy);
            Assert.AreSame(WebRequest.DefaultWebProxy, h.Proxy);
        }

        [TestMethod]
        public void ApplyHttpClientProxy_Custom_UsesGivenUrl()
        {
            // 自定义代理：启用代理且地址精确匹配用户输入。
            var h = new HttpClientHandler();
            Config.ApplyHttpClientProxy(h, Config.ProxyModeCustom, "http://127.0.0.1:7890");
            Assert.IsTrue(h.UseProxy);
            Assert.IsInstanceOfType(h.Proxy, typeof(WebProxy));
            Assert.AreEqual(new Uri("http://127.0.0.1:7890"), ((WebProxy)h.Proxy).Address);
        }

        [TestMethod]
        public void ApplyHttpClientProxy_Custom_EmptyUrl_DoesNotThrow()
        {
            // 边缘：选了自定义却未填地址，应降级为直连而非抛 UriFormatException。
            var h = new HttpClientHandler();
            Config.ApplyHttpClientProxy(h, Config.ProxyModeCustom, "");
            Assert.IsTrue(h.UseProxy);
            Assert.IsNull(((WebProxy)h.Proxy).Address);
        }

        [TestMethod]
        public void ResolveWebSocketProxy_None_ReturnsBypassProxy()
        {
            // 直连：返回 Address 为 null 的 WebProxy，GetProxy 直接返回目标地址（不读系统代理）。
            var p = Config.ResolveWebSocketProxy(Config.ProxyModeNone, "http://127.0.0.1:7890");
            Assert.IsInstanceOfType(p, typeof(WebProxy));
            Assert.IsNull(((WebProxy)p).Address);
        }

        [TestMethod]
        public void ResolveWebSocketProxy_System_ReturnsSystemProxy()
        {
            // 系统代理：透传系统代理对象。
            var p = Config.ResolveWebSocketProxy(Config.ProxyModeSystem, "");
            Assert.AreSame(WebRequest.DefaultWebProxy, p);
        }

        [TestMethod]
        public void ResolveWebSocketProxy_Custom_UsesGivenUrl()
        {
            // 自定义代理：WebSocket 也精确使用用户输入地址。
            var p = Config.ResolveWebSocketProxy(Config.ProxyModeCustom, "http://127.0.0.1:7890");
            Assert.IsInstanceOfType(p, typeof(WebProxy));
            Assert.AreEqual(new Uri("http://127.0.0.1:7890"), ((WebProxy)p).Address);
        }

        [TestMethod]
        public void ProxyDescription_AllModes()
        {
            // 日志描述覆盖三种模式与“自定义未填地址”边界。
            Assert.AreEqual("直连（不使用代理）", Config.ProxyDescription(Config.ProxyModeNone, ""));
            Assert.AreEqual("系统代理", Config.ProxyDescription(Config.ProxyModeSystem, ""));
            Assert.AreEqual("自定义 http://127.0.0.1:7890", Config.ProxyDescription(Config.ProxyModeCustom, "http://127.0.0.1:7890"));
            Assert.AreEqual("自定义 (未填地址)", Config.ProxyDescription(Config.ProxyModeCustom, ""));
        }

        [TestMethod]
        public void ConfigLoad_LegacyProxyField_MigratesToCustom()
        {
            // 上游兼容：旧版 config.json 仅有 "Proxy":"http://x" 时，迁移为 custom + ProxyUrl，
            // 避免破坏已配置代理的用户。
            var path = System.IO.Path.Combine(Config.PluginRoot, "config.json");
            string backup = File.Exists(path) ? File.ReadAllText(path) : null;
            try
            {
                File.WriteAllText(path, "{\"Proxy\":\"http://127.0.0.1:7890\",\"ClientId\":\"x\"}");
                var c = Config.Load();
                Assert.AreEqual(Config.ProxyModeCustom, c.ProxyMode);
                Assert.AreEqual("http://127.0.0.1:7890", c.ProxyUrl);
            }
            finally
            {
                if (backup != null) File.WriteAllText(path, backup);
                else if (File.Exists(path)) File.Delete(path);
            }
        }

        [TestMethod]
        public void ConfigLoad_LegacyProxyEmpty_DefaultsToNone()
        {
            // 旧版留空（曾等同系统代理）迁移后默认直连（none），符合“默认不走系统代理”的要求。
            var path = System.IO.Path.Combine(Config.PluginRoot, "config.json");
            string backup = File.Exists(path) ? File.ReadAllText(path) : null;
            try
            {
                File.WriteAllText(path, "{\"Proxy\":\"\",\"ClientId\":\"x\"}");
                var c = Config.Load();
                Assert.AreEqual(Config.ProxyModeNone, c.ProxyMode);
            }
            finally
            {
                if (backup != null) File.WriteAllText(path, backup);
                else if (File.Exists(path)) File.Delete(path);
            }
        }

        [TestMethod]
        public void ConfigLoad_InvalidMode_FallsBackToNone()
        {
            // 分支：ProxyMode 为非法值时回落到直连，避免未知模式造成不确定行为。
            var path = System.IO.Path.Combine(Config.PluginRoot, "config.json");
            string backup = File.Exists(path) ? File.ReadAllText(path) : null;
            try
            {
                File.WriteAllText(path, "{\"ProxyMode\":\"bogus\",\"ProxyUrl\":\"http://x\"}");
                var c = Config.Load();
                Assert.AreEqual(Config.ProxyModeNone, c.ProxyMode);
            }
            finally
            {
                if (backup != null) File.WriteAllText(path, backup);
                else if (File.Exists(path)) File.Delete(path);
            }
        }
    }
}
