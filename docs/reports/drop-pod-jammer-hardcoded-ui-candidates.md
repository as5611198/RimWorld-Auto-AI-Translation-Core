# Drop Pod Raid Jammer DLL 硬编码文本候选清单

- 扫描对象：`KKDropPodJammer.dll`
- 候选总数：183
- 已知人工补丁文本覆盖：149/149（100.00%）
- 说明：本表是召回优先的扫描结果，不代表每一条都应翻译；后续由 Agent 或人工过滤。

| 声明类型 | 方法 | 字符串序号 | 发现类别 | 原文 |
|---|---|---:|---|---|
| KKDropPodJammer.KKRitualObligationTargetWorker_Custom | CanUseTargetInternal | 2 | review_string_literal | A powered console extender must be adjacent to this comms console to start the ritual. |
| KKDropPodJammer.KKDropPodJammerSettings | DoSettingsWindowContents | 22 | ui_method_literal | F0 |
| KKDropPodJammer.KKRitualObligationTargetWorker_Custom+<GetTargets>d__2 | MoveNext | 0 | review_string_literal | Execution target |
| KKDropPodJammer.KKDropPodJammerMod | SettingsCategory | 0 | review_string_literal | Drop Pod Jammer |
| KKDropPodJammer.KKStageFailTrigger_NoCharterAvailable | Failed | 2 | ui_method_literal | No Jamming Charter Available (def not found) |
| KKDropPodJammer.KKStageFailTrigger_NoCharterAvailable | Failed | 1 | ui_method_literal | KKJammingCharter |
| KKDropPodJammer.KKDropPodJammerSettings | DoSettingsWindowContents | 26 | ui_method_literal | Chance pawn is unharmed (%) (default: {0:F0}) |
| KKDropPodJammer.KKJobGiver_HaulToRitualSpot | TryGiveJob | 1 | review_string_literal | KKHaulToRitualSpot |
| KKDropPodJammer.KKDebugActions | ResetProtocolDebug | 4 | ui_method_literal | DEV: Jamming Protocol reset. |
| KKDropPodJammer.KKCompCommConsoleJammingStatus | CompInspectStringExtra | 1 | review_string_literal | Jamming Protocol: ACTIVE<br> |
| KKDropPodJammer.KKDebugActions | ResetProtocolDebug | 3 | ui_method_literal | Debug Action |
| KKDropPodJammer.KKJammingProtocolTracker | DeactivateProtocol | 0 | review_string_literal | [KKDropPodJammer] Jamming Protocol DEACTIVATED ({0}). Was active since tick {1} via {2}.  |
| KKDropPodJammer.KKRitualOutcomeEffectWorker_FromQuality | TryPlayVanillaOutcomeCue | 6 | review_string_literal | [KKDropPodJammer] Failed to play ritual outcome cue:  |
| KKDropPodJammer.KKCompCommConsoleJammingStatus+<CompGetGizmosExtra>d__1 | MoveNext | 0 | ui_method_literal | DEV: Activate Jamming Protocol |
| KKDropPodJammer.KKStageEndTrigger_ThingDeliveredOrTimeout+<>c__DisplayClass6_0 | <MakeTrigger>b__0 | 7 | ui_method_literal | [KK] [Ritual:{0}] Timed out after {1} ticks waiting for ritual progress! Possible mod conflict (AI/pawn/ritual/job mod?)<br>Pawn job dump:{2} |
| KKDropPodJammer.KKUtil | LogErr | 0 | review_string_literal | [KKDropPodJammer]  |
| KKDropPodJammer.KKDropPodJammerSettings | DoSettingsWindowContents | 16 | ui_method_literal | Maximum number of active long-range scanners counted (default: {0}) |
| KKDropPodJammer.KKDropPodJammerSettings | DoSettingsWindowContents | 18 | ui_method_literal | Jamming Protocol: Crash Injury Distribution Settings |
| KKDropPodJammer.KKRitualOutcomeEffectWorker_FromQuality | TryHandleProtocol | 0 | review_string_literal | Ritual |
| KKDropPodJammer.KKRitualOutcomeEffectWorker_FromQuality | Apply | 6 | ui_method_literal | RITUALLABEL |
| KKDropPodJammer.KKPatch_MineralScannerInspectString | Postfix | 0 | review_string_literal | LongRangeMineralScanner |
| KKDropPodJammer.KKStageEndTrigger_ThingDeliveredOrTimeout+<>c__DisplayClass6_0 | <MakeTrigger>b__0 | 1 | ui_method_literal | [KK] [Ritual:{0}] ThingDeliveredOrTimeout: No itemConsumed flag yet. |
| KKDropPodJammer.KKHarmonyArrivalShared | HandleArrivalCrashIfActive | 1 | review_string_literal | [KKDropPodJammer] Hostile raid arrival detected on map {0}. Protocol ACTIVE. |
| KKDropPodJammer.KKDropPodJammerSettings | ExposeData | 13 | review_string_literal | ingredientValues |
| KKDropPodJammer.KKRitualOutcomeEffectWorker_FromQuality | TryPlayVanillaOutcomeCue | 2 | review_string_literal | RitualOutcomeNegative |
| KKDropPodJammer.KKStageFailTrigger_ThingNotConsumed | Failed | 0 | review_string_literal | [KK] FailTrigger: itemConsumed={0} for ritual instance {1} |
| KKDropPodJammer.KKStageFailTrigger_LossOfPower | Failed | 2 | ui_method_literal | [KK] Ritual cancelled: Lost power to Comm Console or Extender. |
| KKDropPodJammer.KKStageFailTrigger_LossOfPower | Failed | 1 | ui_method_literal | Ritual cancelled: Power was lost to the Comm Console or Console Extender. |
| KKDropPodJammer.KKJobGiver_HaulToRitualSpot | .ctor | 0 | review_string_literal | KKJammingCharter |
| KKDropPodJammer.KKRitualStageAction_ConsumeThing | ExposeData | 3 | review_string_literal | stageId |
| KKDropPodJammer.KKRitualStageAction_ConsumeThing | ExposeData | 5 | review_string_literal | consumptionComplete |
| KKDropPodJammer.KKStageEndTrigger_NoFailures+<>c__DisplayClass1_0 | <MakeTrigger>b__0 | 2 | review_string_literal | [KK] NoFailures: 'failed' field NOT found. |
| KKDropPodJammer.KKDropPodJammerSettings | DoSettingsWindowContents | 2 | ui_method_literal | Jamming Charter Recipe Settings |
| KKDropPodJammer.KKStageEndTrigger_NoFailures+<>c__DisplayClass1_0 | <MakeTrigger>b__0 | 0 | review_string_literal | failed |
| KKDropPodJammer.KKDropPodJammerSettings | DoSettingsWindowContents | 10 | ui_method_literal | Jamming Protocol ritual duration (ticks) (default: {0}) |
| KKDropPodJammer.KKJammingProtocolTracker | DeactivateProtocol | 1 | review_string_literal | Last-known scanners={0}. |
| KKDropPodJammer.KKStageEndTrigger_ThingDeliveredOrTimeout | ExposeData | 3 | review_string_literal | ticksRemaining |
| KKDropPodJammer.KKHarmonyArrivalShared | HandleArrivalCrashIfActive | 2 | review_string_literal | [KKDropPodJammer] CrashChance=100%. All {0} raiders will crash. |
| KKDropPodJammer.KKRitualOutcomeEffectWorker_FromQuality | ChooseOutcomeFromXML | 0 | review_string_literal | failed |
| KKDropPodJammer.KKDropPodJammerSettings | DoSettingsWindowContents | 21 | ui_method_literal | F0 |
| KKDropPodJammer.KKStageFailTrigger_ThingNotConsumed | Failed | 1 | review_string_literal | [KK] FailTrigger: No itemConsumed flag yet for this ritual instance. |
| KKDropPodJammer.KKStageFailTrigger_Checkable | ExposeData | 0 | review_string_literal | failed |
| KKDropPodJammer.KKStageEndTrigger_NoFailures+<>c__DisplayClass1_0 | <MakeTrigger>b__0 | 3 | review_string_literal | [KK] NoFailures: itemConsumed={0} for ritual instance {1} |
| KKDropPodJammer.KKDebugActions | ActivateProtocolDebug | 1 | ui_method_literal | Debug Action |
| KKDropPodJammer.KKRitualOutcomeEffectWorker_FromQuality | GetPerformer | 0 | review_string_literal | Programmer |
| KKDropPodJammer.KKDropPodJammerSettings | DoSettingsWindowContents | 12 | ui_method_literal | Comm Console base % of enemy crash victims (default: 7.5%) |
| KKDropPodJammer.KKCompCommConsoleJammingStatus+<CompGetGizmosExtra>d__1 | MoveNext | 2 | ui_method_literal | DEV: Reset/Deactivate Jamming Protocol |
| KKDropPodJammer.KKRitualOutcomeEffectWorker_FromQuality | TryPlayVanillaOutcomeCue | 0 | review_string_literal | RitualOutcomeNegative |
| KKDropPodJammer.KKStageFailTrigger_Checkable | ExposeData | 2 | review_string_literal | stageId |
| KKDropPodJammer.KKDropPodJammerSettings | ExposeData | 12 | review_string_literal | ingredientKeys |
| KKDropPodJammer.KKDropPodJammerSettings | DoSettingsWindowContents | 9 | ui_method_literal | Work to make Jamming Charter (default: {0}) |
| KKDropPodJammer.KKDropPodJammerSettings | ExposeData | 9 | review_string_literal | crashNoInjuryChance |
| KKDropPodJammer.KKStageFailTrigger_ThingNotConsumed | Failed | 2 | review_string_literal | [KK] Ritual fail: Item was not consumed! |
| KKDropPodJammer.KKJammingProtocolTracker | ExposeData | 4 | review_string_literal | cachedScannerCount |
| KKDropPodJammer.KKHarmonyArrivalShared | ApplyCrashOutcomes | 1 | ui_method_literal | [KKDropPodJammer] Crash outcome: DOWNED ->  |
| KKDropPodJammer.KKDropPodJammerSettings | DoSettingsWindowContents | 27 | ui_method_literal | Tip: Set Killed/Downed/Injured/Unharmed to taste; values will be normalized to sum to 100%. |
| KKDropPodJammer.KKDropPodJammerSettings | ExposeData | 3 | review_string_literal | baseCrashChance |
| KKDropPodJammer.KKDropPodJammerSettings | ExposeData | 10 | review_string_literal | ingredientKeys |
| KKDropPodJammer.KKCompCommConsoleJammingStatus+<CompGetGizmosExtra>d__1 | MoveNext | 1 | ui_method_literal | Force-activate the Jamming Protocol for testing. |
| KKDropPodJammer.KKStageEndTrigger_ThingDeliveredOrTimeout | ExposeData | 4 | review_string_literal | stageId |
| KKDropPodJammer.KKCompCommConsoleJammingStatus+<>c__DisplayClass1_0 | <CompGetGizmosExtra>b__1 | 0 | review_string_literal | ResetProtocol |
| KKDropPodJammer.KKCompCommConsoleJammingStatus | CompInspectStringExtra | 3 | review_string_literal | Jamming Protocol: INACTIVE<br> |
| KKDropPodJammer.KKJobGiver_HaulToRitualSpot | TryGiveJob | 0 | review_string_literal | KKHaulToRitualSpot |
| KKDropPodJammer.KKRitualOutcomeEffectWorker_FromQuality | ChooseOutcomeFromXML | 1 | review_string_literal | success |
| KKDropPodJammer.KKCompCommConsoleJammingStatus | CompInspectStringExtra | 4 | review_string_literal | Projected Crash Chance: {0:P1}<br> |
| KKDropPodJammer.KKHarmonyArrivalShared+<>c | <HandleArrivalCrashIfActive>b__1_0 | 0 | ui_method_literal | null |
| KKDropPodJammer.KKRitualObligationTargetWorker_Custom | CanUseTargetInternal | 0 | review_string_literal | Jamming Protocol already active. Wait for next drop pod raid. |
| KKDropPodJammer.KKDropPodJammerSettings | DoSettingsWindowContents | 7 | ui_method_literal | Ingredient: {0} (default: {1}) |
| KKDropPodJammer.KKStageFailTrigger_NoCharterAvailable | Failed | 5 | ui_method_literal | [KK] Ritual failed: No Jamming Charter Available on map or carried by ritual pawn. |
| KKDropPodJammer.KKDropPodJammerSettings | DoSettingsWindowContents | 14 | ui_method_literal | Long-Range Scanners added % of enemy crash victims (default: 7.5%) |
| KKDropPodJammer.KKUtil | AsPercent | 0 | review_string_literal | F{0} |
| KKDropPodJammer.KKHarmonyArrivalShared | HandleArrivalCrashIfActive | 6 | review_string_literal | Hostile raid processed |
| KKDropPodJammer.KKJammingProtocolTracker | ExposeData | 0 | review_string_literal | protocolActive |
| KKDropPodJammer.KKJammingProtocolTracker | ExposeData | 3 | review_string_literal | activationTick |
| KKDropPodJammer.KKDropPodJammerSettings | DoSettingsWindowContents | 23 | ui_method_literal | Chance pawn is killed (%) (default: {0:F0}) |
| KKDropPodJammer.KKRitualStageAction_ConsumeThing | ExposeData | 2 | review_string_literal | lookDistance |
| KKDropPodJammer.KKDebugActions | ResetProtocolDebug | 1 | ui_method_literal | ResetProtocol |
| KKDropPodJammer.KKRitualOutcomeEffectWorker_FromQuality | Apply | 4 | ui_method_literal | OutcomeLetterLabel |
| KKDropPodJammer.KKDropPodJammerSettings | DoSettingsWindowContents | 25 | ui_method_literal | Chance pawn is injured (but not downed) (%) (default: {0:F0}) |
| KKDropPodJammer.KKDropPodJammerSettings | DoSettingsWindowContents | 15 | ui_method_literal | F1 |
| KKDropPodJammer.KKDebugActions | ActivateProtocolDebug | 2 | ui_method_literal | DEV: Jamming Protocol forced ON (next raid will be jammed). |
| KKDropPodJammer.KKPatch_CenterDrop_TryResolveRedirect | Prefix | 0 | review_string_literal | [KKDropPodJammer] Redirecting HOSTILE CenterDrop to EdgeDrop at spawn-center resolution. |
| KKDropPodJammer.Patch_LordJob_Ritual_Cleanup | Postfix | 0 | review_string_literal | [KKDropPodJammer] Ritual cleanup: clearing ritual state & warning flag. |
| KKDropPodJammer.KKDropPodJammerSettings | ExposeData | 6 | review_string_literal | crashKilledChance |
| KKDropPodJammer.KKUtil | LogInfo | 0 | review_string_literal | [KKDropPodJammer]  |
| KKDropPodJammer.KKRitualOutcomeEffectWorker_FromQuality | Apply | 5 | ui_method_literal | OUTCOMELABEL |
| KKDropPodJammer.KKRitualStageAction_ConsumeThing | ExposeData | 1 | review_string_literal | amountToConsume |
| KKDropPodJammer.KKCompCommConsoleJammingStatus | CompInspectStringExtra | 0 | review_string_literal | Long-Range Scanners {0}/{1} |
| KKDropPodJammer.KKDropPodJammerSettings | DoSettingsWindowContents | 4 | ui_method_literal | Unknown |
| KKDropPodJammer.KKJammingProtocolTracker | ExposeData | 1 | review_string_literal | activationSource |
| KKDropPodJammer.KKUtil | LogWarn | 0 | review_string_literal | [KKDropPodJammer]  |
| KKDropPodJammer.KKHarmonyArrivalShared | HandleArrivalCrashIfActive | 4 | review_string_literal | [KKDropPodJammer] Pawns selected to crash:  |
| KKDropPodJammer.KKPatch_DrawOutcomeChancesForJammer | Prefix | 1 | ui_method_literal | KK_JammingProtocolEffect |
| KKDropPodJammer.KKRitualOutcomeEffectWorker_FromQuality | TryPlayVanillaOutcomeCue | 1 | review_string_literal | RitualOutcomePositive |
| KKDropPodJammer.KKRitualOutcomeEffectWorker_FromQuality | ChooseOutcomeFromXML | 2 | review_string_literal | The ritual failed. |
| KKDropPodJammer.KKStageEndTrigger_ThingDeliveredOrTimeout+<>c__DisplayClass6_0 | <MakeTrigger>b__0 | 0 | ui_method_literal | [KK] [Ritual:{0}] ThingDeliveredOrTimeout: itemConsumed={1} |
| KKDropPodJammer.KKStageEndTrigger_ThingDeliveredOrTimeout | ExposeData | 0 | review_string_literal | thingDefName |
| KKDropPodJammer.KKStageFailTrigger_ThingNotConsumed | ExposeData | 0 | review_string_literal | stageId |
| KKDropPodJammer.KKRitualOutcomeEffectWorker_FromQuality | BuildIntBreakdownLine | 1 | ui_method_literal | Success chance = {0} (Intellectual of {1}: {2}) |
| KKDropPodJammer.KKDropPodJammerMod | .ctor | 0 | review_string_literal | KKDropPodJammer |
| KKDropPodJammer.KKStageEndTrigger_NoFailures | ExposeData | 0 | review_string_literal | stageId |
| KKDropPodJammer.KKRitualOutcomeEffectWorker_FromQuality | ChooseOutcomeFromXML | 3 | review_string_literal | The ritual succeeded. |
| KKDropPodJammer.KKDropPodJammerSettings | DoSettingsWindowContents | 19 | ui_method_literal | F0 |
| KKDropPodJammer.KKCompCommConsoleJammingStatus+<>c__DisplayClass1_0 | <CompGetGizmosExtra>b__0 | 0 | review_string_literal | Dev Gizmo |
| KKDropPodJammer.KKStageFailTrigger_LossOfPower | Failed | 0 | ui_method_literal | KKConsoleExtender |
| KKDropPodJammer.KKJobDriver_HaulToRitualSpot+<>c | <MakeNewToils>b__3_2 | 0 | review_string_literal | KKJammingCharter |
| KKDropPodJammer.KKStageEndTrigger_ThingDeliveredOrTimeout | ExposeData | 1 | review_string_literal | amount |
| KKDropPodJammer.KKDebugActions | ActivateProtocolDebug | 0 | ui_method_literal | No tracker found! |
| KKDropPodJammer.KKDropPodJammerSettings | DoSettingsWindowContents | 11 | ui_method_literal | Jamming Protocol: Crash Chance Settings |
| KKDropPodJammer.KKJammingProtocolTracker | ActivateProtocol | 3 | review_string_literal | Base={0:P0}, +{1:P0}/scanner (max {2}),  |
| KKDropPodJammer.KKJobDriver_HaulToRitualSpot | <MakeNewToils>b__3_0 | 0 | ui_method_literal | Jamming Charter injected into Comm Console |
| KKDropPodJammer.KKDropPodJammerSettings | ExposeData | 7 | review_string_literal | crashDownedChance |
| KKDropPodJammer.KKStageEndTrigger_ThingDeliveredOrTimeout+<>c__DisplayClass6_0 | <MakeTrigger>b__0 | 5 | ui_method_literal | none |
| KKDropPodJammer.KKStageEndTrigger_NoFailures+<>c__DisplayClass1_0 | <MakeTrigger>b__0 | 4 | review_string_literal | [KK] NoFailures: No itemConsumed flag yet for this ritual instance. |
| KKDropPodJammer.KKStageEndTrigger_ThingDeliveredOrTimeout+<>c__DisplayClass6_0 | <MakeTrigger>b__0 | 3 | ui_method_literal | [KK] [Ritual:{0}] Item found at ritual site, ending stage. |
| KKDropPodJammer.KKStageEndTrigger_ActionsComplete | ExposeData | 0 | review_string_literal | stageId |
| KKDropPodJammer.KKJammingProtocolTracker | ActivateProtocol | 4 | review_string_literal | scanners now={0}, tick={1}. |
| KKDropPodJammer.KKDropPodJammerSettings | DoSettingsWindowContents | 5 | ui_method_literal | Silver |
| KKDropPodJammer.KKRitualOutcomeEffectWorker_FromQuality | TryPlayVanillaOutcomeCue | 5 | review_string_literal | Standard_PositiveEvent |
| KKDropPodJammer.KKDropPodJammerSettings | DoSettingsWindowContents | 13 | ui_method_literal | F1 |
| KKDropPodJammer.KKStageEndTrigger_ThingDeliveredOrTimeout+<>c__DisplayClass6_0 | <MakeTrigger>b__0 | 8 | ui_method_literal | Drop Pod Jammer ritual failed: The required pawn could not deliver and consume the Jamming Charter in time. This may be due to another mod interfering with pawn jobs or AI. Try disabling ritual/AI mods if this happens repeatedly. |
| KKDropPodJammer.KKRitualOutcomeEffectWorker_FromQuality | TryPlayVanillaOutcomeCue | 3 | review_string_literal | RitualOutcomePositive |
| KKDropPodJammer.KKDropPodJammerSettings | ExposeData | 5 | review_string_literal | maxScannerCount |
| KKDropPodJammer.KKDropPodJammerSettings | SetupIngredientSettingsAndDefaults | 1 | review_string_literal | Unknown |
| KKDropPodJammer.KKRitualStageAction_ConsumeThing | Apply | 3 | review_string_literal | [KK] Could not find any {0} to consume for ritual at {1}! |
| KKDropPodJammer.KKDropPodJammerSettings | ExposeData | 0 | review_string_literal | debugLogging |
| KKDropPodJammer.KKStageFailTrigger_NoCharterAvailable | Failed | 0 | ui_method_literal | [KK] NoCharterAvailable: item already consumed for this ritual, not failing. |
| KKDropPodJammer.KKStageEndTrigger_NoFailures+<>c__DisplayClass1_0 | <MakeTrigger>b__0 | 6 | review_string_literal | [KK] NoFailures: No failures and item consumed. Stage can end. |
| KKDropPodJammer.KKJammingProtocolTracker | MapComponentTick | 0 | review_string_literal | All comms consoles offline or destroyed |
| KKDropPodJammer.KKStageEndTrigger_ThingDeliveredOrTimeout+<>c__DisplayClass6_0 | <MakeTrigger>b__0 | 6 | ui_method_literal | <br>    Pawn {0} [CurJob: {1}] (AtCell: {2}) |
| KKDropPodJammer.KKCompCommConsoleJammingStatus+<>c__DisplayClass1_0 | <CompGetGizmosExtra>b__1 | 3 | review_string_literal | [KKDropPodJammer][Dev] Protocol reset via gizmo. |
| KKDropPodJammer.KKCompCommConsoleJammingStatus | CompInspectStringExtra | 2 | review_string_literal | Crash Chance: {0:P1}<br> |
| KKDropPodJammer.KKDropPodJammerSettings | DoSettingsWindowContents | 0 | ui_method_literal | Debug options (for mod developer) |
| KKDropPodJammer.KKRitualStageAction_ConsumeThing | ExposeData | 4 | review_string_literal | enoughConsumed |
| KKDropPodJammer.KKStageFailTrigger_Checkable | ExposeData | 1 | review_string_literal | hasBeenChecked |
| KKDropPodJammer.KKDropPodJammerSettings | DoSettingsWindowContents | 1 | ui_method_literal | Enable debug logging (default: Off) |
| KKDropPodJammer.KKCompCommConsoleJammingStatus+<>c__DisplayClass1_0 | <CompGetGizmosExtra>b__0 | 1 | review_string_literal | [KKDropPodJammer][Dev] Protocol activated via gizmo. |
| KKDropPodJammer.KKJammingProtocolTracker | CountPoweredMineralScanners | 0 | review_string_literal | LongRangeMineralScanner |
| KKDropPodJammer.KKHarmonyArrivalShared | HandleArrivalCrashIfActive | 0 | review_string_literal | [KKDropPodJammer] Arrival detected but faction is NOT hostile; skipping crash logic and keeping protocol active. |
| KKDropPodJammer.KKDropPodJammerSettings | ExposeData | 2 | review_string_literal | ritualDurationTicks |
| KKDropPodJammer.KKDebugActions | ResetProtocolDebug | 0 | ui_method_literal | No tracker found! |
| KKDropPodJammer.KKDropPodJammerSettings | DoSettingsWindowContents | 20 | ui_method_literal | F0 |
| KKDropPodJammer.KKDropPodJammerSettings | SetupIngredientSettingsAndDefaults | 2 | review_string_literal | Silver |
| CustomSpectatorFilter.RitualSpectatorFilter_Custom | ExposeData | 0 | review_string_literal | minSkills |
| KKDropPodJammer.KKStageFailTrigger_NoCharterAvailable | Failed | 4 | ui_method_literal | No Jamming Charter Available (not on map or carried by ritual pawn) |
| KKDropPodJammer.KKDropPodJammerSettings | ExposeData | 11 | review_string_literal | ingredientValues |
| KKDropPodJammer.KKRitualStageAction_ConsumeThing | ExposeData | 0 | review_string_literal | thingDefName |
| KKDropPodJammer.KKPatch_ForceEdgeDrop | Postfix | 0 | review_string_literal | CenterDrop |
| KKDropPodJammer.KKStageEndTrigger_NoFailures+<>c__DisplayClass1_0 | <MakeTrigger>b__0 | 5 | review_string_literal | [KK] NoFailures: check: failed={0}  itemConsumed={1} |
| KKDropPodJammer.KKHarmonyArrivalShared | HandleArrivalCrashIfActive | 3 | review_string_literal | [KKDropPodJammer] {0} * {1:P0} = {2:0.##}, floored to {3} raider(s) to crash (min 1). |
| KKDropPodJammer.KKHarmonyArrivalShared+<>c | <KillByCrash>b__3_0 | 0 | review_string_literal | Torso |
| KKDropPodJammer.KKDropPodJammerSettings | SetupIngredientSettingsAndDefaults | 0 | review_string_literal | KKJammingCharterRecipe |
| KKDropPodJammer.KKDropPodJammerSettings | DoSettingsWindowContents | 6 | ui_method_literal | Gold |
| KKDropPodJammer.KKHarmonyArrivalShared | ApplyCrashOutcomes | 0 | ui_method_literal | [KKDropPodJammer] Crash outcome: KILLED ->  |
| KKDropPodJammer.KKDropPodJammerSettings | ExposeData | 8 | review_string_literal | crashInjuredChance |
| KKDropPodJammer.KKDropPodJammerSettings | ExposeData | 4 | review_string_literal | scannerCrashBonus |
| KKDropPodJammer.KKHarmonyArrivalShared | ApplyCrashOutcomes | 2 | ui_method_literal | [KKDropPodJammer] Crash outcome: INJURED ->  |
| KKDropPodJammer.KKStageEndTrigger_ThingDeliveredOrTimeout+<>c__DisplayClass6_0 | <MakeTrigger>b__0 | 2 | ui_method_literal | [KK] [Ritual:{0}] Item consumed (flag set), ending stage. |
| KKDropPodJammer.KKRitualStageAction_ConsumeThing | Apply | 0 | review_string_literal | [KK] Consumed {0}x {1} at {2} for ritual. |
| KKDropPodJammer.KKDebugActions | ResetProtocolDebug | 2 | ui_method_literal | Debug Action |
| KKDropPodJammer.KKDropPodJammerSettings | ExposeData | 1 | review_string_literal | jammingCharterWorkAmount |
| KKDropPodJammer.KKStageEndTrigger_ThingDeliveredOrTimeout | ExposeData | 2 | review_string_literal | originalTicks |
| KKDropPodJammer.KKDropPodJammerSettings | SetupIngredientSettingsAndDefaults | 3 | review_string_literal | Gold |
| KKDropPodJammer.KKPatch_ForceEdgeDrop | Postfix | 1 | review_string_literal | [KKDropPodJammer] Forcing hostile raid to EdgeDrop due to active Jamming Protocol! |
| KKDropPodJammer.KKDropPodJammerSettings | DoSettingsWindowContents | 24 | ui_method_literal | Chance pawn is downed (%) (default: {0:F0}) |
| KKDropPodJammer.KKCompCommConsoleJammingStatus+<>c__DisplayClass1_0 | <CompGetGizmosExtra>b__1 | 1 | review_string_literal | Dev Gizmo |
| KKDropPodJammer.KKRitualOutcomeEffectWorker_FromQuality | TryPlayVanillaOutcomeCue | 4 | review_string_literal | Standard_NegativeEvent |
| KKDropPodJammer.KKDropPodJammerSettings | DoSettingsWindowContents | 3 | ui_method_literal | KKJammingCharterRecipe |
| KKDropPodJammer.KKHarmonyArrivalShared | ApplyCrashOutcomes | 3 | ui_method_literal | [KKDropPodJammer] Crash outcome: NO INJURY ->  |
| KKDropPodJammer.KKStageEndTrigger_NoFailures+<>c__DisplayClass1_0 | <MakeTrigger>b__0 | 1 | review_string_literal | [KK] NoFailures: 'failed' field found, value={0} |
| KKDropPodJammer.KKCompCommConsoleJammingStatus+<CompGetGizmosExtra>d__1 | MoveNext | 3 | ui_method_literal | Turn the protocol off (or call old ResetProtocol if available). |
| KKDropPodJammer.KKRitualOutcomeEffectWorker_FromQuality | BuildIntBreakdownLine | 0 | ui_method_literal | n/a |
| KKDropPodJammer.KKPatch_DrawOutcomeChancesForJammer | Prefix | 0 | ui_method_literal | outcome |
| KKDropPodJammer.KKDropPodJammerSettings | DoSettingsWindowContents | 17 | ui_method_literal | Note: Maximum total crash chance is capped at 100%. |
| KKDropPodJammer.KKJammingProtocolTracker | ActivateProtocol | 1 | review_string_literal | [KKDropPodJammer] Jamming Protocol ACTIVATED ( |
| KKDropPodJammer.KKDropPodJammerSettings | DoSettingsWindowContents | 8 | ui_method_literal | Could not find recipe 'KKJammingCharterRecipe'. Ensure XML is loaded. |
| KKDropPodJammer.KKCompCommConsoleJammingStatus+<>c__DisplayClass1_0 | <CompGetGizmosExtra>b__1 | 2 | review_string_literal | Dev Gizmo |
| KKDropPodJammer.KKRitualObligationTargetWorker_Custom | CanUseTargetInternal | 1 | review_string_literal | KKConsoleExtender |
| KKDropPodJammer.KKStageFailTrigger_NoCharterAvailable | Failed | 3 | ui_method_literal | [KK] Ritual failed: No Jamming Charter Available on map (def missing). |
| KKDropPodJammer.KKJammingProtocolTracker | AnyPoweredCommsConsole | 0 | review_string_literal | CommsConsole |
| KKDropPodJammer.KKPatch_MineralScannerInspectString | Postfix | 1 | review_string_literal | Jamming Protocol: {0:0.##}% Crash Chance. |
| KKDropPodJammer.KKRitualStageAction_ConsumeThing | Apply | 1 | review_string_literal | StageActionComplete |
| KKDropPodJammer.KKRitualStageAction_ConsumeThing | Apply | 2 | review_string_literal | [KK] RitualStageAction_ConsumeThing signaled stage complete after consuming. |
