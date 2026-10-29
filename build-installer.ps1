param([string]$Version='1.0.0')
$ErrorActionPreference='Stop'
Push-Location $PSScriptRoot
try {
    $payload=Join-Path $PSScriptRoot 'dist\staging'
    $compiler=Join-Path $PSScriptRoot 'tools\nsis-3.13\makensis.exe'
    if (!(Test-Path -LiteralPath $compiler)) {
        New-Item -ItemType Directory -Force tools | Out-Null
        $archive=Join-Path $PSScriptRoot 'tools\nsis-3.13.zip'
        curl.exe -L --fail --silent --show-error 'https://downloads.sourceforge.net/project/nsis/NSIS%203/3.13/nsis-3.13.zip' -o $archive
        if ($LASTEXITCODE -ne 0) { throw 'NSIS download failed' }
        if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLower() -ne 'ba63dffc4410ee89193e1cb5a41989991bd77c61068da17e3156d136b7b0b3d8') { throw 'NSIS checksum mismatch' }
        Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $PSScriptRoot 'tools') -Force
    }
    & (Join-Path $PSScriptRoot 'setup-docx.ps1')
    dotnet publish src/ObsidianDocs -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false "-p:Version=$Version" -o $payload --nologo
    if ($LASTEXITCODE -ne 0) { throw '.NET publish failed' }
    if (!(Test-Path native/ocr-worker/target/release/ocr-worker.exe)) {
        cargo build --manifest-path native/ocr-worker/Cargo.toml --release --locked
        if ($LASTEXITCODE -ne 0) { throw 'Rust build failed' }
    }
    Copy-Item native/ocr-worker/target/release/ocr-worker.exe $payload -Force
    Copy-Item native/ocr-worker/target/release/*.dll $payload -Force
    $models=Join-Path $payload 'models'
    New-Item -ItemType Directory -Force $models | Out-Null
    foreach ($name in @('pp-ocrv5_mobile_det.onnx','en_pp-ocrv5_mobile_rec.onnx','eslav_pp-ocrv5_mobile_rec.onnx','ppocrv5_en_dict.txt','ppocrv5_eslav_dict.txt')) {
        $source=Join-Path $env:USERPROFILE ".oar\$name"
        $digestFile=Join-Path $env:USERPROFILE ".oar\.$name.sha256"
        if (!(Test-Path -LiteralPath $source) -or !(Test-Path -LiteralPath $digestFile)) { throw "OCR model missing: $name. Run EN/RU OCR once before packaging." }
        if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLower() -ne (Get-Content -LiteralPath $digestFile -Raw).Trim().ToLower()) { throw "OCR model checksum mismatch: $name" }
        Copy-Item -LiteralPath $source -Destination $models -Force
        Copy-Item -LiteralPath $digestFile -Destination $models -Force
    }
    $loDestination=Join-Path $payload 'native\libreoffice\runtime'
    robocopy native/libreoffice/runtime $loDestination /E /XF *.msi /NFL /NDL /NJH /NJS /NP
    if ($LASTEXITCODE -gt 7) { throw 'LibreOffice runtime copy failed' }
    # App-local Visual C++ redistributables shipped with the signed LO package.
    Copy-Item native/libreoffice/runtime/System64/*.dll $payload -Force
    Copy-Item native/libreoffice/runtime/System64/*.dll (Join-Path $loDestination 'program') -Force
    Copy-Item README.md (Join-Path $payload 'README.md') -Force
    $notices=Join-Path $payload 'ThirdPartyNotices'
    New-Item -ItemType Directory -Force $notices | Out-Null
    $nugetRoot=Join-Path $env:USERPROFILE '.nuget\packages'
    $assets=Get-Content src/ObsidianDocs/obj/project.assets.json -Raw | ConvertFrom-Json
    foreach ($property in $assets.libraries.PSObject.Properties) {
        if ($property.Value.type -ne 'package') { continue }
        $package=Join-Path $nugetRoot $property.Name.ToLower()
        if (!(Test-Path -LiteralPath $package)) { continue }
        $destination=Join-Path $notices ('nuget\'+$property.Name.Replace('/','-'))
        New-Item -ItemType Directory -Force $destination | Out-Null
        Get-ChildItem -LiteralPath $package -File | Where-Object {$_.Name -match '(?i)license|notice|copying|\.nuspec$'} | Copy-Item -Destination $destination -Force
    }
    $registry=Join-Path $env:USERPROFILE '.cargo\registry\src'
    $registryFolder=(Get-ChildItem -LiteralPath $registry -Directory | Select-Object -First 1).FullName
    $lock=Get-Content native/ocr-worker/Cargo.lock -Raw
    foreach ($match in [regex]::Matches($lock,'(?ms)\[\[package\]\]\s+name = "(?<name>[^"]+)"\s+version = "(?<version>[^"]+)"')) {
        $id=$match.Groups['name'].Value+'-'+$match.Groups['version'].Value
        $crate=Join-Path $registryFolder $id
        if (!(Test-Path -LiteralPath $crate)) { continue }
        $destination=Join-Path $notices ('rust\'+$id)
        New-Item -ItemType Directory -Force $destination | Out-Null
        Get-ChildItem -LiteralPath $crate -File | Where-Object {$_.Name -match '(?i)^license|^notice|^copying|^copyright|^Cargo.toml$'} | Copy-Item -Destination $destination -Force
    }
    $sourceNotices=Join-Path $PSScriptRoot 'packaging\notices'
    if (Test-Path -LiteralPath $sourceNotices) { Copy-Item "$sourceNotices\*" $notices -Recurse -Force }
    $rows=@('ObsiDopf '+$Version, 'LibreOffice runtime: native/libreoffice/runtime/LICENSE.html, license.txt and NOTICE', 'OCR: oar-ocr/PaddleOCR PP-OCRv5; EN and RU models included.', 'Third-party metadata and license texts are in this directory.', 'Model cache supplied without personal documents, profiles, API keys or logs.')
    $rows | Set-Content (Join-Path $notices 'README.txt') -Encoding utf8
    $files=Get-ChildItem -LiteralPath $payload -File -Recurse | Where-Object {$_.FullName -ne (Join-Path $payload 'payload-manifest.json')}
    $manifest=@($files | ForEach-Object { [pscustomobject]@{Path=$_.FullName.Substring($payload.Length+1);Length=$_.Length;SHA256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLower()} })
    $manifest | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $payload 'payload-manifest.json') -Encoding utf8
    # Generate exact-file deletion, preserving any user-created files on uninstall.
    $files=Get-ChildItem -LiteralPath $payload -File -Recurse
    $lines=[Collections.Generic.List[string]]::new()
    foreach ($file in $files) {
        $relative=$file.FullName.Substring($payload.Length+1).Replace('$','$$').Replace('"','$\"')
        $lines.Add('Delete "$INSTDIR\'+$relative+'"')
    }
    $directories=Get-ChildItem -LiteralPath $payload -Directory -Recurse | Sort-Object {$_.FullName.Length} -Descending
    foreach ($directory in $directories) { $relative=$directory.FullName.Substring($payload.Length+1).Replace('$','$$').Replace('"','$\"'); $lines.Add('RMDir "$INSTDIR\'+$relative+'"') }
    $lines | Set-Content packaging/uninstall-files.nsh -Encoding utf8
    $output=Join-Path $PSScriptRoot "dist\ObsiDopf-$Version-Setup-x64.exe"
    & $compiler /V2 /INPUTCHARSET UTF8 "/DPAYLOAD=$payload" "/DVERSION=$Version" "/DOUTPUT=$output" packaging/installer.nsi
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed' }
    Get-FileHash -LiteralPath $output -Algorithm SHA256 | ForEach-Object { $_.Hash.ToLower()+'  '+[IO.Path]::GetFileName($output) } | Set-Content ($output+'.sha256') -Encoding ascii
    Get-Item -LiteralPath $output | Select-Object FullName,Length
} finally { Pop-Location }


