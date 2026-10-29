param([string]$Installer=(Join-Path $PSScriptRoot 'dist\ObsiDopf-1.0.0-Setup-x64.exe'))
$ErrorActionPreference='Stop'
$testRoot=Join-Path ([IO.Path]::GetTempPath()) ('ObsidianDocsSetupTest-'+[Guid]::NewGuid().ToString('N'))
$installDirectory=Join-Path $testRoot 'App'
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
Write-Host 'Testing installer in an isolated directory (no shortcuts or registry changes)...'
$process=Start-Process -FilePath $Installer -ArgumentList ('/S /TESTMODE /D='+$installDirectory) -WindowStyle Hidden -PassThru -Wait
if ($process.ExitCode -ne 0) { throw "Installer failed: $($process.ExitCode)" }
$manifestFile=Join-Path $installDirectory 'payload-manifest.json'
if (!(Test-Path -LiteralPath $manifestFile)) { throw 'Installed manifest missing' }
$manifest=Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
Write-Host 'Checking installed file hashes...'
$sha=[Security.Cryptography.SHA256]::Create()
try { foreach ($entry in $manifest) {
    $path=Join-Path $installDirectory $entry.Path
    if (![IO.File]::Exists($path)) { throw "Installed file missing: $($entry.Path)" }
    $stream=[IO.File]::OpenRead($path)
    try { $actual=[BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','').ToLower() }
    finally { $stream.Dispose() }
    if ($actual -ne $entry.SHA256) { throw "Installed file corrupted: $($entry.Path)" }
} } finally { $sha.Dispose() }
foreach ($relative in @('coreclr.dll','ocr-worker.exe','DirectML.dll','vcruntime140.dll','models\pp-ocrv5_mobile_det.onnx','native\libreoffice\runtime\program\soffice.com')) {
    if (!(Test-Path -LiteralPath (Join-Path $installDirectory $relative))) { throw "Required standalone component missing: $relative" }
}
Write-Host 'Running actual installed application: OCR EN/RU, DOCX, PDF comments...'
$originalDotnetRoot=$env:DOTNET_ROOT
try {
    $env:DOTNET_ROOT=Join-Path $testRoot 'NoGlobalDotnet'
    foreach ($test in @('smoke','save-test','comment-test','wheel-test')) {
        $argument=if ($test -eq 'smoke') {'--smoke'} else {'--'+$test}
        $process=Start-Process -FilePath (Join-Path $installDirectory 'ObsiDopf.exe') -ArgumentList $argument -WindowStyle Hidden -PassThru -Wait
        if ($process.ExitCode -ne 0) {
            $errorFile=if ($test -eq 'smoke') {'smoke-error.txt'} else {$test+'.txt'}
            throw "Installed application test failed ($test): $(Get-Content -LiteralPath (Join-Path $installDirectory $errorFile) -Raw -ErrorAction SilentlyContinue)"
        }
    }
} finally { $env:DOTNET_ROOT=$originalDotnetRoot }
# Ensure uninstall preserves files that were not shipped with the app.
$userFile=Join-Path $installDirectory 'keep-user-document.txt'
[IO.File]::WriteAllText($userFile,'User-created content must survive uninstall')
Write-Host 'Testing uninstall and user-file preservation...'
$uninstall=Start-Process -FilePath (Join-Path $installDirectory 'Uninstall.exe') -ArgumentList '/S' -WindowStyle Hidden -PassThru -Wait
if ($uninstall.ExitCode -ne 0) { throw "Uninstaller failed: $($uninstall.ExitCode)" }
$deadline=(Get-Date).AddSeconds(120)
while ((Test-Path -LiteralPath (Join-Path $installDirectory 'install.ini')) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
if (Test-Path -LiteralPath (Join-Path $installDirectory 'install.ini')) { throw 'Uninstaller did not finish' }
foreach ($entry in $manifest) { if (Test-Path -LiteralPath (Join-Path $installDirectory $entry.Path)) { throw "Uninstaller left a packaged file: $($entry.Path)" } }
if (!(Test-Path -LiteralPath $userFile) -or (Get-Content -LiteralPath $userFile -Raw) -ne 'User-created content must survive uninstall') { throw 'Uninstaller modified a user-created file' }
$report=[ordered]@{Installer=(Get-Item -LiteralPath $Installer).FullName;SHA256=(Get-FileHash -LiteralPath $Installer -Algorithm SHA256).Hash.ToLower();FilesVerified=$manifest.Count;Install='PASS';StandaloneRuntime='PASS';OCR_EN_RU='PASS';DOCX='PASS';PDF_Comments='PASS';CtrlS='PASS';TextComments='PASS';ContinuousPages='PASS';Uninstall='PASS';UserFilesPreserved='PASS';TestDirectory=$testRoot}
$report | ConvertTo-Json | Set-Content (Join-Path $PSScriptRoot 'dist\installer-validation.json') -Encoding utf8
Write-Host 'PASS: install, full application tests, uninstall and user-file preservation'

