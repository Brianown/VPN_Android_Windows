# 轻隧 LiteTunnel · Windows 客户端构建脚本
#
# 使用 .NET Framework 4.8 自带的 csc.exe 编译，无需安装 Visual Studio 或 .NET SDK。
# 输出：dist\LiteTunnel.exe
#
param(
    [string]$OutputDir = (Join-Path $PSScriptRoot 'dist')
)

$ErrorActionPreference = 'Stop'

$cscCandidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
$csc = $null
foreach ($candidate in $cscCandidates) {
    if (Test-Path $candidate) {
        $csc = $candidate
        break
    }
}
if (-not $csc) {
    throw '未找到 .NET Framework 4.x 的 csc.exe，请在 Windows 10/11 上运行本脚本。'
}

$icon = Join-Path $PSScriptRoot 'assets\litetunnel.ico'
$manifest = Join-Path $PSScriptRoot 'app.manifest'
if (-not (Test-Path $icon)) {
    throw "缺少图标文件：$icon"
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$sources = Get-ChildItem -Path (Join-Path $PSScriptRoot 'src') -Filter '*.cs' |
    ForEach-Object { $_.FullName } |
    Sort-Object

$arguments = @(
    '/nologo',
    '/target:winexe',
    '/platform:anycpu',
    '/optimize+',
    '/langversion:5',
    ('/win32icon:' + $icon),
    ('/win32manifest:' + $manifest),
    ('/out:' + (Join-Path $OutputDir 'LiteTunnel.exe')),
    '/reference:System.dll',
    '/reference:System.Core.dll',
    '/reference:System.Drawing.dll',
    '/reference:System.ServiceProcess.dll',
    '/reference:System.Windows.Forms.dll'
) + $sources

& $csc $arguments
if ($LASTEXITCODE -ne 0) {
    throw "编译失败（csc 退出码 $LASTEXITCODE）。"
}

Write-Host ''
Write-Host '构建成功：' (Join-Path $OutputDir 'LiteTunnel.exe')
