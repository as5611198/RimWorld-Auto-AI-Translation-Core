# Project Structure

This repository keeps project files, source code, and the packaged RimWorld mod in separate places.

## C# project files

Keep both RimWorld build project files here:

- `RimWorld-Auto-AI-Translation_Core/RimWorld-Auto-AI-Translation_Core.csproj`
- `RimWorld-Auto-AI-Translation_Core/RimWorld-Auto-AI-Translation_Core.RW15.csproj`

The two projects intentionally sit side by side:

- `RimWorld-Auto-AI-Translation_Core.csproj` builds the current RimWorld target.
- `RimWorld-Auto-AI-Translation_Core.RW15.csproj` builds the RimWorld 1.5 target.

## C# source files

Keep actual C# source files here:

- `src/RimWorld-Auto-AI-Translation_Core/`

Both `.csproj` files compile source files from this shared `src` directory. Do not move source files back beside the `.csproj` files.

## Output folders

Build output stays under the project-file folder:

- `RimWorld-Auto-AI-Translation_Core/bin/Debug/`
- `RimWorld-Auto-AI-Translation_Core/bin/Release/`
- `RimWorld-Auto-AI-Translation_Core/bin/Debug-RW15/`
- `RimWorld-Auto-AI-Translation_Core/bin/Release-RW15/`

Intermediate output stays under:

- `RimWorld-Auto-AI-Translation_Core/obj/`

These generated folders should not be committed.

## Installable RimWorld mod package

Keep the installable mod package here:

- `rimworld-mods/AI_TranslationCore/`

Version-specific assemblies belong here:

- `rimworld-mods/AI_TranslationCore/1.5/Assemblies/`
- `rimworld-mods/AI_TranslationCore/1.6/Assemblies/`

`LoadFolders.xml` controls RimWorld version routing for the packaged mod.

## Design context

Start from `docs/designs/README.md`, which is the authoritative design-document index
and records document status and supersession relationships. Then read `DESIGN_NOTES.md`
before removing or simplifying translation-pipeline features. Several systems, such as
UI interception, static cached translation refresh, global translation memory, and
memory injection, exist to avoid missing translations in RimWorld mods that do not
follow standard language-file patterns.

## Build commands

Current RimWorld target:

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Insiders\MSBuild\Current\Bin\MSBuild.exe' 'RimWorld-Auto-AI-Translation_Core\RimWorld-Auto-AI-Translation_Core.csproj' /t:Build /p:Configuration=Release /p:Platform=AnyCPU /v:minimal
```

RimWorld 1.5 target:

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Insiders\MSBuild\Current\Bin\MSBuild.exe' 'RimWorld-Auto-AI-Translation_Core\RimWorld-Auto-AI-Translation_Core.RW15.csproj' /t:Build /p:Configuration=Release /p:Platform=AnyCPU /v:minimal
```

Use `Configuration=Debug` or `Configuration=Release`. `Debug-RW15` and `Release-RW15` are output folder names, not MSBuild configuration names.
