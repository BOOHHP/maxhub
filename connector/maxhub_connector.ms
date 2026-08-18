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
        -- MaxScript 无原生 tabControl，用单行五列 radiobuttons 作页签
        radiobuttons rdoPages labels:#("发现", "已装", "更新", "环境", "诊断") default:1 columns:5 pos:[10,10]

        -- ===== 发现页 =====
        groupBox grpDiscover "发现" pos:[8,40] width:304 height:196
        label lblStatus "正在连接 MaxHub Agent…" pos:[16,58] width:288 align:#left
        listbox lstTools "可用工具:" pos:[16,78] width:288 height:9
        progressBar pbInstall "" pos:[16,232] width:200 height:8 value:0 visible:false color:(color 76 159 224)
        button btnRefresh "刷新" pos:[16,246] width:90 align:#left
        button btnInstall "安装选中" pos:[222,246] width:82 align:#right

        -- ===== 已安装页 =====
        groupBox grpInstalled "已安装" pos:[8,40] width:304 height:196 visible:false
        listbox lstInstalled "已安装工具:" pos:[16,58] width:288 height:10 visible:false
        button btnUninstall "卸载选中" pos:[16,246] width:90 visible:false
        button btnRollback "回滚到上一版" pos:[214,246] width:90 visible:false

        -- ===== 更新页 =====
        groupBox grpUpdates "更新" pos:[8,40] width:304 height:196 visible:false
        listbox lstUpdates "可更新工具:" pos:[16,58] width:288 height:10 visible:false
        button btnUpdateAll "全部更新" pos:[16,246] width:90 visible:false
        button btnUpdateSel "更新选中" pos:[214,246] width:90 visible:false

        -- ===== 环境页 =====
        groupBox grpEnvironment "环境" pos:[8,40] width:304 height:196 visible:false
        label lblEnvAgent "Agent：检查中…" pos:[16,60] width:280 visible:false align:#left
        label lblEnvMax "当前：3ds Max" pos:[16,82] width:280 visible:false align:#left
        label lblEnvInstalled "已安装：检查中…" pos:[16,104] width:280 visible:false align:#left
        label lblEnvUpdates "可更新：检查中…" pos:[16,126] width:280 visible:false align:#left
        label lblEnvPanel "面板：本次会话已加载" pos:[16,148] width:280 visible:false align:#left
        label lblEnvNotice "" pos:[16,174] width:280 height:28 visible:false align:#left
        button btnEnvironmentRefresh "重新检查" pos:[16,246] width:90 visible:false

        -- ===== 诊断页 =====
        groupBox grpDiagnostics "诊断摘要" pos:[8,40] width:304 height:196 visible:false
        label lblDiagService "服务：检查中…" pos:[16,60] width:280 visible:false align:#left
        label lblDiagMax "当前 Max：检查中…" pos:[16,82] width:280 visible:false align:#left
        label lblDiagInstalled "已安装：检查中…" pos:[16,104] width:280 visible:false align:#left
        label lblDiagUpdates "可更新：检查中…" pos:[16,126] width:280 visible:false align:#left
        label lblDiagChecked "检查时间：尚未检查" pos:[16,148] width:280 visible:false align:#left
        label lblDiagIssue "最近问题：无" pos:[16,174] width:280 height:28 visible:false align:#left
        button btnDiagnosticRefresh "重新检查" pos:[16,246] width:90 visible:false
        button btnCopyDiagnostics "复制摘要" pos:[214,246] width:90 visible:false

        timer pollTimer interval:200 active:false

        -- 数据缓存
        local toolIds = #()
        local toolVersions = #()
        local installedIds = #()
        local installedVersions = #()
        local updateIds = #()
        local updateVersions = #()
        local queuedUpdateIds = #()
        local queuedUpdateVersions = #()
        local installedReadable = false
        local updatesReadable = false
        local lastDiagnosticIssue = "无"
        local lastDiagnosticCheck = "尚未检查"
        local currentJobId = undefined
        local currentToolLabel = ""
        local currentPage = 1
        -- 前向声明：showPage 需要引用后面定义的刷新函数
        local refreshDiscover, refreshInstalled, refreshUpdates, refreshEnvironment, refreshDiagnostics

        fn showPage page =
        (
            currentPage = page
            local showDiscover = (page == 1)
            local showInstalled = (page == 2)
            local showUpdates = (page == 3)
            local showEnvironment = (page == 4)
            local showDiagnostics = (page == 5)
            grpDiscover.visible = showDiscover
            lstTools.visible = showDiscover
            pbInstall.visible = (showDiscover and currentJobId != undefined)
            btnRefresh.visible = showDiscover
            btnInstall.visible = showDiscover
            grpInstalled.visible = showInstalled
            lstInstalled.visible = showInstalled
            btnUninstall.visible = showInstalled
            btnRollback.visible = showInstalled
            grpUpdates.visible = showUpdates
            lstUpdates.visible = showUpdates
            btnUpdateAll.visible = showUpdates
            btnUpdateSel.visible = showUpdates
            grpEnvironment.visible = showEnvironment
            lblEnvAgent.visible = showEnvironment
            lblEnvMax.visible = showEnvironment
            lblEnvInstalled.visible = showEnvironment
            lblEnvUpdates.visible = showEnvironment
            lblEnvPanel.visible = showEnvironment
            lblEnvNotice.visible = showEnvironment
            btnEnvironmentRefresh.visible = showEnvironment
            grpDiagnostics.visible = showDiagnostics
            lblDiagService.visible = showDiagnostics
            lblDiagMax.visible = showDiagnostics
            lblDiagInstalled.visible = showDiagnostics
            lblDiagUpdates.visible = showDiagnostics
            lblDiagChecked.visible = showDiagnostics
            lblDiagIssue.visible = showDiagnostics
            btnDiagnosticRefresh.visible = showDiagnostics
            btnCopyDiagnostics.visible = showDiagnostics
            if showInstalled then refreshInstalled()
            if showUpdates then refreshUpdates()
            if showEnvironment then refreshEnvironment()
            if showDiagnostics then refreshDiagnostics()
        )

        fn refreshDiscover =
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

        fn refreshInstalled =
        (
            installedIds = #(); installedVersions = #()
            installedReadable = false
            local items = #()
            if isAgentOnline() then (
                local body = httpGet (agentBaseUrl + "/max/installed?maxYear=" + (currentMaxYear() as string)) timeoutMs:10000
                if body != undefined then (
                    installedReadable = true
                    if body != "" then (
                        for line in (filterString body "\n") do (
                            local parts = filterString line "|"
                            if parts.count >= 2 then (
                                append installedIds parts[1]
                                append installedVersions parts[2]
                                append items (parts[1] + "  v" + parts[2])
                            )
                        )
                    )
                )
            )
            lstInstalled.items = items
            btnUninstall.enabled = (items.count > 0)
            btnRollback.enabled = (items.count > 0)
        )

        fn refreshUpdates =
        (
            updateIds = #(); updateVersions = #()
            updatesReadable = false
            local items = #()
            if isAgentOnline() then (
                local body = httpGet (agentBaseUrl + "/max/updates?maxYear=" + (currentMaxYear() as string)) timeoutMs:10000
                if body != undefined then (
                    updatesReadable = true
                    if body != "" then (
                        for line in (filterString body "\n") do (
                            local parts = filterString line "|"
                            if parts.count >= 4 then (
                                append updateIds parts[1]
                                append updateVersions parts[3]
                                append items (parts[4] + "  " + parts[2] + " → v" + parts[3])
                            )
                        )
                    )
                )
            )
            lstUpdates.items = items
            btnUpdateAll.enabled = (items.count > 0)
            btnUpdateSel.enabled = (items.count > 0)
        )

        fn refreshEnvironment =
        (
            lastDiagnosticCheck = (localTime as string)
            local year = currentMaxYear()
            local online = isAgentOnline()
            lblEnvMax.text = "当前：3ds Max " + (year as string)
            lblEnvPanel.text = "面板：本次会话已加载"
            if not online then (
                installedReadable = false
                updatesReadable = false
                lblEnvAgent.text = "Agent：未响应"
                lblEnvInstalled.text = "已安装：无法读取"
                lblEnvUpdates.text = "可更新：无法读取"
                lblEnvNotice.text = "Agent 未响应；请从 Windows 托盘启动 MaxHub Agent。"
                lastDiagnosticIssue = "Agent 未响应"
            ) else (
                refreshInstalled()
                refreshUpdates()
                lblEnvAgent.text = "Agent：已连接"
                lblEnvInstalled.text = if installedReadable then ("已安装：" + (installedIds.count as string) + " 个") else "已安装：无法读取"
                lblEnvUpdates.text = if updatesReadable then ("可更新：" + (updateIds.count as string) + " 个") else "可更新：无法读取"
                if installedReadable and updatesReadable then (
                    lblEnvNotice.text = if updateIds.count == 0 then "当前已是最新状态。" else "存在可更新工具，请到“更新”页处理。"
                    lastDiagnosticIssue = "无"
                ) else (
                    lblEnvNotice.text = "Agent 已连接，但无法读取工具状态；请稍后重试。"
                    lastDiagnosticIssue = "无法读取工具状态"
                )
            )
        )

        fn refreshDiagnostics =
        (
            refreshEnvironment()
            local year = currentMaxYear()
            lblDiagService.text = "服务：" + (if isAgentOnline() then "已连接" else "未响应")
            lblDiagMax.text = "当前 Max：" + (year as string)
            lblDiagInstalled.text = if installedReadable then ("已安装：" + (installedIds.count as string) + " 个") else "已安装：无法读取"
            lblDiagUpdates.text = if updatesReadable then ("可更新：" + (updateIds.count as string) + " 个") else "可更新：无法读取"
            lblDiagChecked.text = "检查时间：" + lastDiagnosticCheck
            lblDiagIssue.text = "最近问题：" + lastDiagnosticIssue
        )

        fn copyDiagnostics =
        (
            local summary = "MaxHub Connector\n3ds Max: " + (currentMaxYear() as string) + "\nAgent: " + (if isAgentOnline() then "online" else "offline") + "\nInstalled tools: " + (if installedReadable then (installedIds.count as string) else "unavailable") + "\nUpdates: " + (if updatesReadable then (updateIds.count as string) else "unavailable") + "\nLast check: " + lastDiagnosticCheck + "\nLast issue: " + lastDiagnosticIssue
            try (
                setClipboardText summary
                lblDiagIssue.text = "最近问题：诊断摘要已复制"
            ) catch (
                lblDiagIssue.text = "最近问题：无法访问系统剪贴板"
            )
        )

        fn startInstall toolId version =
        (
            rdoPages.state = 1
            showPage 1
            local result = httpPost (agentBaseUrl + "/max/install?toolId=" + toolId + "&version=" + version + "&maxYear=" + (currentMaxYear() as string))
            local parts = if result != undefined then (filterString result "|") else #()
            if parts.count >= 2 and parts[1] == "job" then (
                currentJobId = parts[2]
                currentToolLabel = toolId + " v" + version
                pbInstall.value = 0
                pbInstall.visible = true
                btnInstall.enabled = false
                btnUpdateAll.enabled = false
                btnUpdateSel.enabled = false
                lblStatus.text = "正在安装 " + currentToolLabel + " … 0%"
                pollTimer.active = true
            ) else (
                messageBox ("安装失败：" + (if result == undefined then "Agent 无响应" else result)) title:"MaxHub"
            )
        )

        fn installSelected =
        (
            if lstTools.selection == 0 or lstTools.selection > toolIds.count then (
                messageBox "请先在列表中选择一个工具。" title:"MaxHub"
            ) else (
                startInstall toolIds[lstTools.selection] toolVersions[lstTools.selection]
            )
        )

        fn updateSelected =
        (
            if lstUpdates.selection == 0 or lstUpdates.selection > updateIds.count then (
                messageBox "请先在列表中选择一个工具。" title:"MaxHub"
            ) else (
                startInstall updateIds[lstUpdates.selection] updateVersions[lstUpdates.selection]
            )
        )

        fn updateAll =
        (
            if updateIds.count == 0 then ( messageBox "没有可更新的工具。" title:"MaxHub" )
            else (
                queuedUpdateIds = copy updateIds
                queuedUpdateVersions = copy updateVersions
                startInstall queuedUpdateIds[1] queuedUpdateVersions[1]
            )
        )

        fn uninstallSelected =
        (
            if lstInstalled.selection == 0 or lstInstalled.selection > installedIds.count then (
                messageBox "请先在列表中选择一个工具。" title:"MaxHub"
            ) else (
                local toolId = installedIds[lstInstalled.selection]
                if (queryBox ("确定卸载 " + toolId + " 吗？") title:"MaxHub") then (
                    local result = httpPost (agentBaseUrl + "/max/uninstall?artifactId=" + toolId + "&maxYear=" + (currentMaxYear() as string))
                    local parts = if result != undefined then (filterString result "|") else #()
                    if parts.count >= 1 and parts[1] == "ok" then (
                        local conflicts = if parts.count >= 2 then (parts[2] as integer) else 0
                        local msg = "已卸载 " + toolId
                        if conflicts > 0 then msg += "\n注意：" + (conflicts as string) + " 个文件因被修改而保留，请人工处理。"
                        messageBox msg title:"MaxHub"
                    ) else (
                        messageBox ("卸载失败：" + (if result == undefined then "Agent 无响应" else result)) title:"MaxHub"
                    )
                    refreshInstalled()
                )
            )
        )

        fn rollbackSelected =
        (
            if lstInstalled.selection == 0 or lstInstalled.selection > installedIds.count then (
                messageBox "请先在列表中选择一个工具。" title:"MaxHub"
            ) else (
                local toolId = installedIds[lstInstalled.selection]
                if (queryBox ("确定将 " + toolId + " 回滚到上一版本吗？") title:"MaxHub") then (
                    local result = httpPost (agentBaseUrl + "/max/rollback?artifactId=" + toolId + "&maxYear=" + (currentMaxYear() as string))
                    local parts = if result != undefined then (filterString result "|") else #()
                    if parts.count >= 1 and parts[1] == "ok" then (
                        messageBox ("已回滚 " + toolId + " 到 v" + (if parts.count >= 2 then parts[2] else "上一版")) title:"MaxHub"
                    ) else (
                        messageBox ("回滚失败：" + (if result == undefined then "Agent 无响应" else result)) title:"MaxHub"
                    )
                    refreshInstalled()
                )
            )
        )

        fn finishInstall message success =
        (
            pollTimer.active = false
            currentJobId = undefined
            pbInstall.visible = false
            btnInstall.enabled = true
            btnUpdateAll.enabled = true
            btnUpdateSel.enabled = true
            local continueQueue = false
            if success and queuedUpdateIds.count > 0 then (
                deleteItem queuedUpdateIds 1
                deleteItem queuedUpdateVersions 1
                if queuedUpdateIds.count > 0 then (
                    continueQueue = true
                ) else (
                    messageBox "全部更新完成。" title:"MaxHub"
                )
            ) else if success then (
                messageBox ("安装成功：" + message + "\n脚本类工具立即可用，Startup 类工具重启 Max 后生效。") title:"MaxHub"
            ) else (
                queuedUpdateIds = #()
                queuedUpdateVersions = #()
                messageBox ("安装失败：" + message) title:"MaxHub"
            )
            if continueQueue then (
                startInstall queuedUpdateIds[1] queuedUpdateVersions[1]
            ) else (
                refreshDiscover()
                refreshInstalled()
                refreshUpdates()
            )
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

        on rdoPages changed val do showPage val
        on btnRefresh pressed do refreshDiscover()
        on btnInstall pressed do installSelected()
        on btnUninstall pressed do uninstallSelected()
        on btnRollback pressed do rollbackSelected()
        on btnUpdateSel pressed do updateSelected()
        on btnUpdateAll pressed do updateAll()
        on btnEnvironmentRefresh pressed do refreshEnvironment()
        on btnDiagnosticRefresh pressed do refreshDiagnostics()
        on btnCopyDiagnostics pressed do copyDiagnostics()
        on maxHubRollout open do ( refreshDiscover(); showPage 1 )
    )

    struct MaxHubConnectorStruct
    (
        fn show = createDialog maxHubRollout width:320 height:284,
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
