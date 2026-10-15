param(
    [ValidateSet('1.5', '1.6', '1.7')][string[]]$Versions = @('1.5', '1.6', '1.7'),
    [string]$RimWorldRoot = 'E:\SteamLibrary\steamapps\common\RimWorld',
    [string]$RimWorld15ManagedDir = "$env:USERPROFILE\Documents\rimworld 1.5 data\RimWorldWin64_Data\Managed",
    [string]$RimWorld16ManagedDir = '',
    [string]$Harmony17Path = 'E:\SteamLibrary\steamapps\workshop\content\294100\2009463077\1.7\Assemblies\0Harmony.dll',
    [switch]$Deploy
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectDir = Join-Path $repoRoot 'RimWorld-Auto-AI-Translation_Core'
$packageRoot = Join-Path $repoRoot 'rimworld-mods\AI_TranslationCore'
$steamMod = Join-Path $RimWorldRoot 'Mods\AI_TranslationCore'
$assemblyName = 'RimWorld_Auto_AI_Translation_Core'
$backupRoot = Join-Path $repoRoot ('backups\steam-three-version-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
$versionsToBuild = @($Versions | Select-Object -Unique)

if ($Deploy -and (Get-Process -ErrorAction SilentlyContinue | Where-Object ProcessName -Match '^RimWorld')) {
    throw 'Close RimWorld before deploying its mod assemblies.'
}
if ($Deploy -and !(Test-Path -LiteralPath (Join-Path $steamMod 'About\About.xml'))) {
    throw "Expected an existing Steam mod at $steamMod"
}

$builds = @{
    '1.5' = @{ Project = 'RimWorld-Auto-AI-Translation_Core.RW15.csproj'; Output = 'Release-RW15' }
    '1.6' = @{ Project = 'RimWorld-Auto-AI-Translation_Core.csproj'; Output = 'Release' }
    '1.7' = @{ Project = 'RimWorld-Auto-AI-Translation_Core.RW17.csproj'; Output = 'Release-RW17' }
}

Push-Location $repoRoot
try {
    # Finish every requested build before changing any packaged or installed file.
    foreach ($version in $versionsToBuild) {
        $project = Join-Path $projectDir $builds[$version].Project
        if ($version -eq '1.7') {
            & dotnet build $project -c Release -v minimal "-p:RimWorld17Dir=$RimWorldRoot" "-p:Harmony17Path=$Harmony17Path"
        } else {
            $arguments = @('msbuild', $project, '-t:Rebuild', '-p:Configuration=Release', '-v:minimal')
            if ($version -eq '1.5') { $arguments += "-p:RimWorld15ManagedDir=$RimWorld15ManagedDir" }
            if ($version -eq '1.6' -and $RimWorld16ManagedDir) { $arguments += "-p:RimWorld16ManagedDir=$RimWorld16ManagedDir" }
            & dotnet @arguments
        }
        if ($LASTEXITCODE -ne 0) { throw "Build failed for $version" }
    }

    $copies = @()
    foreach ($version in $versionsToBuild) {
        $output = Join-Path $projectDir ('bin\' + $builds[$version].Output)
        foreach ($name in @("$assemblyName.dll", "$assemblyName.pdb", 'Newtonsoft.Json.dll')) {
            $source = Join-Path $output $name
            if (!(Test-Path -LiteralPath $source)) { throw "Missing build output $source" }
            $copies += [pscustomobject]@{ Source = $source; Relative = "$version\Assemblies\$name" }
        }
    }

    # Back up only affected binaries and metadata. Never mirror-delete the mod,
    # its translations, player settings, or the generated translation pack.
    foreach ($item in $copies) {
        foreach ($location in @(@{Root=$packageRoot;Label='package'}, @{Root=$steamMod;Label='steam'})) {
            if ($location.Label -eq 'steam' -and !$Deploy) { continue }
            $old = Join-Path $location.Root $item.Relative
            if (Test-Path -LiteralPath $old) {
                $backup = Join-Path $backupRoot ($location.Label + '\' + $item.Relative)
                New-Item -ItemType Directory -Path (Split-Path $backup) -Force | Out-Null
                Copy-Item -LiteralPath $old -Destination $backup
            }
        }
    }
    foreach ($relative in @('About\About.xml', 'LoadFolders.xml')) {
        if ($Deploy) {
            $backup = Join-Path $backupRoot ('steam\' + $relative)
            New-Item -ItemType Directory -Path (Split-Path $backup) -Force | Out-Null
            Copy-Item -LiteralPath (Join-Path $steamMod $relative) -Destination $backup
        }
    }
    if ($Deploy -and (Get-Process -ErrorAction SilentlyContinue | Where-Object ProcessName -Match '^RimWorld')) {
        throw 'RimWorld started during the build; deployment cancelled.'
    }

    foreach ($item in $copies) {
        $destination = Join-Path $packageRoot $item.Relative
        New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
        Copy-Item -LiteralPath $item.Source -Destination $destination -Force
        if ($Deploy) {
            $destination = Join-Path $steamMod $item.Relative
            New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
            Copy-Item -LiteralPath $item.Source -Destination $destination -Force
        }
    }
    # Publish metadata only after every advertised version has its own assembly.
    [xml]$metadata = Get-Content -LiteralPath (Join-Path $packageRoot 'About\About.xml') -Raw
    foreach ($version in $metadata.ModMetaData.supportedVersions.li) {
        if (!(Test-Path -LiteralPath (Join-Path $packageRoot "$version\Assemblies\$assemblyName.dll"))) {
            throw "Package advertises $version without its assembly"
        }
        if ($Deploy -and !(Test-Path -LiteralPath (Join-Path $steamMod "$version\Assemblies\$assemblyName.dll"))) {
            throw "Steam installation is missing advertised version $version"
        }
    }
    if ($Deploy) {
        foreach ($relative in @('About\About.xml', 'LoadFolders.xml')) {
            Copy-Item -LiteralPath (Join-Path $packageRoot $relative) -Destination (Join-Path $steamMod $relative) -Force
        }
    }
    $verification = foreach ($item in $copies) {
        $sourceHash = (Get-FileHash -LiteralPath $item.Source -Algorithm SHA256).Hash
        $packageHash = (Get-FileHash -LiteralPath (Join-Path $packageRoot $item.Relative) -Algorithm SHA256).Hash
        $steamHash = if ($Deploy) { (Get-FileHash -LiteralPath (Join-Path $steamMod $item.Relative) -Algorithm SHA256).Hash } else { $null }
        if ($sourceHash -ne $packageHash -or ($Deploy -and $sourceHash -ne $steamHash)) { throw "Hash mismatch: $($item.Relative)" }
        [pscustomobject]@{ File = $item.Relative; SHA256 = $sourceHash; SteamVerified = [bool]$Deploy }
    }
    if ($Deploy) {
        $generatedAbout = Join-Path $RimWorldRoot 'Mods\!Translation_AI_Pack\About\About.xml'
        if (Test-Path -LiteralPath $generatedAbout) {
            [xml]$generated = Get-Content -LiteralPath $generatedAbout -Raw
            if ($generated.ModMetaData.packageId -eq 'AITranslation.Pack') {
                $savedAbout = Join-Path $backupRoot 'generated-pack\About\About.xml'
                New-Item -ItemType Directory -Path (Split-Path $savedAbout) -Force | Out-Null
                Copy-Item -LiteralPath $generatedAbout -Destination $savedAbout
                $versionNode = $generated.SelectSingleNode('/ModMetaData/supportedVersions')
                if ($null -eq $versionNode) { $versionNode = $generated.DocumentElement.AppendChild($generated.CreateElement('supportedVersions')) }
                foreach ($version in @('1.5', '1.6', '1.7')) {
                    if ($null -eq $versionNode.SelectSingleNode("li[text()='$version']")) {
                        $item = $generated.CreateElement('li')
                        $item.InnerText = $version
                        [void]$versionNode.AppendChild($item)
                    }
                }
                $generated.Save($generatedAbout)
            }
        }
    }
    New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
    $verification | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $backupRoot 'verification.json') -Encoding UTF8
    $verification | Select-Object File, SHA256 | Format-Table -AutoSize
    Write-Host "Verified $($verification.Count) packaged files; Steam deployment: $([bool]$Deploy)"
    Write-Host "Backup and verification: $backupRoot"
} finally {
    Pop-Location
}
