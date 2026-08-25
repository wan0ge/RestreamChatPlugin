using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RestreamChatPlugin.Tests
{
    // 覆盖表情动画地址改写、本地路径归一、token 过期判定与 GIF 魔数识别等纯逻辑（无 WPF/网络依赖，可在 net48 测试主机直接运行）：
    //   TwitchAnimatedEmoteUrl —— Twitch 原生动画表情的 v1 静态地址改写为 v2 动画 GIF 地址；
    //   ToLocalPath            —— file:// URI 归一为本地路径（GDI+ Image.FromFile 只认本地路径）；
    //   IsExpired             —— access token 过期判定（提前 5 分钟缓冲）；
    //   IsGifFile             —— 按 GIF 魔数判定文件是否为 GIF（与扩展名无关）。
    // 这些函数关系到“动态表情能否真正播起来”与“token 续期时机”，属于分支结果的关键路径。
    [TestClass]
    public class EmoteAndGifTests
    {
        // ===== TwitchAnimatedEmoteUrl：v1 静态 -> v2 动画 GIF =====

        [TestMethod]
        public void TwitchAnimatedEmoteUrl_V1Twitch_RewrittenToV2Animated()
        {
            // 常规：Restream 下发的 v1 模板静态 PNG 改写为 v2 动画 GIF 地址。
            var v1 = "https://static-cdn.jtvnw.net/emoticons/v1/emotesv2_5d523adb8bbb4786821cd7091e47da21/3.0";
            var v2 = RestreamOverlayWindow.TwitchAnimatedEmoteUrl(v1);
            Assert.AreEqual("https://static-cdn.jtvnw.net/emoticons/v2/emotesv2_5d523adb8bbb4786821cd7091e47da21/animated/dark/3.0", v2);
        }

        [TestMethod]
        public void TwitchAnimatedEmoteUrl_Bttv_Unchanged_Null()
        {
            // 分支：BTTV 地址已是 GIF（/1x 自动返回 GIF），不应改写，返回 null 保持原地址。
            Assert.IsNull(RestreamOverlayWindow.TwitchAnimatedEmoteUrl("https://cdn.betterttv.net/emote/566ca38765dbbdab32ec0560/1x"));
        }

        [TestMethod]
        public void TwitchAnimatedEmoteUrl_7Tv_Unchanged_Null()
        {
            // 分支：7TV 地址已是 GIF，不应改写。
            Assert.IsNull(RestreamOverlayWindow.TwitchAnimatedEmoteUrl("https://cdn.7tv.app/emote/01FCY771D800007PQ2DF3GDTN6/1x.gif"));
        }

        [TestMethod]
        public void TwitchAnimatedEmoteUrl_NonV1Twitch_Unchanged_Null()
        {
            // 分支：已经是 v2 动画地址（非 v1 模板），无需改写，返回 null 避免重复改写。
            Assert.IsNull(RestreamOverlayWindow.TwitchAnimatedEmoteUrl("https://static-cdn.jtvnw.net/emoticons/v2/emotesv2_abc/animated/dark/3.0"));
        }

        [TestMethod]
        public void TwitchAnimatedEmoteUrl_LegacyNumericId_Unchanged_Null()
        {
            // 分支：旧版数字 id 路径（/emoticons/v1/12345/3.0）段数不符 v2 改写规则，返回 null 保持原样。
            Assert.IsNull(RestreamOverlayWindow.TwitchAnimatedEmoteUrl("https://static-cdn.jtvnw.net/emoticons/v1/12345/3.0"));
        }

        [TestMethod]
        public void TwitchAnimatedEmoteUrl_NonJtvnwHost_Unchanged_Null()
        {
            // 分支：非 Twitch 静态 CDN 的地址（如 FFZ），返回 null 保持原样。
            Assert.IsNull(RestreamOverlayWindow.TwitchAnimatedEmoteUrl("https://cdn.frankerfacez.com/emote/abc/1"));
        }

        [TestMethod]
        public void TwitchAnimatedEmoteUrl_MalformedUrl_SafeNull()
        {
            // 边缘：非法 URL 不抛异常，返回 null（调用方回退原地址）。
            Assert.IsNull(RestreamOverlayWindow.TwitchAnimatedEmoteUrl("not-a-url"));
        }

        // ===== ToLocalPath：file:// URI 归一为本地路径（GDI+ Image.FromFile 只认本地路径） =====

        [TestMethod]
        public void ToLocalPath_FileUri_Converted()
        {
            // 常规：file:///C:/... 还原为 C:\... 本地路径（Windows 本地文件）。
            Assert.AreEqual(@"C:\Users\test\emotes\abc.gif", RestreamOverlayWindow.ToLocalPath("file:///C:/Users/test/emotes/abc.gif"));
        }

        [TestMethod]
        public void ToLocalPath_PlainPath_Unchanged()
        {
            // 常规：本就是本地路径时原样返回（不二次处理）。
            var p = @"C:\Users\test\emotes\abc.gif";
            Assert.AreEqual(p, RestreamOverlayWindow.ToLocalPath(p));
        }

        [TestMethod]
        public void ToLocalPath_Null_Unchanged()
        {
            Assert.IsNull(RestreamOverlayWindow.ToLocalPath(null));
        }

        [TestMethod]
        public void ToLocalPath_Empty_Unchanged()
        {
            Assert.AreEqual("", RestreamOverlayWindow.ToLocalPath(""));
        }

        [TestMethod]
        public void ToLocalPath_UncFileUri_Converted()
        {
            // 边缘：UNC 路径的 file:// 也正确还原为 \\server\share 形式。
            Assert.AreEqual(@"\\server\share\emotes\abc.gif", RestreamOverlayWindow.ToLocalPath("file://server/share/emotes/abc.gif"));
        }

        // ===== IsExpired：提前 5 分钟视为过期（续期缓冲） =====

        [TestMethod]
        public void IsExpired_ExactlyFiveMinutesAhead_Expired()
        {
            // 边界：距到期恰好 5 分钟（300s）即落入缓冲，判定过期（应续期）。
            var cfg = new PluginConfig { AccessTokenExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 300 };
            Assert.IsTrue(RestreamPlugin.IsExpired(cfg));
        }

        [TestMethod]
        public void IsExpired_FiveMinutesOneSecondAhead_NotExpired()
        {
            // 边界：超过 5 分钟缓冲（301s）仍视为有效，不立即续期。
            var cfg = new PluginConfig { AccessTokenExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 301 };
            Assert.IsFalse(RestreamPlugin.IsExpired(cfg));
        }

        [TestMethod]
        public void IsExpired_AlreadyPast_Expired()
        {
            // 常规：已过期（过去时间）判定过期。
            var cfg = new PluginConfig { AccessTokenExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60 };
            Assert.IsTrue(RestreamPlugin.IsExpired(cfg));
        }

        [TestMethod]
        public void IsExpired_ZeroTimestamp_NotExpiredGuard()
        {
            // 边界：0 表示未知/无效，IsExpired 返回 false，交由上层按“缺失 token”处理（走 refresh/提示授权）。
            var cfg = new PluginConfig { AccessTokenExpiresAt = 0 };
            Assert.IsFalse(RestreamPlugin.IsExpired(cfg));
        }

        // ===== IsGifFile：按 GIF 魔数判定（与扩展名无关，BTTV 无扩展名 URL 也能识别） =====

        [TestMethod]
        public void IsGifFile_RealGifHeader_True()
        {
            // 常规：写入 GIF89a 魔数的文件应判定为 GIF。
            var tmp = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(tmp, new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', 0x00 });
                Assert.IsTrue(RestreamOverlayWindow.IsGifFile(tmp));
            }
            finally { File.Delete(tmp); }
        }

        [TestMethod]
        public void IsGifFile_PngHeader_False()
        {
            // 分支：PNG 静态图不应被误判为 GIF（Twitch v1 静态表情即 PNG）。
            var tmp = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(tmp, new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A });
                Assert.IsFalse(RestreamOverlayWindow.IsGifFile(tmp));
            }
            finally { File.Delete(tmp); }
        }

        [TestMethod]
        public void IsGifFile_Nonexistent_False()
        {
            // 边缘：文件不存在不抛异常，返回 false（上层回退静态 BitmapImage 或文字）。
            Assert.IsFalse(RestreamOverlayWindow.IsGifFile(Path.Combine(Path.GetTempPath(), "does_not_exist_" + Guid.NewGuid().ToString("N") + ".gif")));
        }
    }
}
