param(
    [Parameter(Mandatory = $false)]
    [string]$PackagesRoot = (Join-Path $PSScriptRoot "..\packages"),

    [Parameter(Mandatory = $false)]
    [string]$ModRoot = (Join-Path $PSScriptRoot "..\rimworld-mods\AI_TranslationCore")
)

$ErrorActionPreference = "Stop"

$managedSource = Join-Path $PackagesRoot "System.Data.SQLite.2.0.4\lib\net471\System.Data.SQLite.dll"
$assemblyTarget = Join-Path $ModRoot "1.6\Assemblies\System.Data.SQLite.dll"

$nativeFiles = @(
    @{ Rid = "win-x64"; Source = "SourceGear.sqlite3.3.53.4\runtimes\win-x64\native\e_sqlite3.dll"; Name = "e_sqlite3.dll" },
    @{ Rid = "linux-x64"; Source = "SourceGear.sqlite3.3.53.4\runtimes\linux-x64\native\libe_sqlite3.so"; Name = "libe_sqlite3.so" },
    @{ Rid = "osx-x64"; Source = "SourceGear.sqlite3.3.53.4\runtimes\osx-x64\native\libe_sqlite3.dylib"; Name = "libe_sqlite3.dylib" },
    @{ Rid = "osx-arm64"; Source = "SourceGear.sqlite3.3.53.4\runtimes\osx-arm64\native\libe_sqlite3.dylib"; Name = "libe_sqlite3.dylib" }
)

if (-not (Test-Path -LiteralPath $managedSource -PathType Leaf)) {
    throw "Missing restored System.Data.SQLite provider: $managedSource"
}

$assemblyDirectory = Split-Path -Parent $assemblyTarget
New-Item -ItemType Directory -Path $assemblyDirectory -Force | Out-Null
Copy-Item -LiteralPath $managedSource -Destination $assemblyTarget -Force

foreach ($native in $nativeFiles) {
    $source = Join-Path $PackagesRoot $native.Source
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Missing restored SQLite native runtime for $($native.Rid): $source"
    }

    $targetDirectory = Join-Path $ModRoot ("1.6\Native\sqlite\" + $native.Rid)
    New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null
    $target = Join-Path $targetDirectory $native.Name
    Copy-Item -LiteralPath $source -Destination $target -Force
    $hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath ($target + ".sha256") -Value $hash -Encoding ascii -NoNewline
}

Write-Output "SQLite runtime assets prepared under $ModRoot\1.6"
