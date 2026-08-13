# Auto Translation Core Design Notes

> Document ID: ATC-DES-000
> Status: historical design baseline. Start from [`docs/designs/README.md`](docs/designs/README.md) for the current document index. The current independent workflow and state model are defined by [`ATC-DES-003`](docs/designs/v4-five-stage-independent-translation-workflow.md); this file continues to define legacy safety, compatibility, translation-pack, reuse, and hot-reload constraints.

This file records the design context behind the major systems in Auto Translation Core.
It is written for future maintainers and AI coding agents that do not have the original
discussion history.

## Product Goal

The primary goal is not only to generate RimWorld language XML files. The real goal is:

- avoid missing translations as much as possible;
- support mods that ship incomplete or unusual localization data;
- let players translate, edit, download, delete, and hot-reload translations in-game;
- preserve a clean local translation pack without modifying original workshop mods;
- reduce repeated AI calls by reusing existing translations, including bundled mod translations and external translation patch mods.

Because of that goal, some systems look more complex than a normal XML generator. They
exist to cover gaps in RimWorld's mod ecosystem and to make translations visible without
requiring a restart.

## Translation Pipeline

The intended high-level pipeline is:

1. Resolve the active target language folder and secondary compatible language folder.
2. Scan active/selected mods and the local translation pack.
3. Build the global translation memory pool from existing language files.
4. For each target mod, collect Keyed and DefInjected sources.
5. Prefer existing translations from the global pool before sending anything to AI.
6. Send only missing or invalid entries to AI.
7. Sanitize and validate AI output, preserving placeholders, grammar tokens, XML safety, and dynamic tokens.
8. Save generated entries into `!Translation_AI_Pack`.
9. Refresh status caches and, when needed, inject changed translations into RimWorld memory.

The global pool is a core feature. It handles cases where:

- a mod includes its own translation;
- another workshop mod is a translation patch;
- translations exist only in an older version folder such as `1.3` or `1.4`;
- simplified/traditional Chinese can be reused across variants;
- the same phrase appears across many mods.

## Core Systems That Should Not Be Removed

### XML and Def Scanning

Keyed and DefInjected scanning is the main source of truth. It should remain the safest
and most deterministic path. The scanner should keep supporting both normal
`Languages/<language>` folders and mod `Defs` files because many mods do not provide
complete language files.

### Global Translation Memory

The global pool prevents repeated AI calls and lets existing human translations cover
missing text in other mods. Removing it would increase cost and reintroduce missing
translations for translation patch workflows.

### Local Translation Pack Output

Generated files belong in the independent local pack, not in original workshop mods.
This is important for Steam updates, user safety, and export/upload workflows.

### Memory Injection / Hot Reload

Writing to `LanguageDatabase` and `DefInjectionPackage` is intentionally supported.
It is complex, but it enables:

- seeing completed translations without restarting RimWorld;
- applying global workbench edits immediately;
- applying downloaded cloud translations immediately;
- clearing deleted translations from memory;
- testing manual XML edits through hot reload.

This system should be optimized and batched, not removed. Heavy operations must be
coalesced and delayed when immediate runtime refresh is unnecessary. For example,
pre-translation cleanup should delete old files and invalidate caches, then let the
final translation completion trigger the real memory refresh.

## Risky Systems That Must Stay Conservative

### UI Interception

UI interception exists because many RimWorld mods draw settings labels, buttons,
tooltips, and hardcoded UI text without language XML entries. Without UI interception,
those strings can never be translated by normal XML generation.

However, UI interception is risky:

- it can consume extra API quota;
- it can cache polluted or structured text;
- it can translate dynamic values that should not be translated;
- it can interfere with special UI such as music managers, file names, IDs, or numeric settings.

Current design stance:

- cache replacement is useful and should remain available;
- generating new AI translations from intercepted UI text should be controlled by a separate setting;
- structured data, JSON-like fragments, key-value fragments, pure numbers, volatile numeric labels, and internal-looking strings should be ignored;
- if performance or compatibility is uncertain, default toward cached replacement and make new generation opt-in or clearly advanced.

