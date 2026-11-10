param([switch]$Standalone, [string]$AndroidUsbArchive)
$ErrorActionPreference = 'Stop'

function Open-VerifiedAndroidArchive([string]$path) {
    $file = [IO.File]::OpenRead((Resolve-Path -LiteralPath $path).Path)
    try {
        if ($file.Length -gt 16MB -or (Get-FileHash -InputStream $file -Algorithm SHA256).Hash -ne
            'B12A2C4EE8BE317422451FC7DCF8EE20A71B5EA7EF9AD73DDD825A25316227A5') {
            throw 'Use the verified official scrcpy 5.0.1 Windows x64 ZIP.'
        }
        $file.Position = 0
        Add-Type -AssemblyName System.IO.Compression
        return [IO.Compression.ZipArchive]::new($file, [IO.Compression.ZipArchiveMode]::Read)
    } catch {
        $file.Dispose()
        throw
    }
}

function Write-AndroidEntry($entry, [string]$folder) {
    $destination = Join-Path $folder $entry.Name
    $temporary = Join-Path $folder ([IO.Path]::GetRandomFileName())
    $created = $false
    try {
        $output = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew)
        $created = $true
        try {
            $inputStream = $entry.Open()
            try { $inputStream.CopyTo($output) }
            finally { $inputStream.Dispose() }
        } finally { $output.Dispose() }

        # Replace the directory entry so staging never writes through an existing hard link.
        if (Test-Path -LiteralPath $destination) {
            if ((Get-Item -LiteralPath $destination -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw 'Unexpected Android helper file link.'
            }
            [IO.File]::Replace($temporary, $destination, [NullString]::Value)
        } else {
            [IO.File]::Move($temporary, $destination)
        }
        $created = $false
    } finally {
        if ($created) { [IO.File]::Delete($temporary) }
    }
}

$projectPath = Join-Path $PSScriptRoot 'XenoCapture\XenoCapture.csproj'
$artifactsPath = Join-Path $PSScriptRoot 'artifacts'
$publishPath = Join-Path $artifactsPath $(if ($Standalone) { 'standalone' } else { 'portable' })
foreach ($directory in $artifactsPath, $publishPath) {
    if ((Test-Path -LiteralPath $directory) -and
        ((Get-Item -LiteralPath $directory -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Unexpected publish directory link.'
    }
}

$archive = $null
try {
    if ($AndroidUsbArchive) { $archive = Open-VerifiedAndroidArchive $AndroidUsbArchive }
    if ($Standalone) {
        & dotnet publish $projectPath -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o $publishPath --nologo
    } else {
        & dotnet publish $projectPath -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -o $publishPath --nologo
    }
    if ($LASTEXITCODE -ne 0) { throw 'Xeno Capture build failed.' }

    if ($archive) {
        $helperDirectory = [IO.Directory]::CreateDirectory((Join-Path $publishPath 'android'))
        if ($helperDirectory.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unexpected Android helper directory link.' }
        $entries = @($archive.Entries | Where-Object {
            $_.FullName -match '^scrcpy-win64-v5\.0\.1/[^/\\]+$' -and $_.Name -notmatch '\.(bat|vbs)$'
        })
        if ($entries.Count -ne 14) { throw 'The Android helper ZIP layout differs from the expected package.' }
        foreach ($entry in $entries) { Write-AndroidEntry $entry $helperDirectory.FullName }
    }
} finally {
    if ($archive) { $archive.Dispose() }
}
Write-Output (Join-Path $publishPath 'XenoCapture.exe')
