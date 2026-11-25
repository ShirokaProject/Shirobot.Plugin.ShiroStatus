# ShiroStatus

基于 [ShiroBot 官方插件模板](https://docs.shiroka.org/plugin/) 的文字状态插件。

在群聊或私聊发送 `#shiro` 或 `#Shiro状态`（也接受 `#Shiro`、`#shiro状态`），回复示例（数据以宿主快照为准）：

```text
==== ShiroBot 状态 ====
版本: 1.0.4 (abcdef)
环境: macOS 27.0.1 arm64 && .NET 11.0.0
运行时长: 0天 0小时 9分钟
内存占用: 128.5 MB
接收消息: 256 条
适配器: OneBot v1.2.3 ×2 · Telegram v0.3.0
```

“适配器”一行列出宿主已加载的全部适配器实例（`Context.GetAdapterInstances()`），同一适配器的多个实例合并为 `×N`。

发送 `#moshiro` 回复一张机器状态卡片（系统监视器风格，标签在左、数值右对齐，每项配图标）：

- 头部：系统图标、主机名、系统版本与架构，右侧为系统运行时长（`1d 7h 14min`）。
- 占用：CPU、内存、Swap 和每块磁盘（最多 4 块，根目录总是保留）的分段占用条，正常为主题色，70% 起转琥珀、90% 起转红。
- 规格：处理器（ARM Linux 无型号时按设备树或厂商/核心编码推断）、GPU（macOS 读 IOAccelerator，Linux 用 `nvidia-smi` 或 `lspci`，Windows 读显示适配器注册表）、内核（Linux 读 `/proc/sys/kernel/osrelease`）、进程数与运行时。
- 适配器：收到这条消息的实例（`Context.AdapterInstance`）的适配器、平台、实例，以及 `Context.User.GetSelfAsync()` 取得的账号（3 秒内拿不到则省略），右上角为实例总数。
- 运行：ShiroBot 版本、运行时长、进程内存与消息数。
- 底部：机器人自己的头像 | ShiroBot 小图标 + `ShiroBot Status v版本`，右侧为生成时间。

主题色取自机器人头像：缩小后按色相加权（饱和度越高权重越大，肤色降权），再把饱和度和亮度收进深浅两色都可读的范围。每个账号只取一次，结果写入插件目录的 `accent-colors.txt`；拿不到头像或头像接近黑白时用默认蓝色。适配器未提供头像地址时，QQ 平台按账号拼官方头像地址。

macOS、Linux、Windows 均使用系统 API 或只读系统文件采集，单项失败只留空；渲染失败时回复一句文字提示。

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
