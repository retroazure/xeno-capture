param([string]$Compiler = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.22621.0\x64\fxc.exe')
$ErrorActionPreference = 'Stop'
# Regenerate only the vendored shader and coefficients. No downloads or installers.
$nisDirectory = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\XenoCapture\ThirdParty\NIS')).Path
if (-not (Test-Path -LiteralPath $Compiler -PathType Leaf)) { throw 'Pass the path to the x64 Windows SDK fxc.exe with -Compiler.' }
$configText = [IO.File]::ReadAllText((Join-Path $nisDirectory 'NIS_Config.h'))
foreach ($table in @('scale','usm')) {
    $tablePattern = 'constexpr float coef_' + $table + '\[kPhaseCount\]\[kFilterSize\]\s*=\s*\{([\s\S]*?)\};'
    $tableMatch = [regex]::Match($configText,$tablePattern)
    if (-not $tableMatch.Success) { throw ('Missing coefficient table: ' + $table) }
    $coefficientMatches = [regex]::Matches($tableMatch.Groups[1].Value,'-?\d+(?:\.\d+)?f')
    if ($coefficientMatches.Count -ne 512) { throw 'Unexpected coefficient layout; review the upstream update.' }
    $coefficients = [single[]]::new(512)
    for ($index=0; $index -lt 512; $index++) { $coefficients[$index] = [single]::Parse($coefficientMatches[$index].Value.TrimEnd('f'),[Globalization.CultureInfo]::InvariantCulture) }
    $bytes = [byte[]]::new(2048); [Buffer]::BlockCopy($coefficients,0,$bytes,0,2048)
    [IO.File]::WriteAllBytes((Join-Path $nisDirectory ('nis-' + $table + '.bin')),$bytes)
}
& $Compiler /nologo /T cs_5_0 /E main /O3 /D NIS_SCALER=1 /D NIS_HDR_MODE=0 /D NIS_USE_HALF_PRECISION=0 /D NIS_BLOCK_WIDTH=32 /D NIS_BLOCK_HEIGHT=24 /D NIS_THREAD_GROUP_SIZE=256 /D NIS_CLAMP_OUTPUT=1 /D NIS_NV12_SUPPORT=0 /D NIS_VIEWPORT_SUPPORT=0 /Fo (Join-Path $nisDirectory 'NIS.cso') (Join-Path $nisDirectory 'NIS_Main.hlsl')
if ($LASTEXITCODE -ne 0) { throw 'NIS shader compilation failed.' }
Get-ChildItem -LiteralPath $nisDirectory -File | Where-Object Name -ne 'SHA256SUMS.txt' | ForEach-Object { $_.Name + ' ' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } | Set-Content -LiteralPath (Join-Path $nisDirectory 'SHA256SUMS.txt')
Write-Output 'NIS bytecode and coefficient resources regenerated. Rebuild and run --upscaling-tests.'
