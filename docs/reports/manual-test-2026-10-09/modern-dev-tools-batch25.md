# 第 25 批失败条目

数据库仅保存了整批错误及原文，没有保存本批模型输出。无法还原返回译文和实际编号顺序。下列 34 条为该批失败条目，不代表已知的原请求顺序。

- ID：`atc1_0142d18b5fb6d25ace3b72075a6580811121da42a369e57a094977f9e4f36e61`；定位：`defType=ModernDevTools.KnownIssueDef;defName=MDT_ExceptionFillingWindow;fieldPath=keywords.0`

exception filling window

- ID：`atc1_0880221b202ec6ce420818f49333b301cb8164241caa755d75713dcac748ad08`；定位：`defType=ModernDevTools.KnownIssueDef;defName=MDT_HarmonyConflict;fieldPath=fix`

Check the Harmony patch analysis above: it reads the live patch registry and names the actual patching mods. One mod named means update or report to that author; two or more on the same method is a real conflict candidate - disable them one at a time to find the pair.

- ID：`atc1_09d39716317fddc2289ea1a922058a31906cc3cb69dd26d4c4df0cb70389755a`；定位：`defType=ModernDevTools.KnownIssueDef;defName=MDT_HarmonyConflict;fieldPath=keywords.0`

wrapper dynamic-method

- ID：`atc1_0d08d2d5566aaef6858bc3ccee4e75f45196644eb91acfff31f4f54c7bb80d3f`；定位：`defType=ModernDevTools.KnownIssueDef;defName=MDT_WeatherFallback;fieldPath=description`

No weather type had a positive chance for the current conditions, so the game defaulted to clear weather. Usually a modded biome or weather mod left no valid weather for this map's situation. The game continues normally.

- ID：`atc1_1727dba1c8d84e4ba6b40bf5330184f5cda4446f2c277cece1ba21f526853eaa`；定位：`defType=ModernDevTools.KnownIssueDef;defName=MDT_RaidStrategyFallback;fieldPath=description`

A raid was created without a strategy, so the game fell back to a straightforward immediate attack. The raid still happens; only the tactics the attackers were meant to use were lost.

- ID：`atc1_18284ef26876d3f96138a970328e9dfcfbfb920968ef0d7fbbbd764c9e257a29`；定位：`defType=ModernDevTools.KnownIssueDef;defName=MDT_PawnGroupFallback;fieldPath=keywords.0`

defaulting to a single random cheap group

- ID：`atc1_26f0363f20dfbe738ff858bacd91d41e8cc2de66d84f59e3767072d4e4f6c31f`；定位：`defType=ModernDevTools.KnownIssueDef;defName=MDT_Reservation;fieldPath=label`

a pawn could not reserve its target

- ID：`atc1_3630669460c54fe83e40eca0f5f00ffec8816a797d1c92b6c449f2a1d6aec4e5`；定位：`defType=ModernDevTools.KnownIssueDef;defName=MDT_Reservation;fieldPath=description`

A pawn failed to claim (reserve) the thing it wanted to work on, so the job was cancelled. A burst of these can leave pawns standing idle. With mods that move pawns between maps, it usually means a job aimed at something on a different map.

- ID：`atc1_375abe52f4aaa7aba839f477c5e55ed09e3f14595b343f7c4821db6c0caed631`；定位：`defType=ModernDevTools.KnownIssueDef;defName=MDT_WeatherFallback;fieldPath=label`

weather fell back to clear

- ID：`atc1_47a792a9dcbdeb0f4e048ae91c2bd8f687a858899e1513e8bc9b10a1e07518aa`；定位：`defType=ModernDevTools.KnownIssueDef;defName=MDT_RootOnGUI;fieldPath=keywords.0`

root level exception in ongui

- ID：`atc1_4acb06b89996e4d54fa1090972cfddab30b64699660fbd37fa6803fe0d280d2c`；定位：`defType=ModernDevTools.KnownIssueDef;defName=MDT_RaidStrategyFallback;fieldPath=label`

raid used a default strategy

