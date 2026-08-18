macroScript SceneBatchRenamer
category:"Company Tools"
buttonText:"Batch Rename"
tooltip:"Batch rename selected nodes"
(
    fileIn ((getDir #userScripts) + "/SceneBatchRenamer.ms")
    sbrRename "SM_" ""
)
