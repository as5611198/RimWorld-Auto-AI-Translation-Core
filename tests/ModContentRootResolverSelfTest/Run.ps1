param(
    [string]$RimWorldManaged = 'E:\SteamLibrary\steamapps\common\RimWorld\RimWorldWin64_Data\Managed',
    [string]$AssemblyPath = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$resolvedAssemblyPath = if ([string]::IsNullOrWhiteSpace($AssemblyPath)) {
    Join-Path $repoRoot 'RimWorld-Auto-AI-Translation_Core\bin\Release\RimWorld_Auto_AI_Translation_Core.dll'
} elseif ([IO.Path]::IsPathRooted($AssemblyPath)) {
    [IO.Path]::GetFullPath($AssemblyPath)
} else {
    [IO.Path]::GetFullPath((Join-Path $repoRoot $AssemblyPath))
}
$harmonyPath = Join-Path $repoRoot 'packages\Lib.Harmony.2.4.2\lib\net48\0Harmony.dll'
$jsonPath = Join-Path $repoRoot 'packages\Newtonsoft.Json.13.0.4\lib\net45\Newtonsoft.Json.dll'

if (!(Test-Path -LiteralPath $resolvedAssemblyPath -PathType Leaf)) {
    throw "Build the Release assembly before running this test: $resolvedAssemblyPath"
}

Get-ChildItem -LiteralPath $RimWorldManaged -File -Filter '*.dll' | ForEach-Object {
    try { [void][Reflection.Assembly]::LoadFrom($_.FullName) } catch { }
}
[void][Reflection.Assembly]::LoadFrom($harmonyPath)
[void][Reflection.Assembly]::LoadFrom($jsonPath)
$assembly = [Reflection.Assembly]::LoadFrom($resolvedAssemblyPath)
$scannerType = $assembly.GetType('AutoTranslator_Core.AutoTranslatorScanner', $true)
$resolveMethod = $scannerType.GetMethod(
    'GetAllEffectiveDefsPaths',
    [Reflection.BindingFlags]'Static,Public',
    $null,
    [Type[]]@([string], [string]),
    $null)
$resolveLangMethod = $scannerType.GetMethod(
    'GetAllEffectiveLangPaths',
    [Reflection.BindingFlags]'Static,Public',
    $null,
    [Type[]]@([string], [string]),
    $null)
$hasExactVersionMethod = $scannerType.GetMethod(
    'HasExactLoadFolderVersion',
    [Reflection.BindingFlags]'Static,NonPublic')
if ($null -eq $hasExactVersionMethod) {
    throw 'Could not resolve HasExactLoadFolderVersion from the production assembly.'
}
$currentVersionProperty = $scannerType.GetProperty(
    'CurrentRimWorldVersion',
    [Reflection.BindingFlags]'Static,NonPublic')
if ($null -eq $currentVersionProperty) {
    throw 'Could not resolve CurrentRimWorldVersion from the production assembly.'
}
$currentVersion = [string]$currentVersionProperty.GetValue($null, $null)
$modType = $assembly.GetType('AutoTranslator_Core.AutoTranslatorMod', $true)
$settingsType = $assembly.GetType('AutoTranslator_Core.AutoTranslatorSettings', $true)
$settingsField = $modType.GetField('Settings', [Reflection.BindingFlags]'Static,Public')
$forcePackagesField = $settingsType.GetField(
    'ForceTranslationPackages',
    [Reflection.BindingFlags]'Instance,Public')
if ($null -eq $settingsField -or $null -eq $forcePackagesField) {
    throw 'Could not resolve force-translation settings from the production assembly.'
}
$originalSettings = $settingsField.GetValue($null)

$tempBase = [IO.Path]::GetTempPath()
$tempRoot = Join-Path $tempBase ('ATC_ModContentRootSelfTest_' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($tempRoot) | Out-Null

function Add-DefsRoot([string]$root) {
    $defs = Join-Path $root 'Defs'
    [IO.Directory]::CreateDirectory($defs) | Out-Null
    [IO.File]::WriteAllText((Join-Path $defs 'Fixture.xml'), '<Defs><ThingDef><defName>Fixture</defName></ThingDef></Defs>')
}

function Add-LanguageRoot([string]$root) {
    $keyed = Join-Path $root 'Languages\English\Keyed'
    [IO.Directory]::CreateDirectory($keyed) | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $keyed 'Fixture.xml'),
        '<LanguageData><Fixture.label>fixture</Fixture.label></LanguageData>')
}

