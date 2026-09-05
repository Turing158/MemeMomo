#Requires -Version 5.1
<#
.SYNOPSIS
    发布 MemeMomo (WPF) 单文件 exe，并尽可能压缩体积。

.DESCRIPTION
    两种体积档位：

      Framework      框架依赖单文件。exe 里只打包 MemeMomo + AvalonEdit + Markdig，
                     体积最小（个位数 MB），但目标机器需要装 .NET 8 桌面运行时。
      SelfContained  自包含单文件。不依赖运行时，体积大得多；脚本会开启单文件
                     压缩、关闭 ReadyToRun、剔除卫星语言资源来尽量压缩。

    WPF 在 .NET 8 上不支持 IL 裁剪（PublishTrimmed），所以自包含档位的下限
    受运行时本身限制，实测 win-x64 约 63 MB。真要小，就用框架依赖档位。

.PARAMETER Mode
    -m fw  框架依赖（默认，约 2 MB）
    -m sc  自包含（约 63 MB）
    长写法 Framework / SelfContained 也接受。

    两个档位都输出到同一个 artifacts\single\MemeMomo.exe，换档位会覆盖上一次的产物。
    要并排保留就自己指定 -o <目录>。

.PARAMETER Invariant
    开启 InvariantGlobalization + UseSystemResourceKeys。
    注意：win-x64 自包含默认走 NLS 而非 ICU，实测这个开关对体积没有帮助
    （63.46 MB 前后不变），却会让 Utils/DateTimeUtils.cs 的区域性时间格式
    和异常消息文本退化。默认不要开，仅在明确需要该行为时使用。

.PARAMETER ReadyToRun
    开启 R2R 预编译。启动更快，但 exe 大概会翻倍，默认关闭。

.PARAMETER NoCompression
    关闭单文件压缩（仅自包含档位有效）。压缩会让首次启动稍慢，换来体积下降。

.PARAMETER Trim
    实验性：强行开启 PublishTrimmed。WPF 官方不支持裁剪，多半直接构建失败，
    即使成功也很可能在运行时因反射/BAML 解析崩溃。仅供试验。

.EXAMPLE
    ./scripts/single-wpf.ps1
    ./scripts/single-wpf.ps1 -m sc
    ./scripts/single-wpf.ps1 -m sc -Clean
    ./scripts/single-wpf.ps1 -m sc -o artifacts\sc-test
#>
[CmdletBinding()]
param(
    # fw = 框架依赖(默认, ~2 MB), sc = 自包含(~63 MB)
    # 长写法 Framework / SelfContained 同样接受。
    [Alias('m')]
    [ValidateSet('fw', 'sc', 'Framework', 'SelfContained')]
    [string]$Mode = 'fw',

    [Alias('r')]
    [string]$Runtime = 'win-x64',

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

$selfContained = ($Mode -in @('sc', 'SelfContained'))

# 归一成长名字，只用于显示。
$modeLabel = if ($selfContained) { 'SelfContained' } else { 'Framework' }

# 单文件产物只有一个 MemeMomo.exe，不按档位分目录；换档位直接覆盖同一个输出目录。
if ([string]::IsNullOrWhiteSpace($Output)) {
    $Output = Join-Path $repoRoot 'artifacts\single'
}

# 单文件压缩只对自包含发布有效，框架依赖发布会被 SDK 忽略。
$compress = $selfContained -and (-not $NoCompression)

$props = [ordered]@{
    PublishSingleFile                    = 'true'
    SelfContained                        = $selfContained.ToString().ToLowerInvariant()
    RuntimeIdentifier                    = $Runtime
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
    # WindowsDesktop SDK 默认拒绝裁剪 WPF；这个内部开关放行，但不代表受支持。
    $props['_SuppressWpfTrimError'] = 'true'
    Write-Warning 'WPF 不支持 IL 裁剪，-Trim 属于实验性开关：构建可能失败，产物也可能在运行时崩溃。'
}

if ($Clean) {
    foreach ($dir in @($Output, (Join-Path $repoRoot 'MemeMomo-wpf\obj\Release'), (Join-Path $repoRoot 'MemeMomo-wpf\bin\Release'))) {
        if (Test-Path -LiteralPath $dir) {
            Write-Host "清理 $dir"
            Remove-Item -LiteralPath $dir -Recurse -Force
        }
    }
}

$propArgs = @()
foreach ($key in $props.Keys) {
    $propArgs += ('-p:{0}={1}' -f $key, $props[$key])
}

Write-Host ''
Write-Host ('模式        : {0}' -f $modeLabel)
Write-Host ('运行时      : {0}' -f $Runtime)
Write-Host ('输出目录    : {0}' -f $Output)
Write-Host ('单文件压缩  : {0}' -f $compress)
Write-Host ('ReadyToRun  : {0}' -f $ReadyToRun.IsPresent)
Write-Host ('Invariant   : {0}' -f $Invariant.IsPresent)
Write-Host ''

& dotnet restore $project -r $Runtime
if ($LASTEXITCODE -ne 0) { throw "dotnet restore 失败 (exit $LASTEXITCODE)" }

$publishArgs = @(
    'publish', $project,
    '-c', $Configuration,
    '-r', $Runtime,
    '-o', $Output,
    '--nologo'
) + $propArgs

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败 (exit $LASTEXITCODE)" }

$exe = Join-Path $Output 'MemeMomo.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    throw "发布结束但找不到 $exe"
}

# 单文件产物旁边常残留 .pdb / .xml，删掉以免误发。
# 注意: -LiteralPath 不带通配符时 -Include 会被忽略，只能自己过滤扩展名。
Get-ChildItem -LiteralPath $Output -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -in @('.pdb', '.xml') } |
    Remove-Item -Force -ErrorAction SilentlyContinue

$exeInfo = Get-Item -LiteralPath $exe
$exeMb = [math]::Round($exeInfo.Length / 1MB, 2)
$totalMb = [math]::Round(((Get-ChildItem -LiteralPath $Output -Recurse -File |
    Measure-Object -Property Length -Sum).Sum / 1MB), 2)

Write-Host ''
Write-Host ('MemeMomo.exe    : {0} MB' -f $exeMb)
Write-Host ('输出总大小  : {0} MB' -f $totalMb)
Write-Host ''
Write-Host '随包文件:'
Get-ChildItem -LiteralPath $Output -Recurse -File |
    Sort-Object Length -Descending |
    Select-Object -First 10 |
    ForEach-Object {
        Write-Host ('  {0,10:N0} KB  {1}' -f ($_.Length / 1KB), $_.Name)
    }

if (-not $selfContained) {
    Write-Host ''
    Write-Host ('提示: 框架依赖发布需要目标机器安装 .NET 8 Desktop Runtime ({0})。' -f $Runtime)
    Write-Host '      需要免安装分发时用: -m sc'
}
