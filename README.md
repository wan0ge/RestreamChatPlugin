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

- GitHub Releases：`RestreamChatPlugin.zip`（内含单文件 `RestreamChatPlugin.dll`）
- 弹幕姬官网插件页

## 安装

1. 下载并解压 `RestreamChatPlugin.zip`。
2. 把 `RestreamChatPlugin.dll` 放入 `我的文档\弹幕姬\Plugins\RestreamChatPlugin\` 文件夹（若 `RestreamChatPlugin` 文件夹不存在则新建）。
3. 重启弹幕姬。

## 使用方法

1. 在弹幕姬「插件」选项卡中找到 **Restream 聚合聊天**，右键选择「管理」打开设置窗口。
2. 点击「授权」，在浏览器中登录 Restream 并允许 Chat API 访问；授权完成会自动回到弹幕姬。
3. 按需选择代理模式（默认为系统代理；网络正常可选直连，或填写自定义代理地址）。
4. 点击「保存并连接」，或回到弹幕姬右键本插件选择「启用」，即可开始接收多平台聊天。

> 若开启「独立浮层」，聊天会以图片表情形式渲染在独立浮层中；关闭时使用弹幕姬自带浮层显示。

## 构建

本插件为弹幕姬（bililive_dm）的插件，依赖其 `BilibiliDM_PluginFramework` 框架。

- **方式一（Visual Studio）**：已安装 Visual Studio 2022 且勾选「.NET 桌面开发」工作负载时，直接运行仓库根目录下的 `build.bat`，它将调用 VS 自带的 MSBuild 编译出 `RestreamChatPlugin.dll`。
- **方式二（命令行）**：在已存在预编译 `BilibiliDM_PluginFramework.dll` 的环境中执行
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
