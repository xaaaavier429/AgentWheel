# Builds AgentWheel.exe on your own PC (needs the .NET 8 SDK: winget install Microsoft.DotNet.SDK.8).
# The result lands in windows\publish\AgentWheel.exe.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
dotnet publish AgentWheel.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
Write-Host ""
Write-Host "完成：$PSScriptRoot\publish\AgentWheel.exe"
Write-Host "影片 / 音訊功能需要 ffmpeg：把 ffmpeg.exe 和 ffprobe.exe 放在 AgentWheel.exe 旁邊，或執行 winget install Gyan.FFmpeg"
