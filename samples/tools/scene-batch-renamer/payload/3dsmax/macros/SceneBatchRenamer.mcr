macroScript SceneBatchRenamer
category:"Company Tools"
buttonText:"Batch Rename"
tooltip:"Batch rename selected nodes"
(
    global sbrRename -- 宏体内未声明的名字是隐式局部，必须显式绑定全局
    fileIn ((getDir #userScripts) + "/SceneBatchRenamer.ms")
    sbrRename "SM_" ""
)
