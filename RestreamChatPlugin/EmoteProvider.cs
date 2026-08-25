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
                    var raw = (string)e["images"]?["url"];
                    if (raw != null) dict[(string)e["code"]] = NormalizeBttv(raw);
                }
            }
            catch { }
            try
            {
                var c = await http.GetStringAsync("https://api.betterttv.net/3/cached/users/twitch/" + id);
                var o = JObject.Parse(c);
                foreach (var e in o["channelEmotes"] ?? new JArray())
                {
                    var raw = (string)e["images"]?["url"];
                    if (raw != null) dict[(string)e["code"]] = NormalizeBttv(raw);
                }
                foreach (var e in o["sharedEmotes"] ?? new JArray())
                {
                    var raw = (string)e["images"]?["url"];
                    if (raw != null) dict[(string)e["code"]] = NormalizeBttv(raw);
                }
            }
            catch { }
        }

        private static string NormalizeBttv(string raw)
        {
            return raw.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? raw : "https://cdn.betterttv.net" + raw;
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
                var g = await http.GetStringAsync("https://7tv.io/v3/emote-set/global");
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
                var file = files[0];
                var fileName = (string)file["name"];
                if (fileName == null) continue;
                dict[name] = baseUrl.TrimEnd('/') + "/" + fileName;
            }
        }
    }
}
