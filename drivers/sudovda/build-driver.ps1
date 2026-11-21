# 從原始碼編譯 SudoVDA，並以拋棄式自簽憑證簽章（ADR-0005）。
#
# 需要：Visual Studio 2022（C++ 桌面開發、Spectre 緩解程式庫、Windows Driver Kit 元件）與 WDK 10.0.26100。
# 產出：本資料夾下的 x64\（SudoVDA.inf、SudoVDA.dll、sudovda.cat）與 AutoDisplayMode-SudoVDA.cer。
# 私鑰在簽完後立即刪除，所以每次重新編譯都會換一張新憑證；已安裝舊版的電腦要先移除再安裝。
#
# 用法（一般權限即可）：powershell -ExecutionPolicy Bypass -File drivers\sudovda\build-driver.ps1

param(
    [string]$Commit = 'a4b09fa2aa731a964d0cb5d139cb1e6240e4da12',
    [string]$WorkDir = (Join-Path $env:TEMP 'adm-sudovda-build')
)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$out = Join-Path $here 'x64'

# 1. 取得指定版本的原始碼
if (Test-Path $WorkDir) { Remove-Item $WorkDir -Recurse -Force }
git clone --quiet https://github.com/SudoMaker/SudoVDA.git $WorkDir
if ($LASTEXITCODE) { throw 'git clone 失敗' }
git -C $WorkDir checkout --quiet $Commit
if ($LASTEXITCODE) { throw "找不到 commit $Commit" }

# 2. 編譯（不讓專案自己簽章，之後用我們的憑證簽）
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw '找不到 MSBuild' }
$sln = Join-Path $WorkDir 'Virtual Display Driver (HDR)\SudoVDA.sln'
# WDK 10.0.26100 內建的 INF 驗證會去載入不存在的 x86\InfVerif.dll 而報錯，所以關掉它，改在下面直接執行 infverif.exe
& $msbuild $sln /m /nologo /v:minimal /p:Configuration=Release /p:Platform=x64 /p:SignMode=Off /p:SkipPackageVerification=true
if ($LASTEXITCODE) { throw '編譯失敗' }
$pkg = Get-ChildItem -Path (Join-Path $WorkDir 'Virtual Display Driver (HDR)') -Recurse -Filter 'sudovda.cat' |
    Where-Object { $_.FullName -match '\\x64\\Release\\' } | Select-Object -First 1
if (-not $pkg) { throw '找不到編譯產出的 sudovda.cat（Inf2Cat 沒有執行？）' }
$pkgDir = $pkg.DirectoryName
$infverif = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\Tools\*\x64\infverif.exe" | Sort-Object FullName -Descending | Select-Object -First 1
& $infverif.FullName /u (Join-Path $pkgDir 'SudoVDA.inf')
if ($LASTEXITCODE) { throw 'INF 驗證失敗' }

# 3. 拋棄式自簽憑證
$subject = 'CN=AutoDisplayMode SudoVDA (disposable ' + (Get-Date -Format 'yyyy-MM-dd') + ')'
$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject -CertStoreLocation Cert:\CurrentUser\My `
    -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -NotAfter (Get-Date).AddYears(30)
try {
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $signtool) { throw '找不到 signtool' }
    & $signtool.FullName sign /q /fd SHA256 /sha1 $cert.Thumbprint /s My (Join-Path $pkgDir 'SudoVDA.dll') (Join-Path $pkgDir 'sudovda.cat')
    if ($LASTEXITCODE) { throw '簽章失敗' }

    # 4. 輸出到 repo
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    New-Item -ItemType Directory $out | Out-Null
    Copy-Item (Join-Path $pkgDir 'SudoVDA.inf'), (Join-Path $pkgDir 'SudoVDA.dll'), (Join-Path $pkgDir 'sudovda.cat') $out
    Get-ChildItem $here -Filter '*.cer' | Remove-Item
    Export-Certificate -Cert $cert -FilePath (Join-Path $here 'AutoDisplayMode-SudoVDA.cer') | Out-Null
}
finally {
    # 5. 刪除私鑰：這張憑證之後不可能再簽任何東西
    Remove-Item -Path ("Cert:\CurrentUser\My\" + $cert.Thumbprint) -DeleteKey
}
Write-Host "完成：$out"
Write-Host "憑證：$subject  $($cert.Thumbprint)（私鑰已刪除）"
