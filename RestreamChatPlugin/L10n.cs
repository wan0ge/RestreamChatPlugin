using System.Globalization;

namespace RestreamChatPlugin
{
    // 本地化：跟随弹幕姬的语言设置（中文 / 日本語 / English）。
    // 弹幕姬在启动时把同一进程的 CultureInfo.CurrentUICulture 设为 Settings.Default.lang
    // （zh / ja-JP / en-US），插件与宿主同进程，据此选取文案，无需框架单独提供语言 API。
    internal static class L10n
    {
        private enum Lang { Zh, Ja, En }

        // 当前语言：读取进程级 UI 文化（由弹幕姬在启动时设定）。
        // 语言切换需重启弹幕姬生效，与弹幕姬自身行为一致；插件在加载与每次打开设置窗口时重新读取，
        // 因此重启后即可反映新语言。
        private static Lang Current
        {
            get
            {
                var name = (CultureInfo.CurrentUICulture?.Name ?? "zh").ToLowerInvariant();
                if (name.StartsWith("ja")) return Lang.Ja;
                if (name.StartsWith("en")) return Lang.En;
                return Lang.Zh;
            }
        }

        // 按当前语言返回对应文案：zh 中文、ja 日本語、en English。
        public static string T(string zh, string ja, string en)
        {
            switch (Current)
            {
                case Lang.Ja: return ja;
                case Lang.En: return en;
                default: return zh;
            }
        }
    }
}
