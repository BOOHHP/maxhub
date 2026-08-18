# MaxHub Connector（MaxScript 实现，阶段 3）

Connector 是 Max 内的薄客户端，由 Agent 统一分发安装（见 `ConnectorInstaller`）。
采用 **MaxScript 脚本实现，不依赖 3ds Max SDK，无需编译**：停靠面板用 rollout，
网络/JSON 通过 dotNet 桥使用随 Max 自带的 .NET Framework 类。

## 为什么不用 .NET SDK 插件

Connector 的全部职责（与 Agent 通信、读取 Max 年份与目录、注册 MacroScript、展示面板）
均可由 MaxScript + dotNet 桥完成。放弃 SDK 路线后：

- 不需要获取/安装任何年份的 Max SDK；
- 不需要按年份编译，一套脚本覆盖 2019-2026，无 ABI 构建矩阵；
- 回归成本 = 在目标 Max 中加载脚本并验证行为。

## 制品打包与发布

```powershell
# 打包（zip 根目录必须含 maxhub_connector.ms 入口）
Compress-Archive -Path connector\maxhub_connector.ms -DestinationPath connector-1.0.0.zip

# 由管理员注册到服务端（脚本跨版本，可声明全范围；真机回归后再收窄）
# POST /api/v1/admin/connectors  package=zip, version=1.0.0, minMaxYear=2019, maxMaxYear=2026
```

用户侧由 Agent `sync-connectors` 自动检测本机 Max 并安装：脚本解压到
`%LOCALAPPDATA%\MaxHub\connectors\max{year}\{version}\`，并在各 Max 的
userStartup 写入加载脚本（`fileIn` 入口）。

## 真机回归清单（每个支持年份，重点 2019 与 2026）

1. Agent `sync-connectors` 后启动 Max，输出 `MaxHub Connector loaded (Max NNNN)`。
2. `MaxHub` 宏可打开面板；Agent 在线时显示已连接与正确年份。
3. Agent 停止时面板显示离线，安装入口不可用，不假报成功。
4. dotNet 桥 `System.Net.WebRequest` 在 2019 与 2026 行为一致（超时、异常路径）。
5. Max 运行中执行 Connector 更新被拒绝，关闭 Max 后更新成功且旧版本可回滚。
