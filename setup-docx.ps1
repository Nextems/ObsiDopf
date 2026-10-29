$ErrorActionPreference = 'Stop'
$runtimeRoot = Join-Path $PSScriptRoot 'native\libreoffice'
$engine = Join-Path $runtimeRoot 'runtime\program\soffice.com'
if (Test-Path -LiteralPath $engine) { Write-Host 'DOCX engine is ready'; exit 0 }
New-Item -ItemType Directory -Force -Path $runtimeRoot | Out-Null
$installer = Join-Path $runtimeRoot 'LibreOffice.msi'
if (!(Test-Path -LiteralPath $installer)) {
    curl.exe -L --fail --silent --show-error 'https://download.documentfoundation.org/libreoffice/stable/26.8.1/win/x86_64/LibreOffice_26.8.1_Win_x86-64.msi' -o $installer
    if ($LASTEXITCODE -ne 0) { throw 'LibreOffice download failed' }
}
$signature = Get-AuthenticodeSignature -LiteralPath $installer
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'The Document Foundation') { throw 'LibreOffice signature verification failed' }
$runtimePath = Join-Path $runtimeRoot 'runtime'
$extract = Start-Process msiexec.exe -ArgumentList @('/a', ('"'+$installer+'"'), '/qn', ('TARGETDIR="'+$runtimePath+'"'), '/norestart') -WindowStyle Hidden -PassThru -Wait
if ($extract.ExitCode -notin @(0,3010) -or !(Test-Path -LiteralPath $engine)) { throw "LibreOffice extraction failed: $($extract.ExitCode)" }
Write-Host 'DOCX engine is ready (local extraction, no global installation)'
