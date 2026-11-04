param([string]$SdkRoot,[string]$OutputDirectory,[switch]$Probe)
$ErrorActionPreference='Stop'
if(-not $SdkRoot) { $SdkRoot=Join-Path $PSScriptRoot '..\artifacts\vendor-sdk\rtx-video-1.1.0' }
if(-not $OutputDirectory) { $OutputDirectory=Join-Path $PSScriptRoot '..\artifacts\gpu-bridge' }
$SdkRoot=(Resolve-Path -LiteralPath $SdkRoot).Path
New-Item -ItemType Directory -Path $OutputDirectory -Force|Out-Null; $OutputDirectory=(Resolve-Path -LiteralPath $OutputDirectory).Path
$vs='C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\MSVC\14.44.35207'
$kit='C:\Program Files (x86)\Windows Kits\10'; $version='10.0.22621.0'
$compiler=Join-Path $vs 'bin\Hostx64\x64\cl.exe'; $source=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\XenoCapture\Native\XenoGpuBridge.cpp')).Path
$arguments=@('/nologo','/std:c++17','/EHsc','/O2','/MT','/DNDEBUG',('/I'+(Join-Path $vs 'include')),('/I'+(Join-Path $kit ('Include\'+$version+'\ucrt'))),('/I'+(Join-Path $kit ('Include\'+$version+'\shared'))),('/I'+(Join-Path $kit ('Include\'+$version+'\um'))),('/I'+(Join-Path $kit ('Include\'+$version+'\winrt'))),('/I'+(Join-Path $SdkRoot 'include')),('/I'+(Join-Path $PSScriptRoot '..\XenoCapture\ThirdParty\NVOF')),('/Fo'+(Join-Path $OutputDirectory 'XenoGpuBridge.obj')))
if($Probe) { $arguments+='/DXENO_GPU_PROBE'; $arguments+=('/Fe'+(Join-Path $OutputDirectory 'XenoGpuProbe.exe')) } else { $arguments+='/LD'; $arguments+=('/Fe'+(Join-Path $OutputDirectory 'XenoGpuBridge.dll')) }
$arguments+=$source; $arguments+='/link'; $arguments+=('/LIBPATH:'+(Join-Path $vs 'lib\x64')); $arguments+=('/LIBPATH:'+(Join-Path $kit ('Lib\'+$version+'\um\x64'))); $arguments+=('/LIBPATH:'+(Join-Path $kit ('Lib\'+$version+'\ucrt\x64')))
$arguments+=(Join-Path $SdkRoot 'lib\Windows\x64\nvsdk_ngx_s.lib'); $arguments+=@('d3d11.lib','dxgi.lib','advapi32.lib','crypt32.lib','wintrust.lib','shlwapi.lib','ole32.lib','shell32.lib','user32.lib')
& $compiler @arguments
if($LASTEXITCODE -ne 0) { throw 'GPU bridge compilation failed.' }