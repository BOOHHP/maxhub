-- Startup Env Check: verify team-required scene settings at Max startup.
(
    if units.SystemType != #Centimeters do
        format "EnvCheck WARNING: system unit is %, expected centimeters\n" units.SystemType
    if not IDisplayGamma.colorCorrectionMode == #gamma do
        format "EnvCheck WARNING: gamma correction disabled\n"
)
