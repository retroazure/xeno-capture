param([switch]$Standalone, [string]$AndroidUsbArchive)
$ErrorActionPreference = 'Stop'
$projectPath = Join-Path $PSScriptRoot 'XenoCapture\XenoCapture.csproj'
if ($AndroidUsbArchive) {
    $AndroidUsbArchive = (Resolve-Path -LiteralPath $AndroidUsbArchive).Path
    $archiveHash = (Get-FileHash -LiteralPath $AndroidUsbArchive -Algorithm SHA256).Hash
    if ($archiveHash -ne 'B12A2C4EE8BE317422451FC7DCF8EE20A71B5EA7EF9AD73DDD825A25316227A5') {
        throw 'Use the verified official scrcpy 5.0.1 Windows x64 ZIP.'
    }
}
if ($Standalone) {
    $publishPath = Join-Path $PSScriptRoot 'artifacts\standalone'
    & dotnet publish $projectPath -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o $publishPath --nologo
} else {
    $publishPath = Join-Path $PSScriptRoot 'artifacts\portable'
    & dotnet publish $projectPath -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -o $publishPath --nologo
}
if ($LASTEXITCODE -ne 0) { throw 'Xeno Capture build failed.' }
if ($AndroidUsbArchive) {
    $helperFolder = Join-Path $publishPath 'android'
    $publishDirectory = Get-Item -LiteralPath $publishPath -Force
    if ($publishDirectory.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unexpected publish directory link.' }
    $helperDirectory = [IO.Directory]::CreateDirectory($helperFolder)
    if ($helperDirectory.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unexpected Android helper directory link.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($AndroidUsbArchive)
    try {
        $entries = @($archive.Entries | Where-Object {
            $_.FullName -match '^scrcpy-win64-v5\.0\.1/[^/\\]+$' -and $_.Name -notmatch '\.(bat|vbs)$'
        })
        if ($entries.Count -ne 14) { throw 'The Android helper ZIP layout differs from the expected package.' }
        foreach ($entry in $entries) {
            $destination = Join-Path $helperFolder $entry.Name
            if ((Test-Path -LiteralPath $destination) -and
                ((Get-Item -LiteralPath $destination -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw 'Unexpected Android helper file link.'
            }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destination, $true)
        }
    } finally { $archive.Dispose() }
}
Write-Output (Join-Path $publishPath 'XenoCapture.exe')
