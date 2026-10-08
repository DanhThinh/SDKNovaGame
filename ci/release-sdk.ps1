<#
.SYNOPSIS
    Phát hành một phiên bản package com.novagames.sdk.

.DESCRIPTION
    1. Kiểm tra version dạng semver (1.2.3) và lớn hơn version hiện tại.
    2. Ghi version vào Packages/com.novagames.sdk/package.json.
    3. Đồng bộ sample: Assets/NovaSdkSamples/Demo -> Packages/com.novagames.sdk/Samples~/Demo.
    4. Commit "SDK v<version>" và tạo tag v<version>.
    Push do người phát hành tự làm: git push; git push origin v<version>

    Game nâng SDK bằng cách đổi tag trong Packages/manifest.json của game đó:
    "com.novagames.sdk": "<git-url>?path=/Packages/com.novagames.sdk#v<version>"

.EXAMPLE
    ./ci/release-sdk.ps1 -Version 1.0.1
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$ProjectPath = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version phải có dạng 1.2.3 (nhận: $Version)" }

$packageDir = Join-Path $ProjectPath 'Packages/com.novagames.sdk'
$packageJson = Join-Path $packageDir 'package.json'
$samplesSrc = Join-Path $ProjectPath 'Assets/NovaSdkSamples/Demo'
$samplesDst = Join-Path $packageDir 'Samples~/Demo'

Push-Location $ProjectPath
try {
    if (git status --porcelain) { throw 'Working tree còn thay đổi chưa commit: commit hoặc stash trước khi phát hành.' }
    if (git tag --list "v$Version") { throw "Tag v$Version đã tồn tại." }

    $raw = Get-Content $packageJson -Raw
    $current = ([regex]'"version"\s*:\s*"([^"]+)"').Match($raw).Groups[1].Value
    if ([version]$Version -le [version]$current) { throw "Version mới ($Version) phải lớn hơn version hiện tại ($current)." }

    $raw = ([regex]'("version"\s*:\s*")[^"]+(")').Replace($raw, "`${1}$Version`${2}", 1)
    [System.IO.File]::WriteAllText($packageJson, $raw, (New-Object System.Text.UTF8Encoding $false))

    # Samples~ bị Unity bỏ qua nên project SDK làm việc trên bản ở Assets; bản trong package là bản copy khi phát hành.
    if (Test-Path $samplesDst) { Remove-Item $samplesDst -Recurse -Force }
    New-Item -ItemType Directory -Force $samplesDst | Out-Null
    Copy-Item (Join-Path $samplesSrc '*') $samplesDst -Recurse -Force

    git add -A -- $packageDir
    git commit -m "SDK v$Version"
    git tag -a "v$Version" -m "NovaGames Mobile SDK v$Version"

    Write-Host "Đã tạo commit + tag v$Version (từ $current)." -ForegroundColor Green
    Write-Host "Push: git push; git push origin v$Version"
}
finally {
    Pop-Location
}
