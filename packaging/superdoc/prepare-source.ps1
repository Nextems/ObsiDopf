param([string]$Version='1.1.0')
$ErrorActionPreference='Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must have the form major.minor.patch' }
$projectRoot=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$lock=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'source-lock.json') -Raw | ConvertFrom-Json
$cache=Join-Path $projectRoot '.release-work\source-cache'
New-Item -ItemType Directory -Force -Path $cache | Out-Null
function Get-PinnedArchive([string]$Url,[string]$File,[string]$Digest) {
    if (!(Test-Path -LiteralPath $File)) {
        & curl.exe -L --fail --silent --show-error $Url -o $File
        if ($LASTEXITCODE -ne 0) { throw "Source download failed: $Url" }
    }
    if ((Get-FileHash -LiteralPath $File -Algorithm SHA256).Hash.ToLowerInvariant() -ne $Digest) {
        throw "Source archive checksum mismatch: $File. The existing cache was not changed."
    }
}
$upstream=Join-Path $cache 'superdoc-66c22186.zip'
$npm=Join-Path $cache 'superdoc-1.47.2.tgz'
Get-PinnedArchive $lock.sourceArchiveUrl $upstream $lock.sourceArchiveSha256
Get-PinnedArchive $lock.npmArchiveUrl $npm $lock.npmArchiveSha256
$stage=Join-Path $projectRoot ('dist\superdoc-source-'+$Version+'-'+(Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
$upstreamDirectory=Join-Path $stage 'upstream'
New-Item -ItemType Directory -Force -Path $upstreamDirectory | Out-Null
Copy-Item -LiteralPath $upstream -Destination (Join-Path $upstreamDirectory 'superdoc-66c22186.zip')
Copy-Item -LiteralPath $npm -Destination (Join-Path $upstreamDirectory 'superdoc-1.47.2.tgz')
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive=[IO.Compression.ZipFile]::OpenRead($upstream)
try {
    $relative='packages/super-editor/src/editors/v1/core/presentation-editor/PresentationEditor.ts'
    $entries=@($archive.Entries | Where-Object { $_.FullName.EndsWith('/'+$relative,[StringComparison]::Ordinal) })
    if ($entries.Count -ne 1) { throw 'Pinned source archive has no unique PresentationEditor.ts' }
    $entry=$entries[0]
    [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,(Join-Path $upstreamDirectory 'PresentationEditor.ts'))
} finally { $archive.Dispose() }
$npmDirectory=Join-Path $stage 'npm-package'
New-Item -ItemType Directory -Force -Path $npmDirectory | Out-Null
& tar.exe -xzf $npm -C $npmDirectory
if ($LASTEXITCODE -ne 0) { throw 'Cannot extract the published npm package' }
& node (Join-Path $PSScriptRoot 'collect-sources.mjs') $stage (Join-Path $npmDirectory 'package')
if ($LASTEXITCODE -ne 0) { throw 'Cannot collect the engine sources' }
$sourceArchive=Join-Path $projectRoot "dist\ObsiDopf-$Version-SuperDoc-1.47.2-Source.zip"
$stream=[IO.File]::Open($sourceArchive,[IO.FileMode]::Create,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
$zip=[IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Create,$false)
try {
    # The full npm package is already preserved as its original tgz. Include the
    # extracted source-map originals separately, without duplicating dist/types.
    foreach ($file in (Get-ChildItem -LiteralPath $stage -Recurse -File | Where-Object { !$_.FullName.StartsWith($npmDirectory+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) } | Sort-Object FullName)) {
        $relative=$file.FullName.Substring($stage.Length+1).Replace('\','/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$file.FullName,$relative,[IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $zip.Dispose(); $stream.Dispose() }
$hash=(Get-FileHash -LiteralPath $sourceArchive -Algorithm SHA256).Hash.ToLowerInvariant()
($hash+'  '+[IO.Path]::GetFileName($sourceArchive)) | Set-Content -LiteralPath ($sourceArchive+'.sha256') -Encoding ascii
Get-Item -LiteralPath $sourceArchive | Select-Object FullName,Length
