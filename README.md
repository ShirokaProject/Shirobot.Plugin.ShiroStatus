# ShiroStatus

基于 [ShiroBot 官方插件模板](https://docs.shiroka.org/plugin/) 的文字状态插件。

在群聊或私聊发送 `#shiro`，回复示例（数据以宿主快照为准）：

```text
==== ShiroBot 状态 ====
版本: 1.0.2 (abcdef)
环境: macOS 27.0.1 arm64 && .NET 10.0.13
运行时长: 0天 0小时 9分钟
内存占用: 128.5 MB
接收消息: 256 条
```

版本、环境、运行时长、内存和消息数读取 `Context.GetHostStatus()` 的宿主状态快照，运行时长与 Dashboard 使用同一数据源，显示到分钟；不再根据操作系统进程启动时间推算。旧宿主返回 `null` 时不回复。

commit 取宿主入口程序集构建元数据的前六位，与宿主启动日志一致；未记录 commit 时省略括号。内存为宿主进程工作集，按 1024² 字节换算，保留一位小数；消息数为宿主启动以来收到的总数。

命令采用 SDK 的精确匹配路由，不接受额外参数；插件没有额外配置，使用平台无关 API。

## 本地开发

使用官方 `ShiroBot.Templates` 1.0.1 生成，与旁边 AIChat 插件一致：.NET 10、ShiroBot.SDK 1.0.2、独立 `.shirobot-dev/host/<RID>/` 开发宿主、portable PDB 和 Rider `ShiroBot` Run/Debug 配置。

```sh
cd Shirobot.Plugin.ShiroStatus
dotnet build -c Debug          # 构建并复制 DLL/PDB 到开发宿主
sh scripts/dev.sh --prepare    # 准备环境，不启动
sh scripts/dev.sh              # 构建并启动
```

SDK 使用 NuGet.org 发布的 `ShiroBot.SDK` 1.0.2，无需本地 NuGet 源。官方脚本按 SDK 版本自动下载宿主，配置和数据位于本项目独立的 `.shirobot-dev/host/<RID>/` 目录。Dashboard 地址及首次启动的登录 Token 请查看开发宿主 `config.toml` 的 `[api]` 配置。

在 Dashboard 添加并启用适配器实例，连接机器人、启用插件并允许对应会话后发送 `#shiro`。Rider 中可在 `src/Plugin.cs` 的 `HandleStatusAsync` 设置断点，选择 `ShiroBot` 配置直接调试。修改代码后停止宿主，重新构建并启动。

Windows 使用 `powershell -ExecutionPolicy Bypass -File scripts/dev.ps1`；在 Windows 生成的运行配置应指向 `ShiroBot.exe`。

## 验证与分发

```sh
dotnet run --project Tests/Verification -p:ShiroBotDevelopmentHostEnabled=false
dotnet build -c Release
dotnet run --project scripts/version-check -c Release -- bin/Release/net10.0/ShiroStatus.dll 0.1.0 0.1.0
```

验证程序覆盖群聊和私聊路由、额外参数不匹配、纯文字回复、六行文字格式、commit 元数据、内存换算、消息总数、宿主快照数据、9 分钟实时状态、旧宿主空快照，以及跨天/零值/负值时长。

将 `bin/Release/net10.0/ShiroStatus.dll` 复制到目标宿主的 `plugins/` 目录。保留了官方模板的 GitHub Actions 构建、版本检查和 tag 发布工作流；插件版本为 `0.1.0`，对应 tag `v0.1.0`；推送 `v0.1.0` 可发布 DLL 与 ZIP。
