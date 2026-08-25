# RestreamChatPlugin

通过 [Restream](https://restream.io) Chat API 把 Twitch / YouTube / Kick 等多平台直播聊天聚合，滚成弹幕姬（bililive_dm）的弹幕。**无需连接 B 站直播间**，只要有一个 Restream 账号并授权 Chat API，即可把各平台的聊天与表情直接显示在本地的弹幕姬窗口里。

## 功能特性

- **多平台聊天聚合**：连接 Restream Chat API 后，同时接收 Twitch、YouTube、Kick 等已绑定平台的聊天消息。
- **OAuth 授权登录**：在插件设置窗口点击授权，跳转 Restream 登录并回调到本地 `http://localhost:8989/callback` 获取访问令牌；令牌过期后使用 refresh_token 自动续期。
- **代理设置**：支持三种模式——直连、使用系统代理、自定义代理地址，分别对 HTTP 请求与 WebSocket 连接生效（适用于网络环境特殊的用户）。
- **表情渲染**：可选「独立浮层」，把 Twitch / Kappa 等第三方表情包渲染成图片；默认关闭，使用弹幕姬自带浮层。
- **本地化**：界面跟随弹幕姬的语言设置（中文 / 日本語 / English）。
- **启用记忆**：弹幕姬不持久化插件的启用/停用状态，开启「自动启用」后会在弹幕姬启动时自动恢复启用。
- **单文件部署**：产物 `RestreamChatPlugin.dll` 已内嵌 Newtonsoft.Json，无需额外依赖文件。

## 环境要求

- Windows + [.NET Framework 4.8](https://dotnet.microsoft.com/download/dotnet-framework/net48)
- [弹幕姬（bililive_dm）](https://www.danmuji.org/)

## 下载

- GitHub Releases：`RestreamChatPlugin.dll`（单文件，已内嵌 Newtonsoft.Json）
- 弹幕姬官网插件页

## 安装

下载 `RestreamChatPlugin.dll`，放入 `我的文档\弹幕姬\plugins\`，重启弹幕姬即可。插件首次运行会在 `Plugins\RestreamChatPlugin\` 下自动创建数据目录，用于存放配置文件与表情包缓存。

## 使用方法

1. 准备 Restream 应用：在 [Restream 开发者后台](https://developers.restream.io/apps) 创建应用，记下 Client ID 与 Client Secret，并把回调地址（Redirect URI）设为 `http://localhost:8989/callback`。
2. 在弹幕姬「插件」选项卡中找到 **Restream 聚合聊天**，右键选择「管理」打开设置窗口，在第①步填入上面的 Client ID 与 Client Secret。
3. 点击「授权」完成 Restream 登录与 Chat API 授权，回调到本机后自动回到弹幕姬。
4. 按需选择代理模式（默认系统代理；网络正常可选直连或填写自定义代理地址）。
5. 点击「保存并连接」，或回到弹幕姬右键本插件选择「启用」，即可开始接收多平台聊天。

> 若开启「独立浮层」，聊天会以图片表情形式渲染在独立浮层中；关闭时使用弹幕姬自带浮层显示。

## 构建

本插件为弹幕姬（bililive_dm）的插件，依赖其 `BilibiliDM_PluginFramework` 框架，无法脱离框架独立编译。

- **Visual Studio**：在 `BilibiliDM_PluginFramework` 框架工程可用的环境下（例如克隆 bililive_dm 主仓库），用 Visual Studio 打开 `RestreamChatPlugin\RestreamChatPlugin.csproj`（或主仓库的 `Bililive_dm.sln`），构建 `RestreamChatPlugin` 工程即可。也可直接运行 `RestreamChatPlugin\build.bat`（需已安装 VS 且勾选「.NET 桌面开发」），它会调用 VS 自带的 MSBuild 编译出 `RestreamChatPlugin.dll`。
- **命令行**：在已存在预编译 `BilibiliDM_PluginFramework.dll` 的环境中执行
  ```
  dotnet build RestreamChatPlugin.csproj /p:Configuration=Debug /p:Platform=AnyCPU /p:BuildProjectReferences=false
  ```
  产物位于 `RestreamChatPlugin\bin\Debug\RestreamChatPlugin.dll`，为单文件（Newtonsoft.Json 已内嵌）。

## 测试

- `RestreamChatPlugin.Tests`：基于 MSTest 的单元测试，覆盖代理地址解析、OAuth 续期、本地化等逻辑。
- `RestreamChatPlugin.Tests.Runner`：轻量反射运行器，在无法使用 VSTest testhost 的环境下直接加载测试程序集并逐个执行 `[TestMethod]`，便于在仅具备 .NET Framework 4.8 运行时的机器上查看真实结果。

## 目录结构

```
RestreamChatPlugin/              插件主工程（legacy csproj + 内嵌 Newtonsoft.Json）
RestreamChatPlugin.Tests/        MSTest 单元测试
RestreamChatPlugin.Tests.Runner/ 反射式测试运行器
LICENSE                          MIT
```

## 许可证

[MIT](LICENSE) © 2026 wan0ge

## 联系方式

意见或建议请发邮件至 HXDD233@qq.com
