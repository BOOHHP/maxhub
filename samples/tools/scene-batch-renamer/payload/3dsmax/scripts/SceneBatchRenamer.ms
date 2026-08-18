-- Scene Batch Renamer: rename selected nodes with prefix/suffix/index template.
fn sbrRename prefixStr suffixStr = (
    local idx = 1
    for obj in selection do (
        obj.name = prefixStr + obj.name + suffixStr + "_" + (formattedPrint idx format:"03d")
        idx += 1
    )
    format "SceneBatchRenamer: renamed % nodes\n" (selection.count)
)
