param([switch]$Standalone)
$ErrorActionPreference = 'Stop'
$projectPath = Join-Path $PSScriptRoot 'XenoCapture\XenoCapture.csproj'
if ($Standalone) {
    $publishPath = Join-Path $PSScriptRoot 'artifacts\standalone'
    & dotnet publish $projectPath -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o $publishPath --nologo
} else {
    $publishPath = Join-Path $PSScriptRoot 'artifacts\portable'
    & dotnet publish $projectPath -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -o $publishPath --nologo
}
if ($LASTEXITCODE -ne 0) { throw 'Xeno Capture build failed.' }
Write-Output (Join-Path $publishPath 'XenoCapture.exe')
