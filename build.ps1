<#
  OmniPen 1.5 - Script build + dong goi
  Tac gia: Ho Van Thuyen - Truong THPT Hoang Hoa Tham

  Ket qua (thu muc dist\):
    OmniPen_Setup_v1.5.exe          Bo cai dat (tu chon ban 64/32-bit)
    OmniPen_v1.5_x64_portable.exe   Ban chay ngay, khong can cai (64-bit)
    OmniPen_v1.5_x86_portable.exe   Ban chay ngay, khong can cai (32-bit)

  Tham so:
    -SkipInstaller   Chi build exe, khong tao bo cai
    -X64Only         Chi build ban 64-bit (nhanh hon; bo cai se chi co ban 64-bit)
#>
param(
    [switch]$SkipInstaller,
    [switch]$X64Only
)

$ErrorActionPreference = 'Stop'
$Version = '1.5'
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $Root

function Step([string]$m) { Write-Host "`n==> $m" -ForegroundColor Cyan }
function Fail([string]$m) { Write-Host "`n[LOI] $m" -ForegroundColor Red; exit 1 }
function Refresh-Path {
    $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' +
                [Environment]::GetEnvironmentVariable('Path', 'User')
}
function Have-Winget { [bool](Get-Command winget -ErrorAction SilentlyContinue) }

# ---------------------------------------------------------------- .NET SDK
Step 'Kiem tra .NET SDK (can ban 8 tro len)'
function Test-DotnetSdk {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { return $false }
    $sdks = & dotnet --list-sdks 2>$null
    return [bool]($sdks | Where-Object { $_ -match '^(8|9|1\d)\.' })
}

if (-not (Test-DotnetSdk)) {
    if (-not (Have-Winget)) {
        Fail 'Chua co .NET 8 SDK va may khong co winget. Hay tai .NET 8 SDK tai https://dotnet.microsoft.com/download/dotnet/8.0 roi chay lai.'
    }
    Write-Host 'Dang cai .NET 8 SDK qua winget (can Internet, vai phut)...'
    winget install --id Microsoft.DotNet.SDK.8 -e --silent --accept-package-agreements --accept-source-agreements
    Refresh-Path
    if (-not (Test-DotnetSdk)) { Fail 'Cai .NET SDK chua thanh cong. Mo lai cua so moi va chay lai build.bat.' }
}
& dotnet --version

# ---------------------------------------------------------------- Publish
$project = Join-Path $Root 'src\OmniPen\OmniPen.csproj'
$runtimes = if ($X64Only) { @('win-x64') } else { @('win-x64', 'win-x86') }

if (Test-Path "$Root\publish") { Remove-Item "$Root\publish" -Recurse -Force }
New-Item -ItemType Directory -Force -Path "$Root\dist" | Out-Null

foreach ($rid in $runtimes) {
    Step "Build OmniPen $Version ($rid) - self-contained, mot file exe duy nhat"
    & dotnet publish $project -c Release -r $rid -o "$Root\publish\$rid" --nologo
    if ($LASTEXITCODE -ne 0) { Fail "Build $rid that bai." }

    $arch = $rid.Substring(4)
    Copy-Item "$Root\publish\$rid\OmniPen.exe" "$Root\dist\OmniPen_v${Version}_${arch}_portable.exe" -Force
}

if ($SkipInstaller) {
    Step 'Xong (bo qua bo cai dat).'
    if (-not $env:CI) { Start-Process explorer.exe "$Root\dist" }
    exit 0
}

# ---------------------------------------------------------------- Inno Setup
Step 'Tim Inno Setup 6 (ISCC.exe)'
function Find-Iscc {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )
    foreach ($c in $candidates) { if ($c -and (Test-Path $c)) { return $c } }
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

$iscc = Find-Iscc
if (-not $iscc) {
    if (-not (Have-Winget)) {
        Fail 'Chua co Inno Setup 6. Tai tai https://jrsoftware.org/isdl.php, cai dat roi chay lai build.bat. (File exe ban portable da co trong thu muc dist.)'
    }
    Write-Host 'Dang cai Inno Setup 6 qua winget...'
    winget install --id JRSoftware.InnoSetup -e --silent --accept-package-agreements --accept-source-agreements
    Refresh-Path
    $iscc = Find-Iscc
    if (-not $iscc) { Fail 'Khong tim thay ISCC.exe sau khi cai Inno Setup.' }
}
Write-Host $iscc

Step 'Tao bo cai dat'
$isccArgs = @("/Q")
if ($X64Only) { $isccArgs += "/DX64Only" }   # Bo cai chi danh cho Windows 64-bit
& $iscc @isccArgs "$Root\installer\OmniPen.iss"
if ($LASTEXITCODE -ne 0) { Fail 'ISCC bao loi khi tao bo cai.' }

Step 'HOAN TAT'
Get-ChildItem "$Root\dist" | ForEach-Object {
    '{0,-40} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB)
} | Write-Host
if (-not $env:CI) { Start-Process explorer.exe "$Root\dist" }