Relevant code areas:

- `src/RimWorld-Auto-AI-Translation_Core/UI/Interception/`
- `EnableUINewTranslation`
- `UIInterceptor.TextSafety.cs`
- `UIInterceptor.Queue.cs`

### Static Cached Translation Refresh

Some mods cache translated strings in static fields at startup. After memory injection,
RimWorld's language database may contain the new translation while a mod's own static
field still contains the old text. Static cached translation refresh exists to repair
that specific case.

This is not intended to blindly translate arbitrary assembly strings. It should stay
bounded and conservative:

- scan only plausible assemblies and types;
- prefer types with `StaticConstructorOnStartup`;
- only update fields when a real translation key/source match can be inferred;
- process in small batches per pump;
- never treat arbitrary strings, IDs, paths, state names, or serialized values as display text.

If this feature causes false positives, tighten candidate detection or add allow/deny
rules. Do not broaden it into a general "replace all static strings" system.

Relevant code area:

- `src/RimWorld-Auto-AI-Translation_Core/Scanning/AutoTranslatorScanner.StaticTranslationRefresh.cs`

### Pack Maintenance and Detox

Maintenance exists to repair legacy files and prevent broken XML or unsafe generated
translations from poisoning the pack. It should not run expensive work at moments where
the user expects translation to start immediately.

Guidelines:

- startup/background maintenance should be coalesced;
- translation startup should avoid unnecessary hot reload or memory injection;
- file caches must handle deleted files gracefully;
- legacy repair should not delete valid current translations unless the target pattern is proven unsafe.

## Feature Classification

Keep as core:

- Keyed scanning;
- DefInjected scanning;
- Def source extraction;
- global translation memory;
- AI fallback for missing entries;
- placeholder/token validation;
- local pack generation;
- status cache refresh after translation;
- memory injection after generated translations are written;
- manual hot reload.

Keep but guard carefully:

- UI interception cache replacement;
- UI interception new AI generation;
- static cached translation refresh;
- package-scoped memory clear/injection;
- background pack maintenance;
- automatic old-translation cleanup on mod update.

Avoid or require explicit opt-in/debug mode:

- broad assembly string replacement;
- translating pure numeric UI text;
- translating file paths, IDs, XML fragments, JSON fragments, or key-value fragments;
- triggering many package-scoped memory drops before a translation job begins;
- deleting suspected duplicate translations without strong package/path evidence.

## Performance Rules

The mod is often used with hundreds of RimWorld mods. Any feature that scans all mods,
all XML files, all assemblies, or all active language data must follow these rules:

- cache directory and XML fingerprints when possible;
- invalidate caches only for changed paths;
- batch main-thread memory writes;
- coalesce repeated memory drop requests;
- do heavy file parsing on background threads when safe;
- keep Unity/RimWorld object mutation on the main thread;
- never block translation startup with maintenance that can wait until after translation.

## Compatibility Notes

RimWorld 1.5 and 1.6 can differ in APIs such as `LongEventHandler.QueueLongEvent`.
Avoid binding directly to version-specific overloads unless guarded by reflection or
separate project references.

This repository builds both targets from the shared source tree. When adding new C#
files, both project files usually need explicit `<Compile Include=... />` entries.

## Review Checklist For Future AI Agents

Before removing or simplifying a system, answer these questions:

1. Does this system cover text that normal language XML cannot cover?
2. Does removing it create missing translations for hardcoded UI, static cached strings, or DefInjected data?
3. Is the feature core, guarded, or experimental according to this document?
4. Can the bug be fixed by narrowing scope, batching work, or adding guards instead of deleting the feature?
5. Does the change preserve both RimWorld 1.5 and 1.6 builds?
6. Does the change preserve the local translation pack as the write target?
7. Does it avoid repeated AI calls for translations that already exist in the global pool?

If the answer is unclear, prefer adding a safer toggle, deny rule, cache invalidation,
or batch limit rather than deleting the feature outright.
