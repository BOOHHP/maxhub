# MaxHub 协议（阶段 0 冻结）

本目录是 MaxHub 的协议唯一来源。`MaxHub.Core` 中的校验器必须与这些 schema 保持一致，任何变更先改这里并升级 `schemaVersion`。

## 文件

| 文件 | 内容 |
| --- | --- |
| `manifest.schema.json` | 工具包 `manifest.json` 的 v1 冻结格式。`hostType` 固定为 `3dsmax`。 |
| `installed-ledger.schema.json` | 本机安装账本 `installed.json` 的 v1 冻结格式。 |

## MVP 安装目标白名单

| 逻辑目标 | 解析位置（相对 Max 用户目录） | MVP |
| --- | --- | --- |
| `userScripts` | `%LOCALAPPDATA%\Autodesk\3dsMax\<year> - 64bit\ENU\scripts` | 支持 |
| `userMacros` | `...\ENU\usermacros` | 支持 |
| `userStartup` | `...\ENU\scripts\startup` | 支持，安装计划必须标注风险 |
| `userPlugins` / `projectScripts` / `sharedScripts` | — | 第二阶段，MVP 校验器拒绝 |

## 路径安全规则

- manifest 内所有路径使用正斜杠、相对路径。
- 禁止绝对路径、盘符、`.`、`..`、空段和反斜杠。
- `install.targets[].source` 必须位于 `payload/3dsmax/` 下。

## Max 版本支持

`compatibility.minVersion`/`maxVersion` 取值范围 2019-2026，平台仅 `win-x64`。
