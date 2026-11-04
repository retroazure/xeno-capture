param([string]$Compiler = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.22621.0\x64\fxc.exe')
$ErrorActionPreference = 'Stop'
$shaderDirectory = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\XenoCapture\Shaders')).Path
if (-not (Test-Path -LiteralPath $Compiler -PathType Leaf)) { throw 'Pass the x64 Windows SDK fxc.exe path with -Compiler.' }
foreach ($entry in @('Downsample','Compare','Motion','Hardware','Synthesize','SynthesizeSlow')) {
    & $Compiler /nologo /T cs_5_0 /E $entry /O3 /Fo (Join-Path $shaderDirectory ('Frame' + $entry + '.cso')) (Join-Path $shaderDirectory 'FrameInterpolation.hlsl')
    if ($LASTEXITCODE -ne 0) { throw ('Shader compilation failed: ' + $entry) }
}
foreach ($shader in @(
    @{ Name = 'PreviewVertex'; Profile = 'vs_4_0'; Entry = 'Vertex'; Defines = @() }
    @{ Name = 'PreviewPixel'; Profile = 'ps_4_0'; Entry = 'Preview'; Defines = @() }
    @{ Name = 'PreviewAiPixel'; Profile = 'ps_4_0'; Entry = 'Preview'; Defines = @('/D', 'AI_RESAMPLING=1') }
)) {
    $defines = $shader.Defines
    & $Compiler /nologo /T $shader.Profile /E $shader.Entry /O3 @defines /Fo (Join-Path $shaderDirectory ($shader.Name + '.cso')) (Join-Path $shaderDirectory 'Preview.hlsl')
    if ($LASTEXITCODE -ne 0) { throw ('Shader compilation failed: ' + $shader.Name) }
}
Get-ChildItem -LiteralPath $shaderDirectory -File | Where-Object Name -ne 'SHA256SUMS.txt' | ForEach-Object { $_.Name + ' ' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } | Set-Content -LiteralPath (Join-Path $shaderDirectory 'SHA256SUMS.txt')
