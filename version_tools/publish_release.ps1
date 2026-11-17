# 发布 release（自包含运行时放在 runtime\ 子目录，根目录保持干净）
#
# 用法：
#   .\publish_release.ps1                     # 输出到 new_structure\release
#   .\publish_release.ps1 -OutDir D:\x\rel

param(
    [string]$OutDir = (Join-Path $PSScriptRoot '..\..\release'),
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$codeDir = Join-Path $PSScriptRoot '..'
$OutDir = [System.IO.Path]::GetFullPath($OutDir)

Write-Host "发布到：$OutDir"
if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }

Push-Location $codeDir
try {
    dotnet publish -c $Configuration -r win-x64 --self-contained false `
        -p:AppHostDotNetSearch=AppRelative `
        -p:AppHostRelativeDotNet=runtime `
        -p:DebugType=None -p:DebugSymbols=false `
        -o $OutDir -nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败：$LASTEXITCODE" }
}
finally {
    Pop-Location
}

# ---- 选择与目标框架匹配的 .NET 运行时（不能用字符串排序，"9.0.6" 会排到 "10.0.14" 前面）----
$csproj = Get-Content (Join-Path $codeDir 'AngelinaISAssistant.csproj') -Raw
$major = [int]([regex]::Match($csproj, '<TargetFramework>net(\d+)\.').Groups[1].Value)

$entries = foreach ($line in (dotnet --list-runtimes)) {
    $m = [regex]::Match($line, '^(?<name>Microsoft\.(?:NETCore|WindowsDesktop)\.App)\s+(?<ver>\S+)\s+\[(?<dir>.+)\]$')

    if ($m.Success) {
        [pscustomobject]@{
            Name    = $m.Groups['name'].Value
            Version = $m.Groups['ver'].Value
            Dir     = $m.Groups['dir'].Value
        }
    }
}

function Select-Runtime([string]$name) {
    $entries |
        Where-Object { $_.Name -eq $name -and $_.Version -match "^$major\." } |
        Sort-Object { [version]($_.Version -replace '-.*$', '') } -Descending |
        Select-Object -First 1
}

$core = Select-Runtime 'Microsoft.NETCore.App'
$desktop = Select-Runtime 'Microsoft.WindowsDesktop.App'

if (-not $core -or -not $desktop) {
    throw "找不到 .NET $major 的运行时（Microsoft.NETCore.App / Microsoft.WindowsDesktop.App），请先安装与 TargetFramework 匹配的运行时。"
}

$dotnetRoot = Split-Path (Split-Path $core.Dir -Parent) -Parent
$coreVer = $core.Version
$desktopVer = $desktop.Version

$fxrVer = (Get-ChildItem (Join-Path $dotnetRoot 'host\fxr') -Directory |
           Where-Object { $_.Name -match "^$major\." } |
           Sort-Object { [version]($_.Name -replace '-.*$', '') } -Descending |
           Select-Object -First 1).Name

if (-not $fxrVer) {
    throw "找不到 .NET $major 的 hostfxr（$dotnetRoot\host\fxr）。"
}

$runtime = Join-Path $OutDir 'runtime'

New-Item -ItemType Directory -Force `
    (Join-Path $runtime "host\fxr\$fxrVer"), `
    (Join-Path $runtime "shared\Microsoft.NETCore.App\$coreVer"), `
    (Join-Path $runtime "shared\Microsoft.WindowsDesktop.App\$desktopVer") | Out-Null

Copy-Item (Join-Path $dotnetRoot "host\fxr\$fxrVer\hostfxr.dll") (Join-Path $runtime "host\fxr\$fxrVer\") -Force

robocopy (Join-Path $core.Dir $coreVer) (Join-Path $runtime "shared\Microsoft.NETCore.App\$coreVer") /E /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "复制 NETCore 运行时失败：$LASTEXITCODE" }

robocopy (Join-Path $desktop.Dir $desktopVer) (Join-Path $runtime "shared\Microsoft.WindowsDesktop.App\$desktopVer") /E /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "复制 WindowsDesktop 运行时失败：$LASTEXITCODE" }

# 只保留界面支持的语言（简体/繁体/日语）；英文是内置中性资源，没有独立目录。
# 这些目录里只有 *.resources.dll 卫星程序集，删掉后 .NET/WPF 会回退到英文中性资源。
$keepCultures = @('zh-Hans', 'zh-Hant', 'ja')
$desktopShared = Join-Path $runtime "shared\Microsoft.WindowsDesktop.App\$desktopVer"
$removed = 0

Get-ChildItem $desktopShared -Directory | Where-Object {
    $_.Name -match '^[a-z]{2}(-[A-Za-z]{2,4})?$' -and $keepCultures -notcontains $_.Name
} | ForEach-Object {
    $files = Get-ChildItem $_.FullName -Recurse -File
    $foreign = $files | Where-Object { $_.Name -notlike '*.resources.dll' }

    if ($files.Count -gt 0 -and $foreign.Count -eq 0) {
        Remove-Item $_.FullName -Recurse -Force
        $removed++
    }
}

Write-Host ("已裁剪 {0} 个未使用语言目录（保留 zh-Hans / zh-Hant / ja）" -f $removed)

Write-Host ("运行时：NETCore {0} / Desktop {1} / fxr {2}" -f $coreVer, $desktopVer, $fxrVer)
Write-Host ("完成：{0} 个文件，{1:N1} MB" -f `
    (Get-ChildItem $OutDir -Recurse -File).Count, `
    ((Get-ChildItem $OutDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB))

# 显式成功退出：robocopy 的退出码 1-7 表示成功复制，不能泄漏成脚本退出码
exit 0