- ID：`atc1_525657dbbe0bb8d51e07be6282908096f2bfbc5216435f48cd04dbacece3f919`；定位：`defType=ModernDevTools.KnownIssueDef;defName=MDT_RootOnGUI;fieldPath=fix`

A mod running each frame has a bug. Use the stack trace to find it, disable it to confirm, and report the trace. If attribution points only at vanilla, a mod's global draw or update hook is still the likely cause.

- ID：`atc1_534ae9064c895e7f1280163ba28ce9baa58efc300f120047e00ade790b33bdd6`；定位：`defType=ModernDevTools.KnownIssueDef;defName=MDT_WeatherFallback;fieldPath=fix`

Nothing is broken. If it repeats on one map, report it to the author of the biome or weather mod - its weather commonalities exclude this map's conditions.

- ID：`atc1_55cb3ec377e0e0d84b03edc52dd75d9699d86e999ace31d5bfe93e0623ff5233`；定位：`defType=ModernDevTools.KnownIssueDef;defName=MDT_PawnGroupFallback;fieldPath=fix`

Nothing is broken - the event went ahead with fallback pawns. If it repeats for one faction, report this line to the author of the mod that adds that faction; its group definitions do not cover this point range.

- ID：`atc1_626af8e57c552e537360ecc350d38942b1f1a6a8c2821a05ea1e0f1df23339dd`；定位：`defType=ModernDevTools.KnownIssueDef;defName=MDT_LongEventException;fieldPath=keywords.0`

exception from asynchronous event

- ID：`atc1_63bc239ea6a67f067d2dc0a6f09a48d74ffb2831d8e7c5e3913873dd25a88825`；定位：`defType=ModernDevTools.KnownIssueDef;defName=MDT_HarmonyConflict;fieldPath=label`

the error passes through a modified method

- ID：`atc1_7205d87ab924e80d7f11a48bc5cce93ff76f15a66b7587915463222064aa2a2d`；定位：`defType=ModernDevTools.KnownIssueDef;defName=MDT_RootOnGUI;fieldPath=label`

a per-frame game loop hook threw an error

- ID：`atc1_86f0171c5080a306bb5270d30778915e4714c95fc7840bb56c49cbab2e4d133c`；定位：`defType=ModernDevTools.KnownIssueDef;defName=MDT_RootOnGUI;fieldPath=description`

A top-level per-frame hook threw, so RimWorld aborted the rest of that frame's drawing (OnGUI) or update logic (Update). Symptoms are often several unrelated UI elements flickering or not updating at once, or the same error repeating every frame.

- ID：`atc1_8b5ed8180500042c06dd633562fde3ab7408e6e31bb51562224c9b12b24ba2ed`；定位：`defType=ModernDevTools.KnownIssueDef;defName=MDT_RaidStrategyFallback;fieldPath=keywords.1`

no raid strategy found

- ID：`atc1_8c0b1e50e056cfe8dd2559f224d308e299e0074279d67fe99c9bb978d6dbf1fa`；定位：`defType=ModernDevTools.KnownIssueDef;defName=MDT_PawnGroupFallback;fieldPath=description`

The game could not build the intended group of pawns for a faction at the requested point value, so it spawned a single random cheap group instead. This usually means the faction has no pawn kinds that fit the points or group type - common with modded factions. The event still happens, just with weaker or different pawns than intended.

- ID：`atc1_9064dcfacb769c0cd032149e6823bb282d94bee58fbfe070e9507242e35a99ce`；定位：`defType=ModernDevTools.KnownIssueDef;defName=MDT_ExceptionFillingWindow;fieldPath=fix`

Usually a mod's UI code. Check the likely source and stack trace for the mod, then update or disable it and report the trace to the author.

- ID：`atc1_b24dcbe42afa521659fcb6958f5750c91092cf32dfd77e5321560a69642eb549`；定位：`defType=ModernDevTools.KnownIssueDef;defName=MDT_WeatherFallback;fieldPath=keywords.0`

all weather commonalities were zero

- ID：`atc1_b76674c61ae08c5fb00f86a6b6052ba5cbbadfbc0975ec101d2be8af405ae26d`；定位：`defType=ModernDevTools.KnownIssueDef;defName=MDT_LongEventException;fieldPath=keywords.1`

