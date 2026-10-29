param([switch]$SkipRust)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    & (Join-Path $PSScriptRoot 'setup-docx.ps1')
    if (!$SkipRust) {
        cargo build --manifest-path native/ocr-worker/Cargo.toml --release --locked
        if ($LASTEXITCODE -ne 0) { throw 'Rust build failed' }
    }
    $relativeOutput = 'releases\build-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff')
    $output = Join-Path $PSScriptRoot $relativeOutput
    dotnet build src/ObsidianDocs -c Release -o $output --nologo
    if ($LASTEXITCODE -ne 0) { throw '.NET build failed' }
    Copy-Item native/ocr-worker/target/release/ocr-worker.exe $output -Force
    Get-ChildItem native/ocr-worker/target/release/*.dll | Copy-Item -Destination $output -Force
    [System.IO.File]::WriteAllText((Join-Path $PSScriptRoot 'current-build.txt'), $relativeOutput)
    Write-Host "Ready: $output/ObsiDopf.exe"
} finally { Pop-Location }



