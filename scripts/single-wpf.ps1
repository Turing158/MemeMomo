#Requires -Version 5.1
<#
.SYNOPSIS
    一次发布 MemeMomo (WPF) 的四个 Windows 单文件 exe。

.DESCRIPTION
    每次运行固定发布以下组合：

      Framework      win-x64 和 win-x86。目标机器需要安装 .NET 8 Desktop Runtime。
      SelfContained  win-x64 和 win-x86。自包含运行时，脚本默认开启单文件压缩。

    输出文件名为：

      MemeMomo-版本号-x64.exe
      MemeMomo-版本号-x64-packages.exe
      MemeMomo-版本号-x86.exe
      MemeMomo-版本号-x86-packages.exe

    版本号从 MemeMomo-wpf\MemeMomo.csproj 的 <Version> 读取。每个档位先发布到
    独立临时目录，再将单文件 exe 移到输出目录，避免不同运行时或档位互相覆盖。

.PARAMETER Configuration
    构建配置，默认 Release。

.PARAMETER Output
    输出目录，默认 artifacts\single。目录中会生成四个 exe 和共享的第三方声明文件。

.PARAMETER Invariant
    开启 InvariantGlobalization + UseSystemResourceKeys。仅在明确需要时使用。

.PARAMETER ReadyToRun
    开启 R2R 预编译。启动更快，但 exe 体积会明显增大，默认关闭。

.PARAMETER NoCompression
    关闭 SelfContained 单文件压缩。Framework 档位不受影响。

.PARAMETER Trim
    实验性：强行开启 PublishTrimmed。WPF 官方不支持裁剪，产物可能构建失败或
    在运行时崩溃，仅供试验。

.PARAMETER Clean
    删除输出目录以及当前配置的 bin/obj 后再发布。

.EXAMPLE
    ./scripts/single-wpf.ps1
    ./scripts/single-wpf.ps1 -Clean
    ./scripts/single-wpf.ps1 -o artifacts\release
#>
[CmdletBinding()]
param(
    [Alias('c')]
    [string]$Configuration = 'Release',

    [Alias('o')]
    [string]$Output = '',

    [switch]$Invariant,
    [switch]$NoCompression,
    [Alias('r2r')]
    [switch]$ReadyToRun,
    [switch]$Trim,
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'MemeMomo-wpf\MemeMomo.csproj'

if (-not (Test-Path -LiteralPath $project)) {
    throw "找不到项目文件: $project"
}

if ([string]::IsNullOrWhiteSpace($Output)) {
    $Output = Join-Path $repoRoot 'artifacts\single'
}
$Output = [IO.Path]::GetFullPath($Output)

# 从项目文件读取产品版本，避免脚本中的版本号与实际程序集版本脱节。
[xml]$projectDocument = Get-Content -Raw -LiteralPath $project
$version = @(
    $projectDocument.Project.PropertyGroup |
        ForEach-Object { $_.Version; $_.VersionPrefix } |
        Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }
) | Select-Object -First 1
$version = ([string]$version).Trim()
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "项目文件未定义 <Version> 或 <VersionPrefix>: $project"
}

# 文件名只允许常见的版本字符，避免预发布版本中的特殊字符破坏路径。
$fileVersion = $version -replace '[^0-9A-Za-z._-]', '-'

$targets = @(
    [pscustomobject]@{
        Runtime = 'win-x64'
        Architecture = 'x64'
        Mode = 'Framework'
        SelfContained = $false
        PackageSuffix = ''
    }
    [pscustomobject]@{
        Runtime = 'win-x64'
        Architecture = 'x64'
        Mode = 'SelfContained'
        SelfContained = $true
        PackageSuffix = '-packages'
    }
    [pscustomobject]@{
        Runtime = 'win-x86'
        Architecture = 'x86'
        Mode = 'Framework'
        SelfContained = $false
        PackageSuffix = ''
    }
    [pscustomobject]@{
        Runtime = 'win-x86'
        Architecture = 'x86'
        Mode = 'SelfContained'
        SelfContained = $true
        PackageSuffix = '-packages'
    }
)

if ($Clean) {
    foreach ($dir in @(
        $Output,
        (Join-Path $repoRoot ("MemeMomo-wpf\obj\{0}" -f $Configuration)),
        (Join-Path $repoRoot ("MemeMomo-wpf\bin\{0}" -f $Configuration))
    )) {
        if (Test-Path -LiteralPath $dir) {
            Write-Host "清理 $dir"
            Remove-Item -LiteralPath $dir -Recurse -Force
        }
    }
}

New-Item -ItemType Directory -Path $Output -Force | Out-Null

