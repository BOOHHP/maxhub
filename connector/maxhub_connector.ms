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
        progressBar pbInstall "" width:290 height:8 value:0 visible:false color:(color 76 159 224)
        button btnRefresh "刷新" width:100 align:#left across:2
        button btnInstall "安装选中" width:100 align:#right
        timer pollTimer interval:200 active:false

        local toolIds = #()
        local toolVersions = #()
        local currentJobId = undefined
        local currentToolLabel = ""

        fn httpPost url timeoutMs:60000 =
        (
            try (
                local request = (dotNetClass "System.Net.WebRequest").Create url
                request.Method = "POST"
                request.ContentLength = 0
                request.Timeout = timeoutMs
                local response = request.GetResponse()
                local reader = dotNetObject "System.IO.StreamReader" (response.GetResponseStream())
                local body = reader.ReadToEnd()
                reader.Close(); response.Close()
                body
            ) catch ( undefined )
        )

        fn refreshState =
        (
            toolIds = #(); toolVersions = #()
            if isAgentOnline() then (
                local year = currentMaxYear()
                lblStatus.text = "Agent 已连接 | Max " + (year as string)
                local body = httpGet (agentBaseUrl + "/max/tools?maxYear=" + (year as string)) timeoutMs:10000
                local items = #()
                if body != undefined and body != "" then (
                    for line in (filterString body "\n") do (
                        local parts = filterString line "|"
                        if parts.count >= 3 then (
                            append toolIds parts[1]
                            append toolVersions parts[2]
                            append items (parts[3] + "  v" + parts[2])
                        )
                    )
                )
                lstTools.items = items
                btnInstall.enabled = true
            ) else (
                lblStatus.text = "Agent 离线 — 请先启动 MaxHub Agent（不可安装）"
                lstTools.items = #()
                btnInstall.enabled = false
            )
        )

        fn installSelected =
        (
            if lstTools.selection == 0 or lstTools.selection > toolIds.count then (
                messageBox "请先在列表中选择一个工具。" title:"MaxHub"
            ) else (
                local toolId = toolIds[lstTools.selection]
                local version = toolVersions[lstTools.selection]
                local result = httpPost (agentBaseUrl + "/max/install?toolId=" + toolId + "&version=" + version + "&maxYear=" + (currentMaxYear() as string))
                local parts = if result != undefined then (filterString result "|") else #()
                if parts.count >= 2 and parts[1] == "job" then (
                    -- 异步任务：启动轮询，进度条推进由 Agent 侧统一计算
                    currentJobId = parts[2]
                    currentToolLabel = toolId + " v" + version
                    pbInstall.value = 0
                    pbInstall.visible = true
                    btnInstall.enabled = false
                    lblStatus.text = "正在安装 " + currentToolLabel + " … 0%"
                    pollTimer.active = true
                ) else (
                    messageBox ("安装失败：" + (if result == undefined then "Agent 无响应" else result)) title:"MaxHub"
                )
            )
        )

        fn finishInstall message success =
        (
            pollTimer.active = false
            currentJobId = undefined
            pbInstall.visible = false
            btnInstall.enabled = true
            if success then (
                messageBox ("安装成功：" + message + "\n脚本类工具立即可用，Startup 类工具重启 Max 后生效。") title:"MaxHub"
            ) else (
                messageBox ("安装失败：" + message) title:"MaxHub"
            )
            refreshState()
        )

        on pollTimer tick do
        (
            if currentJobId == undefined then ( pollTimer.active = false )
            else (
                local body = httpGet (agentBaseUrl + "/max/install-status?jobId=" + currentJobId) timeoutMs:2000
                local parts = if body != undefined then (filterString body "|") else #()
                if parts.count >= 1 then (
                    case parts[1] of (
                        "running": (
                            local percent = if parts.count >= 2 then (parts[2] as integer) else 0
                            pbInstall.value = percent
                            lblStatus.text = "正在安装 " + currentToolLabel + " … " + (percent as string) + "%"
                        )
                        "ok": ( pbInstall.value = 100; finishInstall (if parts.count >= 2 then parts[2] else currentToolLabel) true )
                        default: ( finishInstall (if parts.count >= 2 then parts[2] else "未知错误") false )
                    )
                )
            )
        )

        on btnRefresh pressed do refreshState()
        on btnInstall pressed do installSelected()
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

    -- 随 Max 启动自动打开面板（启动队列中 fileIn 时 UI 已可用；失败不阻断后续启动脚本）
    try ( MaxHubConnector.show() ) catch (
        format "MaxHub Connector auto-open failed: %\n" (getCurrentException())
    )

    format "MaxHub Connector loaded (Max %)\n" (currentMaxYear() as string)
)
