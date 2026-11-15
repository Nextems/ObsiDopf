param([string]$Version='1.1.0',[switch]$SourcesOnly)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must have the form major.minor.patch' }
    $outputRoot=Join-Path $projectRoot 'dist'
    $installer=Join-Path $outputRoot "ObsiDopf-$Version-Setup-x64.exe"
    if (!$SourcesOnly -and !(Test-Path -LiteralPath $installer)) { throw 'Build the installer first, or use -SourcesOnly for source preparation.' }
    $engineArchive=Join-Path $outputRoot "ObsiDopf-$Version-SuperDoc-1.47.2-Source.zip"
    $engineInputs=@(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'packaging\superdoc') -File)
    $engineInputs+=@(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'web\word') -File | Where-Object { $_.Extension -in @('.mjs','.js','.css','.html','.json','.md','.txt') })
    $engineInputs+=@(Get-Item -LiteralPath (Join-Path $projectRoot 'LICENSE'),(Join-Path $projectRoot 'COPYRIGHT.md'))
    if (!(Test-Path -LiteralPath $engineArchive) -or ($engineInputs | Where-Object { $_.LastWriteTimeUtc -gt (Get-Item -LiteralPath $engineArchive).LastWriteTimeUtc })) {
        & (Join-Path $projectRoot 'packaging\superdoc\prepare-source.ps1') -Version $Version
        if (!$?) { throw 'Cannot prepare the engine source archive' }
    }
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    # Archive the current worktree, including new source files. git archive HEAD
    # would silently omit the unpublished editor/chat changes.
    $relativePaths=@(& git -c core.quotepath=false ls-files --cached --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate release source files' }
    $sourceArchive=Join-Path $outputRoot "ObsiDopf-$Version-Source.zip"
    $stream=[IO.File]::Open($sourceArchive,[IO.FileMode]::Create,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    $zip=[IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Create,$false)
    try {
        foreach ($relativePath in ($relativePaths | Sort-Object -Unique)) {
            $sourcePath=Join-Path $projectRoot $relativePath
            if (!(Test-Path -LiteralPath $sourcePath -PathType Leaf)) { continue }
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$sourcePath,
                ('ObsiDopf-'+$Version+'/'+$relativePath.Replace('\','/')),
                [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $zip.Dispose(); $stream.Dispose() }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'RELEASE-NOTES.md') -Destination (Join-Path $outputRoot "ObsiDopf-$Version-Release-Notes.md") -Force
    $artifactPaths=@($sourceArchive,$engineArchive,(Join-Path $outputRoot "ObsiDopf-$Version-Release-Notes.md"))
    if (!$SourcesOnly) { $artifactPaths=@($installer)+$artifactPaths }
    $artifacts=@(foreach ($path in $artifactPaths) {
        $file=Get-Item -LiteralPath $path
        [pscustomobject]@{Name=$file.Name;Bytes=$file.Length;SHA256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}
    })
    $artifacts | ForEach-Object { $_.SHA256+'  '+$_.Name } | Set-Content -LiteralPath (Join-Path $outputRoot "ObsiDopf-$Version-SHA256SUMS.txt") -Encoding ascii
    [ordered]@{
        Product='ObsiDopf'; Version=$Version; Platform='win-x64'; Stage=$(if($SourcesOnly){'source-preparation'}else{'release-candidate'})
        PreparedUtc=[DateTime]::UtcNow.ToString('o'); BaseCommit=(& git rev-parse HEAD).Trim()
        Source='Current working tree, including uncommitted files; Source.zip is the build source snapshot.'
        Build=$(if($SourcesOnly){'Source preparation only; this command did not build an installer.'}else{'dotnet publish Release, self-contained; NSIS solid LZMA'})
        NativeComponents='Unchanged OCR and LibreOffice reused from the previous installed release.'
        Validation='Compilation and packaging only. No application, installer, functional tests or Gemini API requests executed.'
        Prerequisites=@('Windows 10/11 x64','Microsoft Edge WebView2 Runtime for DOC/DOCX')
        License='Original ObsiDopf code: AGPL-3.0-only; third-party licenses preserved.'
        EngineSource='SuperDoc 1.47.2: pinned repository snapshot, complete published source-map contents, original npm archive, preferred-source changes and build instructions.'
        Publication='Not published. Finish the component notice review before public distribution.'
        Artifacts=$artifacts
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outputRoot "ObsiDopf-$Version-Release.json") -Encoding utf8
    $artifacts | Select-Object Name,Bytes
} finally { Pop-Location }
