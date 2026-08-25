using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Threading.Tasks;
using System.Security.Cryptography;

namespace RestreamChatPlugin
{
    // 插件自带的独立弹幕浮层：透明、置顶、鼠标穿透，用于正确渲染
    // Unicode emoji（😀 等）与 Twitch/YouTube 平台表情包（emote 以图片内联显示）。
    // 纯代码构建（不依赖 XAML 编译），确保在类库工程里也能通过编译。
    // 替换规则：消息按空白分词，命中 emote 名称则内联图片，否则按文字渲染；
    // emoji 与平台表情包均以图片内联渲染（各自按需从 CDN 下载并缓存到本地，离线可复用），
    // 图片不可用时分别回退 Segoe UI Emoji 字体 / 文字。
    // 布局（OverlayMode）：
    //   scroll  = 弹幕滚动模式，消息从右向左飞过屏幕（默认）。
    //   sidebar = 侧边栏列表模式，消息在屏幕一侧（左/右）从下往上堆叠，像聊天记录。
    public class RestreamOverlayWindow : Window
    {
        private readonly Dictionary<string, string> _emotes;
        private Canvas _scrollRoot;
        private StackPanel _sidebarRoot;
        private readonly List<double> _tracks = new List<double>();
        private int _trackIndex;
        private readonly int _maxSidebarItems = 50;
        // 侧边栏每条消息存活时长（秒）：到期淡出移除，作为实时提醒而非永久聊天记录。
        private const int SidebarItemLifetimeSeconds = 8;

        public new double FontSize { get; set; } = 22;
        public double BackgroundOpacity { get; set; } = 0.55;
        public string Mode { get; set; } = "scroll";   // scroll / sidebar
        public string Side { get; set; } = "right";     // left / right（仅 sidebar 模式生效）

        public RestreamOverlayWindow(Dictionary<string, string> emotes, string mode = "scroll", string side = "right")
        {
            _emotes = emotes ?? new Dictionary<string, string>();
            Mode = mode;
            Side = side;

            // 宿主弹幕姬可能在应用级把 TextFormattingMode 设为 Display（中文更清晰），
            // 该属性会沿可视化树继承到本浮层，导致 Segoe UI Emoji 被渲染成单色符号。
            // 显式覆盖为 Ideal，恢复彩色 emoji 字体（COLR/CPAL）合成。
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
            // 文字渲染模式必须为非 Aliased（Auto 在液晶屏走 ClearType、其余走 Grayscale），
            // 否则彩色 emoji 会被降级为单色符号。透明浮层易从宿主继承 Aliased，故显式钉死。
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.Auto);

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;

            if (Mode == "sidebar")
            {
                _sidebarRoot = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    IsHitTestVisible = false
                };
                Content = _sidebarRoot;
            }
            else
            {
                _scrollRoot = new Canvas { IsHitTestVisible = false, Background = Brushes.Transparent };
                Content = _scrollRoot;
            }

            Loaded += (s, e) =>
            {
                PositionToScreen();
                if (Mode != "sidebar") BuildTracks();
            };

            // 让本窗口始终位于所有窗口之上，包括其它同样置顶（Topmost）的应用
            // （如全屏播放器）：WPF 的 Topmost=true 只能压过非置顶窗口，且当其它窗口
            // 通过置顶技术抢占 z 序时，本窗口会被挤到下方。采用与弹幕姬自带浮层一致的做法：
            // ①扩展样式加 WS_EX_TRANSPARENT|WS_EX_TOOLWINDOW；②失焦（Deactivated）时重新置顶；
            // ③每秒用定时器重新置顶一次，强制回到置顶 z 序顶端。
            SourceInitialized += (s, e) =>
            {
                var hWnd = new WindowInteropHelper(this).Handle;
                var ex = GetWindowLong(hWnd, GWL_EXSTYLE);
                SetWindowLong(hWnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW);
            };

            Deactivated += (s, e) => { Topmost = true; };

