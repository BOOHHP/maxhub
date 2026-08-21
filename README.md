# MaxHub — 3ds Max 脚本与插件分发平台

MaxHub 是一个面向公司内部 3ds Max 美术/TA 团队的脚本与插件分发管理平台。它由三部分组成：

- **MaxHub Server**：ASP.NET Core 后端，提供工具市场、上传审核、分发下载、成员角色管理
- **MaxHub Agent**：Windows 桌面托盘客户端，自动识别本机 3ds Max 版本，一键安装/卸载/更新脚本
- **MaxHub Connector**：3ds Max 内的工具中心面板（MaxScript），查看与运行已安装工具

## 架构

```
┌─────────────────────────────────────────────────────────┐
│                     MaxHub Server                       │
│  工具市场 · 上传审核 · 分发下载 · 成员角色 · 使用统计     │
└───────────────┬───────────────────────────┬─────────────┘
                │ HTTP (REST + 签名)         │ HTTP
        ┌───────▼────────┐          ┌────────▼────────┐
        │  MaxHub Agent  │          │  Web Portal     │
        │ 桌面托盘客户端   │          │ 工具市场/上传/后台│
        │ 安装/卸载/更新   │          └─────────────────┘
        └───────┬────────┘
                │ 本地 HTTP
        ┌───────▼────────┐
        │ MaxHub Connector│  ← 3ds Max 内面板
        │ 查看/运行工具    │
        └────────────────┘
```

## 核心特性

- **飞书扫码登录**：使用飞书企业自建应用 OAuth，扫码即登录
- **自管角色体系**：admin / reviewer / publisher 三级角色，后台网页管理成员
- **脚本自动识别**：上传本地脚本时自动提取名称与功能描述（头部注释 → rollout 标题 → 文件名 → 关键词）
- **签名校验**：所有工具制品经 ECDSA P-256 签名，Agent 安装前验签，防止篡改
- **多版本兼容**：支持 3ds Max 2019–2026，Agent 自动识别本机安装版本
- **Web Portal**：工具市场（公开浏览）、上传发布、后台管理三页门户

## 快速开始

### 服务器

```powershell
dotnet run --project src\MaxHub.Server --urls http://127.0.0.1:5100
```

以上命令显式使用本地开发地址 `http://127.0.0.1:5100`。不传 `--urls` 时，仓库的 launch profile 默认使用 `http://localhost:5239`。

Development 环境在未配置真实飞书凭据时使用 Mock Provider；只有验证真实飞书 OAuth 时，才需要在 git 忽略的 `appsettings.Local.json` 中配置本地凭据。

### Agent

```powershell
# 开发调试：Agent 默认连接生产环境，必须先指向本地 Server
$env:MAXHUB_SERVER = 'http://127.0.0.1:5100'
dotnet run --project src\MaxHub.Agent.Tray

# 发布自包含单文件 exe
.\tools\publish-agent.ps1
```

产物位于 `artifacts/MaxHubAgent-{version}-win-x64.exe`。发布脚本同时尽力同步公司局域网镜像，并输出 SHA-256。

### Connector

将 `connector/maxhub_connector.ms` 放入 3ds Max 的启动脚本目录，或通过 Agent 安装。

## 项目结构

```
src/
├── MaxHub.Server/       ASP.NET Core 后端 + Web Portal
├── MaxHub.Agent.Tray/   Windows 托盘客户端（WPF）
├── MaxHub.Agent.Cli/    命令行工具
├── MaxHub.Agent.Core/   Agent 核心逻辑（检测/安装/远程）
├── MaxHub.Agent.Service/ Agent 本地 HTTP 服务
└── MaxHub.Core/         共享核心（manifest/打包/签名/脚本解析）
connector/               3ds Max Connector 面板（MaxScript）
protocol/                协议 schema（manifest / installed-ledger）
samples/tools/           示例工具包
tests/                   单元与集成测试
tools/                   发布脚本
```

## 技术栈

- .NET 8 / ASP.NET Core / EF Core + SQLite
- WPF（托盘客户端）
- MaxScript（Connector）
- ECDSA P-256（制品签名）
- 飞书开放平台 OAuth

## 测试

```bash
dotnet test MaxHub.sln
```

## 开发历史

项目从立项到当前版本的架构演进、开发时间线、关键问题与生产发布说明见 [MaxHub-开发文档.md](MaxHub-开发文档.md)。

## 许可

内部使用。
