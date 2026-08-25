using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;

namespace RestreamChatPlugin
{
    // 插件持久化配置（JSON）。保存 OAuth 凭据与 token，便于自动续期。
    public class PluginConfig
    {
        public string ClientId { get; set; } = "";
        public string ClientSecret { get; set; } = "";
        public string AccessToken { get; set; } = "";
        public string RefreshToken { get; set; } = "";
        public long AccessTokenExpiresAt { get; set; } = 0; // Unix 秒，0 表示未知/无效
        public string ProxyUrl { get; set; } = ""; // 自定义代理地址，仅当 ProxyMode=custom 时使用
        public string ProxyMode { get; set; } = "none"; // none=直连（不使用代理）/ system=系统代理 / custom=自定义（见 ProxyUrl）
        public bool UseOwnOverlay { get; set; } = false; // 独立浮层仅弹幕模式、不跟随弹幕姬侧边栏等布局；默认关，使用弹幕姬自带浮层
        public string OverlayMode { get; set; } = "scroll"; // 独立浮层布局：scroll=弹幕滚动, sidebar=侧边栏列表
        public string OverlaySide { get; set; } = "right"; // 侧边栏模式的位置：left / right
        public bool DebugLog { get; set; } = false; // 调试日志开关，默认关；开启后写插件内部目录的“调试日志.log”
        public bool AutoStart { get; set; } = false; // 记住启用状态：弹幕姬主程序不持久化插件启用/停用，重启后默认未启用；此标记供 Inited 自动恢复启用
    }

    public static class Config
    {
        // 插件内部目录：单文件 DLL 部署时，在 DLL 同级建一个以 DLL 命名的子目录
        // （如 Plugins\RestreamChatPlugin.dll -> Plugins\RestreamChatPlugin\），
        // config.json 与调试日志落在此目录内（与 点歌姬 单文件部署同构）。
        internal static string PluginRoot
        {
            get
            {
                try
                {
                    var loc = Assembly.GetExecutingAssembly().Location;
                    if (!string.IsNullOrEmpty(loc))
                    {
                        var dir = Path.GetDirectoryName(loc);
                        var name = Path.GetFileNameWithoutExtension(loc); // 如 RestreamChatPlugin
                        // DLL 位于 <插件名>\bin\ 时，bin 的父目录即插件目录，不再追加名称避免嵌套。
                        if (string.Equals(Path.GetFileName(dir), "bin", StringComparison.OrdinalIgnoreCase))
                            dir = Path.GetDirectoryName(dir);
                        // 若 DLL 已被放在同名目录内（<插件名>\<插件名>.dll），直接用该目录。
                        if (string.Equals(Path.GetFileName(dir), name, StringComparison.OrdinalIgnoreCase))
                            return dir;
                        // 否则在 DLL 同级建一个以 DLL 命名的子目录，数据文件与单文件 DLL 同层。
                        return Path.Combine(dir, name);
                    }
                }
                catch { }
                return System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "弹幕姬", "Plugins", "RestreamChatPlugin");
            }
        }

        private static string ConfigPath =>
            System.IO.Path.Combine(PluginRoot, "config.json");

        public static PluginConfig Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath);
                    var c = JsonConvert.DeserializeObject<PluginConfig>(json);
                    if (c != null)
                    {
                        // 迁移旧版 "Proxy" 字段（单一字符串）：曾表示“空=系统代理 / 非空=自定义”。
                        // 新模型默认直连（none），仅当旧值非空时沿用为 custom，避免破坏已配置代理的用户。
                        try
                        {
                            // 旧版配置只有 "Proxy" 字段、没有 "ProxyMode" 键（当前类序列化总会写出 ProxyMode，
                            // 故 ProxyMode 键缺失即代表旧版）。按旧语义迁移：非空=自定义(custom)、空=直连(none)。
                            var jo = JObject.Parse(json);
                            if (jo["ProxyMode"] == null)
                            {
                                var legacy = (string)jo["Proxy"];
                                if (!string.IsNullOrWhiteSpace(legacy))
                                {
                                    c.ProxyUrl = legacy.Trim();
                                    c.ProxyMode = ProxyModeCustom;
                                }
                                else
                                {
                                    c.ProxyMode = ProxyModeNone;
                                }
                            }
                        }
                        catch { }
                        if (!IsValidProxyMode(c.ProxyMode)) c.ProxyMode = ProxyModeNone;
                        return c;
                    }
                }
            }
            catch { }
            return new PluginConfig();
        }

        public static void Save(PluginConfig cfg)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ConfigPath));
                File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(cfg, Formatting.Indented));
            }
            catch { }
        }

        public const string ProxyModeNone = "none";   // 直连，不读取系统代理
        public const string ProxyModeSystem = "system"; // 使用系统代理（如 Clash/v2ray 设置的系统代理）
        public const string ProxyModeCustom = "custom";  // 使用 ProxyUrl 指定的自定义代理
        private static readonly string[] ProxyModeValues = { ProxyModeNone, ProxyModeSystem, ProxyModeCustom };

        // 校验代理模式是否合法。
        public static bool IsValidProxyMode(string mode) =>
            Array.IndexOf(ProxyModeValues, mode ?? "") >= 0;

        // 为 HttpClientHandler 设定代理：none 禁用代理走直连；system 用系统代理；custom 用 ProxyUrl。
        public static void ApplyHttpClientProxy(HttpClientHandler handler, string mode, string proxyUrl)
        {
            if (mode == ProxyModeNone)
            {
                handler.UseProxy = false;
                return;
            }
            handler.UseProxy = true;
            handler.Proxy = mode == ProxyModeSystem
                ? WebRequest.DefaultWebProxy
                : ProxyFromUrl(proxyUrl);
        }

        // 为 ClientWebSocket 解析代理：none 返回 Address 为 null 的 WebProxy（GetProxy 返回目标地址，直连，不读取系统代理）；
        // system 用系统代理；custom 用 ProxyUrl。
        public static IWebProxy ResolveWebSocketProxy(string mode, string proxyUrl)
        {
            if (mode == ProxyModeNone)
                return new WebProxy();
            if (mode == ProxyModeSystem)
                return WebRequest.DefaultWebProxy;
            return ProxyFromUrl(proxyUrl);
        }

        // 自定义代理地址：空（用户未填）时返回 Address 为 null 的 WebProxy（等效直连，不抛异常）；否则按地址构造。
        private static IWebProxy ProxyFromUrl(string proxyUrl)
        {
            if (string.IsNullOrWhiteSpace(proxyUrl)) return new WebProxy();
            return new WebProxy(proxyUrl);
        }

        // 代理设置的日志描述。
        public static string ProxyDescription(string mode, string proxyUrl)
        {
            if (mode == ProxyModeNone) return "直连（不使用代理）";
            if (mode == ProxyModeSystem) return "系统代理";
            return "自定义 " + (string.IsNullOrWhiteSpace(proxyUrl) ? "(未填地址)" : proxyUrl);
        }
    }
}
