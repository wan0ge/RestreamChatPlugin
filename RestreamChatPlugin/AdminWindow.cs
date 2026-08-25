using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using System.Threading.Tasks;

namespace RestreamChatPlugin
{
    // 纯代码实现的设置窗口（不依赖 XAML）。内置 Restream OAuth 授权流程。
    // 卡片式现代排版：顶部标题栏 + 分步卡片 + 底部操作栏，圆角/阴影/强调色统一。
    public class AdminWindow : Window
    {
        private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x4F, 0x46, 0xE5));
        private static readonly Brush AccentText = new SolidColorBrush(Color.FromRgb(0x43, 0x35, 0xC9));
        private static readonly Brush TextStrong = new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37));
        private static readonly Brush TextNormal = new SolidColorBrush(Color.FromRgb(0x37, 0x41, 0x51));
        private static readonly Brush TextMuted = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
        private static readonly Brush CardBorder = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB));
        private static readonly Brush CardBg = Brushes.White;
        private static readonly Brush PageBg = new SolidColorBrush(Color.FromRgb(0xF4, 0xF5, 0xF7));

        private readonly RestreamPlugin _plugin;
        private TextBox _clientIdBox;
        private PasswordBox _clientSecretBox;
        private TextBox _proxyBox;
        private RadioButton _proxyNone;
        private RadioButton _proxySystem;
        private RadioButton _proxyCustom;
        private TextBox _manualTokenBox;
        private TextBox _codeBox;
        private CheckBox _overlayChk;
        private CheckBox _debugChk;
        private RadioButton _modeScroll;
        private RadioButton _modeSidebar;
        private RadioButton _sideRight;
        private RadioButton _sideLeft;
        private TextBlock _status;
        private TextBlock _msg;
        private TextBox _redirectBox;
        private readonly Action<string> _authHandler;

        public AdminWindow(RestreamPlugin plugin)
        {
            _plugin = plugin;
            var cfg = Config.Load();

            // 授权流程结束时刷新状态文案（在后台线程触发，需切回 UI 线程更新控件）。
            _authHandler = err =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (err == null) RefreshStatus();
                    else _msg.Text = "授权失败：" + err + "\n（可重试「登录并授权」，或用第 ④ 步手动填入 access token）";
                });
            };
            _plugin.AuthorizationCompleted += _authHandler;
            Closed += (s, e) => _plugin.AuthorizationCompleted -= _authHandler;

            Title = "Restream 聚合聊天集成 · 设置";
            Width = 660;
            Height = 780;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = PageBg;
            FontFamily = new FontFamily("Microsoft YaHei, Segoe UI, PingFang SC, sans-serif");

            // 整体布局：标题栏（固定）/ 卡片区（可滚动）/ 底部操作栏（固定）
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new Border
            {
                Padding = new Thickness(22, 16, 22, 16),
                Background = new LinearGradientBrush(
                    Color.FromRgb(0x4F, 0x46, 0xE5), Color.FromRgb(0x7C, 0x3A, 0xED),
                    new Point(0, 0), new Point(1, 1))
            };
            // 标题栏：左侧标题与副标题，右侧版本徽标。
            var headerGrid = new Grid { VerticalAlignment = VerticalAlignment.Center };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var titles = new StackPanel();
            titles.Children.Add(new TextBlock
            {
                Text = L10n.T("Restream 聚合聊天集成", "Restream アグリゲートチャット統合", "Restream Aggregated Chat Integration"),
                FontSize = 21,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White
            });
            titles.Children.Add(new TextBlock
            {
                Text = L10n.T("把 Twitch / YouTube 等多平台直播聊天集成至弹幕姬",
                    "Twitch / YouTube など複数プラットフォームのライブチャットを弾幕姫に統合",
                    "Aggregate live chats from Twitch / YouTube and more platforms into Bililive DM"),
                FontSize = 12.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE7, 0xFF)),
                Margin = new Thickness(0, 4, 0, 0)
            });
            headerGrid.Children.Add(titles);
            Grid.SetColumn(titles, 0);
            var versionBadge = new Border
            {
                Margin = new Thickness(16, 0, 0, 0),
                Padding = new Thickness(12, 5, 12, 5),
                MinHeight = 24,
                CornerRadius = new CornerRadius(12),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
                Child = new TextBlock
                {
                    Text = _plugin.PluginVer,
                    Foreground = Brushes.White,
                    FontSize = 12,
                    FontWeight = FontWeights.Bold,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            headerGrid.Children.Add(versionBadge);
            Grid.SetColumn(versionBadge, 1);
            header.Child = headerGrid;
            grid.Children.Add(header);

            var body = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(18, 16, 18, 8)
            };
            body.Content = BuildCards(cfg);
            grid.Children.Add(body);
            Grid.SetRow(body, 1);

            var footer = BuildFooter(cfg);
            grid.Children.Add(footer);
            Grid.SetRow(footer, 2);

            Content = grid;

            // token 状态随时间变化（自动续期 / 过期），每 30 秒刷新一次底部状态文本，
            // 避免打开窗口后一直显示刚打开时的静态快照（如“约 0 分钟到期”直到重启才更新）。
            var statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            statusTimer.Tick += (s, e) =>
            {
                try { if (_status != null) _status.Text = TokenStatusText(Config.Load()); } catch { }
            };
            statusTimer.Start();
            this.Closed += (s, e) => statusTimer.Stop();

            RestreamPlugin.Trace("设置窗口已打开");
        }

        // 中部所有分步卡片
        private StackPanel BuildCards(PluginConfig cfg)
        {
            var root = new StackPanel();

            // ① 准备工作（首次使用）
            root.Children.Add(Card(0, L10n.T("准备工作（仅首次需要）", "準備作業（初回のみ）", "Preparation (first time only)"),
                Hint(L10n.T("Restream Chat 会把你账号下所有已连接平台的聊天聚合成一条实时流。先在 Restream 侧按下面 6 步准备好应用与授权：",
                    "Restream Chat はアカウントに連携した全プラットフォームのチャットを一つのリアルタイム流に統合します。まず Restream 側で以下 6 ステップでアプリと認可を準備してください：",
                    "Restream Chat merges chats from all connected platforms on your account into one real-time stream. Prepare the app and authorization on the Restream side in 6 steps below:")),
                StepList(
                    L10n.T("打开 Restream 官网（restream.io）注册并登录账号。", "Restream 公式サイト（restream.io）を開き、登録してログインします。", "Open the Restream website (restream.io), sign up and log in."),
                    L10n.T("在 Restream 后台「Channels」里添加并授权你要直播到的平台（如 Twitch、YouTube、Facebook、Kick、Discord、X 等）——这些就是聊天来源。", "Restream 管理画面の「Channels」で配信先プラットフォーム（Twitch、YouTube、Facebook、Kick、Discord、X など）を追加・認可します——これらがチャットの取得元です。", "In Restream dashboard «Channels», add and authorize the platforms you stream to (e.g. Twitch, YouTube, Facebook, Kick, Discord, X, etc.) — these are the chat sources."),
                    L10n.T("打开 Restream 开发者后台（developers.restream.io/apps），点击 Create App 创建一个应用。", "Restream デベロッパーコンソール（developers.restream.io/apps）を開き、Create App でアプリを作成します。", "Open the Restream developer console (developers.restream.io/apps) and click Create App to create an application."),
                    L10n.T("在应用设置里：把 Redirect URI 设为下方复制的地址；Scopes 勾选 chat.read。", "アプリ設定で：Redirect URI を下のコピー欄のアドレスに設定し、Scopes で chat.read を選択します。", "In the app settings: set the Redirect URI to the address copied below, and check the chat.read scope."),
                    L10n.T("记下应用生成的 Client ID 与 Client Secret，填到下方第 ② 步。", "アプリ生成の Client ID と Client Secret を控え、下の手順 ② に入力します。", "Note the Client ID and Client Secret generated by the app, and fill them in step ② below."),
                    L10n.T("使用第 ③ 步的授权并连接即可开始使用。", "手順 ③ の認可で接続すれば利用開始です。", "Authorize and connect in step ③ to start using it.")),
                Label(L10n.T("Redirect URI（复制到应用设置的第 4 步）", "Redirect URI（アプリ設定の手順 4 へコピー）", "Redirect URI (copy to step 4 of the app settings)")),
                RedirectRow(),
                Hint(L10n.T("端口 8989 为本插件本地回调专用，极少被其它程序占用；万一被占用，可用下方第 ③ 步「手动粘贴 code」方式授权，无需改动此地址。",
                    "ポート 8989 は本プラグインのローカルコールバック専用で、他プログラムが占有することは稀です。万一占有されている場合は、下の手順 ③「code を手動貼り付け」で認可でき、このアドレスを変更する必要はありません。",
                    "Port 8989 is dedicated to this plugin's local callback and is rarely occupied by other programs; if it is, use the «manually paste code» method in step ③ below — no change to this address needed.")),
                LinkRow(
                    new LinkItem(L10n.T("打开 Restream 官网", "Restream 公式サイトを開く", "Open Restream website"), "https://restream.io"),
                    new LinkItem(L10n.T("打开 Restream 开发者后台", "Restream デベロッパーコンソールを開く", "Open Restream developer console"), "https://developers.restream.io/apps"))));

            // ② 填入凭证
            root.Children.Add(Card(1, L10n.T("填入应用凭证", "アプリ認証情報の入力", "Enter app credentials"),
                Label(L10n.T("Client ID", "Client ID", "Client ID")),
                Input(_clientIdBox = new TextBox { Text = cfg.ClientId, Margin = new Thickness(0, 0, 0, 6) }),
                Label(L10n.T("Client Secret", "Client Secret", "Client Secret")),
                Input(_clientSecretBox = new PasswordBox { Password = cfg.ClientSecret, Margin = new Thickness(0, 0, 0, 6) })));

            // ③ 授权并连接
            root.Children.Add(Card(2, L10n.T("授权并连接", "認可して接続", "Authorize and connect"),
                Hint(L10n.T("点击下方按钮会自动打开浏览器，在 Restream 页面登录并同意授权，本插件通过本地回调自动拿到 token，无需手动复制 code。",
                    "下のボタンでブラウザが自動で開き、Restream ページでログインして認可すると、本プラグインはローカルコールバックで token を自動取得し、code を手動コピーする必要はありません。",
                    "Click the button below to open your browser automatically; after logging in and agreeing on the Restream page, this plugin obtains the token via the local callback — no manual code copying.")),
                PrimaryButton(L10n.T("登录并授权（自动捕获回调，无需复制 code）", "ログインして認可（コールバックを自動取得、code コピー不要）", "Log in and authorize (auto-capture callback, no code copy)"), () =>
                {
                    RestreamPlugin.Trace("设置：点击 登录并授权");
                    SaveBasics();
                    _plugin.StartLoginFlow();
                    _msg.Text = L10n.T("已打开浏览器，请在 Restream 页面登录并点击「同意」。授权成功后会自动连接，无需其它操作。",
                        "ブラウザを開きました。Restream ページでログインし「同意」をクリックしてください。認可成功後に自動接続します。",
                        "Browser opened. Log in on the Restream page and click «Agree». It connects automatically after a successful authorization.");
                }),
                Label(L10n.T("备用：上方自动授权失败（如端口被占用）时，手动粘贴回调地址里的 ?code=", "予備：上の自動認可が失敗した場合（ポート占有など）、コールバック URL の ?code= を手動で貼り付け", "Fallback: if the automatic authorization above fails (e.g. port occupied), paste the ?code= from the callback URL")),
                Input(_codeBox = new TextBox { Margin = new Thickness(0, 0, 0, 6) }),
                SecondaryButton(L10n.T("用 code 兑换并连接", "code で交換して接続", "Exchange code and connect"), async () =>
                {
                    RestreamPlugin.Trace("设置：点击 用 code 兑换并连接");
                    SaveBasics();
                    await _plugin.ExchangeAndConnect(_codeBox.Text);
                })));

            // ④ 可选设置
            var proxyIndex = cfg.ProxyMode == Config.ProxyModeCustom ? 2 : (cfg.ProxyMode == Config.ProxyModeSystem ? 1 : 0);
            root.Children.Add(Card(3, L10n.T("可选设置", "オプション設定", "Optional settings"),
                Label(L10n.T("代理模式", "プロキシモード", "Proxy mode")),
                RadioRow3(out _proxyNone, out _proxySystem, out _proxyCustom,
                    L10n.T("直连（不使用代理）", "直結（プロキシなし）", "Direct (no proxy)"),
                    L10n.T("系统代理", "システムプロキシ", "System proxy"),
                    L10n.T("自定义代理", "カスタムプロキシ", "Custom proxy"),
                    proxyIndex),
                Label(L10n.T("自定义代理地址（仅选「自定义代理」时填写）：示例 http://127.0.0.1:7890", "カスタムプロキシのアドレス（「カスタムプロキシ」選択時のみ）：例 http://127.0.0.1:7890", "Custom proxy address (fill only when «Custom proxy» is selected): example http://127.0.0.1:7890")),
                Input(_proxyBox = new TextBox { Text = cfg.ProxyUrl, Margin = new Thickness(0, 0, 0, 6), IsEnabled = cfg.ProxyMode == Config.ProxyModeCustom }),
                Label(L10n.T("手动 access token（选填）：不想走 OAuth 时直接粘贴", "手動 access token（任意）：OAuth を使わない場合は直接貼り付け", "Manual access token (optional): paste directly if you prefer not to use OAuth")),
                Input(_manualTokenBox = new TextBox { Text = cfg.AccessToken, Margin = new Thickness(0, 0, 0, 6) })));

            // 切换代理模式时启用/禁用自定义地址输入框。
            _proxyNone.Checked += (s, e) => _proxyBox.IsEnabled = false;
            _proxySystem.Checked += (s, e) => _proxyBox.IsEnabled = false;
            _proxyCustom.Checked += (s, e) => _proxyBox.IsEnabled = true;

            // ⑤ 显示与高级
            root.Children.Add(Card(4, L10n.T("显示与高级", "表示と詳細設定", "Display and advanced"),
                (_overlayChk = new CheckBox
                {
                    Content = L10n.T("使用独立浮层（开启才能把 Twitch/Kappa 等表情包渲染成图片；默认关，使用弹幕姬自带浮层）",
                        "独立オーバーレイを使用（オンにすると Twitch/Kappa などの絵文字を画像化。既定オフで弾幕姫標準オーバーレイを使用）",
                        "Use a standalone overlay (on to render Twitch/Kappa emotes as images; off by default, uses Bililive DM's built-in overlay)"),
                    IsChecked = cfg.UseOwnOverlay,
                    Margin = new Thickness(0, 0, 0, 8),
                    Foreground = TextNormal
                }),
                Label(L10n.T("独立浮层布局（开启独立浮层后生效）", "独立オーバーレイのレイアウト（独立オーバーレイ有効時）", "Standalone overlay layout (applies when the standalone overlay is on)")),
                RadioRow(out _modeScroll, out _modeSidebar, L10n.T("弹幕滚动", "弾幕スクロール", "Danmaku scroll"), L10n.T("侧边栏列表", "サイドバー一覧", "Sidebar list"), cfg.OverlayMode == "sidebar" ? 1 : 0),
                Label(L10n.T("侧边栏位置（选侧边栏列表时生效）", "サイドバー位置（サイドバー一覧選択時）", "Sidebar position (applies when sidebar list is selected)")),
                RadioRow(out _sideRight, out _sideLeft, L10n.T("右侧", "右側", "Right"), L10n.T("左侧", "左側", "Left"), cfg.OverlaySide == "left" ? 1 : 0),
                (_debugChk = new CheckBox
                {
                    Content = L10n.T("调试日志（开启后把连接 / 授权 / 收消息等运行细节写入插件目录的 调试日志.log，便于排查问题）",
                        "デバッグログ（オンにすると接続／認可／受信などの詳細をプラグイン目录の 调试日志.log に記録し、トラブルシューティングに利用）",
                        "Debug log (when on, writes connection / authorization / message-receiving details to 调试日志.log in the plugin folder for troubleshooting)"),
                    IsChecked = cfg.DebugLog,
                    Margin = new Thickness(0, 8, 0, 0),
                    Foreground = TextNormal
                })));

            _overlayChk.Checked += (s, e) => RestreamPlugin.Trace("设置：勾选 使用独立浮层");
            _overlayChk.Unchecked += (s, e) => RestreamPlugin.Trace("设置：取消勾选 使用独立浮层");
            _debugChk.Checked += (s, e) => RestreamPlugin.Trace("设置：勾选 调试日志");
            _debugChk.Unchecked += (s, e) => RestreamPlugin.Trace("设置：取消勾选 调试日志");

            return root;
        }

        // 底部操作栏（固定）
        private UIElement BuildFooter(PluginConfig cfg)
        {
            var dock = new Border
            {
                BorderThickness = new Thickness(0, 1, 0, 0),
                BorderBrush = CardBorder,
                Background = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFB)),
                Padding = new Thickness(18, 12, 18, 14)
            };
            var stack = new StackPanel();
            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 8)
            };
            actions.Children.Add(SecondaryButton(L10n.T("仅保存", "保存のみ", "Save only"), () =>
            {
                RestreamPlugin.Trace("设置：点击 仅保存");
                SaveBasics();
                _msg.Text = L10n.T("已保存配置。点「保存并连接」或回到弹幕姬右键「启用」即可开始接收弹幕。",
                    "設定を保存しました。「保存して接続」を押すか、弾幕姫で右クリック「有効化」で受信開始します。",
                    "Configuration saved. Click «Save and connect» or right-click «Enable» in Bililive DM to start receiving danmaku.");
            }));
            actions.Children.Add(PrimaryButton(L10n.T("保存并连接", "保存して接続", "Save and connect"), () =>
            {
                RestreamPlugin.Trace("设置：点击 保存并连接");
                SaveBasics();
                _plugin.Start();
                RefreshStatus();
                _msg.Text = L10n.T("已保存配置并开始连接。", "設定を保存し、接続を開始しました。", "Configuration saved and connection started.");
            }));
            stack.Children.Add(actions);

            _status = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = TokenStatusText(cfg),
                Foreground = TextStrong,
                FontSize = 12.5
            };
            _msg = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = TextMuted,
                FontSize = 12.5,
                Margin = new Thickness(0, 4, 0, 0)
            };
            stack.Children.Add(_status);
            stack.Children.Add(_msg);
            dock.Child = stack;
            return dock;
        }

        // ===== 现代 UI 基础构件 =====

        private static Border Card(int step, string title, params UIElement[] children)
        {
            var card = new Border
            {
                Background = CardBg,
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                BorderBrush = CardBorder,
                Margin = new Thickness(0, 0, 0, 14),
                Padding = new Thickness(18, 16, 18, 16),
                Effect = new DropShadowEffect
                {
                    BlurRadius = 14,
                    ShadowDepth = 2,
                    Opacity = 0.10,
                    Color = Colors.Black
                }
            };
            var stack = new StackPanel();
            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            header.Children.Add(NumBadge(step));
            header.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                Foreground = TextStrong,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            stack.Children.Add(header);
            foreach (var c in children) stack.Children.Add(c);
            card.Child = stack;
            return card;
        }

        private static Border NumBadge(int n)
        {
            return new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                Background = Accent,
                Child = new TextBlock
                {
                    Text = n.ToString(),
                    Foreground = Brushes.White,
                    FontWeight = FontWeights.Bold,
                    FontSize = 13,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
        }

        private static StackPanel StepList(params string[] steps)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            int i = 1;
            foreach (var s in steps)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 9) };
                row.Children.Add(NumBadge(i));
                row.Children.Add(new TextBlock
                {
                    Text = s,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(11, 0, 0, 0),
                    MaxWidth = 560,
                    Foreground = TextNormal,
                    LineHeight = 18
                });
                sp.Children.Add(row);
                i++;
            }
            return sp;
        }

        private UIElement RedirectRow()
        {
            var box = new TextBox
            {
                Text = RestreamPlugin.RedirectUri,
                IsReadOnly = true,
                Height = 32,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(10, 0, 10, 0),
                BorderThickness = new Thickness(1),
                BorderBrush = CardBorder,
                Background = new SolidColorBrush(Color.FromRgb(0xF9, 0xF9, 0xFB)),
                FontFamily = new FontFamily("Consolas, Microsoft YaHei"),
                Margin = new Thickness(0, 0, 8, 0)
            };
            _redirectBox = box;
            Button copyBtn = null;
            copyBtn = SecondaryButton(L10n.T("复制", "コピー", "Copy"), () =>
            {
                RestreamPlugin.Trace("设置：点击 复制 Redirect URI");
                copyBtn.IsEnabled = false;
                copyBtn.Content = L10n.T("复制中…", "コピー中…", "Copying…");
                _msg.Text = L10n.T("正在复制，请稍候…", "コピー中、しばらくお待ちください…", "Copying, please wait…");
                // 在后台 STA 线程写剪贴板：避免 UU 远程等占用剪贴板时阻塞 UI 线程导致卡死；
                // 后台线程自带重试与退避，成功/失败后再切回 UI 线程更新状态。
                TrySetClipboardAsync(RestreamPlugin.RedirectUri, ok =>
                {
                    _msg.Text = ok
                        ? L10n.T("已复制 Redirect URI 到剪贴板，直接粘贴到 Restream 应用设置即可。",
                            "Redirect URI をクリップボードにコピーしました。Restream のアプリ設定にそのまま貼り付けてください。",
                            "Copied the Redirect URI to the clipboard. Paste it directly into the Restream app settings.")
                        : L10n.T("复制失败（可能被 UU 远程等程序占用）。已为你选中上方地址，请直接按 Ctrl+C 复制。",
                            "コピー失敗（UU リモート等が占有している可能性があります）。上のアドレスを選択済みです。そのまま Ctrl+C でコピーしてください。",
                            "Copy failed (possibly occupied by UU Remote etc.). The address above is selected for you — press Ctrl+C to copy.");
                    copyBtn.Content = ok ? L10n.T("已复制 ✓", "コピー済み ✓", "Copied ✓") : L10n.T("复制", "コピー", "Copy");
                    if (!ok) SelectRedirectText();
                    var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                    t.Tick += (s, e) => { t.Stop(); copyBtn.Content = L10n.T("复制", "コピー", "Copy"); copyBtn.IsEnabled = true; };
                    t.Start();
                });
            });
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 6),
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(box);
            row.Children.Add(copyBtn);
            return row;
        }

        private static StackPanel LinkRow(params LinkItem[] links)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 4) };
            foreach (var link in links)
            {
                var b = new Button
                {
                    Content = link.Text,
                    Height = 28,
                    Padding = new Thickness(4, 0, 4, 0),
                    Margin = new Thickness(0, 0, 16, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Foreground = AccentText,
                    FontWeight = FontWeights.Bold,
                    Cursor = Cursors.Hand
                };
                b.Click += (s, e) =>
                {
                    RestreamPlugin.Trace("设置：打开外链 " + link.Url);
                    try { Process.Start(new ProcessStartInfo { FileName = link.Url, UseShellExecute = true }); } catch { }
                };
                sp.Children.Add(b);
            }
            return sp;
        }

        private static TextBlock Label(string text)
        {
            return new TextBlock
            {
                Text = text,
                Margin = new Thickness(0, 8, 0, 3),
                FontWeight = FontWeights.Bold,
                Foreground = TextStrong,
                FontSize = 12.5
            };
        }

        private static TextBlock Hint(string text)
        {
            return new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
                Foreground = TextMuted,
                FontSize = 12.5,
                LineHeight = 17
            };
        }

        private static T Input<T>(T box) where T : Control
        {
            box.Height = 32;
            box.Padding = new Thickness(10, 0, 10, 0);
            box.BorderThickness = new Thickness(1);
            box.BorderBrush = CardBorder;
            box.Background = Brushes.White;
            box.VerticalContentAlignment = VerticalAlignment.Center;
            return box;
        }

        private static StackPanel RadioRow(out RadioButton left, out RadioButton right, string leftText, string rightText, int checkedIndex)
        {
            left = new RadioButton { Content = leftText, Margin = new Thickness(0, 0, 14, 0), IsChecked = checkedIndex == 0, Foreground = TextNormal };
            right = new RadioButton { Content = rightText, Margin = new Thickness(0, 0, 14, 0), IsChecked = checkedIndex == 1, Foreground = TextNormal };
            return new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 8),
                Children = { left, right }
            };
        }

        private static StackPanel RadioRow3(out RadioButton a, out RadioButton b, out RadioButton c, string aText, string bText, string cText, int checkedIndex)
        {
            a = new RadioButton { Content = aText, Margin = new Thickness(0, 0, 14, 0), IsChecked = checkedIndex == 0, Foreground = TextNormal };
            b = new RadioButton { Content = bText, Margin = new Thickness(0, 0, 14, 0), IsChecked = checkedIndex == 1, Foreground = TextNormal };
            c = new RadioButton { Content = cText, Margin = new Thickness(0, 0, 14, 0), IsChecked = checkedIndex == 2, Foreground = TextNormal };
            return new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 8),
                Children = { a, b, c }
            };
        }

        private static Button MakeButton(string content, Brush bg, Brush fg, Brush borderBrush, Thickness border, Action onClick)
        {
            var b = new Button
            {
                Content = content,
                Height = 36,
                Padding = new Thickness(18, 0, 18, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.Bold,
                Foreground = fg,
                Background = bg,
                BorderBrush = borderBrush,
                BorderThickness = border,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 8, 0)
            };
#pragma warning disable CS0618 // FrameworkElementFactory 已过时，但代码内联构造按钮模板仍是最直接可用的方式
            var borderFactory = new FrameworkElementFactory(typeof(Border));
            borderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            borderFactory.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
            borderFactory.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
            borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            // 让按钮的 Padding 真正作用于文字：ContentPresenter 的 Margin 绑定到 Button.Padding。
            cp.SetValue(ContentPresenter.MarginProperty, new TemplateBindingExtension(Button.PaddingProperty));
            borderFactory.AppendChild(cp);
            b.Template = new ControlTemplate(typeof(Button)) { VisualTree = borderFactory };
#pragma warning restore CS0618
            b.Click += (s, e) => onClick();
            return b;
        }

        private static Button PrimaryButton(string content, Action onClick)
        {
            return MakeButton(content, Accent, Brushes.White, null, new Thickness(0), onClick);
        }

        private static Button SecondaryButton(string content, Action onClick)
        {
            return MakeButton(content, Brushes.White, AccentText, Accent, new Thickness(1), onClick);
        }

        // 在后台 STA 线程稳健写入系统剪贴板：WPF 的 Clipboard.SetText 在剪贴板被其它进程
        // （如 UU 远程）占用时会抛 COMException（OpenClipboard 失败）。放到独立 STA 线程重试，
        // 既不阻塞 UI 线程（避免卡死），又能靠退避吸收占用；写成功后通过回调切回 UI 线程更新状态。
        // 重试几次仍失败才判定真正失败，并提示用户手动 Ctrl+C（已选中文本框）。
        private void TrySetClipboardAsync(string text, Action<bool> onDone)
        {
            var tcs = new TaskCompletionSource<bool>();
            var thread = new System.Threading.Thread(() =>
            {
                const int maxAttempts = 5;
                bool ok = false;
                Exception lastEx = null;
                for (var attempt = 0; attempt < maxAttempts; attempt++)
                {
                    try
                    {
                        Clipboard.SetText(text);
                        ok = true;
                        RestreamPlugin.Trace("复制第 " + (attempt + 1) + " 次：SetText 未抛异常，视为成功");
                        break;
                    }
                    catch (Exception ex)
                    {
                        lastEx = ex;
                        RestreamPlugin.Trace("复制第 " + (attempt + 1) + " 次尝试异常：" + ex.Message);
                        // SetText 可能已写入却抛异常（剪贴板被占用），校验内容以判定真伪失败。
                        try
                        {
                            if (Clipboard.ContainsText() && Clipboard.GetText() == text)
                            {
                                ok = true;
                                RestreamPlugin.Trace("复制第 " + (attempt + 1) + " 次：异常但内容已在剪贴板，判定成功");
                                break;
                            }
                        }
                        catch { }
                    }
                    if (attempt < maxAttempts - 1)
                    {
                        // 退避：200ms 起步逐步加大，给 UU 远程等占用方释放剪贴板的时间。
                        System.Threading.Thread.Sleep(200 + attempt * 200);
                    }
                }
                if (!ok) RestreamPlugin.Trace("复制失败：" + maxAttempts + " 次尝试后仍写不进剪贴板（" + lastEx?.Message + "）");
                tcs.TrySetResult(ok);
            });
            // Clipboard 操作要求 STA 单元；后台线程显式设为 STA 并后台运行，不阻塞 UI。
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            // 后台线程不可触碰 UI 控件，结果就绪后切回 UI 线程执行回调。
            tcs.Task.ContinueWith(t => Dispatcher.BeginInvoke(new Action(() => onDone(t.Result))));
        }

        // 复制失败时选中只读文本框内容，方便用户直接 Ctrl+C（其场景已确认 Ctrl+V 可粘贴）。
        private void SelectRedirectText()
        {
            try
            {
                if (_redirectBox != null)
                {
                    _redirectBox.Focus();
                    _redirectBox.SelectAll();
                }
            }
            catch { }
        }

        private void SaveBasics()
        {
            var cfg = Config.Load();
            cfg.ClientId = _clientIdBox.Text.Trim();
            cfg.ClientSecret = _clientSecretBox.Password;
            cfg.ProxyMode = _proxyCustom.IsChecked == true ? Config.ProxyModeCustom
                : (_proxySystem.IsChecked == true ? Config.ProxyModeSystem : Config.ProxyModeNone);
            cfg.ProxyUrl = _proxyBox.Text.Trim();
            cfg.UseOwnOverlay = _overlayChk.IsChecked == true;
            cfg.OverlayMode = _modeSidebar.IsChecked == true ? "sidebar" : "scroll";
            cfg.OverlaySide = _sideLeft.IsChecked == true ? "left" : "right";
            cfg.DebugLog = _debugChk.IsChecked == true;
            // 手动 token 仅在填写时写入；留空不覆盖已有的 OAuth token。
            // 手动填入的 token 没有过期信息（也未持有 refresh_token 可续期），因此清掉陈旧的
            // AccessTokenExpiresAt，避免状态栏把旧时间戳当作“约 0 分钟到期”而误导用户。
            var manual = _manualTokenBox.Text.Trim();
            if (!string.IsNullOrEmpty(manual)) { cfg.AccessToken = manual; cfg.AccessTokenExpiresAt = 0; }
            Config.Save(cfg);
            RestreamPlugin.Trace("设置已保存：ClientId=" + (string.IsNullOrEmpty(cfg.ClientId) ? "(空)" : "已填")
                + " Proxy=" + Config.ProxyDescription(cfg.ProxyMode, cfg.ProxyUrl)
                + " UseOwnOverlay=" + cfg.UseOwnOverlay + " OverlayMode=" + cfg.OverlayMode
                + " OverlaySide=" + cfg.OverlaySide + " DebugLog=" + cfg.DebugLog
                + " ManualToken=" + (string.IsNullOrEmpty(manual) ? "(空)" : "已填"));
            _plugin.SetUseOwnOverlay(cfg.UseOwnOverlay);
            _plugin.SetDebugLog(cfg.DebugLog);
        }

        private void RefreshStatus()
        {
            _status.Text = TokenStatusText(Config.Load());
        }

        private static string TokenStatusText(PluginConfig cfg)
        {
            var pluginState = L10n.T("插件状态：由弹幕姬“外挂程序”列表的启用/停用控制",
                "プラグイン状態：弾幕姫の「外挂程序」リストの有効/無効で制御",
                "Plugin state: controlled by the Enable/Disable toggle in Bililive DM's plugin list");
            string current;
            if (string.IsNullOrWhiteSpace(cfg.AccessToken))
                current = L10n.T("当前状态：尚未授权。\n请完成第 ③ 步 OAuth 授权，或第 ④ 步手动填入 access token。",
                    "現在の状態：未認可。\n手順 ③ の OAuth 認可、または手順 ④ で access token を手動入力してください。",
                    "Current state: not authorized.\nComplete the OAuth authorization in step ③, or paste an access token manually in step ④.");
            else if (cfg.AccessTokenExpiresAt > 0)
            {
                var dt = DateTimeOffset.FromUnixTimeSeconds(cfg.AccessTokenExpiresAt).ToLocalTime();
                var left = dt - DateTimeOffset.Now;
                // token 已过期但 Restream 不在会话中途强制失效，当前连接仍有效；续期在重连/下次需要时自动发生，
                // 故显示“已过期、连接仍有效”，不显示误导性的“约 0 分钟”。
                if (left.TotalMinutes <= 0)
                    current = L10n.T("当前状态：access token 已过期，但当前连接仍有效；插件将在重连或下次需要时自动续期，无需手动操作。",
                        "現在の状態：access token は期限切れですが、現在の接続は有効です。プラグインは再接続時または必要時に自動更新し、手動操作は不要です。",
                        "Current state: the access token has expired, but the current connection is still valid; the plugin renews it automatically on reconnect or when needed — no manual action required.");
                else
                    current = L10n.T($"当前状态：已授权，access token 约 {(int)left.TotalMinutes} 分钟后到期（到期前插件自动续期，无需重复操作）。",
                        $"現在の状態：認可済み、access token は約 {(int)left.TotalMinutes} 分後に期限切れ（期限前にプラグインが自動更新、再操作不要）。",
                        $"Current state: authorized; the access token expires in about {(int)left.TotalMinutes} minutes (the plugin renews it automatically before expiry, no repeat action needed).");
            }
            else
                current = L10n.T("当前状态：已配置 access token（无过期信息），可直接连接。",
                    "現在の状態：access token を設定済み（期限情報なし）、そのまま接続可能。",
                    "Current state: an access token is configured (no expiry info), connect directly.");
            return current + "\n" + pluginState;
        }
    }

    // 设置窗口内的外链条目（官网 / 开发者后台等）。
    internal sealed class LinkItem
    {
        public LinkItem(string text, string url)
        {
            Text = text;
            Url = url;
        }

        public string Text { get; }
        public string Url { get; }
    }
}
