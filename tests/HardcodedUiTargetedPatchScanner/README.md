# Hardcoded UI targeted patch prototype

This tool is deliberately separate from the RimWorld DLL. It reads an assembly
with Mono.Cecil and reflection-only metadata, so the scanned assembly is not
executed or rewritten. The first prototype accepts only a literal immediately
followed by `Verse.Widgets.Label(UnityEngine.Rect, string)`.

Build the fixture and scanner with the .NET Framework 4.8 reference pack:

```powershell
dotnet build tests/HardcodedUiFixture/HardcodedUiFixture.csproj -c Release
dotnet build tests/HardcodedUiTargetedPatchScanner/HardcodedUiTargetedPatchScanner.csproj -c Release
```

Generate a report-only manifest (the default):

```powershell
& tests/HardcodedUiTargetedPatchScanner/bin/Release/net48/HardcodedUiTargetedPatchScanner.exe `
  tests/HardcodedUiFixture/bin/Release/net48/HardcodedUiFixture.dll `
  tests/HardcodedUiTargetedPatchScanner/fixture-manifest.json `
  --package-id atc.hardcodedui.fixture `
  --relative-path Assemblies/HardcodedUiFixture.dll `
  --translation '硬編碼測試標籤'
```

The runtime still refuses the file until the top-level `approved` property is
set to `true`. It also requires the fixture package id, the exact assembly
SHA-256, method fingerprint, method signature, literal ordinal, and supported
call signature (plus the assembly MVID and method metadata token). Any mismatch
leaves the original text untouched.
