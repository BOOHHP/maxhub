# Quick Exporter: export current selection as FBX to the project export folder.
from pymxs import runtime as rt


def export_selection(output_dir):
    if rt.selection.count == 0:
        print("QuickExporter: nothing selected")
        return
    name = rt.selection[0].name
    target = f"{output_dir}/{name}.fbx"
    rt.exportFile(target, rt.Name("noPrompt"), selectedOnly=True)
    print(f"QuickExporter: exported {target}")
