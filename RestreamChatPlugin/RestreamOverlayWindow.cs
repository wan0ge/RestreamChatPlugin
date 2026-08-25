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

            var cacheFile = EmoteCacheFile(url);
            var localPath = Path.Combine(EmoteCacheDir, cacheFile);
            if (File.Exists(localPath))
            {
                TrySetEmoteImage(img, localPath, span, fallbackText);
            }
            else
            {
                EnsureEmoteCachedAsync(cacheFile, url).ContinueWith(t =>
                {
                    var path = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
                    try { img.Dispatcher.Invoke(() => TrySetEmoteImage(img, path, span, fallbackText)); }
                    catch { }
                    if (path == null) _emoteDownloads.TryRemove(cacheFile, out _);
                });
            }
            return span;
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

        // 确保某表情包图片已下载到本地缓存：下载首个可用结果并落盘后返回本地路径，失败返回 null。
        // 并发的同名请求共享同一任务，避免重复下载。
        private static Task<string> EnsureEmoteCachedAsync(string cacheFile, string url)
        {
            return _emoteDownloads.GetOrAdd(cacheFile, key => Task.Run(async () =>
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
                            RestreamPlugin.Trace("表情包已缓存：" + cacheFile);
                            return path;
                        }
                    }
                }
                catch (Exception ex)
                {
                    RestreamPlugin.Trace("表情包下载失败：" + url + " -> " + ex.Message);
                }
                return null;
            }));
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

        // 设置图片源（按内容解码，扩展名不影响识别）；源无效时抛异常，由上层决定回退。
        private static void SetImageSource(Image img, string url)
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.UriSource = new Uri(url);
            bi.EndInit();
            img.Source = bi;
        }

        // 表情包图片：缓存命中或下载完成后调用；加载失败（文件损坏/格式不支持）或无可用
        // 图片时回退为文字，保证消息不丢。
        private void TrySetEmoteImage(Image img, string path, Span span, string fallbackText)
        {
            try
            {
                if (path == null) throw new InvalidOperationException("无可用图片");
                SetImageSource(img, new Uri(path).AbsoluteUri);
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
                SetImageSource(img, new Uri(path).AbsoluteUri);
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
