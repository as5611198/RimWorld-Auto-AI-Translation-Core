# Scanner integration across local game builds

Default invocation still builds and tests the local RW17 product:

```powershell
dotnet run --project tests/DefXmlScanContextSelfTest -c Release
```

To test an already built DLL, select its version and absolute path:

```powershell
dotnet build tests/DefXmlScanContextSelfTest -c Release -p:GameVersion=1.5 -p:ProductDll=C:/Temp/ATC/1.5/RimWorld_Auto_AI_Translation_Core.dll
./tests/DefXmlScanContextSelfTest/bin/Release/1.5/net48/DefXmlScanContextSelfTest.exe
```

Use GameVersion=1.6 for RW16. Its references now come from
`app_294100/depot_294104/RimWorldWin64_Data/Managed`. Set `ATC_TEST_MANAGED`
to that absolute directory before execution. This Unity Mono build uses APIs
absent from Windows .NET Framework, so run the net48 test with its supplied Mono:

```powershell
clang tests/DefXmlScanContextSelfTest/mono-host.c -O2 -o .codex/mono-host.exe
$runtime = "$PWD/app_294100/depot_294104"
$env:ATC_TEST_MANAGED = "$runtime/RimWorldWin64_Data/Managed"
& ./.codex/mono-host.exe "$runtime/MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll" `
  $env:ATC_TEST_MANAGED "$runtime/MonoBleedingEdge/etc" `
  "$PWD/tests/DefXmlScanContextSelfTest/bin/Release/1.6/net48/DefXmlScanContextSelfTest.exe"
```

The host loads only the explicitly supplied Mono and self-test assembly. It does
not load UnityPlayer or start RimWorld. No 1.6 assertions are skipped when its
actual runtime is supplied. For RW17 use GameVersion=1.7 and run the generated
net10.0 DLL with dotnet.

RW15 dependencies come from `Documents/rimworld 1.5 data/RimWorldWin64_Data/Managed`; RW17 from the installed game root on E:. The source records loaded assembly paths and CLR in test output. Legacy native logging is intercepted inside this test process. Fixtures use TEMP; the generated translation-pack getter is redirected to TEMP for status checks. No game entry point or player configuration is initialized.

Self-repair checks cover native XML read hooks, structural prevention before paid
requests, preservation failures, alias migration, activation recovery, conflicting
values, provenance, and actual two-phase injection counts. Network request methods
are forbidden during these checks. These headless checks do not establish in-game
acceptance with a player's complete active mod stack.

For the rollback guard and native setter attribution regression alone, use
`dotnet run --project tests/DefXmlScanContextSelfTest -c Release -- --config-guard`.
With the explicit Unity Mono host above, set `ATC_TEST_CONFIG_GUARD_ONLY=1`
instead (the host accepts only its runtime paths and assembly). This focused run
checks real native package and memory injection, implied Def deferral, rollback,
failure provenance, and the absence of ATC global setter patches. It does not run
the UI lifecycle suite, which requires native engine APIs unavailable headlessly
in the local 1.6 runtime. Leave the variable unset for the full suite.