# 临时目录是脚本私有的；即使上一次运行中断，也不会污染最终输出目录。
$tempRoot = Join-Path $Output '.single-wpf-publish'
if (Test-Path -LiteralPath $tempRoot) {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

Write-Host ''
Write-Host ('项目版本    : {0}' -f $version)
Write-Host ('配置        : {0}' -f $Configuration)
Write-Host ('输出目录    : {0}' -f $Output)
Write-Host ('发布组合    : {0}' -f ($targets.Count))
Write-Host ('ReadyToRun  : {0}' -f $ReadyToRun.IsPresent)
Write-Host ('Invariant   : {0}' -f $Invariant.IsPresent)
Write-Host ''

$publishedFiles = @()
$restoredRuntimes = @{}

try {
    for ($index = 0; $index -lt $targets.Count; $index++) {
        $target = $targets[$index]
        $targetLabel = '{0} {1} ({2}/{3})' -f $target.Runtime, $target.Mode, ($index + 1), $targets.Count
        $targetOutput = Join-Path $tempRoot ("{0}-{1}-{2}" -f $target.Runtime, $target.Mode, $target.Architecture)
        New-Item -ItemType Directory -Path $targetOutput -Force | Out-Null

        $compress = $target.SelfContained -and (-not $NoCompression)
        $props = [ordered]@{
            PublishSingleFile                    = 'true'
            SelfContained                        = $target.SelfContained.ToString().ToLowerInvariant()
            RuntimeIdentifier                    = $target.Runtime
            PublishReadyToRun                    = $ReadyToRun.ToString().ToLowerInvariant()
            IncludeNativeLibrariesForSelfExtract = 'true'
            EnableCompressionInSingleFile        = $compress.ToString().ToLowerInvariant()
            SatelliteResourceLanguages           = 'en'
            DebugType                            = 'none'
            DebugSymbols                         = 'false'
            GenerateDocumentationFile            = 'false'
        }

        if ($Invariant) {
            $props['InvariantGlobalization'] = 'true'
            $props['UseSystemResourceKeys'] = 'true'
        }

        if ($Trim) {
            $props['PublishTrimmed'] = 'true'
            $props['TrimMode'] = 'partial'
            # WindowsDesktop SDK 默认拒绝裁剪；这个内部开关只放行构建检查。
            $props['_SuppressWpfTrimError'] = 'true'
            if ($index -eq 0) {
                Write-Warning 'WPF 不支持 IL 裁剪，-Trim 属于实验性开关：构建可能失败，产物也可能在运行时崩溃。'
            }
        }

        $propArgs = @()
        foreach ($key in $props.Keys) {
            $propArgs += ('-p:{0}={1}' -f $key, $props[$key])
        }

        if (-not $restoredRuntimes.ContainsKey($target.Runtime)) {
            Write-Host ('还原      : {0}' -f $target.Runtime)
            & dotnet restore $project -r $target.Runtime
            if ($LASTEXITCODE -ne 0) {
                throw "dotnet restore 失败 (runtime=$($target.Runtime), exit $LASTEXITCODE)"
            }
            $restoredRuntimes[$target.Runtime] = $true
        }

        Write-Host ('发布      : {0}' -f $targetLabel)
        Write-Host ('压缩      : {0}' -f $compress)
        $publishArgs = @(
            'publish', $project,
            '-c', $Configuration,
            '-r', $target.Runtime,
            '-o', $targetOutput,
            '--nologo'
        ) + $propArgs

        & dotnet @publishArgs
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish 失败 (runtime=$($target.Runtime), mode=$($target.Mode), exit $LASTEXITCODE)"
        }

        $publishedExe = Join-Path $targetOutput 'MemeMomo.exe'
        if (-not (Test-Path -LiteralPath $publishedExe)) {
            throw "发布结束但找不到 $publishedExe"
        }

        $finalName = 'MemeMomo-{0}-{1}{2}.exe' -f $fileVersion, $target.Architecture, $target.PackageSuffix
        $finalPath = Join-Path $Output $finalName
        Move-Item -LiteralPath $publishedExe -Destination $finalPath -Force

        $fileInfo = Get-Item -LiteralPath $finalPath
        $publishedFiles += [pscustomobject]@{
            Name = $finalName
            Runtime = $target.Runtime
            Mode = $target.Mode
            SizeMb = [math]::Round($fileInfo.Length / 1MB, 2)
        }
    }

    $notice = Join-Path $repoRoot 'MemeMomo-wpf\THIRD-PARTY-NOTICES.md'
    if (Test-Path -LiteralPath $notice) {
        Copy-Item -LiteralPath $notice -Destination (Join-Path $Output 'THIRD-PARTY-NOTICES.md') -Force
    }
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}

$totalMb = [math]::Round((($publishedFiles | ForEach-Object {
    (Get-Item -LiteralPath (Join-Path $Output $_.Name)).Length
} | Measure-Object -Sum).Sum / 1MB), 2)

Write-Host ''
Write-Host '生成文件:'
$publishedFiles | ForEach-Object {
    Write-Host ('  {0,10:N2} MB  {1}' -f $_.SizeMb, $_.Name)
}
Write-Host ('输出总大小  : {0} MB' -f $totalMb)
Write-Host ''
Write-Host 'Framework 档位需要目标机器安装 .NET 8 Desktop Runtime；SelfContained 档位可免安装运行时。'
