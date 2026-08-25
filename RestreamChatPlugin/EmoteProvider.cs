using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace RestreamChatPlugin
{
    // 拉取 Twitch 频道表情包（emote）的 名称 -> 图片URL 映射。
    // 数据源：BTTV + FFZ + 7TV（均包含原生 Twitch 表情如 Kappa/PogChamp 以及第三方表情）。
    // 任意源失败都不影响其余，最终合并为一个字典；空结果表示降级为纯文字显示。
    public static class EmoteProvider
    {
        public static async Task<Dictionary<string, string>> FetchTwitchAsync(string broadcasterId, string proxyMode, string proxyUrl)
        {
            var dict = new Dictionary<string, string>();
            if (string.IsNullOrWhiteSpace(broadcasterId)) return dict;

            using (var handler = new HttpClientHandler())
            {
                Config.ApplyHttpClientProxy(handler, proxyMode, proxyUrl);
                using (var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) })
                {
                    await AddBttv(http, broadcasterId, dict);
                    await AddFfz(http, broadcasterId, dict);
                    await Add7Tv(http, broadcasterId, dict);
                }
            }
            return dict;
        }

        private static async Task AddBttv(HttpClient http, string id, Dictionary<string, string> dict)
        {
            try
            {
                var g = await http.GetStringAsync("https://api.betterttv.net/3/cached/emotes/global");
                foreach (var e in JArray.Parse(g))
                {
                    var code = (string)e["code"];
                    var eid = (string)e["id"];
                    if (code != null && eid != null) dict[code] = BttvEmoteUrl(eid);
                }
            }
            catch { }
            try
            {
                var c = await http.GetStringAsync("https://api.betterttv.net/3/cached/users/twitch/" + id);
                foreach (var e in JArray.Parse(c))
                {
                    var code = (string)e["code"];
                    var eid = (string)e["id"];
                    if (code != null && eid != null) dict[code] = BttvEmoteUrl(eid);
                }
            }
            catch { }
        }

        // BTTV 表情图片地址：v3 API 以表情 id 拼 CDN 路径（/1x 对动画表情自动返回 GIF）。
        internal static string BttvEmoteUrl(string emoteId)
        {
            return "https://cdn.betterttv.net/emote/" + emoteId + "/1x";
        }

        private static async Task AddFfz(HttpClient http, string id, Dictionary<string, string> dict)
        {
            try
            {
                var g = await http.GetStringAsync("https://api.frankerfacez.com/v1/set/global");
                MergeFfz(JObject.Parse(g), dict);
            }
            catch { }
            try
            {
                var c = await http.GetStringAsync("https://api.frankerfacez.com/v1/room/id/" + id);
                MergeFfz(JObject.Parse(c), dict);
            }
            catch { }
        }

        private static void MergeFfz(JObject o, Dictionary<string, string> dict)
        {
            var sets = o["sets"] as JObject;
            if (sets == null) return;
            foreach (var set in sets.Properties())
            {
                var emoticons = set.Value["emoticons"] as JArray;
                if (emoticons == null) continue;
                foreach (var em in emoticons)
                {
                    var name = (string)em["name"];
                    var urls = em["urls"] as JObject;
                    if (name == null || urls == null) continue;
                    var url = (string)urls["4"] ?? (string)urls["2"] ?? (string)urls["1"];
                    if (url != null) dict[name] = url;
                }
            }
        }

        private static async Task Add7Tv(HttpClient http, string id, Dictionary<string, string> dict)
        {
            try
            {
                var g = await http.GetStringAsync("https://7tv.io/v3/emote-sets/global");
                Merge7Tv(JObject.Parse(g), dict);
            }
            catch { }
            try
            {
                var c = await http.GetStringAsync("https://7tv.io/v3/users/twitch/" + id);
                var set = JObject.Parse(c)["emote_set"] as JObject;
                if (set != null) Merge7Tv(set, dict);
            }
            catch { }
        }

        private static void Merge7Tv(JObject set, Dictionary<string, string> dict)
        {
            var emotes = set["emotes"] as JArray;
            if (emotes == null) return;
            foreach (var e in emotes)
            {
                var name = (string)e["name"];
                var host = e["data"]?["host"] as JObject;
                var files = host?["files"] as JArray;
                var baseUrl = (string)host?["url"];
                if (name == null || files == null || files.Count == 0 || baseUrl == null) continue;
                // 优先选 1x 的 GIF：WPF 原生可渲染且能动画；WebP/AVIF 无原生解码器，故不选用。
                // 7tv 的 host.url 为协议相对地址（//cdn.7tv.app/...），补齐 https: 才能被 Uri 解析。
                string fileName = null;
                foreach (var f in files)
                {
                    var fn = (string)f["name"];
                    if (fn != null && fn.EndsWith("1x.gif", StringComparison.OrdinalIgnoreCase)) { fileName = fn; break; }
                }
                if (fileName == null)
                    foreach (var f in files)
                    {
                        var fn = (string)f["name"];
                        if (fn != null && fn.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)) { fileName = fn; break; }
                    }
                if (fileName == null) continue; // WebP/AVIF：无原生解码，留空使该表情回退为文字
                dict[name] = SevenTvEmoteUrl(baseUrl, fileName);
            }
        }

        // 7TV 表情图片地址：host.url 为协议相对地址（//cdn.7tv.app/...），补齐 https: 后拼文件名。
        internal static string SevenTvEmoteUrl(string hostUrl, string fileName)
        {
            return "https:" + hostUrl.TrimEnd('/') + "/" + fileName;
        }
    }
}
