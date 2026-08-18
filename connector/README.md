# MaxHub Connector 构建矩阵（阶段 3）

Connector 是 Max 内的薄客户端，由 Agent 统一分发安装（见 `ConnectorInstaller`）。
本目录是构建骨架；**编译与真机回归需要各年份 3ds Max SDK 与真实 Max 环境，当前开发机不具备，属于待办**。

## 构建矩阵

| Max 年份 | 内部版本 | .NET 目标 | SDK ABI 分组 | 状态 |
| --- | --- | --- | --- | --- |
| 2019 | 21.0 | net47 | A (2019-2021) | 骨架 |
| 2020 | 22.0 | net47 | A | 骨架 |
| 2021 | 23.0 | net47 | A | 骨架 |
| 2022 | 24.0 | net48 | B (2022-2024) | 骨架 |
| 2023 | 25.0 | net48 | B | 骨架 |
| 2024 | 26.0 | net48 | B | 骨架 |
| 2025 | 27.0 | net48 | C (2025-2026，需验证) | 骨架 |
| 2026 | 28.0 | net48 | C | 骨架 |

ABI 分组为假设值：制品是否可跨年份复用必须在真机验证后，才能在服务端以
`minMaxYear`/`maxMaxYear` 声明分组范围；验证前一律按单年份发布。

## 构建方式

```powershell
dotnet build connector/MaxHub.Connector -p:MaxYear=2024 -p:MaxSdkDir="C:\Program Files\Autodesk\3ds Max 2024"
```

产物打为 zip 后通过 `POST /api/v1/admin/connectors` 注册，由 Agent 的
`sync-connectors` 命令按本机检测结果自动安装。

## 真机回归清单（每个支持年份）

1. Agent `sync-connectors` 后启动 Max，Connector 通过 startup 脚本加载成功。
2. 停靠面板打开，能列出当前年份兼容的工具。
3. Agent 停止时面板显示离线，安装按钮禁用。
4. 安装/更新/卸载/回滚在面板内闭环，重启要求正确提示。
5. Max 运行中执行 Connector 更新被拒绝，关闭 Max 后更新成功。
