# 构建 Zentwik Next 安装包。
#
#   pwsh -File scripts/build-installer.ps1
#   pwsh -File scripts/build-installer.ps1 -SkipPublish        # 复用上次 pkg/ 快速重打包
#
# 产物：dist\Zentwik-Next-<version>-Setup.exe

param(
    [string]$Configuration = "Release",
    [string]$Version,
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$pkg = Join-Path $root "pkg"
$dist = Join-Path $root "dist"
$iss = Join-Path $root "scripts\installer\zentwik-next.iss"

if (-not $Version) {
    $props = [xml](Get-Content (Join-Path $root "Directory.Build.props"))
    $Version = $props.Project.PropertyGroup.Version | Select-Object -First 1
}

if (-not $Version) {
    throw "未能从 Directory.Build.props 读取版本号，请用 -Version 指定。"
}

# 0.1.0-alpha -> 0.1.0.0：版本资源只接受纯数字四段。
$numeric = ($Version -replace "-.*$", "") -replace "[^0-9.]", ""
$parts = @($numeric.Split(".") | Where-Object { $_ -ne "" })
while ($parts.Count -lt 4) { $parts += "0" }
$numeric = ($parts[0..3] -join ".")

$iscc = $null
foreach ($candidate in @(
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
)) {
    if (Test-Path $candidate) { $iscc = $candidate; break }
}

if (-not $iscc) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { $iscc = $command.Source }
}

if (-not $iscc) {
    throw "未找到 ISCC.exe。请先安装 Inno Setup 6：winget install JRSoftware.InnoSetup"
}

Write-Host "版本 $Version（资源版本 $numeric）" -ForegroundColor Cyan
Write-Host "ISCC  $iscc"

if (-not $SkipPublish) {
    if (Test-Path $pkg) { Remove-Item $pkg -Recurse -Force }
    New-Item -ItemType Directory -Path $pkg | Out-Null

    # 两个项目发布到同一目录：Contracts/Domain/Localization 目标框架一致，同名 dll 内容相同，不存在覆盖冲突。
    Write-Host "发布代理与服务端…"
    dotnet publish (Join-Path $root "src\Zentwik.Agent") -c $Configuration -o $pkg | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "发布 Agent 失败。" }

    Write-Host "发布命令行…"
    dotnet publish (Join-Path $root "src\Zentwik.Cli") -c $Configuration -o $pkg | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "发布 CLI 失败。" }
}

foreach ($required in @("Zentwik.Agent.exe", "zentwik.exe", "PawnIO\RyzenSMU.bin")) {
    if (-not (Test-Path (Join-Path $pkg $required))) {
        throw "发布目录缺少 $required。"
    }
}

if (-not (Test-Path $dist)) { New-Item -ItemType Directory -Path $dist | Out-Null }

& $iscc "/DMyAppVersion=$Version" "/DMyAppVersionNumeric=$numeric" $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC 编译失败。" }

$output = Join-Path $dist "Zentwik-Next-$Version-Setup.exe"
if (-not (Test-Path $output)) { throw "未找到预期产物：$output" }

$size = [math]::Round((Get-Item $output).Length / 1MB, 1)
Write-Host ""
Write-Host "完成：$output（$size MB）" -ForegroundColor Green
