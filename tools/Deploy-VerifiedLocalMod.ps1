param(
    [Parameter(Mandatory)][string]$DeliveryPath,
    [string]$RimWorldRoot = 'E:\SteamLibrary\steamapps\common\RimWorld'
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$delivery = (Resolve-Path -LiteralPath $DeliveryPath).Path
$source = Join-Path $delivery 'player/AI_TranslationCore'
$target = Join-Path $RimWorldRoot 'Mods/AI_TranslationCore'
$manifest = Get-Content -LiteralPath (Join-Path $delivery 'verification.json') -Raw | ConvertFrom-Json
if (Get-Process -ErrorAction SilentlyContinue | Where-Object ProcessName -Match '^RimWorld') { throw 'Close RimWorld before deployment.' }
foreach ($root in @($source, $target)) {
    $about = [xml](Get-Content -LiteralPath (Join-Path $root 'About/About.xml') -Raw)
    if ($about.ModMetaData.packageId -ine 'Auto.AITranslation.Core') { throw "Unexpected mod identity: $root" }
}
$files = @(Get-ChildItem -LiteralPath $source -File -Recurse | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath($source, $_.FullName).Replace('\', '/')
    $record = @($manifest.files | Where-Object path -CEQ "AI_TranslationCore/$relative")
    $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    if ($record.Count -ne 1 -or $hash -ne $record[0].sha256) { throw "Unverified package file: $relative" }
    [pscustomobject]@{ Relative = $relative; Source = $_.FullName; SHA256 = $hash }
})
foreach ($version in @('1.5','1.6','1.7')) {
    if (!($files.Relative -contains "$version/Assemblies/RimWorld_Auto_AI_Translation_Core.dll")) { throw "Missing version $version" }
}
foreach ($file in $files | Where-Object Relative -Like '*.xml') { $null = [xml](Get-Content -LiteralPath $file.Source -Raw) }
$backup = Join-Path $repo ('backups/local-verified-deploy-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $backup | Out-Null
Copy-Item -LiteralPath $target -Destination (Join-Path $backup 'installed') -Recurse
if (Get-Process -ErrorAction SilentlyContinue | Where-Object ProcessName -Match '^RimWorld') { throw 'RimWorld started; deployment cancelled.' }
$changed = [Collections.Generic.List[object]]::new()
try {
    foreach ($file in $files) {
        $destination = Join-Path $target $file.Relative
        $existed = Test-Path -LiteralPath $destination -PathType Leaf
        if ($existed -and (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -eq $file.SHA256) { continue }
        $changed.Add([pscustomobject]@{ Relative = $file.Relative; Existed = $existed })
        New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
        Copy-Item -LiteralPath $file.Source -Destination $destination -Force
    }
    foreach ($file in $files) {
        if ((Get-FileHash -LiteralPath (Join-Path $target $file.Relative) -Algorithm SHA256).Hash -ne $file.SHA256) {
            throw "Installed hash mismatch: $($file.Relative)"
        }
    }
} catch {
    foreach ($item in $changed) {
        $destination = Join-Path $target $item.Relative
        if ($item.Existed) { Copy-Item -LiteralPath (Join-Path $backup "installed/$($item.Relative)") -Destination $destination -Force }
        elseif (Test-Path -LiteralPath $destination -PathType Leaf) { Remove-Item -LiteralPath $destination }
    }
    throw
}
[ordered]@{ installedAt = (Get-Date).ToString('o'); delivery = $delivery; target = $target; backup = $backup;
    filesVerified = $files.Count; filesChanged = $changed.Count; gameLaunched = $false; files = $files
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $backup 'verification.json') -Encoding utf8
Write-Output "Installed and hash-verified $($files.Count) files; changed $($changed.Count)."
Write-Output "Target: $target"
Write-Output "Backup: $backup"
