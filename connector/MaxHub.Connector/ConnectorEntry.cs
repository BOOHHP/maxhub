#if MAX2019 || MAX2020 || MAX2021 || MAX2022 || MAX2023 || MAX2024 || MAX2025 || MAX2026

using System;
using System.Net.Http;

namespace MaxHub.Connector
{
    /// <summary>
    /// Connector 入口骨架。由 MaxHub Agent 写入的 startup 加载脚本以
    /// dotNet.loadAssembly 加载；加载后向 Agent 的本地接口注册当前 Max 实例。
    /// 薄客户端约束：不保存长期令牌、不下载解压任意包、不做覆盖式更新。
    /// </summary>
    public static class ConnectorEntry
    {
        private const string AgentBaseUrl = "http://127.0.0.1:47810"; // Agent 本地接口，仅监听回环

        private static readonly HttpClient Http = new HttpClient { BaseAddress = new Uri(AgentBaseUrl) };

        /// <summary>Max 启动脚本调用的初始化入口。</summary>
        public static void Initialize(int maxYear)
        {
            // TODO(阶段3-真实环境): 
            // 1. 通过 Autodesk.Max SDK 注册停靠面板（发现/已安装/更新/环境/诊断）
            // 2. 与 Agent 协议版本协商，展示离线状态
            // 3. 工具安装计划确认与 MacroScript 注册
            // 需要各年份 3ds Max SDK 与真机回归，见 connector/README.md 的矩阵。
        }

        /// <summary>Agent 不可用时 Connector 必须明确展示离线，不得假报安装成功。</summary>
        public static bool IsAgentOnline()
        {
            try
            {
                var response = Http.GetAsync("/health").GetAwaiter().GetResult();
                return response.IsSuccessStatusCode;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}

#endif