            _topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _topmostTimer.Tick += TopmostKeepAlive_Tick;
            Loaded += (s, e) => _topmostTimer.Start();
            Closed += (s, e) => _topmostTimer.Stop();
        }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

        private readonly DispatcherTimer _topmostTimer;
        private int _topmostKeepAliveTicks;

        // 每秒重新置顶一次，抵御其它置顶窗口（如全屏播放器）抢占 z 序：
        // 置顶的本质是把本窗口移到置顶链顶端，后调用者胜出；本定时器在其它窗口
        // 抢占后再次置顶，使本浮层持续位于最上层。
        private void TopmostKeepAlive_Tick(object sender, EventArgs e)
        {
            var hWnd = new WindowInteropHelper(this).Handle;
            var wasTopmost = (GetWindowLong(hWnd, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;
            if (!wasTopmost)
            {
                var fg = GetForegroundWindow();
                var sb = new System.Text.StringBuilder(256);
                GetWindowText(fg, sb, sb.Capacity);
                RestreamPlugin.Trace("置顶保活：本窗口丢失 WS_EX_TOPMOST，准备重新置顶；前台窗口=" + sb);
            }
            Topmost = false;
            Topmost = true;
            _topmostKeepAliveTicks++;
            if (_topmostKeepAliveTicks % 30 == 0)
                RestreamPlugin.Trace("置顶保活心跳：WS_EX_TOPMOST=是（每隔约30秒）");
        }

        private void PositionToScreen()
        {
            var wa = SystemParameters.WorkArea;
            if (wa == null) return;
            Left = wa.Left;
            Top = wa.Top;
            Height = wa.Height;

            if (Mode == "sidebar")
            {
                // 侧边栏仅占一侧窄带（屏幕宽度的 28%，最小 280、最大 480）。
                var w = Math.Min(480, Math.Max(280, wa.Width * 0.28));
                Width = w;
                if (_sidebarRoot != null)
                    _sidebarRoot.HorizontalAlignment = Side == "left" ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                Left = Side == "left" ? wa.Left : wa.Left + wa.Width - w;
            }
            else
            {
                Width = wa.Width;
                if (_scrollRoot != null)
                {
                    _scrollRoot.Width = wa.Width;
                    _scrollRoot.Height = wa.Height;
                }
            }
        }

        private void BuildTracks()
        {
            _tracks.Clear();
            var h = SystemParameters.WorkArea.Height;
            var trackHeight = FontSize + 12;
            var count = Math.Max(4, (int)(h / (trackHeight + 6)));
            for (var i = 0; i < count; i++)
                _tracks.Add(i * (trackHeight + 6) + 6);
        }

        public void ShowDanmaku(string platform, string user, string text, List<EmoteRange> emotes)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(new Action(() => ShowDanmaku(platform, user, text, emotes)));
                return;
            }

            var tb = BuildTextBlock(platform, user, text, emotes);
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(
                    (byte)(BackgroundOpacity * 255), 40, 40, 40)),
                CornerRadius = new CornerRadius(4),
                Child = tb,
                Margin = new Thickness(0, 0, 0, 4),
                IsHitTestVisible = false
            };

            if (Mode == "sidebar" && _sidebarRoot != null)
            {
                _sidebarRoot.Children.Add(border);
                // 超出容量则丢弃最旧的一条，保持列表长度可控。
                while (_sidebarRoot.Children.Count > _maxSidebarItems)
                    _sidebarRoot.Children.RemoveAt(0);
                // 淡入 + 轻微上移，观感更顺滑。
                border.Opacity = 0;
                var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180));
                var slide = new ThicknessAnimation(
                    new Thickness(0, 8, 0, 4), new Thickness(0, 0, 0, 4), TimeSpan.FromMilliseconds(180));
                border.BeginAnimation(UIElement.OpacityProperty, fade);
                border.BeginAnimation(FrameworkElement.MarginProperty, slide);
                // 存活数秒后淡出移除：侧边栏定位为实时提醒，消息不应永久堆积。
                var life = new DispatcherTimer { Interval = TimeSpan.FromSeconds(SidebarItemLifetimeSeconds) };
                life.Tick += (s, e) =>
                {
                    life.Stop();
                    var outFade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(400));
                    outFade.Completed += (ss, ee) =>
                    {
                        if (border.Parent is StackPanel parent) parent.Children.Remove(border);
                    };
                    border.BeginAnimation(UIElement.OpacityProperty, outFade);
                };
                life.Start();
            }
            else if (_scrollRoot != null)
            {
                _scrollRoot.Children.Add(border);
                border.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var itemWidth = border.DesiredSize.Width;

                var trackY = _tracks.Count > 0 ? _tracks[_trackIndex] : 6;
                _trackIndex = _tracks.Count > 0 ? (_trackIndex + 1) % _tracks.Count : 0;
                Canvas.SetTop(border, trackY);

                var startX = SystemParameters.WorkArea.Width;
                Canvas.SetLeft(border, startX);

                var seconds = Math.Max(6, itemWidth / 120.0);
                var anim = new DoubleAnimation(startX, -itemWidth, TimeSpan.FromSeconds(seconds));
                anim.Completed += (s, e) => _scrollRoot.Children.Remove(border);
                border.BeginAnimation(Canvas.LeftProperty, anim);
            }
        }

        private TextBlock BuildTextBlock(string platform, string user, string text, List<EmoteRange> emotes)
        {
            var tb = new TextBlock
            {
                FontSize = FontSize,
                FontFamily = new FontFamily("Segoe UI Emoji, Microsoft YaHei, SimSun"),
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(8, 2, 8, 2),
                IsHitTestVisible = false
            };
            // 确保本条消息以 Ideal 模式排版，彩色 emoji 不被降级为单色符号。
            TextOptions.SetTextFormattingMode(tb, TextFormattingMode.Ideal);
            TextOptions.SetTextRenderingMode(tb, TextRenderingMode.Auto);
            tb.Inlines.Add(new Run("[" + platform + "] ") { Foreground = Brushes.LightGreen });
            if (!string.IsNullOrEmpty(user))
                AppendRichText(tb, user + ": ", Brushes.Yellow);
            AppendMessage(tb, text, emotes);
            return tb;
        }

        // 渲染一条消息正文：优先用 Restream 下发的表情范围（replaces，权威且覆盖 Twitch 原生表情）
        // 内联图片；范围未覆盖的纯文本段再做 BTTV/FFZ/7TV 名称匹配作为补充。
        // 任一表情图片加载失败时回退为文字，保证消息不丢。
        private void AppendMessage(TextBlock tb, string text, List<EmoteRange> emotes)
        {
            if (emotes != null && emotes.Count > 0)
            {
                var ranges = emotes.OrderBy(r => r.Start).ToList();
                var cursor = 0;
                foreach (var r in ranges)
                {
                    if (r.Start > cursor)
                        AppendTextSegments(tb, text.Substring(cursor, r.Start - cursor));
                    if (r.Start <= r.End && r.End < text.Length)
                    {
                        // 优先用 Restream 下发的表情范围（replaces）内联图片；加载失败回退为文字。
                        tb.Inlines.Add(MakeEmoteInline(text.Substring(r.Start, r.End - r.Start + 1), r.Url));
                        cursor = r.End + 1;
                    }
                }
                if (cursor < text.Length)
                    AppendTextSegments(tb, text.Substring(cursor));
                return;
            }
            AppendTextSegments(tb, text);
        }

        // 对纯文本段按空白分词，命中 _emotes 名称则内联表情图片，否则按富文本渲染
        //（emoji 片段走 Segoe UI Emoji 彩色字体，普通文字走默认字体）。
        private void AppendTextSegments(TextBlock tb, string seg)
        {
            if (string.IsNullOrEmpty(seg)) return;
            var tokens = seg.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.None);
            for (var i = 0; i < tokens.Length; i++)
            {
                var token = tokens[i];
                if (_emotes.TryGetValue(token, out var url))
                    tb.Inlines.Add(MakeEmoteInline(token, url));
                else
                    AppendRichText(tb, token);
                if (i < tokens.Length - 1) tb.Inlines.Add(new Run(" "));
            }
        }

        // 把一段文本写入 TextBlock：自动区分 emoji 片段与普通文字。
        // emoji 片段交由 MakeEmojiInline 以 Twemoji 图片渲染（带本地缓存），
        // 图片不可用时回退字体；普通文字沿用默认字体。
        // foreground 不为空时两类都套用该颜色（用于平台/用户名前缀）。
        private void AppendRichText(TextBlock tb, string text, Brush foreground = null)
        {
            if (string.IsNullOrEmpty(text)) return;
            var i = 0;
            while (i < text.Length)
            {
                if (IsEmojiStart(text[i]))
                {
                    var j = EmojiTokenEnd(text, i);
                    tb.Inlines.Add(MakeEmojiInline(text.Substring(i, j - i)));
                    i = j;
                }
                else
                {
                    var j = i;
                    while (j < text.Length && !IsEmojiStart(text[j])) j++;
                    var run = new Run(text.Substring(i, j - i));
                    if (foreground != null) run.Foreground = foreground;
                    tb.Inlines.Add(run);
                    i = j;
                }
            }
        }

        // 是否为 emoji 起始字符：代理对（SMP，覆盖绝大多数彩色 emoji）或常见 BMP emoji 区块。
        private static bool IsEmojiStart(char c)
        {
            if (char.IsHighSurrogate(c)) return true; // 0x1F000+ 彩色 emoji
            var cp = (int)c;
            return (cp >= 0x2600 && cp <= 0x26FF)   // 杂项符号（☀♥★…）
                || (cp >= 0x2700 && cp <= 0x27BF)   // 装饰符号（✅❌⭐…）
                || (cp >= 0x2B00 && cp <= 0x2BFF)   // 杂项符号与箭头（⚠➡➰…）
                || (cp == 0x2764)                    // 实心黑心（默认文本呈现，靠字体强制彩色）
                || (cp == 0x2744)                    // 雪花
                || (cp == 0x2728);                   // 闪光
        }

        // emoji 连续片段的延续字符：ZWJ 连接符、变体选择符、肤色修饰符、键帽组合符。
        private static bool IsEmojiContinue(char c)
        {
            return c == 0x200D                                       // 零宽连接符 ZWJ
                || (c >= 0xFE00 && c <= 0xFE0F)                      // 变体选择符
                || (c >= 0x1F3FB && c <= 0x1F3FF)                    // 肤色修饰符
                || (c == 0x20E3);                                    // 键帽组合符
        }

        // 计算一个 emoji 片段的结束下标：从基础 emoji 起，向后吸收 ZWJ 序列
        // （ZWJ 必须紧跟另一个 emoji/修饰符才延续）与变体选择符、肤色修饰符、键帽组合符；
        // 遇到下一个独立基础 emoji 即结束，保证相邻多个 emoji 各自成图而非拼成无效文件名。
        internal static int EmojiTokenEnd(string s, int i)
        {
            int j = i;
            if (char.IsHighSurrogate(s[j]) && j + 1 < s.Length && char.IsLowSurrogate(s[j + 1])) j += 2;
            else j++;
            while (j < s.Length)
            {
                if (s[j] == 0x200D) // ZWJ：仅当其后仍有 emoji/修饰符时才延续序列
                {
                    var k = j + 1;
                    if (k < s.Length && (IsEmojiStart(s[k]) || IsEmojiContinue(s[k])))
                    {
                        j = k;
                        if (char.IsHighSurrogate(s[j]) && j + 1 < s.Length && char.IsLowSurrogate(s[j + 1])) j += 2;
                        else j++;
                        continue;
                    }
                    break;
                }
                if (IsEmojiContinue(s[j])) { j++; continue; }
                break;
            }
            return j;
        }

        // 构造一个表情内联元素：优先用本地缓存图片（即时、离线可用），
        // 未缓存则从 URL 下载并落盘；下载失败或离线时回退为文字，保证消息不丢。
        // 与 emoji（MakeEmojiInline）同源思路，均按本地持久化目录缓存。
        private Inline MakeEmoteInline(string fallbackText, string url)
        {
            var span = new Span { BaselineAlignment = BaselineAlignment.Center };
            var img = new Image
            {
                Height = FontSize,
                Width = FontSize,
                VerticalAlignment = VerticalAlignment.Center,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(2, 0, 2, 0),
                IsHitTestVisible = false
            };
            span.Inlines.Add(new InlineUIContainer(img) { BaselineAlignment = BaselineAlignment.Center });

            // Twitch 原生动画表情：Restream 下发的地址是 v1 模板静态 PNG，其动画资源在 v2 模板
            // （多帧 GIF）。改写为 v2 动画地址优先下载；非 Twitch 表情或非 v1 模板时回退原地址。
            // 缓存键按有效地址计算，避免旧的静态缓存被误用为动画。
            var effectiveUrl = TwitchAnimatedEmoteUrl(url) ?? url;
            var fallbackUrl = effectiveUrl == url ? null : url;
            var cacheFile = EmoteCacheFile(effectiveUrl);
            var localPath = Path.Combine(EmoteCacheDir, cacheFile);
            if (File.Exists(localPath))
            {
                TrySetEmoteImage(img, localPath, span, fallbackText);
            }
            else
            {
                EnsureEmoteCachedAsync(cacheFile, effectiveUrl, fallbackUrl).ContinueWith(t =>
                {
                    var path = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
                    if (path == null) _emoteDownloads.TryRemove(cacheFile, out _);
                    // 仅当表情图片仍在可视化树中才渲染：若消息已消失（被移除出树），不再设置源，
                    // 避免对其启动 GIF 动画定时器却永远等不到 Unloaded 而泄漏 GDI+ 资源与定时器。
                    if (img.IsLoaded)
                    {
                        try { img.Dispatcher.Invoke(() => TrySetEmoteImage(img, path, span, fallbackText)); }
                        catch { }
                    }
                });
            }
            return span;
        }

        // Twitch 原生动画表情地址改写：将 v1 模板的静态 PNG（/emoticons/v1/{id}/{scale}）改写为
        // v2 模板的动画 GIF（/emoticons/v2/{id}/animated/dark/3.0）。非 Twitch 静态 CDN 或非 v1
        // 模板（如 BTTV/7TV 已是 GIF、或旧版数字 id）返回 null，保持原地址不变。
        internal static string TwitchAnimatedEmoteUrl(string url)
        {
            try
            {
                var u = new Uri(url);
                if (!u.Host.Equals("static-cdn.jtvnw.net", StringComparison.OrdinalIgnoreCase)) return null;
                var seg = u.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (seg.Length != 4 || seg[0] != "emoticons" || seg[1] != "v1") return null;
                var id = seg[2];
                if (string.IsNullOrEmpty(id)) return null;
                // 仅 emotesv2_ 前缀的现代表情具备 v2 动画资源；其余（如旧版数字 id）无 v2 资源，
                // 改写后必 404，直接保持原 v1 静态地址（由上层回退），避免无谓的网络请求。
                if (!id.StartsWith("emotesv2_", StringComparison.OrdinalIgnoreCase)) return null;
                RestreamPlugin.Trace("Twitch 原生动画表情改写：" + url + " -> v2/animated");
                return "https://static-cdn.jtvnw.net/emoticons/v2/" + id + "/animated/dark/3.0";
            }
            catch { return null; }
        }

        // 表情包缓存文件名：以 URL 的 SHA-1 作键（避免远程路径中的特殊字符污染本地文件名），
        // 扩展名沿用原 URL 末段以保留图片格式（PNG/WebP 等）。
        internal static string EmoteCacheFile(string url)
        {
            using (var sha = SHA1.Create())
            {
                var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(url));
                var hex = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                var ext = Path.GetExtension(new Uri(url).AbsolutePath);
                if (string.IsNullOrEmpty(ext) || ext.Length > 5) ext = ".png";
                return hex + ext;
            }
        }

        // 确保某表情包图片已下载到本地缓存：优先下载有效地址，失败时回退原地址（静态 PNG），
        // 落盘后返回本地路径，全部失败返回 null。并发的同名请求共享同一任务，避免重复下载。
        private static Task<string> EnsureEmoteCachedAsync(string cacheFile, string effectiveUrl, string fallbackUrl)
        {
            return _emoteDownloads.GetOrAdd(cacheFile, key => Task.Run(async () =>
            {
                var path = await DownloadEmoteToFile(effectiveUrl, cacheFile);
                if (path == null && fallbackUrl != null) path = await DownloadEmoteToFile(fallbackUrl, cacheFile);
                return path;
            }));
        }

        // 将单个表情地址下载并落盘到本地缓存：成功返回本地路径，失败（网络/404/非图片）返回 null。
        private static async Task<string> DownloadEmoteToFile(string url, string cacheFile)
        {
            try
            {
                Directory.CreateDirectory(EmoteCacheDir);
                using (var http = NewImageHttpClient())
                {
                    var bytes = await http.GetByteArrayAsync(url).ConfigureAwait(false);
                    if (bytes != null && bytes.Length > 0)
                    {
                        var path = Path.Combine(EmoteCacheDir, cacheFile);
                        var tmp = path + ".tmp";
                        File.WriteAllBytes(tmp, bytes);
                        if (File.Exists(path)) File.Delete(path);
                        File.Move(tmp, path);
                        RestreamPlugin.Trace("表情包已缓存：" + cacheFile + " <- " + url);
                        return path;
                    }
                }
            }
            catch (Exception ex)
            {
                RestreamPlugin.Trace("表情包下载失败：" + url + " -> " + ex.Message);
            }
            return null;
        }

        // emoji 渲染为 Twemoji 高清图片：多 CDN 镜像回退（国内 jsDelivr 常被墙，
        // 依次尝试 cdn/fastly/gcore 镜像），任一镜像成功即用彩色图片，全部失败才回退字体。
        // 与平台表情包（MakeEmoteInline）同源思路，彻底规避彩色字体被降级为单色符号。
        private static readonly string[] EmojiCdnBases = new[]
        {
            "https://cdn.jsdelivr.net/gh/twitter/twemoji@14.0.2/assets/72x72/",
            "https://fastly.jsdelivr.net/gh/twitter/twemoji@14.0.2/assets/72x72/",
            "https://gcore.jsdelivr.net/gh/twitter/twemoji@14.0.2/assets/72x72/"
        };

        // emoji 本地缓存目录：首次下载后落盘，后续直接从本地加载（即时且离线可用，
        // 与平台表情包同源思路）。每个 emoji 仅需下载一次。
        private static string EmojiCacheDir => Path.Combine(Config.PluginRoot, "emoji");

        // 构建图片下载用的 HTTP 客户端，代理设置跟随插件配置（默认直连）。
        // emoji / 表情包下载后落盘缓存，仅首次需要联网，按次创建客户端的开销可忽略。
        private static HttpClient NewImageHttpClient()
        {
            var cfg = Config.Load();
            var handler = new HttpClientHandler();
            Config.ApplyHttpClientProxy(handler, cfg.ProxyMode, cfg.ProxyUrl);
            return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        }

        // 表情包本地缓存目录：与 emoji 同源思路，按 URL 哈希落盘，离线可复用。
        private static string EmoteCacheDir => Path.Combine(Config.PluginRoot, "emotes");

        // 进行中的下载任务：相同键（emoji 码点 / 表情包 URL 哈希）并发请求只下载一次，
        // 避免重复拉取与文件争用。
        private static readonly ConcurrentDictionary<string, Task<string>> _emojiDownloads =
            new ConcurrentDictionary<string, Task<string>>();
        private static readonly ConcurrentDictionary<string, Task<string>> _emoteDownloads =
            new ConcurrentDictionary<string, Task<string>>();

        // 构造一个 emoji 内联元素：优先使用本地缓存图片（即时、离线可用），
        // 未缓存则从 CDN 镜像下载并落盘；下载失败或离线时回退 Segoe UI Emoji 字体。
        private Inline MakeEmojiInline(string emojiText)
        {
            var span = new Span { BaselineAlignment = BaselineAlignment.Center };
            var codepoint = EmojiCodepoints(emojiText, keepVariation: false, keepZwj: true);
            if (codepoint == null)
            {
                span.Inlines.Add(MakeEmojiFallbackRun(emojiText));
                return span;
            }
            var cacheFile = codepoint + ".png";
            var localPath = Path.Combine(EmojiCacheDir, cacheFile);
            var img = new Image
            {
                Height = FontSize,
                Width = FontSize,
                VerticalAlignment = VerticalAlignment.Center,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(2, 0, 2, 0),
                IsHitTestVisible = false
            };
            span.Inlines.Add(new InlineUIContainer(img) { BaselineAlignment = BaselineAlignment.Center });
            if (File.Exists(localPath))
            {
                // 本地缓存命中：直接加载，无需联网。
                TrySetEmojiImage(img, localPath, span, emojiText);
            }
            else
            {
                // 未缓存：后台下载首个可用镜像并落盘，完成后切回 UI 线程加载图片；
                // 全部镜像失败则回退字体。
                var urls = EmojiCdnUrls(emojiText).ToArray();
                EnsureEmojiCachedAsync(cacheFile, urls).ContinueWith(t =>
                {
                    var path = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
                    try { img.Dispatcher.Invoke(() => TrySetEmojiImage(img, path, span, emojiText)); }
                    catch { }
                    if (path == null) _emojiDownloads.TryRemove(cacheFile, out _);
                });
            }
            return span;
        }

        // 确保某 emoji 的 PNG 已下载到本地缓存：逐镜像尝试，首个成功即落盘并返回本地路径，
        // 全部失败返回 null。并发的同名请求共享同一任务，避免重复下载。
        private static Task<string> EnsureEmojiCachedAsync(string cacheFile, string[] urls)
        {
            return _emojiDownloads.GetOrAdd(cacheFile, key => Task.Run(async () =>
            {
                try
                {
                    Directory.CreateDirectory(EmojiCacheDir);
                    using (var http = NewImageHttpClient())
                    {
                        foreach (var url in urls)
                        {
                            try
                            {
                                var bytes = await http.GetByteArrayAsync(url).ConfigureAwait(false);
                                if (bytes != null && bytes.Length > 0)
                                {
                                    var path = Path.Combine(EmojiCacheDir, cacheFile);
                                    // 先写临时文件再原子替换，避免并发读到一个半截文件。
                                    var tmp = path + ".tmp";
                                    File.WriteAllBytes(tmp, bytes);
                                    if (File.Exists(path)) File.Delete(path);
                                    File.Move(tmp, path);
                                    RestreamPlugin.Trace("emoji 已缓存：" + cacheFile);
                                    return path;
                                }
                            }
                            catch (Exception ex)
                            {
                                RestreamPlugin.Trace("emoji 下载失败（尝试下一镜像）：" + url + " -> " + ex.Message);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    RestreamPlugin.Trace("emoji 缓存写入异常：" + cacheFile + " -> " + ex.Message);
                }
                return null;
            }));
        }

        // 设置图片源：入参为本地文件路径或 file:// URI 字符串（二者皆可），统一转回本地路径后
        // 按内容解码（扩展名不影响识别）。动画 GIF 走 GDI+ 逐帧绘制（StartGifAnimation），
        // 其余格式走静态 BitmapImage。WPF 的 Image 默认只显示 GIF 首帧、不会动，故需手动驱动。
        private static void SetImageSource(Image img, string path)
        {
            var local = ToLocalPath(path);
            if (IsGifFile(local) && StartGifAnimation(img, local)) return;
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.UriSource = new Uri(local);
            bi.EndInit();
            img.Source = bi;
        }

        // 把本地文件路径或 file:// URI 字符串统一转回本地文件路径。
        // GDI+ 的 Image.FromFile 只接受普通路径、不接受 file:// URI，故此处归一成路径。
        internal static string ToLocalPath(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            if (s.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                try { return new Uri(s).LocalPath; } catch { }
            }
            return s;
        }

        // 是否 GIF 文件：读前 3 字节「GIF」魔数判定（与扩展名无关，BTTV 等无扩展名 URL 也能识别）。
        // 入参为本地路径或 file:// URI 字符串，转成本地路径后再读。
        internal static bool IsGifFile(string path)
        {
            try
            {
                var local = ToLocalPath(path);
                using (var fs = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var head = new byte[3];
                    return fs.Read(head, 0, 3) == 3 && head[0] == 'G' && head[1] == 'I' && head[2] == 'F';
                }
            }
            catch { return false; }
        }

        // 播放动画 GIF：用 GDI+（System.Drawing.Image）解码并逐帧绘制到 WriteableBitmap。
        // GIF 多帧时 GifBitmapDecoder 配合 BitmapCacheOption.None 二次取帧会抛异常，使定时器首帧后即停、表现为静态，
        // 故此处用 GDI+ 逐帧 SelectActiveFrame 后用 Graphics.DrawImage 绘到 32 位 ARGB 位图再拷贝至 WriteableBitmap，
        // 透明背景正确保留。单帧 GIF 作为静态图返回。成功返回 true，解码失败返回 false
        // （交由上层回退静态 BitmapImage 或文字）。图片从可视化树移除（Unloaded）时停止定时器并释放 GDI+ 资源，
        // 避免空转与泄漏。
        private static bool StartGifAnimation(Image img, string path)
        {
            System.Drawing.Image gdi;
            try
            {
                gdi = System.Drawing.Image.FromFile(path);
            }
            catch (Exception ex)
            {
                RestreamPlugin.Trace("GIF 解码失败：" + path + " -> " + ex.Message);
                return false;
            }
            int frameCount;
            try { frameCount = gdi.GetFrameCount(System.Drawing.Imaging.FrameDimension.Time); }
            catch { frameCount = 1; }
            if (frameCount <= 1)
            {
                // 单帧：直接绘成静态图后释放 GDI+ 资源。
                var wb = new WriteableBitmap(Math.Max(1, gdi.Width), Math.Max(1, gdi.Height), 96, 96, PixelFormats.Bgra32, null);
                try { DrawGdiFrameToWriteable(gdi, wb); img.Source = wb; }
                catch (Exception ex) { RestreamPlugin.Trace("GIF 单帧绘制失败：" + path + " -> " + ex.Message); }
                gdi.Dispose();
                RestreamPlugin.Trace("GIF 为单帧（静态）：" + path);
                return true;
            }
            var bitmap = new WriteableBitmap(gdi.Width, gdi.Height, 96, 96, PixelFormats.Bgra32, null);
            img.Source = bitmap;
            var delays = ReadGifFrameDelays(gdi, frameCount);
            var idx = 0;
            DrawGdiFrameToWriteable(gdi, bitmap);
            RestreamPlugin.Trace("GIF 动画开始：" + path + " 帧数=" + frameCount);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delays[0]) };
            timer.Tick += (s, e) =>
            {
                try
                {
                    idx = (idx + 1) % frameCount;
                    gdi.SelectActiveFrame(System.Drawing.Imaging.FrameDimension.Time, idx);
                    DrawGdiFrameToWriteable(gdi, bitmap);
                    timer.Interval = TimeSpan.FromMilliseconds(delays[idx]);
                }
                catch (Exception ex)
                {
                    RestreamPlugin.Trace("GIF 帧切换异常（停止动画）：" + path + " idx=" + idx + " -> " + ex.Message);
                    timer.Stop();
                }
            };
            img.Unloaded += (s, e) =>
            {
                timer.Stop();
                try { gdi.Dispose(); } catch { }
            };
            timer.Start();
            return true;
        }

        // 按 GIF 的帧延时（PropertyItem 0x5100，单位 1/100 秒）构建每帧间隔；读取失败或缺失时回退 100ms。
        private static int[] ReadGifFrameDelays(System.Drawing.Image gdi, int frameCount)
        {
            var delays = new int[frameCount];
            for (var i = 0; i < frameCount; i++) delays[i] = 100;
            try
            {
                var pi = gdi.GetPropertyItem(0x5100);
                if (pi != null && pi.Type == 4 && pi.Value != null)
                {
                    var n = pi.Value.Length / 4;
                    for (var i = 0; i < n && i < frameCount; i++)
                        delays[i] = Math.Max(10, BitConverter.ToInt32(pi.Value, i * 4) * 10);
                }
            }
            catch { }
            return delays;
        }

        // 把 GDI+ 图像当前激活帧绘制到 32 位 ARGB 位图，再拷贝像素到 WriteableBitmap（Bgra32）。
        // 先清为透明再用 DrawImage 绘制，使 GIF 的透明色索引正确保留为透明像素。
        private static void DrawGdiFrameToWriteable(System.Drawing.Image gdi, WriteableBitmap wb)
        {
            using (var bmp = new System.Drawing.Bitmap(gdi.Width, gdi.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.Clear(System.Drawing.Color.Transparent);
                g.DrawImage(gdi, 0, 0, gdi.Width, gdi.Height);
                var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
                var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, bmp.PixelFormat);
                try
                {
                    wb.WritePixels(new Int32Rect(0, 0, bmp.Width, bmp.Height), data.Scan0, data.Stride * bmp.Height, data.Stride);
                }
                finally { bmp.UnlockBits(data); }
            }
        }

        // 表情包图片：缓存命中或下载完成后调用；加载失败（文件损坏/格式不支持）或无可用
        // 图片时回退为文字，保证消息不丢。
        private void TrySetEmoteImage(Image img, string path, Span span, string fallbackText)
        {
            try
            {
                if (path == null) throw new InvalidOperationException("无可用图片");
                SetImageSource(img, path);
            }
            catch
            {
                try { span.Inlines.Clear(); span.Inlines.Add(new Run(fallbackText)); }
                catch { }
            }
        }

        // emoji 图片：缓存命中或下载完成后调用；加载失败时回退 Segoe UI Emoji 字体。
        private void TrySetEmojiImage(Image img, string path, Span span, string emojiText)
        {
            try
            {
                if (path == null) throw new InvalidOperationException("无可用图片");
                SetImageSource(img, path);
            }
            catch
            {
                try { span.Inlines.Clear(); span.Inlines.Add(MakeEmojiFallbackRun(emojiText)); }
                catch { }
            }
        }

        // emoji 字体回退（图片全失败时）：强制 Ideal 模式以尽量保留彩色字形。
        private static Run MakeEmojiFallbackRun(string emojiText)
        {
            var run = new Run(emojiText) { FontFamily = new FontFamily("Segoe UI Emoji") };
            TextOptions.SetTextFormattingMode(run, TextFormattingMode.Ideal);
            TextOptions.SetTextRenderingMode(run, TextRenderingMode.Auto);
            return run;
        }

        // 生成某 emoji 的全部候选图片 URL：对每个 CDN 镜像，先尝试完整码点串（保留变体选择符/ZWJ，
        // 如家庭/彩虹旗），失败则尝试精简码点串（去掉 FE0F/ZWJ，如红心 ❤️ -> 2764）。
        // 顺序：mirror1 完整、mirror1 精简、mirror2 完整、mirror2 精简 … 以最大化可达性。
        internal static IEnumerable<string> EmojiCdnUrls(string emojiText)
        {
            var full = EmojiCodepoints(emojiText, keepVariation: true, keepZwj: true);
            var stripped = EmojiCodepoints(emojiText, keepVariation: false, keepZwj: false);
            foreach (var baseUrl in EmojiCdnBases)
            {
                if (full != null) yield return baseUrl + full + ".png";
                if (stripped != null && stripped != full) yield return baseUrl + stripped + ".png";
            }
        }

        // 把 emoji 字符串转成 Twemoji 文件名用的连字符码点串。
        // keepVariation=false 时去掉变体选择符（U+FE0F）；keepZwj=false 时去掉零宽连接符（U+200D）。
        // 例如 ❤️(U+2764 U+FE0F) -> 去变体后为 "2764"；👨‍👩‍👦 -> 保留 ZWJ 为 "1f468-200d-1f469-200d-1f467"。
        internal static string EmojiCodepoints(string s, bool keepVariation, bool keepZwj)
        {
            if (string.IsNullOrEmpty(s)) return null;
            var parts = new List<string>();
            var i = 0;
            while (i < s.Length)
            {
                int cp;
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    cp = char.ConvertToUtf32(s[i], s[i + 1]);
                    i += 2;
                }
                else
                {
                    cp = s[i];
                    i += 1;
                }
                if (cp == 0xFE0F && !keepVariation) continue;
                if (cp == 0x200D && !keepZwj) continue;
                parts.Add(cp.ToString("x"));
            }
            return parts.Count == 0 ? null : string.Join("-", parts);
        }
    }
}