could not execute post-long-event

- ID：`atc1_b9372d0f9ae7bceb81831d1a78c11858201ac60f1b9d24441b451bcb8c2e24cb`；定位：`defType=ModernDevTools.KnownIssueDef;defName=MDT_HarmonyConflict;fieldPath=description`

A method in this error's path was replaced at runtime by Harmony, the library mods use to change game behaviour. That is normal - a single mod's patch produces the same frames - and does not by itself mean two mods conflict. It does mean a mod's change is in the code path, and the stack trace can show the original method name even when a mod's rewrite caused the error.

- ID：`atc1_babea64a65d4c477211f10b1a51cb8607723188e858a491809d7a3f336baa29b`；定位：`defType=ModernDevTools.KnownIssueDef;defName=MDT_RaidStrategyFallback;fieldPath=fix`

Nothing is broken and no action is needed. If it repeats, report this line to the author of the mod that triggers that raid or incident.

- ID：`atc1_ccddccd312210281c6777d9c000f4191c6dda0efd2be06545e27805fffd6886c`；定位：`defType=ModernDevTools.KnownIssueDef;defName=MDT_RaidStrategyFallback;fieldPath=keywords.0`

parms raidstrategy was null but shouldn't be

- ID：`atc1_d086d72af3c1574ff585c9c586ee284644f16dde6d24a652231c667a4b692458`；定位：`defType=ModernDevTools.KnownIssueDef;defName=MDT_RootOnGUI;fieldPath=keywords.1`

root level exception in update

- ID：`atc1_d6b8138f429fb94db56002803335de9b99a6bc1a83ec9e70522436a978c5f525`；定位：`defType=ModernDevTools.KnownIssueDef;defName=MDT_PawnGroupFallback;fieldPath=label`

a pawn group used a cheap fallback

- ID：`atc1_d897556fdb431645afae803b158b1f5c5860381c5a30c67be8fcb7bcaa1e5707`；定位：`defType=ModernDevTools.KnownIssueDef;defName=MDT_RaidArrivalFallback;fieldPath=fix`

Nothing is broken and no action is needed - the raid went ahead. If you see it for every raid from one faction and want it fixed properly, report this line to the author of the mod that adds that faction or raid strategy.

- ID：`atc1_e015eabd144759a7dba8c548d2543bbf7226f10794797b3f66f6eedd222e31ac`；定位：`defType=ModernDevTools.KnownIssueDef;defName=MDT_ExceptionFillingWindow;fieldPath=label`

a UI window threw an error

- ID：`atc1_e23850d7f8a69ec3f7ea405dbda5fbde887db424d9607bfd182d0ac38b87f540`；定位：`defType=ModernDevTools.KnownIssueDef;defName=MDT_RaidArrivalFallback;fieldPath=description`

The game could not pick an arrival mode for a raid, so it sent the raiders in on foot from the map edge instead. This happens when none of the arrival modes allowed by that raid strategy can be used with this particular faction and map - for example when a faction cannot use drop pods, or a raid strategy lists arrival modes that do not fit the situation. The raid itself still runs normally; only how the attackers arrive changed.

- ID：`atc1_e815045d2a7f7b0ffd7c252a18b174ef38a2ad2811c9ce0781f49df159c847dd`；定位：`defType=ModernDevTools.KnownIssueDef;defName=MDT_RaidArrivalFallback;fieldPath=keywords.0`

could not resolve arrival mode for raid

- ID：`atc1_ececadc648a0dda42ca30e68ab44138271e233a5ca66f4a054a7ea71d8942a11`；定位：`defType=ModernDevTools.KnownIssueDef;defName=MDT_ExceptionFillingWindow;fieldPath=description`

A window's draw code threw an exception, so RimWorld closed or skipped the window. A UI panel may vanish or misbehave.

- ID：`atc1_fb27128aaf3e5221bc97bf269e4f6d0643d8a4cebe80b3574a73d4c1f8307114`；定位：`defType=ModernDevTools.KnownIssueDef;defName=MDT_RaidArrivalFallback;fieldPath=label`

raid used a default arrival mode
