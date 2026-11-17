# 生成文件夹级增量更新包（.aisapatch，HDiffPatch 原生目录补丁）
#
# 用法：
#   .\make_update.ps1 -OldDir ..\release_old -NewDir ..\release -FromVersion v0.0.2 -ToVersion v0.0.4
#
# 产物（默认输出到 NewDir 的上一级）：
#   angelina_isa_v{From}_v{To}.aisapatch
#
# 说明：
#   hdiffz 的目录模式会逐个文件比较并跨文件复用相同数据，补丁比"逐文件 hdiff"更小；
#   hpatchz 打补丁时直接读旧目录、写出完整的新目录，不需要额外的清单或解包。

param(
    [Parameter(Mandatory = $true)][string]$OldDir,
    [Parameter(Mandatory = $true)][string]$NewDir,
    [Parameter(Mandatory = $true)][string]$FromVersion,
    [Parameter(Mandatory = $true)][string]$ToVersion,
    [string]$OutDir = '',
    [string]$Hdiffz = ''
)

$ErrorActionPreference = 'Stop'

if (-not $Hdiffz) {
    $Hdiffz = Join-Path $PSScriptRoot 'hdiffz.exe'
}

$OldDir = (Resolve-Path $OldDir).Path.TrimEnd('\')
$NewDir = (Resolve-Path $NewDir).Path.TrimEnd('\')

if (-not $OutDir) {
    $OutDir = Split-Path $NewDir -Parent
}
New-Item -ItemType Directory -Force $OutDir | Out-Null

if (-not (Test-Path $Hdiffz)) {
    throw "找不到 hdiffz.exe：$Hdiffz"
}

# 只对外发布干净目录：先剔除运行期产物，避免把它们写进补丁
$ExcludeNames = @('_update_stage', '_update_pkg', 'reports', 'logs', '_update.cmd', '_update_remove.txt')

function Copy-Filtered([string]$src, [string]$dst) {
    New-Item -ItemType Directory -Force $dst | Out-Null

    foreach ($item in Get-ChildItem $src -Force) {
        if ($ExcludeNames -contains $item.Name) { continue }
        if ($item.Extension -eq '.log') { continue }
        if ($item.PSIsContainer) {
            Copy-Filtered $item.FullName (Join-Path $dst $item.Name)
        }
        else {
            Copy-Item $item.FullName (Join-Path $dst $item.Name) -Force
        }
    }
}

$work = Join-Path $env:TEMP ("aisa_diff_" + [Guid]::NewGuid().ToString('N'))
$oldClean = Join-Path $work 'old'
$newClean = Join-Path $work 'new'

try {
    Copy-Filtered $OldDir $oldClean
    Copy-Filtered $NewDir $newClean

    $package = Join-Path $OutDir ("angelina_isa_v{0}_v{1}.aisapatch" -f $FromVersion.TrimStart('v','V'), $ToVersion.TrimStart('v','V'))
    if (Test-Path $package) { Remove-Item $package -Force }

    # -D 强制目录模式；-s 流式读取（适合大目录）；-c 压缩；-p 并行
    & $Hdiffz -D -s-64m -c-lzma2-9-16m -p-8 $oldClean $newClean $package | Out-Null

    if ($LASTEXITCODE -ne 0) {
        throw "hdiffz 失败，退出码 $LASTEXITCODE"
    }

    $info = & $Hdiffz -info $package 2>&1
    Write-Host ("更新包已生成：{0}（{1:N1} KB）" -f $package, ((Get-Item $package).Length / 1KB))
    Write-Host ($info -join [Environment]::NewLine)
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
