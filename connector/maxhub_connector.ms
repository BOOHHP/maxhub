-- MaxHub Connector (MaxScript, 无 SDK 依赖)
-- 由 Agent 的 startup 加载脚本 fileIn；一套脚本覆盖 Max 2019-2026。
-- 薄客户端约束：不保存长期令牌、不下载解压任意包、不做覆盖式更新。

global MaxHubConnector
(
    local agentBaseUrl = "http://127.0.0.1:47810" -- Agent 本地接口，仅回环
    -- 主 rollout 的按钮事件会引用后面定义的反馈弹窗入口，必须先声明作用域变量
    local openMaxHubFeedback

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

    fn encodeBase64 value =
    (
        try (
            local bytes = (dotNetClass "System.Text.Encoding").UTF8.GetBytes value
            (dotNetClass "System.Convert").ToBase64String bytes
        ) catch ( "" )
    )

    fn httpPostJson url body timeoutMs:10000 =
    (
        try (
            local request = (dotNetClass "System.Net.WebRequest").Create url
            request.Method = "POST"
            request.ContentType = "application/json"
            request.Timeout = timeoutMs
            local bytes = (dotNetClass "System.Text.Encoding").UTF8.GetBytes body
            request.ContentLength = bytes.Length
            local stream = request.GetRequestStream()
            stream.Write bytes 0 bytes.Length
            stream.Close()
            local response = request.GetResponse()
            local reader = dotNetObject "System.IO.StreamReader" (response.GetResponseStream())
            local result = reader.ReadToEnd()
            reader.Close(); response.Close()
            result
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

    fn decodeBase64 value =
    (
        try (
            local bytes = (dotNetClass "System.Convert").FromBase64String value
            (dotNetClass "System.Text.Encoding").UTF8.GetString bytes
        ) catch ( "" )
    )

    rollout maxHubRollout "MaxHub 工具中心"
    (
        -- 页签：checkbutton 有按下态，作分区切换按钮
        checkbutton tabDiscover "发现" pos:[10,8] width:102 height:28 checked:true
        checkbutton tabInstalled "已安装" pos:[116,8] width:102 height:28
        checkbutton tabUpdates "更新" pos:[222,8] width:102 height:28
        checkbutton tabEnvironment "环境" pos:[328,8] width:102 height:28
        checkbutton tabDiagnostics "诊断" pos:[434,8] width:102 height:28

        -- ===== 发现页 =====
        label lblStatus "正在连接 MaxHub Agent…" pos:[12,46] width:520 align:#left
        label lblDiscoverTitle "可用工具（按分类排列）" pos:[12,64] width:524 align:#left
        listbox lstTools pos:[12,84] width:524 height:12
        groupBox grpToolDescription "工具详情" pos:[12,278] width:524 height:98
        editText txtToolDescription "" pos:[20,298] width:508 height:70 readOnly:true text:"请选择一个工具查看说明。"
        button btnFeedback "💬 反馈" pos:[130,398] width:100 height:28 visible:false
        progressBar pbInstall "" pos:[12,382] width:524 height:8 value:0 visible:false color:(color 76 159 224)
        button btnRefresh "刷新列表" pos:[12,398] width:110 height:28 align:#left
        button btnInstall "安装选中" pos:[426,398] width:110 height:28 align:#right

        -- ===== 已安装页 =====
        label lblInstalledTitle "已安装工具（双击运行）" pos:[12,46] width:524 visible:false align:#left
        listbox lstInstalled pos:[12,66] width:524 height:19 visible:false
        button btnRun "▶ 运行选中" pos:[12,398] width:110 height:28 visible:false
        button btnUninstall "卸载选中" pos:[310,398] width:110 height:28 visible:false
        button btnRollback "回滚上一版" pos:[426,398] width:110 height:28 visible:false

        -- ===== 更新页 =====
        label lblUpdatesTitle "可更新工具" pos:[12,46] width:524 visible:false align:#left
        listbox lstUpdates pos:[12,66] width:524 height:19 visible:false
        button btnUpdateAll "全部更新" pos:[12,398] width:110 height:28 visible:false
        button btnUpdateSel "更新选中" pos:[426,398] width:110 height:28 visible:false

        -- ===== 环境页 =====
        groupBox grpEnvironment "环境状态" pos:[12,46] width:524 height:220 visible:false
        label lblEnvAgent "Agent：检查中…" pos:[26,72] width:490 visible:false align:#left
        label lblEnvMax "当前：3ds Max" pos:[26,98] width:490 visible:false align:#left
        label lblEnvInstalled "已安装：检查中…" pos:[26,124] width:490 visible:false align:#left
        label lblEnvUpdates "可更新：检查中…" pos:[26,150] width:490 visible:false align:#left
        label lblEnvPanel "面板：本次会话已加载" pos:[26,176] width:490 visible:false align:#left
        label lblEnvNotice "" pos:[26,206] width:490 height:40 visible:false align:#left
        button btnEnvironmentRefresh "重新检查" pos:[12,398] width:110 height:28 visible:false

        -- ===== 诊断页 =====
        groupBox grpDiagnostics "诊断摘要" pos:[12,46] width:524 height:220 visible:false
        label lblDiagService "服务：检查中…" pos:[26,72] width:490 visible:false align:#left
        label lblDiagMax "当前 Max：检查中…" pos:[26,98] width:490 visible:false align:#left
        label lblDiagInstalled "已安装：检查中…" pos:[26,124] width:490 visible:false align:#left
        label lblDiagUpdates "可更新：检查中…" pos:[26,150] width:490 visible:false align:#left
        label lblDiagChecked "检查时间：尚未检查" pos:[26,176] width:490 visible:false align:#left
        label lblDiagIssue "最近问题：无" pos:[26,206] width:490 height:40 visible:false align:#left
        button btnDiagnosticRefresh "重新检查" pos:[12,398] width:110 height:28 visible:false
        button btnCopyDiagnostics "复制摘要" pos:[426,398] width:110 height:28 visible:false

        timer pollTimer interval:200 active:false

        -- 数据缓存
        local toolIds = #()
        local toolNames = #()
        local toolVersions = #()
        local toolDetails = #()
        local installedIds = #()
        local installedNames = #()
        local installedVersions = #()
        local installedDetails = #()
        local updateIds = #()
        local updateNames = #()
        local updateVersions = #()
        local updateDetails = #()
        local queuedUpdateIds = #()
        local queuedUpdateVersions = #()
        local installedReadable = false
        local updatesReadable = false
        local lastDiagnosticIssue = "无"
        local lastDiagnosticCheck = "尚未检查"
        local currentJobId = undefined
        local currentToolLabel = ""
        local currentPage = 1
        local resizingToMinimum = false
        -- 前向声明：showPage 需要引用后面定义的刷新函数
        local refreshDiscover, refreshInstalled, refreshUpdates, refreshEnvironment, refreshDiagnostics

        fn showPage page =
        (
            currentPage = page
            tabDiscover.checked = (page == 1)
            tabInstalled.checked = (page == 2)
            tabUpdates.checked = (page == 3)
            tabEnvironment.checked = (page == 4)
            tabDiagnostics.checked = (page == 5)
            local showDiscover = (page == 1)
            local showInstalled = (page == 2)
            local showUpdates = (page == 3)
            local showEnvironment = (page == 4)
            local showDiagnostics = (page == 5)
            lblStatus.visible = showDiscover
            lblDiscoverTitle.visible = showDiscover
            lstTools.visible = showDiscover
            grpToolDescription.visible = (showDiscover or showInstalled or showUpdates)
            txtToolDescription.visible = (showDiscover or showInstalled or showUpdates)
            pbInstall.visible = (showDiscover and currentJobId != undefined)
            btnRefresh.visible = showDiscover
            btnInstall.visible = showDiscover
            btnFeedback.visible = (showDiscover or showInstalled or showUpdates)
            lstInstalled.visible = showInstalled
            lblInstalledTitle.visible = showInstalled
            btnRun.visible = showInstalled
            btnUninstall.visible = showInstalled
            btnRollback.visible = showInstalled
            lstUpdates.visible = showUpdates
            lblUpdatesTitle.visible = showUpdates
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

        -- 自适应布局：窗口拉伸时列表撑满、按钮吸底
        fn layoutControls size =
        (
            local w = if size.x < 480 then 480 else (size.x as integer)
            local h = if size.y < 400 then 400 else (size.y as integer)
            if not resizingToMinimum and (size.x < 480 or size.y < 400) then (
                resizingToMinimum = true
                setDialogSize maxHubRollout [w, h]
                resizingToMinimum = false
            )
            local margin = 12
            local contentW = w - margin * 2
            local btnY = h - 40
            local progressY = btnY - 16
            local groupHeight = btnY - 58
            local detailWidth = contentW - 28
            local environmentDetailHeight = groupHeight - 178
            if environmentDetailHeight < 40 then environmentDetailHeight = 40
            local tabW = ((w - 20 - 16) / 5) as integer
            tabDiscover.pos = [10, 8]; tabDiscover.width = tabW
            tabInstalled.pos = [10 + (tabW + 4), 8]; tabInstalled.width = tabW
            tabUpdates.pos = [10 + (tabW + 4) * 2, 8]; tabUpdates.width = tabW
            tabEnvironment.pos = [10 + (tabW + 4) * 3, 8]; tabEnvironment.width = tabW
            tabDiagnostics.pos = [10 + (tabW + 4) * 4, 8]; tabDiagnostics.width = tabW
            -- listbox 声明时 height 是行数，运行时 .height 是像素；直接按可用像素撑满
            local detailHeight = 88 + (((h - 400) / 4) as integer)
            if detailHeight > 140 then detailHeight = 140
            local detailY = progressY - 8 - detailHeight
            local discoverListHeight = detailY - 8 - 84
            local categorizedListHeight = detailY - 8 - 66
            if discoverListHeight < 80 then discoverListHeight = 80
            if categorizedListHeight < 98 then categorizedListHeight = 98
            lblDiscoverTitle.pos = [margin, 64]; lblDiscoverTitle.width = contentW
            lstTools.pos = [margin, 84]; lstTools.width = contentW; lstTools.height = discoverListHeight
            grpToolDescription.pos = [margin, detailY]; grpToolDescription.width = contentW; grpToolDescription.height = detailHeight
            txtToolDescription.pos = [margin + 8, detailY + 20]
            txtToolDescription.width = contentW - 16; txtToolDescription.height = detailHeight - 28
            lblInstalledTitle.pos = [margin, 46]; lblInstalledTitle.width = contentW
            lstInstalled.pos = [margin, 66]; lstInstalled.width = contentW; lstInstalled.height = categorizedListHeight
            lblUpdatesTitle.pos = [margin, 46]; lblUpdatesTitle.width = contentW
            lstUpdates.pos = [margin, 66]; lstUpdates.width = contentW; lstUpdates.height = categorizedListHeight
            lblStatus.pos = [margin, 46]; lblStatus.width = contentW
            pbInstall.pos = [margin, progressY]
            -- 部分 Max 版本的 ProgressBar 运行时不支持 width，失败时保留声明宽度，避免重开面板抛异常
            try ( pbInstall.width = contentW ) catch ()
            btnRefresh.pos = [margin, btnY]
            btnFeedback.pos = [margin + 118, btnY]
            btnInstall.pos = [w - margin - 110, btnY]
            btnRun.pos = [margin, btnY]
            btnUninstall.pos = [w - margin - 226, btnY]
            btnRollback.pos = [w - margin - 110, btnY]
            btnUpdateAll.pos = [margin, btnY]
            btnUpdateSel.pos = [w - margin - 110, btnY]
            grpEnvironment.pos = [margin, 46]; grpEnvironment.width = contentW; grpEnvironment.height = groupHeight
            lblEnvAgent.width = detailWidth; lblEnvMax.width = detailWidth
            lblEnvInstalled.width = detailWidth; lblEnvUpdates.width = detailWidth; lblEnvPanel.width = detailWidth
            lblEnvNotice.width = detailWidth; lblEnvNotice.height = environmentDetailHeight
            grpDiagnostics.pos = [margin, 46]; grpDiagnostics.width = contentW; grpDiagnostics.height = groupHeight
            lblDiagService.width = detailWidth; lblDiagMax.width = detailWidth
            lblDiagInstalled.width = detailWidth; lblDiagUpdates.width = detailWidth; lblDiagChecked.width = detailWidth
            lblDiagIssue.width = detailWidth; lblDiagIssue.height = environmentDetailHeight
            btnEnvironmentRefresh.pos = [margin, btnY]
            btnDiagnosticRefresh.pos = [margin, btnY]
            btnCopyDiagnostics.pos = [w - margin - 110, btnY]
        )

        fn refreshDiscover =
        (
            toolIds = #(); toolNames = #(); toolVersions = #(); toolDetails = #()
            txtToolDescription.text = "请选择一个工具查看说明。"
            if isAgentOnline() then (
                local year = currentMaxYear()
                lblStatus.text = "Agent 已连接 | Max " + (year as string)
                local body = httpGet (agentBaseUrl + "/max/tools-v2?maxYear=" + (year as string)) timeoutMs:10000
                local useV2 = (body != undefined)
                if not useV2 then body = httpGet (agentBaseUrl + "/max/tools?maxYear=" + (year as string)) timeoutMs:10000
                local items = #()
                if body != undefined and body != "" then (
                    for line in (filterString body "\n") do (
                        local parts = filterString line "|"
                        if (useV2 and parts.count >= 5) or ((not useV2) and parts.count >= 3) then (
                            local toolName = if useV2 then (decodeBase64 parts[3]) else parts[3]
                            local category = if useV2 then (decodeBase64 parts[4]) else "其他"
                            local description = if useV2 then (decodeBase64 parts[5]) else "暂无说明。"
                            local minYear = if useV2 and parts.count >= 7 then parts[6] else (year as string)
                            local maxYear = if useV2 and parts.count >= 7 then parts[7] else (year as string)
                            local rangeText = "Max " + minYear + "–" + maxYear
                            append toolIds parts[1]
                            append toolNames toolName
                            append toolVersions parts[2]
                            append toolDetails ("工具名称：" + toolName + "\r\n分类：" + category + "    版本：v" + parts[2] + "    适用：" + rangeText + "\r\n\r\n" + description)
                            append items ("[" + category + "]  " + toolName + "  v" + parts[2] + "  [" + minYear + "–" + maxYear + "]")
                        )
                    )
                )
                lstTools.items = items
                btnInstall.enabled = true
            ) else (
                lblStatus.text = "Agent 离线 — 请先启动 MaxHub Agent（不可安装）"
                lstTools.items = #()
                txtToolDescription.text = "Agent 离线，暂时无法读取工具说明。"
                btnInstall.enabled = false
            )
        )

        fn refreshInstalled =
        (
            installedIds = #(); installedNames = #(); installedVersions = #(); installedDetails = #()
            installedReadable = false
            txtToolDescription.text = "请选择一个已安装工具查看说明。"
            local items = #()
            if isAgentOnline() then (
                local year = currentMaxYear()
                local body = httpGet (agentBaseUrl + "/max/installed-v2?maxYear=" + (year as string)) timeoutMs:10000
                local useV2 = (body != undefined)
                if not useV2 then body = httpGet (agentBaseUrl + "/max/installed?maxYear=" + (year as string)) timeoutMs:10000
                if body != undefined then (
                    installedReadable = true
                    if body != "" then (
                        for line in (filterString body "\n") do (
                            local parts = filterString line "|"
                            if (useV2 and parts.count >= 7) or ((not useV2) and parts.count >= 2) then (
                                append installedIds parts[1]
                                append installedVersions parts[2]
                                local displayName = if useV2 then (decodeBase64 parts[3]) else (if parts.count >= 3 then parts[3] else "未命名工具")
                                append installedNames displayName
                                local category = if useV2 then (decodeBase64 parts[4]) else "其他"
                                local description = if useV2 then (decodeBase64 parts[5]) else "暂无说明。"
                                local minYear = if useV2 then parts[6] else (year as string)
                                local maxYear = if useV2 then parts[7] else (year as string)
                                append installedDetails ("工具名称：" + displayName + "\r\n分类：" + category + "    已安装：v" + parts[2] + "    适用：Max " + minYear + "–" + maxYear + "\r\n\r\n" + description)
                                append items ("[" + category + "]  " + displayName + "  v" + parts[2] + "  [" + minYear + "–" + maxYear + "]")
                            )
                        )
                    )
                )
            )
            lstInstalled.items = items
            btnRun.enabled = (items.count > 0)
            btnUninstall.enabled = (items.count > 0)
            btnRollback.enabled = (items.count > 0)
        )

        fn refreshUpdates =
        (
            updateIds = #(); updateNames = #(); updateVersions = #(); updateDetails = #()
            updatesReadable = false
            txtToolDescription.text = "请选择一个可更新工具查看说明。"
            local items = #()
            if isAgentOnline() then (
                local year = currentMaxYear()
                local body = httpGet (agentBaseUrl + "/max/updates-v2?maxYear=" + (year as string)) timeoutMs:10000
                local useV2 = (body != undefined)
                if not useV2 then body = httpGet (agentBaseUrl + "/max/updates?maxYear=" + (year as string)) timeoutMs:10000
                if body != undefined then (
                    updatesReadable = true
                    if body != "" then (
                        for line in (filterString body "\n") do (
                            local parts = filterString line "|"
                            if (useV2 and parts.count >= 8) or ((not useV2) and parts.count >= 4) then (
                                append updateIds parts[1]
                                append updateVersions parts[3]
                                local displayName = if useV2 then (decodeBase64 parts[4]) else parts[4]
                                append updateNames displayName
                                local category = if useV2 then (decodeBase64 parts[5]) else "其他"
                                local description = if useV2 then (decodeBase64 parts[6]) else "暂无说明。"
                                local minYear = if useV2 then parts[7] else (year as string)
                                local maxYear = if useV2 then parts[8] else (year as string)
                                append updateDetails ("工具名称：" + displayName + "\r\n分类：" + category + "    更新：v" + parts[2] + " → v" + parts[3] + "    适用：Max " + minYear + "–" + maxYear + "\r\n\r\n" + description)
                                append items ("[" + category + "]  " + displayName + "  " + parts[2] + " → " + parts[3] + "  [" + minYear + "–" + maxYear + "]")
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

        -- 运行选中工具：向 Agent 查询脚本入口路径，.ms 用 fileIn，.py 用 Max 内置 Python
        fn runSelected =
        (
            if lstInstalled.selection == 0 or lstInstalled.selection > installedIds.count then (
                messageBox "请先在列表中选择一个工具。" title:"MaxHub"
            ) else (
                local toolId = installedIds[lstInstalled.selection]
                local body = httpGet (agentBaseUrl + "/max/run-info?artifactId=" + toolId + "&maxYear=" + (currentMaxYear() as string)) timeoutMs:5000
                local parts = if body != undefined then (filterString body "|") else #()
                if parts.count >= 3 and parts[1] == "ok" then (
                    local scriptType = parts[2]
                    local scriptPath = parts[3]
                    try (
                        if scriptType == "py" then ( python.ExecuteFile scriptPath )
                        else ( fileIn scriptPath )
                    ) catch (
                        messageBox ("运行出错：" + (getCurrentException())) title:"MaxHub"
                    )
                ) else (
                    local err = if parts.count >= 2 then parts[2] else "Agent 无响应"
                    messageBox ("无法运行：" + err) title:"MaxHub"
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

        on tabDiscover changed state do showPage 1
        on tabInstalled changed state do showPage 2
        on tabUpdates changed state do showPage 3
        on tabEnvironment changed state do showPage 4
        on tabDiagnostics changed state do showPage 5
        on lstTools selected sel do
        (
            if sel > 0 and sel <= toolDetails.count then
                txtToolDescription.text = toolDetails[sel]
            else
                txtToolDescription.text = "请选择一个工具查看说明。"
        )
        on lstInstalled selected sel do
        (
            if sel > 0 and sel <= installedDetails.count then
                txtToolDescription.text = installedDetails[sel]
            else
                txtToolDescription.text = "请选择一个已安装工具查看说明。"
        )
        on lstUpdates selected sel do
        (
            if sel > 0 and sel <= updateDetails.count then
                txtToolDescription.text = updateDetails[sel]
            else
                txtToolDescription.text = "请选择一个可更新工具查看说明。"
        )
        on btnRefresh pressed do refreshDiscover()
        on btnInstall pressed do installSelected()
        on btnFeedback pressed do
        (
            local toolId = ""; local toolName = ""
            -- case 的左括号必须与 of 同行，否则脚本加载即编译失败
            case currentPage of (
                1: (
                    if lstTools.selection > 0 and lstTools.selection <= toolIds.count then (
                        toolId = toolIds[lstTools.selection]
                        toolName = toolNames[lstTools.selection]
                    )
                )
                2: (
                    if lstInstalled.selection > 0 and lstInstalled.selection <= installedIds.count then (
                        toolId = installedIds[lstInstalled.selection]
                        toolName = installedNames[lstInstalled.selection]
                    )
                )
                3: (
                    if lstUpdates.selection > 0 and lstUpdates.selection <= updateIds.count then (
                        toolId = updateIds[lstUpdates.selection]
                        toolName = updateNames[lstUpdates.selection]
                    )
                )
                default: ()
            )
            if toolId == "" then ( messageBox "请先在列表中选择一个工具。" title:"MaxHub" )
            else ( openMaxHubFeedback toolId toolName )
        )
        on btnRun pressed do runSelected()
        on lstInstalled doubleClicked sel do runSelected()
        on btnUninstall pressed do uninstallSelected()
        on btnRollback pressed do rollbackSelected()
        on btnUpdateSel pressed do updateSelected()
        on btnUpdateAll pressed do updateAll()
        on btnEnvironmentRefresh pressed do refreshEnvironment()
        on btnDiagnosticRefresh pressed do refreshDiagnostics()
        on btnCopyDiagnostics pressed do copyDiagnostics()
        on maxHubRollout resized size do layoutControls size
        on maxHubRollout open do ( layoutControls [548, 440]; refreshDiscover(); showPage 1 )
    )

    -- ===== 反馈弹窗：针对当前选中工具，经 Agent 转发并由服务器投飞书 =====
    global maxHubFeedbackToolId = ""

    rollout maxHubFeedbackRollout "工具反馈"
    (
        label lblFeedbackTarget "对象：未选择工具" pos:[12,14] width:356 align:#left
        editText txtFeedback "" pos:[12,40] width:356 height:96 text:""
        label lblFeedbackStatus "" pos:[12,142] width:356 height:30 align:#left
        button btnFeedbackSubmit "提交反馈" pos:[12,178] width:100 height:26
        button btnFeedbackClose "关闭" pos:[268,178] width:100 height:26

        on btnFeedbackSubmit pressed do
        (
            local message = txtFeedback.text
            if message.count < 5 then ( lblFeedbackStatus.text = "✕ 反馈内容需 5-2000 字。" )
            else if message.count > 2000 then ( lblFeedbackStatus.text = "✕ 反馈内容需 5-2000 字。" )
            else (
                lblFeedbackStatus.text = "◌ 提交中…"
                -- Base64 避免在 MaxScript 中手写 JSON 字符转义（字符字面量跨版本兼容性差）
                local body = "{\"messageBase64\":\"" + (encodeBase64 message) + "\"}"
                local result = httpPostJson (agentBaseUrl + "/max/feedback?toolId=" + maxHubFeedbackToolId + "&maxYear=" + (currentMaxYear() as string)) body
                local parts = if result != undefined then (filterString result "|") else #()
                if parts.count >= 1 and parts[1] == "ok" then (
                    lblFeedbackStatus.text = "✓ 已提交，上传者会收到飞书消息。"
                    txtFeedback.text = ""
                ) else if parts.count >= 2 then (
                    lblFeedbackStatus.text = "✕ " + parts[2]
                ) else (
                    lblFeedbackStatus.text = "✕ MaxHub Agent 未响应，请先启动 Agent。"
                )
            )
        )

        on btnFeedbackClose pressed do destroyDialog maxHubFeedbackRollout
    )

    -- 全局入口：主 rollout 的反馈按钮调用；弹窗已打开则只刷新对象
    fn openMaxHubFeedback toolId toolName =
    (
        maxHubFeedbackToolId = toolId
        -- rollout 没有稳定的通用 isValid 判断；旧弹窗存在则关闭，不存在时忽略异常
        try ( destroyDialog maxHubFeedbackRollout ) catch ()
        createDialog maxHubFeedbackRollout width:380 height:216 \
            style:#(#style_titlebar, #style_border, #style_sysmenu)
        maxHubFeedbackRollout.lblFeedbackTarget.text = "对象：" + toolName
        maxHubFeedbackRollout.txtFeedback.text = ""
        maxHubFeedbackRollout.lblFeedbackStatus.text = ""
    )

    struct MaxHubConnectorStruct
    (
        fn show = createDialog maxHubRollout width:548 height:440 \
            style:#(#style_titlebar, #style_border, #style_sysmenu, #style_resizing),
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