function Set-LoadFolders([string]$root, [string]$xml) {
    [IO.File]::WriteAllText((Join-Path $root 'LoadFolders.xml'), $xml)
}

function Resolve-Defs([string]$root, [string]$packageId = '') {
    if ([string]::IsNullOrWhiteSpace($packageId)) {
        $packageId = 'test.fixture.' + [Guid]::NewGuid().ToString('N')
    }
    $arguments = [object[]]@($packageId, $root)
    $resolved = $resolveMethod.Invoke($null, $arguments)
    return @($resolved | ForEach-Object { [IO.Path]::GetFullPath([string]$_) })
}

function Resolve-Languages([string]$root, [string]$packageId = '') {
    if ([string]::IsNullOrWhiteSpace($packageId)) {
        $packageId = 'test.fixture.' + [Guid]::NewGuid().ToString('N')
    }
    $resolved = $resolveLangMethod.Invoke($null, [object[]]@($packageId, $root))
    return @($resolved | ForEach-Object { [IO.Path]::GetFullPath([string]$_) })
}

function Assert-Paths([string]$name, [string[]]$actual, [string[]]$expected) {
    if ($actual.Count -ne $expected.Count) {
        throw "$name expected $($expected.Count) paths, got $($actual.Count): $($actual -join '; ')"
    }
    for ($index = 0; $index -lt $expected.Count; $index++) {
        $expectedPath = [IO.Path]::GetFullPath($expected[$index])
        if (![string]::Equals($actual[$index], $expectedPath, [StringComparison]::OrdinalIgnoreCase)) {
            throw "$name path $index expected '$expectedPath', got '$($actual[$index])'"
        }
    }
    Write-Host "PASS $name"
}

function Assert-PathSet([string]$name, [string[]]$actual, [string[]]$expected) {
    $actualSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $expectedSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($path in $actual) { [void]$actualSet.Add([IO.Path]::GetFullPath($path)) }
    foreach ($path in $expected) { [void]$expectedSet.Add([IO.Path]::GetFullPath($path)) }
    if (!$actualSet.SetEquals($expectedSet)) {
        throw "$name expected '$($expectedSet -join '; ')', got '$($actualSet -join '; ')'"
    }
    Write-Host "PASS $name"
}

