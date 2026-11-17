# 发布 release（自包含运行时放在 runtime\ 子目录，根目录保持干净）
#
# 用法：
#   .\publish_release.ps1                     # 输出到 ..\release
#   .\publish_release.ps1 -OutDir D:\x\rel

param(
    [string]$OutDir = (Join-Path $PSScriptRoot '..\release'),
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

# ---- 组装 runtime\（DOTNET_ROOT 布局）----
$dotnetRoot = Split-Path (Get-Command dotnet).Source -Parent
if (-not (Test-Path (Join-Path $dotnetRoot 'host'))) {
    $dotnetRoot = 'C:\Program Files\dotnet'
}

$coreVer = (Get-ChildItem (Join-Path $dotnetRoot 'shared\Microsoft.NETCore.App') -Directory |
            Sort-Object Name -Descending | Select-Object -First 1).Name
$desktopVer = (Get-ChildItem (Join-Path $dotnetRoot 'shared\Microsoft.WindowsDesktop.App') -Directory |
               Sort-Object Name -Descending | Select-Object -First 1).Name
$fxrVer = (Get-ChildItem (Join-Path $dotnetRoot 'host\fxr') -Directory |
           Sort-Object Name -Descending | Select-Object -First 1).Name

$runtime = Join-Path $OutDir 'runtime'

New-Item -ItemType Directory -Force `
    (Join-Path $runtime "host\fxr\$fxrVer"), `
    (Join-Path $runtime "shared\Microsoft.NETCore.App\$coreVer"), `
    (Join-Path $runtime "shared\Microsoft.WindowsDesktop.App\$desktopVer") | Out-Null

Copy-Item (Join-Path $dotnetRoot "host\fxr\$fxrVer\hostfxr.dll") (Join-Path $runtime "host\fxr\$fxrVer\") -Force

robocopy (Join-Path $dotnetRoot "shared\Microsoft.NETCore.App\$coreVer") (Join-Path $runtime "shared\Microsoft.NETCore.App\$coreVer") /E /NFL /NDL /NJH /NJS /NP | Out-Null
robocopy (Join-Path $dotnetRoot "shared\Microsoft.WindowsDesktop.App\$desktopVer") (Join-Path $runtime "shared\Microsoft.WindowsDesktop.App\$desktopVer") /E /NFL /NDL /NJH /NJS /NP | Out-Null

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
