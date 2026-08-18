-- MaxHub Connector (MaxScript, 无 SDK 依赖)
-- 由 Agent 的 startup 加载脚本 fileIn；一套脚本覆盖 Max 2019-2026。
-- 薄客户端约束：不保存长期令牌、不下载解压任意包、不做覆盖式更新。

global MaxHubConnector
(
    local agentBaseUrl = "http://127.0.0.1:47810" -- Agent 本地接口，仅回环

    -- 通过 dotNet 桥使用随 Max 自带的 .NET Framework 网络类，无需 Autodesk SDK
    fn httpGet url timeoutMs:2000 =
    (
        try (
            local request = (dotNetClass "System.Net.WebRequest").Create url
            request.Timeout = timeoutMs
            local response = request.GetResponse()
            local reader = dotNetObject "System.IO.StreamReader" (response.GetResponseStream())
            local body = reader.ReadToEnd()
            reader.Close(); response.Close()
            body
        ) catch ( undefined )
    )

    fn isAgentOnline =
    (
        (httpGet (agentBaseUrl + "/health")) != undefined
    )

    fn currentMaxYear =
    (
        -- maxVersion 内部号: 21000=2019 ... 28000=2026
        ((maxVersion())[1] / 1000) + 1998
    )

    rollout maxHubRollout "MaxHub 工具中心"
    (
        label lblStatus "正在连接 MaxHub Agent…" align:#left
        listbox lstTools "可用工具:" items:#() height:12
        button btnRefresh "刷新" width:100 align:#left

        fn refreshState =
        (
            if isAgentOnline() then (
                lblStatus.text = "Agent 已连接 | Max " + (currentMaxYear() as string)
                -- TODO(真机回归): 调 Agent 本地接口拉取当前 Max 年份兼容工具列表
                -- TODO(真机回归): 安装/更新/回滚走 Agent 安装计划确认流程
            ) else (
                lblStatus.text = "Agent 离线 — 请先启动 MaxHub Agent（不可安装）"
                lstTools.items = #()
            )
        )

        on btnRefresh pressed do refreshState()
        on maxHubRollout open do refreshState()
    )

    struct MaxHubConnectorStruct
    (
        fn show = createDialog maxHubRollout width:320,
        fn online = isAgentOnline()
    )
    MaxHubConnector = MaxHubConnectorStruct()

    -- 注册入口宏，供工具栏/菜单绑定
    macroScript MaxHubOpen category:"MaxHub" buttonText:"MaxHub" tooltip:"打开 MaxHub 工具中心"
    (
        MaxHubConnector.show()
    )

    format "MaxHub Connector loaded (Max %)\n" (currentMaxYear() as string)
)
