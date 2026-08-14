# Codex Project Center

面向 Windows 的原生 Codex 任务收件箱，将本机和远程任务统一整理为“待我处理”“进行中”和“最近完成”。

> 本项目是非官方社区项目，与 OpenAI 无隶属、赞助或认可关系。Codex、ChatGPT 和 OpenAI 是其各自权利人的商标。

## 功能

- 原生 WPF + .NET Framework，无 Electron、WebView 或浏览器进程
- 分类显示“待我处理”“进行中”“最近完成”
- 聚合本机和 Codex 托管远程主机的 session
- 识别普通任务和侧边任务，并尽可能返回对应 Codex 界面
- 待处理任务由用户确认“已处理”后才进入最近完成
- 通过 Codex Desktop IPC 校准实时状态和等待标志
- 每 25 秒低频校准，空闲时无高频轮询
- 关闭窗口后驻留系统托盘
- 支持键盘切换栏目和快速打开任务

## 与监控 HUD 的区别

本项目重点不是 Token 或额度展示，而是 human-in-the-loop 工作流：哪些任务仍在运行、哪些已经完成但需要用户查看、哪些已经由用户确认处理。

## 兼容性说明

项目会读取 Codex 本地 session、桌面日志、全局状态，并使用本地 IPC、系统 OpenSSH 和有限的 UI Automation。部分数据结构和导航能力不是稳定公开 API，Codex Desktop 更新后可能需要同步适配。

当前支持 Windows 10/11。远程功能要求系统已安装 OpenSSH Client，并且 Codex Desktop 已保存对应远程项目配置。

## 构建

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

生成文件：`dist\CodexProjectCenter.exe`

## 运行

双击 `start.cmd`，或运行：

```powershell
.\dist\CodexProjectCenter.exe
```

## 创建桌面快捷方式

```powershell
powershell -ExecutionPolicy Bypass -File .\install-shortcut.ps1
```

## 后台自检

不会打开窗口或抢占焦点：

```powershell
.\dist\CodexProjectCenter.exe --self-test .\dist\self-test.json
```

## Performance diagnostics

Threshold-based diagnostics are written to:

`%LOCALAPPDATA%\CodexProjectCenter\project-center.log`

Search the log for `[PERF]`. Normal operations below the thresholds produce no
performance records, and each category is rate-limited. Categories cover full
refresh, local sessions, remote SSH, IPC flow, desktop logs and UI rendering.

报告包含任务数量、状态分类、主机分布、耗时和内存占用。

## 隐私与安全

- 所有状态处理默认在本机完成，本项目不提供云端服务或遥测。
- 应用会读取任务标题、状态、工作目录和短预览文本。
- 远程任务使用当前系统已有的 SSH 配置读取。
- 详细说明见 [PRIVACY.md](PRIVACY.md) 和 [SECURITY.md](SECURITY.md)。

## 贡献

构建和测试说明见 [CONTRIBUTING.md](CONTRIBUTING.md)。提交问题前请删除日志中的用户名、路径、主机名、任务正文和任务 ID。

## License

[MIT](LICENSE)