try {
    $definedExactVersions = [Collections.Generic.List[string]]::new()
    $definedExactVersions.Add('1.5')
    $definedExactVersions.Add('1.6')
    $hasExactVersion = [bool]$hasExactVersionMethod.Invoke(
        $null,
        [object[]]@($definedExactVersions, $currentVersion))
    if (!$hasExactVersion) {
        throw 'defined exact version must remain authoritative even when its branch has no entries'
    }

    $malformedExactVersions = [Collections.Generic.List[string]]::new()
    $malformedExactVersions.Add('v' + $currentVersion + 'foo')
    $hasMalformedExactVersion = [bool]$hasExactVersionMethod.Invoke(
        $null,
        [object[]]@($malformedExactVersions, $currentVersion))
    if ($hasMalformedExactVersion) {
        throw 'malformed exact version lookalike must not select a manifest branch'
    }
    Write-Host 'PASS exact branch identity uses strict defined-version text'

    $physical = Join-Path $tempRoot 'physical-fallback'
    [IO.Directory]::CreateDirectory($physical) | Out-Null
    Add-DefsRoot (Join-Path $physical '1.5')
    Add-DefsRoot (Join-Path $physical 'Common')
    Add-DefsRoot $physical
    Assert-Paths 'physical prior/Common/root fallback' (Resolve-Defs $physical) @(
        (Join-Path $physical '1.5\Defs'),
        (Join-Path $physical 'Common\Defs'),
        (Join-Path $physical 'Defs'))

    $mixed = Join-Path $tempRoot 'mixed-normal-and-custom'
    [IO.Directory]::CreateDirectory($mixed) | Out-Null
    Add-DefsRoot $mixed
    Add-DefsRoot (Join-Path $mixed 'CustomContent')
    Add-LanguageRoot $mixed
    Add-LanguageRoot (Join-Path $mixed 'CustomContent')
    $mixedPackageId = 'test.fixture.force.' + [Guid]::NewGuid().ToString('N')
    Assert-Paths 'normal scan excludes a nested custom content root' (Resolve-Defs $mixed $mixedPackageId) @(
        (Join-Path $mixed 'Defs'))
    Assert-Paths 'normal language scan excludes a nested custom content root' (Resolve-Languages $mixed $mixedPackageId) @(
        (Join-Path $mixed 'Languages'))

    $forceSettings = [Activator]::CreateInstance($settingsType)
    $forcePackages = $forcePackagesField.GetValue($forceSettings)
    [void]$forcePackages.Add($mixedPackageId)
    $settingsField.SetValue($null, $forceSettings)
    Assert-PathSet 'force scan adds the nested custom content root' (Resolve-Defs $mixed $mixedPackageId) @(
        (Join-Path $mixed 'Defs'),
        (Join-Path $mixed 'CustomContent\Defs'))
    Assert-PathSet 'force language scan adds the nested custom content root' (Resolve-Languages $mixed $mixedPackageId) @(
        (Join-Path $mixed 'Languages'),
        (Join-Path $mixed 'CustomContent\Languages'))

    $multiVersion = Join-Path $tempRoot 'force-excludes-inactive-versions'
    [IO.Directory]::CreateDirectory($multiVersion) | Out-Null
    Add-DefsRoot (Join-Path $multiVersion '1.5')
    Add-DefsRoot (Join-Path $multiVersion '1.6')
    $multiVersionPackageId = 'test.fixture.force.' + [Guid]::NewGuid().ToString('N')
    Assert-Paths 'normal scan selects only the current physical version' (Resolve-Defs $multiVersion $multiVersionPackageId) @(
        (Join-Path $multiVersion ($currentVersion + '\Defs')))
    [void]$forcePackages.Add($multiVersionPackageId)
    Assert-Paths 'force scan still excludes inactive physical versions' (Resolve-Defs $multiVersion $multiVersionPackageId) @(
        (Join-Path $multiVersion ($currentVersion + '\Defs')))

    $conditionalForce = Join-Path $tempRoot 'force-excludes-inactive-conditional'
    [IO.Directory]::CreateDirectory($conditionalForce) | Out-Null
    Add-DefsRoot $conditionalForce
    Add-DefsRoot (Join-Path $conditionalForce 'Conditional')
    Set-LoadFolders $conditionalForce ('<loadFolders><v{0}><li IfModActive="definitely.not.active">Conditional</li><li>/</li></v{0}></loadFolders>' -f $currentVersion)
    $conditionalForcePackageId = 'test.fixture.force.' + [Guid]::NewGuid().ToString('N')
    Assert-Paths 'normal scan excludes an inactive conditional integration' (Resolve-Defs $conditionalForce $conditionalForcePackageId) @(
        (Join-Path $conditionalForce 'Defs'))
    [void]$forcePackages.Add($conditionalForcePackageId)
    Assert-Paths 'force scan still excludes an inactive conditional integration' (Resolve-Defs $conditionalForce $conditionalForcePackageId) @(
        (Join-Path $conditionalForce 'Defs'))
    $settingsField.SetValue($null, $originalSettings)

    $strictPhysical = Join-Path $tempRoot 'physical-strict-version'
    [IO.Directory]::CreateDirectory($strictPhysical) | Out-Null
    Add-DefsRoot (Join-Path $strictPhysical ($currentVersion + 'foo'))
    Add-DefsRoot (Join-Path $strictPhysical '1.5')
    Assert-Paths 'physical version names require a complete parse' (Resolve-Defs $strictPhysical) @(
        (Join-Path $strictPhysical '1.5\Defs'))

    $prior = Join-Path $tempRoot 'manifest-prior'
    [IO.Directory]::CreateDirectory($prior) | Out-Null
    Add-DefsRoot (Join-Path $prior '1.5')
    Add-DefsRoot $prior
    Set-LoadFolders $prior '<loadFolders><v1.5><li>1.5</li></v1.5></loadFolders>'
    Assert-Paths 'manifest prior fallback' (Resolve-Defs $prior) @((Join-Path $prior '1.5\Defs'))

    $default = Join-Path $tempRoot 'manifest-default'
    [IO.Directory]::CreateDirectory($default) | Out-Null
    Add-DefsRoot (Join-Path $default 'DefaultContent')
    Add-DefsRoot $default
    Set-LoadFolders $default '<loadFolders><default><li>DefaultContent</li></default></loadFolders>'
    Assert-Paths 'manifest default fallback' (Resolve-Defs $default) @((Join-Path $default 'DefaultContent\Defs'))

    $strictManifest = Join-Path $tempRoot 'manifest-strict-version'
    [IO.Directory]::CreateDirectory($strictManifest) | Out-Null
    Add-DefsRoot (Join-Path $strictManifest 'InvalidContent')
    Add-DefsRoot (Join-Path $strictManifest 'DefaultContent')
    Set-LoadFolders $strictManifest ('<loadFolders><v{0}foo><li>InvalidContent</li></v{0}foo><default><li>DefaultContent</li></default></loadFolders>' -f $currentVersion)
    Assert-Paths 'manifest version names require a complete parse' (Resolve-Defs $strictManifest) @(
        (Join-Path $strictManifest 'DefaultContent\Defs'))

    $empty = Join-Path $tempRoot 'manifest-empty'
    [IO.Directory]::CreateDirectory($empty) | Out-Null
    Add-DefsRoot $empty
    Set-LoadFolders $empty ('<loadFolders><v{0}></v{0}></loadFolders>' -f $currentVersion)
    Assert-Paths 'selected empty branch stays empty' (Resolve-Defs $empty) @()

    $rootOnly = Join-Path $tempRoot 'manifest-root-only'
    [IO.Directory]::CreateDirectory($rootOnly) | Out-Null
    Add-DefsRoot $rootOnly
    Add-DefsRoot (Join-Path $rootOnly '1.5')
    Set-LoadFolders $rootOnly ('<loadFolders><v{0}><li>/</li></v{0}></loadFolders>' -f $currentVersion)
    Assert-Paths 'root-only branch does not merge physical fallback' (Resolve-Defs $rootOnly) @((Join-Path $rootOnly 'Defs'))

    $precedence = Join-Path $tempRoot 'manifest-precedence'
    [IO.Directory]::CreateDirectory($precedence) | Out-Null
    Add-DefsRoot (Join-Path $precedence 'First')
    Add-DefsRoot (Join-Path $precedence 'Second')
    Set-LoadFolders $precedence ('<loadFolders><v{0}><li>First</li><li>Second</li></v{0}></loadFolders>' -f $currentVersion)
    Assert-Paths 'manifest paths use descending load order' (Resolve-Defs $precedence) @(
        (Join-Path $precedence 'Second\Defs'),
        (Join-Path $precedence 'First\Defs'))

    $conditional = Join-Path $tempRoot 'manifest-conditional'
    [IO.Directory]::CreateDirectory($conditional) | Out-Null
    Add-DefsRoot (Join-Path $conditional 'Conditional')
    Add-DefsRoot $conditional
    Set-LoadFolders $conditional ('<loadFolders><v{0}><li IfModActive="definitely.not.active">Conditional</li></v{0}></loadFolders>' -f $currentVersion)
    Assert-Paths 'inactive conditional branch stays empty' (Resolve-Defs $conditional) @()
}
finally {
    $settingsField.SetValue($null, $originalSettings)
    $resolvedTemp = [IO.Path]::GetFullPath($tempRoot)
    $resolvedBase = [IO.Path]::GetFullPath($tempBase)
    if ($resolvedTemp.StartsWith($resolvedBase, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedTemp).StartsWith('ATC_ModContentRootSelfTest_', [StringComparison]::Ordinal)) {
        [IO.Directory]::Delete($resolvedTemp, $true)
    }
}
