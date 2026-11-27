using System;
using System.Collections.Generic;
using System.Linq;
using CombatExtended;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace CE_MagazineFed
{
    public struct OriginalAmmoProps
    {
        public bool reloadOneAtATime;
        public float reloadTime;
        public int magazineSize;
        public bool hasStatReloadTime;
        public float origStatReloadTime;
    }

    public static class MagazineUtility
    {
        private static readonly Dictionary<string, Color> CategoryColorCache = new Dictionary<string, Color>();
        private static readonly Dictionary<ThingDef, OriginalAmmoProps> OriginalAmmoPropsCache = new Dictionary<ThingDef, OriginalAmmoProps>();
        private static readonly Dictionary<AmmoSetDef, bool> PistolCaliberCache = new Dictionary<AmmoSetDef, bool>();

        /// <summary>
        /// Returns true if the given CE AmmoSetDef represents a pistol / SMG / PDW caliber,
        /// false for rifle / high-caliber / shotgun / heavy-advanced calibers.
        /// </summary>
        public static bool IsPistolCaliber(AmmoSetDef ammoSet)
        {
            if (ammoSet == null) return false;
            if (PistolCaliberCache.TryGetValue(ammoSet, out bool cached)) return cached;

            bool result = ComputeIsPistolCaliber(ammoSet);
            PistolCaliberCache[ammoSet] = result;
            return result;
        }

        private static bool ComputeIsPistolCaliber(AmmoSetDef ammoSet)
        {
            bool hasExplicitRifleOrHeavy = false;
            bool hasAdvancedPistolHint = false;

            if (ammoSet.ammoTypes != null)
            {
                for (int i = 0; i < ammoSet.ammoTypes.Count; i++)
                {
                    AmmoDef ammo = ammoSet.ammoTypes[i]?.ammo;
                    if (ammo == null) continue;

                    if (ammo.thingCategories != null)
                    {
                        for (int c = 0; c < ammo.thingCategories.Count; c++)
                        {
                            ThingCategoryDef cat = ammo.thingCategories[c];
                            while (cat != null)
                            {
                                string catDef = cat.defName;
                                if (catDef == "AmmoPistols")
                                {
                                    return true;
                                }
                                if (catDef == "AmmoRifles" || catDef == "AmmoHighCaliber" || catDef == "AmmoShotguns")
                                {
                                    hasExplicitRifleOrHeavy = true;
                                }
                                cat = cat.parent;
                            }
                        }
                    }

                    if (MatchesPistolKeyword(ammo.defName))
                    {
                        hasAdvancedPistolHint = true;
                    }

                    string texPath = ammo.graphicData?.texPath;
                    if (!string.IsNullOrEmpty(texPath))
                    {
                        if (texPath.IndexOf("Things/Ammo/Pistol", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            texPath.IndexOf("Charged/Small", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            texPath.IndexOf("PlasmaCell/Pistol", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            hasAdvancedPistolHint = true;
                        }
                    }
                }
            }

            if (hasExplicitRifleOrHeavy) return false;
            if (hasAdvancedPistolHint) return true;
            if (MatchesPistolKeyword(ammoSet.defName)) return true;

            return false;
        }

        private static bool MatchesPistolKeyword(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name.IndexOf("Pistol", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("SMG", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("PDW", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Sidearm", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool TryGetOriginalAmmoProps(ThingDef def, out OriginalAmmoProps orig)
        {
            orig = default;
            if (def == null) return false;
            if (OriginalAmmoPropsCache.TryGetValue(def, out orig)) return true;

            var props = def.GetCompProperties<CompProperties_AmmoUser>();
            if (props == null) return false;

            bool hasStat = def.statBases != null && def.statBases.StatListContains(CE_StatDefOf.ReloadTime);
            orig = new OriginalAmmoProps
            {
                reloadOneAtATime = props.reloadOneAtATime,
                reloadTime = props.reloadTime,
                magazineSize = props.magazineSize,
                hasStatReloadTime = hasStat,
                origStatReloadTime = hasStat ? def.statBases.GetStatValueFromList(CE_StatDefOf.ReloadTime, props.reloadTime) : props.reloadTime
            };
            OriginalAmmoPropsCache[def] = orig;
            return true;
        }

        private static float RoundToOneDecimal(float value, float originalPositiveCheck)
        {
            if (originalPositiveCheck <= 0f) return 0f;
            float rounded = (float)Math.Round(value, 1, MidpointRounding.AwayFromZero);
            return Mathf.Max(0.1f, rounded);
        }

        /// <summary>
        /// Snapshots untouched CE CompProperties_AmmoUser values for all ThingDefs and applies saved overrides.
        /// Must run at startup after all Defs and XML patches are loaded.
        /// </summary>
        public static void CacheOriginalWeaponPropsAndApplyOverrides()
        {
            int overriddenCount = 0;
            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (!TryGetOriginalAmmoProps(def, out _)) continue;
                if (ApplyWeaponFeedOverride(def)) overriddenCount++;
            }
            if (overriddenCount > 0)
            {
                Log.Message($"[CE_MagazineFed] Applied custom feed mode & reload time overrides to {overriddenCount} weapon defs.");
            }
        }

        /// <summary>
        /// Resolves a weapon's default feeding mode (XML FeedModeExtension > CE original reloadOneAtATime).
        /// </summary>
        public static WeaponFeedModeOverride GetDefaultFeedMode(ThingDef def, out bool fromXmlExtension)
        {
            fromXmlExtension = false;
            var ext = def?.GetModExtension<FeedModeExtension>();
            if (ext != null && ext.defaultFeedMode != WeaponFeedModeOverride.Auto)
            {
                fromXmlExtension = true;
                return ext.defaultFeedMode;
            }
            bool origSingleLoad = TryGetOriginalAmmoProps(def, out var orig)
                ? orig.reloadOneAtATime
                : (def?.GetCompProperties<CompProperties_AmmoUser>()?.reloadOneAtATime ?? false);
            return origSingleLoad ? WeaponFeedModeOverride.SingleLoad : WeaponFeedModeOverride.MagazineFed;
        }

        /// <summary>
        /// Synchronizes a weapon ThingDef's CompProperties_AmmoUser (reloadOneAtATime & reloadTime)
        /// with the active feeding mode (Player Mod Settings Override > XML FeedModeExtension > CE original).
        /// Returns true if reload properties were modified from their original CE values.
        /// </summary>
        public static bool ApplyWeaponFeedOverride(ThingDef def)
        {
            if (def == null || !TryGetOriginalAmmoProps(def, out var orig)) return false;
            var props = def.GetCompProperties<CompProperties_AmmoUser>();
            if (props == null) return false;

            if (!IsEligibleWeaponDef(def, out _, out _))
            {
                return false;
            }

            var playerOv = CE_MagazineFedMod.Settings?.GetWeaponOverride(def) ?? WeaponFeedModeOverride.Auto;
            bool usingPlayerOverride = playerOv != WeaponFeedModeOverride.Auto;
            var ext = def.GetModExtension<FeedModeExtension>();
            bool hasXmlMode = ext != null && ext.defaultFeedMode != WeaponFeedModeOverride.Auto;

            WeaponFeedModeOverride effectiveMode = usingPlayerOverride
                ? playerOv
                : (hasXmlMode
                    ? ext.defaultFeedMode
                    : (orig.reloadOneAtATime ? WeaponFeedModeOverride.SingleLoad : WeaponFeedModeOverride.MagazineFed));

            bool targetReloadOneAtATime = orig.reloadOneAtATime;
            float targetReloadTime = orig.reloadTime;
            float baseStatReload = orig.origStatReloadTime > 0f ? orig.origStatReloadTime : orig.reloadTime;
            float targetStatReloadTime = baseStatReload;
            bool modified = false;

            if (effectiveMode == WeaponFeedModeOverride.VanillaCE)
            {
                // Restore original CE values
            }
            else if (!usingPlayerOverride && hasXmlMode && ext.overrideReloadTime > 0f)
            {
                // Mod author specified an explicit reloadTime override in XML
                targetReloadOneAtATime = (effectiveMode == WeaponFeedModeOverride.SingleLoad);
                targetReloadTime = RoundToOneDecimal(ext.overrideReloadTime, ext.overrideReloadTime);
                targetStatReloadTime = targetReloadTime;
                modified = (targetReloadOneAtATime != orig.reloadOneAtATime) || Math.Abs(targetReloadTime - orig.reloadTime) > 0.005f;
            }
            else if (usingPlayerOverride || (hasXmlMode && ext.autoAdjustReloadTime))
            {
                if (orig.reloadOneAtATime && effectiveMode == WeaponFeedModeOverride.MagazineFed)
                {
                    // Single-load weapon converted to Magazine-Fed: origReloadTime * magSize * 0.9 (rounded to 1 decimal)
                    targetReloadOneAtATime = false;
                    targetReloadTime = RoundToOneDecimal(orig.reloadTime * orig.magazineSize * 0.9f, orig.reloadTime);
                    targetStatReloadTime = RoundToOneDecimal(baseStatReload * orig.magazineSize * 0.9f, baseStatReload);
                    modified = true;
                }
                else if (!orig.reloadOneAtATime && effectiveMode == WeaponFeedModeOverride.SingleLoad)
                {
                    // Magazine-fed weapon converted to Single-Load: origReloadTime / magSize (rounded to 1 decimal)
                    targetReloadOneAtATime = true;
                    int magDiv = Mathf.Max(1, orig.magazineSize);
                    targetReloadTime = RoundToOneDecimal(orig.reloadTime / magDiv, orig.reloadTime);
                    targetStatReloadTime = RoundToOneDecimal(baseStatReload / magDiv, baseStatReload);
                    modified = true;
                }
            }

            props.reloadOneAtATime = targetReloadOneAtATime;
            props.reloadTime = targetReloadTime;

            // CE's VerbPropertiesCE.useEquipmentStatValues defaults to true and CE injects CE_StatDefOf.ReloadTime
            // into def.statBases at startup (potentially after our StaticConstructorOnStartup).
            // Always synchronize CE_StatDefOf.ReloadTime so JobDriver_Reload, PortLoad, and ReloadMagazine use the updated value!
            if (def.statBases != null && (modified || orig.hasStatReloadTime || def.statBases.StatListContains(CE_StatDefOf.ReloadTime)))
            {
                def.SetStatBaseValue(CE_StatDefOf.ReloadTime, targetStatReloadTime);
            }
            else if (modified && def.statBases == null)
            {
                def.statBases = new List<StatModifier>();
                def.SetStatBaseValue(CE_StatDefOf.ReloadTime, targetStatReloadTime);
            }

            return modified;
        }

        /// <summary>
        /// True if the pawn is a player-faction mechanoid that is still under player control
        /// (excludes hostile/feral/disconnected mechanoids).
        /// </summary>
        public static bool IsPlayerControlledMechanoid(Pawn pawn)
        {
            if (pawn == null || pawn.RaceProps == null || !pawn.RaceProps.IsMechanoid) return false;
            if (pawn.Faction != Faction.OfPlayer) return false;
            if (pawn.HostileTo(Faction.OfPlayer)) return false;
            if (ModsConfig.BiotechActive && pawn.OverseerSubject != null && pawn.OverseerSubject.State != OverseerSubjectState.Overseen)
            {
                return false;
            }
            return true;
        }

        public static bool ShouldUseMagazineSystem(Pawn pawn)
        {
            if (pawn == null || pawn.Dead) return false;
            if (pawn.RaceProps == null) return false;
            if (pawn.inventory == null || pawn.inventory.innerContainer == null) return false;
            if (pawn.TryGetComp<CompInventory>() == null) return false;

            var settings = CE_MagazineFedMod.Settings;
            var scope = settings?.factionScope ?? FactionScope.PlayerOnly;

            if (pawn.RaceProps.IsMechanoid)
            {
                if (!(settings?.enableForMechanoids ?? false)) return false;
                if (scope == FactionScope.AllFactions) return true;
                return IsPlayerControlledMechanoid(pawn);
            }

            if (!pawn.RaceProps.Humanlike) return false;

            if (scope == FactionScope.AllFactions)
            {
                return true;
            }
            return pawn.IsColonistPlayerControlled || pawn.Faction == Faction.OfPlayer;
        }

        public static float GetCaliberReferenceBulk(AmmoSetDef ammoSet)
        {
            if (ammoSet?.ammoTypes != null)
            {
                for (int i = 0; i < ammoSet.ammoTypes.Count; i++)
                {
                    var ad = ammoSet.ammoTypes[i]?.ammo;
                    if (ad != null)
                    {
                        return ad.GetStatValueAbstract(CE_StatDefOf.Bulk);
                    }
                }
            }
            return 0.02f;
        }

        public static float GetCaliberReferenceMass(AmmoSetDef ammoSet)
        {
            if (ammoSet?.ammoTypes != null)
            {
                for (int i = 0; i < ammoSet.ammoTypes.Count; i++)
                {
                    var ad = ammoSet.ammoTypes[i]?.ammo;
                    if (ad != null)
                    {
                        return ad.GetStatValueAbstract(StatDefOf.Mass);
                    }
                }
            }
            return 0.01f;
        }

        public static float EstimateEmptyMagazineMass(int capacity)
        {
            return Mathf.Clamp(0.05f + capacity * 0.004f, 0.06f, 0.65f);
        }

        public static float EstimateFullMagazineBulk(AmmoSetDef ammoSet, int capacity)
        {
            float emptyShellBulk = Mathf.Clamp(0.12f + capacity * 0.009f, 0.15f, 1.8f);
            float fullRoundsBulk = GetCaliberReferenceBulk(ammoSet) * capacity * 0.85f;
            return emptyShellBulk + fullRoundsBulk;
        }

        public static float EstimateFullMagazineMass(AmmoSetDef ammoSet, int capacity)
        {
            return EstimateEmptyMagazineMass(capacity) + GetCaliberReferenceMass(ammoSet) * capacity;
        }

        public static float ComputeCompatibleLooseAmmoBulkInInventory(Pawn pawn, AmmoSetDef ammoSet)
        {
            if (pawn?.inventory?.innerContainer == null || ammoSet?.ammoTypes == null) return 0f;
            float totalBulk = 0f;
            var container = pawn.inventory.innerContainer;
            for (int i = 0; i < container.Count; i++)
            {
                Thing t = container[i];
                if (t.def is AmmoDef ad && t.stackCount > 0)
                {
                    for (int j = 0; j < ammoSet.ammoTypes.Count; j++)
                    {
                        if (ammoSet.ammoTypes[j].ammo == ad)
                        {
                            totalBulk += ad.GetStatValueAbstract(CE_StatDefOf.Bulk) * t.stackCount;
                            break;
                        }
                    }
                }
            }
            return totalBulk;
        }

        /// <summary>
        /// Estimates how many additional empty magazines of (ammoSet, capacity) can safely fit in `pawn`'s inventory.
        /// When `includeCompatibleLooseAmmoReclaim` is true, accounts for bulk freed by packing loose ammo already in inventory.
        /// </summary>
        public static int CountMaxNewMagazinesFit(Pawn pawn, AmmoSetDef ammoSet, int capacity, bool includeCompatibleLooseAmmoReclaim = false)
        {
            if (pawn == null || ammoSet == null || capacity <= 0) return 0;
            var compInv = pawn.TryGetComp<CompInventory>();
            if (compInv == null) return 20;

            compInv.UpdateInventory();
            float availBulk = Mathf.Max(0f, compInv.capacityBulk - compInv.currentBulk);
            if (includeCompatibleLooseAmmoReclaim)
            {
                availBulk += ComputeCompatibleLooseAmmoBulkInInventory(pawn, ammoSet);
            }
            float availWeight = Mathf.Max(0f, compInv.capacityWeight - compInv.currentWeight);

            float magBulk = Mathf.Max(0.01f, EstimateFullMagazineBulk(ammoSet, capacity));
            float emptyMass = Mathf.Max(0.01f, EstimateEmptyMagazineMass(capacity));

            int fitByBulk = Mathf.FloorToInt((availBulk + 0.001f) / magBulk);
            int fitByWeight = Mathf.FloorToInt((availWeight + 0.001f) / emptyMass);
            return Mathf.Clamp(Mathf.Min(fitByBulk, fitByWeight), 0, 20);
        }

        public static bool CanFitNewMagazine(Pawn pawn, AmmoSetDef ammoSet, int capacity, bool includeCompatibleLooseAmmoReclaim = false)
        {
            return CountMaxNewMagazinesFit(pawn, ammoSet, capacity, includeCompatibleLooseAmmoReclaim) > 0;
        }

        /// <summary>
        /// Computes initial total magazine count for newly configured weapons.
        /// For mechanoids, caps spare magazines based on remaining CarryBulk and CarryWeight so light/heavy mechs are not overburdened.
        /// </summary>
        public static int ComputeInitialMagCountForPawn(Pawn pawn, CompAmmoUser compAmmo)
        {
            int desired = Mathf.Clamp(CE_MagazineFedMod.Settings?.defaultMagCount ?? 4, 1, 20);
            if (pawn == null || compAmmo == null || compAmmo.CurAmmoSet == null) return desired;
            if (pawn.RaceProps == null || !pawn.RaceProps.IsMechanoid) return desired;

            var compInv = pawn.TryGetComp<CompInventory>();
            if (compInv == null) return desired;

            compInv.UpdateInventory();
            float existingAmmoBulk = 0f;
            float existingAmmoMass = 0f;
            var container = pawn.inventory?.innerContainer;
            if (container != null)
            {
                for (int i = 0; i < container.Count; i++)
                {
                    Thing t = container[i];
                    if (t is Thing_CEMagazine mag && mag.Matches(compAmmo.CurAmmoSet, compAmmo.MagSize))
                    {
                        existingAmmoBulk += mag.GetFullMagazineBulk();
                        existingAmmoMass += t.GetStatValue(StatDefOf.Mass);
                    }
                    else if (t.def is AmmoDef ad && t.stackCount > 0 && compAmmo.CurAmmoSet.ammoTypes != null)
                    {
                        for (int j = 0; j < compAmmo.CurAmmoSet.ammoTypes.Count; j++)
                        {
                            if (compAmmo.CurAmmoSet.ammoTypes[j].ammo == ad)
                            {
                                existingAmmoBulk += ad.GetStatValueAbstract(CE_StatDefOf.Bulk) * t.stackCount;
                                existingAmmoMass += ad.GetStatValueAbstract(StatDefOf.Mass) * t.stackCount;
                                break;
                            }
                        }
                    }
                }
            }

            float nonAmmoBulk = Mathf.Max(0f, compInv.currentBulk - existingAmmoBulk);
            float nonAmmoWeight = Mathf.Max(0f, compInv.currentWeight - existingAmmoMass);
            float usableBulk = Mathf.Max(0f, compInv.capacityBulk - nonAmmoBulk);
            float usableWeight = Mathf.Max(0f, compInv.capacityWeight - nonAmmoWeight);

            float magBulk = Mathf.Max(0.01f, EstimateFullMagazineBulk(compAmmo.CurAmmoSet, compAmmo.MagSize));
            float magMass = Mathf.Max(0.01f, EstimateFullMagazineMass(compAmmo.CurAmmoSet, compAmmo.MagSize));

            int maxSpareByBulk = Mathf.FloorToInt((usableBulk * 0.75f) / magBulk);
            int maxSpareByWeight = Mathf.FloorToInt((usableWeight * 0.80f) / magMass);
            int maxTotal = 1 + Mathf.Max(0, Mathf.Min(maxSpareByBulk, maxSpareByWeight));

            return Mathf.Clamp(desired, 1, maxTotal);
        }

        /// <summary>
        /// Computes toil duration and rounds-per-toil batch size for packing magazines.
        /// Uses ReloadSpeed and applies the mechanoid packing speed multiplier (1.0x..10.0x) with sub-tick batching.
        /// </summary>
        public static int GetPackingTicksAndBatch(Pawn pawn, bool isFieldPacking, int maxBatchRounds, out int batchRounds)
        {
            float minStat = isFieldPacking ? 0.2f : 0.25f;
            float speed = Mathf.Max(minStat, pawn != null ? pawn.GetStatValue(CE_StatDefOf.ReloadSpeed) : 1f);
            if (pawn?.RaceProps != null && pawn.RaceProps.IsMechanoid)
            {
                speed *= CE_MagazineFedMod.Settings?.EffectiveMechPackingSpeedMult ?? 1f;
            }

            int baseTicks = isFieldPacking
                ? (CE_MagazineFedMod.Settings?.TicksPerRoundField ?? 18)
                : (CE_MagazineFedMod.Settings?.TicksPerRoundIdle ?? 9);
            float exactTicksPerRound = baseTicks / Mathf.Max(0.05f, speed);
            int minTicks = isFieldPacking ? 4 : 2;
            int cap = Mathf.Max(1, maxBatchRounds);

            if (exactTicksPerRound < 3f && cap > 1)
            {
                batchRounds = Mathf.Clamp(Mathf.CeilToInt(6f / Mathf.Max(0.05f, exactTicksPerRound)), 1, Mathf.Min(cap, 25));
                return Mathf.Max(minTicks, Mathf.RoundToInt(exactTicksPerRound * batchRounds));
            }

            batchRounds = 1;
            return Mathf.Max(minTicks, Mathf.CeilToInt(exactTicksPerRound));
        }

        /// <summary>
        /// Shared player-triggered command to immediately pack & organize a colonist/mechanoid's magazines.
        /// </summary>
        public static bool TryOrderPackNow(Pawn pawn, bool showMessages = true)
        {
            if (pawn == null || pawn.Dead) return false;

            bool allowDraftedInvPack = pawn.Drafted && MoveWhileReloadUtility.CanMoveWhileReloading(pawn);
            if (pawn.Drafted && !allowDraftedInvPack)
            {
                if (showMessages)
                {
                    Messages.Message("CEMF_UI_PackNowDrafted".Translate(pawn.LabelShortCap), pawn, MessageTypeDefOf.RejectInput, false);
                }
                return false;
            }
            if (pawn.CurJobDef == CEMF_DefOf.CEMF_PackMagazines)
            {
                if (showMessages)
                {
                    Messages.Message("CEMF_UI_PackNowAlready".Translate(pawn.LabelShortCap), pawn, MessageTypeDefOf.NeutralEvent, false);
                }
                return false;
            }
            if (!JobGiver_PackMagazines.CanAttemptPacking(pawn, allowDrafted: allowDraftedInvPack))
            {
                if (showMessages)
                {
                    Messages.Message("CEMF_UI_PackNowIncapable".Translate(pawn.LabelShortCap), pawn, MessageTypeDefOf.RejectInput, false);
                }
                return false;
            }

            if (allowDraftedInvPack)
            {
                if (!JobGiver_PackMagazines.HasInventoryOnlyPackingWork(pawn))
                {
                    if (showMessages)
                    {
                        Messages.Message("CEMF_UI_PackNowDraftedNoAmmo".Translate(pawn.LabelShortCap), pawn, MessageTypeDefOf.NeutralEvent, false);
                    }
                    return false;
                }
                Job draftedJob = JobMaker.MakeJob(CEMF_DefOf.CEMF_PackMagazines, pawn);
                draftedJob.playerForced = true;
                bool draftedAccepted = MoveWhileReloadUtility.StartJobPreservingMove(pawn, draftedJob, useOrderedJob: true);
                if (draftedAccepted && showMessages)
                {
                    Messages.Message("CEMF_UI_PackNowOrdered".Translate(pawn.LabelShortCap), pawn, MessageTypeDefOf.PositiveEvent, false);
                }
                return draftedAccepted;
            }

            var mgr = GameComponent_MagazineManager.Instance;
            if (mgr != null)
            {
                bool preferRecycle = CE_MagazineFedMod.Settings?.preferRecycleMapMagazines ?? true;
                foreach (var w in GetManagedWeapons(pawn))
                {
                    var ca = w.TryGetComp<CompAmmoUser>();
                    if (IsMagazineFedWeapon(ca))
                    {
                        var cfg = mgr.GetOrCreateConfig(pawn, ca);
                        if (cfg != null)
                        {
                            mgr.EnsureMagazinesForConfig(pawn, cfg, allowCreateNew: !preferRecycle, reservePackingHeadroom: true);
                        }
                    }
                }
            }

            if (!JobGiver_PackMagazines.HasActionablePackingWork(pawn))
            {
                if (showMessages)
                {
                    if (mgr != null && mgr.PawnNeedsPacking(pawn, out var missing) && missing.Count > 0)
                    {
                        var compInv = pawn.TryGetComp<CompInventory>();
                        bool blockedBySpace = compInv != null && missing.All(kv => kv.Key == null || !compInv.CanFitInInventory(kv.Key, out int fit) || fit <= 0);
                        if (blockedBySpace)
                        {
                            Messages.Message("CEMF_UI_PackNowInventoryFull".Translate(pawn.LabelShortCap), pawn, MessageTypeDefOf.RejectInput, false);
                            return false;
                        }
                    }
                    Messages.Message("CEMF_UI_PackNowNotNeeded".Translate(pawn.LabelShortCap), MessageTypeDefOf.NeutralEvent, false);
                }
                return false;
            }

            Job job = JobMaker.MakeJob(CEMF_DefOf.CEMF_PackMagazines, pawn);
            job.playerForced = true;
            bool accepted = MoveWhileReloadUtility.StartJobPreservingMove(pawn, job, useOrderedJob: true);
            if (accepted && showMessages)
            {
                Messages.Message("CEMF_UI_PackNowOrdered".Translate(pawn.LabelShortCap), pawn, MessageTypeDefOf.PositiveEvent, false);
            }
            return accepted;
        }

        public static bool ShouldUseMagazineSystem(CompAmmoUser compAmmo)
        {
            if (compAmmo == null) return false;
            if (compAmmo.turret != null) return false;
            if (compAmmo.parent is Building_AutoloaderCE) return false;
            return ShouldUseMagazineSystem(compAmmo.Holder);
        }

        /// <summary>
        /// Checks whether a weapon ThingDef is eligible for magazine/single-load management in Settings UI.
        /// Excludes turrets, single-shot weapons (magSize <= 1), and multi-charge packs/fuel tanks.
        /// </summary>
        public static bool IsEligibleWeaponDef(ThingDef def, out CompProperties_AmmoUser props, out WeaponFeedModeOverride defaultMode)
        {
            props = null;
            defaultMode = WeaponFeedModeOverride.VanillaCE;
            if (def == null || !def.IsRangedWeapon) return false;
            if (def.building != null) return false;
            if (def.weaponTags != null && def.weaponTags.Contains("TurretGun")) return false;

            props = def.GetCompProperties<CompProperties_AmmoUser>();
            if (props == null || props.ammoSet == null || props.magazineSize <= 1) return false;
            if (props.ammoSet.ammoTypes == null || props.ammoSet.ammoTypes.Count == 0) return false;

            var types = props.ammoSet.ammoTypes;
            for (int i = 0; i < types.Count; i++)
            {
                var ad = types[i].ammo;
                if (ad == null) continue;
                if (ad.ammoCount > 1 || ad.HasComp(typeof(CompApparelReloadable)))
                {
                    return false;
                }
            }

            defaultMode = GetDefaultFeedMode(def, out _);
            return true;
        }

        /// <summary>
        /// Resolves the effective feeding mode for a live CompAmmoUser.
        /// Priority: Player Mod Settings Override > XML FeedModeExtension > CE reloadOneAtATime auto-detection.
        /// </summary>
        public static WeaponFeedModeOverride GetEffectiveFeedMode(CompAmmoUser compAmmo)
        {
            if (compAmmo == null || !compAmmo.UseAmmo) return WeaponFeedModeOverride.VanillaCE;
            if (compAmmo.turret != null || compAmmo.parent is Building_AutoloaderCE) return WeaponFeedModeOverride.VanillaCE;
            if (compAmmo.Props == null || compAmmo.MagSize <= 1) return WeaponFeedModeOverride.VanillaCE;
            if (compAmmo.CurAmmoSet?.ammoTypes == null || compAmmo.CurAmmoSet.ammoTypes.Count == 0) return WeaponFeedModeOverride.VanillaCE;

            // Exclude special multi-charge packs / fuel canisters (e.g. flamethrower tanks, reusable packs)
            var types = compAmmo.CurAmmoSet.ammoTypes;
            for (int i = 0; i < types.Count; i++)
            {
                var ad = types[i].ammo;
                if (ad == null) continue;
                if (ad.ammoCount > 1 || ad.HasComp(typeof(CompApparelReloadable)))
                {
                    return WeaponFeedModeOverride.VanillaCE;
                }
            }

            ThingDef weaponDef = compAmmo.parent?.def;
            var ov = CE_MagazineFedMod.Settings?.GetWeaponOverride(weaponDef) ?? WeaponFeedModeOverride.Auto;
            if (ov != WeaponFeedModeOverride.Auto)
            {
                return ov;
            }
            return GetDefaultFeedMode(weaponDef, out _);
        }

        /// <summary>
        /// True if this weapon uses detachable magazines (honors per-weapon Settings overrides).
        /// </summary>
        public static bool IsMagazineFedWeapon(CompAmmoUser compAmmo)
        {
            return GetEffectiveFeedMode(compAmmo) == WeaponFeedModeOverride.MagazineFed;
        }

        /// <summary>
        /// True if this weapon uses single-round loading with a multi-round ordered ammo plan (honors per-weapon Settings overrides).
        /// </summary>
        public static bool IsSingleLoadMultiCapacityWeapon(CompAmmoUser compAmmo)
        {
            return GetEffectiveFeedMode(compAmmo) == WeaponFeedModeOverride.SingleLoad;
        }

        public static bool IsManagedWeapon(CompAmmoUser compAmmo)
        {
            return IsMagazineFedWeapon(compAmmo) || IsSingleLoadMultiCapacityWeapon(compAmmo);
        }

        /// <summary>
        /// True if CE's JobGiver_TakeAndEquip would try to auto-pickup loose ammo for a magazine-fed weapon
        /// (checking Primary if equipped, otherwise first ranged weapon in inventory, matching CE's logic),
        /// AND no non-magazine-fed weapon on the pawn shares that same AmmoSetDef.
        /// </summary>
        public static bool ShouldBlockTakeAndEquipLooseAmmo(Pawn pawn)
        {
            if (!(CE_MagazineFedMod.Settings?.preventAutoPickupLooseAmmo ?? true)) return false;
            if (pawn == null || !ShouldUseMagazineSystem(pawn)) return false;

            CompAmmoUser targetComp = null;
            if (pawn.equipment?.Primary != null)
            {
                targetComp = pawn.equipment.Primary.TryGetComp<CompAmmoUser>();
            }
            else
            {
                var compInv = pawn.TryGetComp<CompInventory>();
                if (compInv?.rangedWeaponList != null)
                {
                    for (int i = 0; i < compInv.rangedWeaponList.Count; i++)
                    {
                        var ca = compInv.rangedWeaponList[i].TryGetComp<CompAmmoUser>();
                        if (ca != null)
                        {
                            targetComp = ca;
                            break;
                        }
                    }
                }
            }

            if (targetComp == null || !IsMagazineFedWeapon(targetComp) || targetComp.CurAmmoSet == null)
            {
                return false;
            }

            // Allow if pawn also carries a non-mag-fed weapon (e.g. pump shotgun, single-load revolver) using the same AmmoSet
            AmmoSetDef targetSet = targetComp.CurAmmoSet;
            if (pawn.inventory?.innerContainer != null)
            {
                var container = pawn.inventory.innerContainer;
                for (int i = 0; i < container.Count; i++)
                {
                    if (container[i] is ThingWithComps twc && twc != targetComp.parent)
                    {
                        var ca = twc.TryGetComp<CompAmmoUser>();
                        if (ca != null && ca.UseAmmo && ca.CurAmmoSet == targetSet && !IsMagazineFedWeapon(ca))
                        {
                            return false;
                        }
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// True if `thingDef` is loose AmmoDef belonging to a magazine-fed weapon equipped/carried by `pawn`,
        /// AND no non-magazine-fed weapon (e.g. pump shotgun, bow) on `pawn` uses that same AmmoDef.
        /// </summary>
        public static bool IsLooseAmmoBlockedForAutoPickup(Pawn pawn, ThingDef thingDef)
        {
            if (!(CE_MagazineFedMod.Settings?.preventAutoPickupLooseAmmo ?? true)) return false;
            if (pawn == null || !(thingDef is AmmoDef ammoDef)) return false;
            if (!ShouldUseMagazineSystem(pawn)) return false;

            bool usedByMagFed = false;
            bool usedByNonMagFed = false;

            // Check primary equipped weapon
            if (pawn.equipment?.Primary != null)
            {
                CheckWeaponUsesAmmo(pawn.equipment.Primary, ammoDef, ref usedByMagFed, ref usedByNonMagFed);
            }

            // Check inventory ranged weapons (sidearms)
            if (pawn.inventory?.innerContainer != null)
            {
                var container = pawn.inventory.innerContainer;
                for (int i = 0; i < container.Count; i++)
                {
                    if (container[i] is ThingWithComps twc && twc != pawn.equipment?.Primary)
                    {
                        CheckWeaponUsesAmmo(twc, ammoDef, ref usedByMagFed, ref usedByNonMagFed);
                    }
                }
            }

            return usedByMagFed && !usedByNonMagFed;
        }

        private static void CheckWeaponUsesAmmo(ThingWithComps weapon, AmmoDef ammoDef, ref bool usedByMagFed, ref bool usedByNonMagFed)
        {
            var ca = weapon.TryGetComp<CompAmmoUser>();
            if (ca == null || !ca.UseAmmo || ca.CurAmmoSet?.ammoTypes == null) return;
            bool matches = false;
            var types = ca.CurAmmoSet.ammoTypes;
            for (int i = 0; i < types.Count; i++)
            {
                if (types[i].ammo == ammoDef)
                {
                    matches = true;
                    break;
                }
            }
            if (!matches) return;

            if (IsMagazineFedWeapon(ca)) usedByMagFed = true;
            else usedByNonMagFed = true;
        }

        /// <summary>
        /// Returns all managed weapons currently equipped or carried in inventory by the pawn.
        /// </summary>
        public static List<ThingWithComps> GetManagedWeapons(Pawn pawn)
        {
            var list = new List<ThingWithComps>();
            if (pawn == null) return list;

            if (pawn.equipment?.Primary != null)
            {
                var comp = pawn.equipment.Primary.TryGetComp<CompAmmoUser>();
                if (IsManagedWeapon(comp))
                {
                    list.Add(pawn.equipment.Primary);
                }
            }

            if (pawn.inventory?.innerContainer != null)
            {
                var container = pawn.inventory.innerContainer;
                for (int i = 0; i < container.Count; i++)
                {
                    if (container[i] is ThingWithComps twc)
                    {
                        var comp = twc.TryGetComp<CompAmmoUser>();
                        if (IsManagedWeapon(comp) && !list.Contains(twc))
                        {
                            list.Add(twc);
                        }
                    }
                }
            }
            return list;
        }

        /// <summary>
        /// Returns all Thing_CEMagazine in pawn's inventory matching (ammoSet, capacity).
        /// </summary>
        public static List<Thing_CEMagazine> GetMagazinesInInventory(Pawn pawn, AmmoSetDef ammoSet, int capacity)
        {
            var result = new List<Thing_CEMagazine>();
            if (pawn?.inventory?.innerContainer == null || ammoSet == null) return result;
            var container = pawn.inventory.innerContainer;
            for (int i = 0; i < container.Count; i++)
            {
                if (container[i] is Thing_CEMagazine mag && mag.Matches(ammoSet, capacity))
                {
                    result.Add(mag);
                }
            }
            return result;
        }

        public static List<Thing_CEMagazine> GetLoadedMagazinesInInventory(Pawn pawn, AmmoSetDef ammoSet, int capacity)
        {
            var mags = GetMagazinesInInventory(pawn, ammoSet, capacity);
            mags.RemoveAll(m => m.IsEmpty);
            // Sort: fullest first, then lowest preferredPlanIndex
            mags.Sort((a, b) =>
            {
                int c = b.LoadedCount.CompareTo(a.LoadedCount);
                if (c != 0) return c;
                int pa = a.preferredPlanIndex >= 0 ? a.preferredPlanIndex : 999;
                int pb = b.preferredPlanIndex >= 0 ? b.preferredPlanIndex : 999;
                return pa.CompareTo(pb);
            });
            return mags;
        }

        public static AmmoDef GetPrimaryDominantAmmo(List<AmmoDef> rounds)
        {
            if (rounds == null || rounds.Count == 0) return null;
            var counts = new Dictionary<AmmoDef, int>();
            AmmoDef best = null;
            int bestCount = 0;
            for (int i = 0; i < rounds.Count; i++)
            {
                AmmoDef ad = rounds[i];
                if (ad == null) continue;
                int c = counts.TryGetValue(ad, out int cur) ? cur + 1 : 1;
                counts[ad] = c;
                if (c > bestCount)
                {
                    bestCount = c;
                    best = ad;
                }
            }
            return best;
        }

        public static HashSet<AmmoDef> GetDominantAmmoSet(Dictionary<AmmoDef, int> composition)
        {
            var result = new HashSet<AmmoDef>();
            if (composition == null || composition.Count == 0) return result;

            int maxCount = 0;
            foreach (var kv in composition)
            {
                if (kv.Key != null && kv.Value > maxCount)
                {
                    maxCount = kv.Value;
                }
            }
            if (maxCount <= 0) return result;

            foreach (var kv in composition)
            {
                if (kv.Key != null && kv.Value == maxCount)
                {
                    result.Add(kv.Key);
                }
            }
            return result;
        }

        public static Dictionary<AmmoDef, int> ResolveMagazineComposition(Pawn pawn, Thing_CEMagazine mag)
        {
            if (mag == null) return new Dictionary<AmmoDef, int>();

            if (pawn != null && mag.preferredPlanIndex >= 0 && mag.ammoSet != null)
            {
                var cfg = GameComponent_MagazineManager.Instance?.GetConfig(pawn, mag.ammoSet, mag.capacity, isSingleLoad: false);
                if (cfg != null && mag.preferredPlanIndex < cfg.targetTotalMags)
                {
                    var plan = cfg.GetPlan(mag.preferredPlanIndex);
                    if (plan != null && mag.rounds != null && mag.rounds.Count > 0
                        && (plan.MatchesExactly(mag.rounds) || plan.IsPrefixMatch(mag.rounds) || plan.IsSuffixMatch(mag.rounds)))
                    {
                        return plan.GetCountsByDef();
                    }
                }
            }
            return mag.GetAmmoCounts();
        }

        /// <summary>
        /// Returns 2 for exact recipe/ratio match, 1 for dominant-ammo match, 0 for no match.
        /// </summary>
        public static int GetMagazineMatchTier(Dictionary<AmmoDef, int> preferredComp, Dictionary<AmmoDef, int> candidateComp)
        {
            if (preferredComp == null || candidateComp == null || preferredComp.Count == 0 || candidateComp.Count == 0)
            {
                return 0;
            }

            int prefDistinct = 0;
            int prefTotal = 0;
            foreach (var kv in preferredComp)
            {
                if (kv.Key != null && kv.Value > 0)
                {
                    prefDistinct++;
                    prefTotal += kv.Value;
                }
            }

            int candDistinct = 0;
            int candTotal = 0;
            foreach (var kv in candidateComp)
            {
                if (kv.Key != null && kv.Value > 0)
                {
                    candDistinct++;
                    candTotal += kv.Value;
                }
            }

            if (prefTotal <= 0 || candTotal <= 0) return 0;

            if (prefDistinct == candDistinct)
            {
                bool exactCounts = true;
                bool closeRatio = true;
                foreach (var kv in preferredComp)
                {
                    if (kv.Key == null || kv.Value <= 0) continue;
                    if (!candidateComp.TryGetValue(kv.Key, out int candCount) || candCount <= 0)
                    {
                        exactCounts = false;
                        closeRatio = false;
                        break;
                    }
                    if (candCount != kv.Value) exactCounts = false;
                    float rPref = (float)kv.Value / prefTotal;
                    float rCand = (float)candCount / candTotal;
                    if (Math.Abs(rPref - rCand) > 0.12f)
                    {
                        closeRatio = false;
                    }
                }

                if (exactCounts || closeRatio)
                {
                    return 2;
                }
            }

            var prefDom = GetDominantAmmoSet(preferredComp);
            var candDom = GetDominantAmmoSet(candidateComp);
            foreach (var ad in prefDom)
            {
                if (candDom.Contains(ad))
                {
                    return 1;
                }
            }
            return 0;
        }

        public static Thing_CEMagazine FindBestLoadedMagazine(
            Pawn pawn,
            AmmoSetDef ammoSet,
            int capacity,
            Dictionary<AmmoDef, int> preferredComposition = null,
            int minLoadedCountExclusive = 0)
        {
            var loaded = GetLoadedMagazinesInInventory(pawn, ammoSet, capacity);
            if (minLoadedCountExclusive > 0)
            {
                loaded.RemoveAll(m => m.LoadedCount <= minLoadedCountExclusive);
            }
            if (loaded.Count == 0) return null;

            if (preferredComposition == null || preferredComposition.Count == 0)
            {
                return loaded[0];
            }

            int maxLoaded = loaded[0].LoadedCount;
            int prefThreshold = Math.Max(1, Mathf.CeilToInt(maxLoaded * 0.5f));

            Thing_CEMagazine best = null;
            int bestTier = -1;
            int bestLoaded = -1;
            int bestSlot = int.MaxValue;

            for (int i = 0; i < loaded.Count; i++)
            {
                var m = loaded[i];
                int tier = m.LoadedCount >= prefThreshold
                    ? GetMagazineMatchTier(preferredComposition, ResolveMagazineComposition(pawn, m))
                    : 0;
                int slot = m.preferredPlanIndex >= 0 ? m.preferredPlanIndex : 999;

                bool isBetter = tier > bestTier
                    || (tier == bestTier && m.LoadedCount > bestLoaded)
                    || (tier == bestTier && m.LoadedCount == bestLoaded && slot < bestSlot);
                if (isBetter)
                {
                    best = m;
                    bestTier = tier;
                    bestLoaded = m.LoadedCount;
                    bestSlot = slot;
                }
            }
            return best;
        }

        public static Thing_CEMagazine FindBestLoadedMagazine(Pawn pawn, CompAmmoUser compAmmo, int minLoadedCountExclusive = 0)
        {
            if (pawn == null || compAmmo == null || compAmmo.CurAmmoSet == null) return null;
            var cm = compAmmo.parent?.TryGetComp<CompWeaponMagazine>();
            var pref = (cm != null && cm.hasManualMagPref) ? cm.GetManualPrefComposition() : null;
            return FindBestLoadedMagazine(pawn, compAmmo.CurAmmoSet, compAmmo.MagSize, pref, minLoadedCountExclusive);
        }

        /// <summary>
        /// Analyzes how a single-load weapon's loadedRounds differ from `plan`.
        /// Returns true if restoration work is needed and `nextNeededAmmo` is known.
        /// </summary>
        public static bool AnalyzeSingleLoadRestore(
            List<AmmoDef> loadedRounds,
            AmmoPlan plan,
            out int frontMismatchToEject,
            out int matchingTailLen,
            out AmmoDef nextNeededAmmo)
        {
            frontMismatchToEject = 0;
            matchingTailLen = 0;
            nextNeededAmmo = null;
            if (plan == null) return false;

            plan.Normalize();
            int count = loadedRounds?.Count ?? 0;
            if (plan.MatchesExactly(loadedRounds)) return false;

            if (count == 0 || plan.IsPrefixMatch(loadedRounds))
            {
                frontMismatchToEject = 0;
                matchingTailLen = 0;
                nextNeededAmmo = count < plan.slots.Count ? plan.slots[count] : null;
                return nextNeededAmmo != null;
            }

            if (plan.IsSuffixMatch(loadedRounds))
            {
                frontMismatchToEject = 0;
                matchingTailLen = count;
                int idx = Math.Max(0, plan.capacity - count - 1);
                nextNeededAmmo = plan.slots[idx];
                return nextNeededAmmo != null;
            }

            int rIdx = count - 1;
            int pIdx = plan.capacity - 1;
            while (rIdx >= 0 && pIdx >= 0 && loadedRounds[rIdx] == plan.slots[pIdx])
            {
                matchingTailLen++;
                rIdx--;
                pIdx--;
            }
            frontMismatchToEject = count - matchingTailLen;
            int nextIdx = matchingTailLen > 0 ? Math.Max(0, plan.capacity - matchingTailLen - 1) : 0;
            nextNeededAmmo = plan.slots[nextIdx];
            return nextNeededAmmo != null;
        }

        public static bool HasReachableAmmoOnMap(Pawn pawn, AmmoDef ammoDef, int minCountRequired)
        {
            if (pawn?.Map == null || ammoDef == null || minCountRequired <= 0) return false;
            var compInv = pawn.TryGetComp<CompInventory>();
            if (compInv != null && (!compInv.CanFitInInventory(ammoDef, out int fit) || fit <= 0))
            {
                return false;
            }

            var list = pawn.Map.listerThings?.ThingsOfDef(ammoDef);
            if (list == null || list.Count == 0) return false;

            int sum = 0;
            for (int i = 0; i < list.Count; i++)
            {
                Thing t = list[i];
                if (t == null || t.Destroyed || !t.Spawned || t.stackCount <= 0) continue;
                if (t.IsForbidden(pawn) || t.IsBurning()) continue;
                if (t is AmmoThing at && at.IsCookingOff) continue;
                if (!pawn.CanReserveAndReach(t, PathEndMode.ClosestTouch, Danger.Some, 10, 1)) continue;

                sum += t.stackCount;
                if (sum >= minCountRequired)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Checks whether a single-load weapon can safely progress/restore toward `plan`
        /// without stripping rounds when replacement ammo is unavailable.
        /// </summary>
        public static bool CanRestoreSingleLoadWeapon(Pawn pawn, CompAmmoUser ca, CompWeaponMagazine cm, AmmoPlan plan, bool includeStockpile)
        {
            if (pawn == null || ca == null || cm == null || plan == null) return false;
            cm.SyncFromCompAmmo();

            if (!AnalyzeSingleLoadRestore(cm.loadedRounds, plan, out int frontMismatch, out int matchingTailLen, out AmmoDef nextNeeded))
            {
                return false;
            }

            if (frontMismatch == 0)
            {
                if (CountLooseAmmoInInventory(pawn, nextNeeded) > 0) return true;
                return includeStockpile && HasReachableAmmoOnMap(pawn, nextNeeded, 1);
            }

            int frontNeedEnd = plan.capacity - matchingTailLen;
            if (frontNeedEnd <= 0) return false;

            var frontNeed = new Dictionary<AmmoDef, int>();
            for (int i = 0; i < frontNeedEnd; i++)
            {
                AmmoDef ad = plan.slots[i];
                if (ad == null) continue;
                frontNeed[ad] = frontNeed.TryGetValue(ad, out int cur) ? cur + 1 : 1;
            }
            if (frontNeed.Count == 0) return false;

            var availNow = new Dictionary<AmmoDef, int>();
            foreach (var ad in frontNeed.Keys)
            {
                availNow[ad] = CountLooseAmmoInInventory(pawn, ad);
            }
            for (int i = 0; i < frontMismatch && i < cm.loadedRounds.Count; i++)
            {
                AmmoDef ejected = cm.loadedRounds[i];
                if (ejected != null && availNow.ContainsKey(ejected))
                {
                    availNow[ejected] += 1;
                }
            }

            foreach (var kv in frontNeed)
            {
                int deficit = kv.Value - availNow[kv.Key];
                if (deficit <= 0) continue;
                if (!includeStockpile) return false;
                if (!HasReachableAmmoOnMap(pawn, kv.Key, deficit)) return false;
            }
            return true;
        }

        /// <summary>
        /// Ejects only the front `count` rounds from a single-load weapon back to pawn's inventory (or ground),
        /// preserving the matching rear rounds in the tube/cylinder.
        /// </summary>
        public static void EjectFrontRoundsForSingleLoad(Pawn pawn, CompAmmoUser ca, CompWeaponMagazine cm, int count)
        {
            if (pawn == null || ca == null || cm == null || count <= 0) return;
            cm.SyncFromCompAmmo();

            int toRemove = Math.Min(count, cm.loadedRounds.Count);
            if (toRemove <= 0) return;

            var ejectedCounts = new Dictionary<AmmoDef, int>();
            for (int i = 0; i < toRemove; i++)
            {
                AmmoDef ad = cm.loadedRounds[i];
                if (ad == null) continue;
                ejectedCounts[ad] = ejectedCounts.TryGetValue(ad, out int cur) ? cur + 1 : 1;
            }
            cm.loadedRounds.RemoveRange(0, toRemove);
            cm.PushToCompAmmo();

            foreach (var kv in ejectedCounts)
            {
                AddOrDropLooseAmmo(pawn, kv.Key, kv.Value);
            }
            pawn.TryGetComp<CompInventory>()?.UpdateInventory();
        }

        public static bool HasLoadedMagazineInInventory(Pawn pawn, AmmoSetDef ammoSet, int capacity)
        {
            if (pawn?.inventory?.innerContainer == null || ammoSet == null) return false;
            var container = pawn.inventory.innerContainer;
            for (int i = 0; i < container.Count; i++)
            {
                if (container[i] is Thing_CEMagazine mag && mag.Matches(ammoSet, capacity) && !mag.IsEmpty)
                {
                    return true;
                }
            }
            return false;
        }

        public static int TotalRoundsInInventoryMagazines(Pawn pawn, AmmoSetDef ammoSet, int capacity)
        {
            if (pawn?.inventory?.innerContainer == null || ammoSet == null) return 0;
            int total = 0;
            var container = pawn.inventory.innerContainer;
            for (int i = 0; i < container.Count; i++)
            {
                if (container[i] is Thing_CEMagazine mag && mag.Matches(ammoSet, capacity))
                {
                    total += mag.LoadedCount;
                }
            }
            return total;
        }

        public static bool HasCompatibleLooseAmmoInInventory(Pawn pawn, AmmoSetDef ammoSet)
        {
            if (pawn?.inventory?.innerContainer == null || ammoSet?.ammoTypes == null) return false;
            var container = pawn.inventory.innerContainer;
            for (int i = 0; i < container.Count; i++)
            {
                Thing t = container[i];
                if (t.def is AmmoDef ad && t.stackCount > 0)
                {
                    for (int j = 0; j < ammoSet.ammoTypes.Count; j++)
                    {
                        if (ammoSet.ammoTypes[j].ammo == ad) return true;
                    }
                }
            }
            return false;
        }

        public static int CountLooseAmmoInInventory(Pawn pawn, AmmoDef ammoDef)
        {
            if (pawn?.inventory?.innerContainer == null || ammoDef == null) return 0;
            int total = 0;
            var container = pawn.inventory.innerContainer;
            for (int i = 0; i < container.Count; i++)
            {
                if (container[i].def == ammoDef)
                {
                    total += container[i].stackCount;
                }
            }
            return total;
        }

        public static int CountLooseAmmoInColony(Map map, AmmoDef ammoDef)
        {
            if (map == null || ammoDef == null) return 0;
            return map.resourceCounter?.GetCount(ammoDef) ?? map.listerThings.ThingsOfDef(ammoDef).Sum(t => t.stackCount);
        }

        /// <summary>
        /// Consumes up to `count` loose bullets of `ammoDef` from pawn's inventory. Returns actual consumed count.
        /// </summary>
        public static int ConsumeLooseAmmoFromInventory(Pawn pawn, AmmoDef ammoDef, int count)
        {
            if (pawn?.inventory?.innerContainer == null || ammoDef == null || count <= 0) return 0;
            int remaining = count;
            var container = pawn.inventory.innerContainer;
            for (int i = container.Count - 1; i >= 0 && remaining > 0; i--)
            {
                Thing t = container[i];
                if (t.def != ammoDef) continue;
                int take = Math.Min(remaining, t.stackCount);
                if (take >= t.stackCount)
                {
                    container.Remove(t);
                    t.Destroy(DestroyMode.Vanish);
                }
                else
                {
                    t.stackCount -= take;
                }
                remaining -= take;
            }
            pawn.TryGetComp<CompInventory>()?.UpdateInventory();
            return count - remaining;
        }

        /// <summary>
        /// Adds loose ammo of `ammoDef` x `count` into pawn's inventory (or drops at feet if container rejects).
        /// </summary>
        public static void AddOrDropLooseAmmo(Pawn pawn, AmmoDef ammoDef, int count)
        {
            if (pawn == null || ammoDef == null || count <= 0) return;
            int remaining = count;
            while (remaining > 0)
            {
                int stack = Math.Min(remaining, ammoDef.stackLimit);
                Thing ammo = ThingMaker.MakeThing(ammoDef);
                ammo.stackCount = stack;
                bool added = pawn.inventory?.innerContainer != null && pawn.inventory.innerContainer.TryAdd(ammo, true);
                if (!added && pawn.MapHeld != null)
                {
                    GenPlace.TryPlaceThing(ammo, pawn.PositionHeld, pawn.MapHeld, ThingPlaceMode.Near);
                }
                remaining -= stack;
            }
            pawn.TryGetComp<CompInventory>()?.UpdateInventory();
        }

        /// <summary>
        /// Packs an NPC/raider's generated loose ammo into physical magazines for their mag-fed weapons.
        /// </summary>
        public static void PackNpcInventoryIntoMagazines(Pawn pawn)
        {
            if (pawn == null || !ShouldUseMagazineSystem(pawn)) return;
            if (pawn.Faction == Faction.OfPlayer) return;

            var weapons = GetManagedWeapons(pawn);
            foreach (var w in weapons)
            {
                var compAmmo = w.TryGetComp<CompAmmoUser>();
                if (!IsMagazineFedWeapon(compAmmo)) continue;

                var compMag = w.TryGetComp<CompWeaponMagazine>();
                if (compMag != null)
                {
                    compMag.SyncFromCompAmmo();
                    compMag.hasInsertedMagazine = true;
                    compMag.insertedMagAmmoSet = compAmmo.CurAmmoSet;
                    compMag.insertedMagCapacity = compAmmo.MagSize;
                }

                AmmoSetDef set = compAmmo.CurAmmoSet;
                int cap = compAmmo.MagSize;
                if (set == null || cap <= 1) continue;

                // Gather all compatible loose ammo in inventory
                var looseRounds = new List<AmmoDef>();
                var container = pawn.inventory.innerContainer;
                for (int i = container.Count - 1; i >= 0; i--)
                {
                    Thing t = container[i];
                    if (t.def is AmmoDef ad && set.ammoTypes.Any(al => al.ammo == ad))
                    {
                        for (int k = 0; k < t.stackCount; k++)
                        {
                            looseRounds.Add(ad);
                        }
                        container.Remove(t);
                        t.Destroy(DestroyMode.Vanish);
                    }
                }

                // Pack into full magazines (plus a final partial mag if any left), continuing from any already-packed slots
                int idx = 0;
                int magSlot = GetMagazinesInInventory(pawn, set, cap).Count + (compMag != null && compMag.hasInsertedMagazine ? 1 : 0);
                while (idx < looseRounds.Count)
                {
                    int take = Math.Min(cap, looseRounds.Count - idx);
                    var mag = Thing_CEMagazine.Create(set, cap, magSlot++);
                    for (int k = 0; k < take; k++)
                    {
                        mag.rounds.Add(looseRounds[idx + k]);
                    }
                    idx += take;
                    if (!container.TryAdd(mag, true) && pawn.MapHeld != null)
                    {
                        GenPlace.TryPlaceThing(mag, pawn.PositionHeld, pawn.MapHeld, ThingPlaceMode.Near);
                    }
                }
            }
            pawn.TryGetComp<CompInventory>()?.UpdateInventory();
        }

        /// <summary>
        /// Optional realism/flavor feature (disabled by default in Mod Settings):
        /// Gives a small chance per packed round to mildly bruise or scratch the pawn's biological right thumb.
        /// Never destroys/severs the thumb and ignores mechanoids or bionic/prosthetic hands.
        /// </summary>
        public static void TryApplyPackingThumbInjury(Pawn pawn, bool isFieldPacking)
        {
            if (!(CE_MagazineFedMod.Settings?.enablePackingThumbInjury ?? false)) return;
            if (pawn == null || pawn.Dead || pawn.health?.hediffSet == null) return;
            if (pawn.RaceProps == null || !pawn.RaceProps.IsFlesh || pawn.RaceProps.IsMechanoid) return;

            // 0.1% per round during idle packing, 0.2% per round under hurried combat field-packing
            float chance = isFieldPacking ? 0.002f : 0.001f;
            if (!Rand.Chance(chance)) return;

            BodyPartRecord rightThumb = FindBiologicalRightThumb(pawn);
            if (rightThumb == null) return;

            // Never sever/destroy the thumb from loading magazines (only inflict minor 1-dmg injury when thumb HP > 2)
            float curHp = pawn.health.hediffSet.GetPartHealth(rightThumb);
            if (curHp <= 2f) return;

            DamageDef dmgDef = Rand.Chance(0.55f) ? DamageDefOf.Scratch : DamageDefOf.Blunt;
            DamageInfo dinfo = new DamageInfo(dmgDef, 1f, 0f, -1f, null, rightThumb, CEMF_DefOf.CEMF_Magazine);
            dinfo.SetIgnoreArmor(true);
            pawn.TakeDamage(dinfo);

            if (pawn.Map != null)
            {
                MoteMaker.ThrowText(pawn.Position.ToVector3Shifted(), pawn.Map, "CEMF_Mote_ThumbInjured".Translate(), Color.red);
            }
            if (pawn.IsColonistPlayerControlled)
            {
                Messages.Message("CEMF_Msg_ThumbInjured".Translate(pawn.LabelShortCap), pawn, MessageTypeDefOf.NegativeHealthEvent, false);
            }
        }

        private static BodyPartRecord FindBiologicalRightThumb(Pawn pawn)
        {
            var hediffSet = pawn?.health?.hediffSet;
            var allParts = pawn?.RaceProps?.body?.AllParts;
            if (hediffSet == null || allParts == null) return null;

            for (int i = 0; i < allParts.Count; i++)
            {
                var part = allParts[i];
                if (part == null || part.def == null) continue;
                if (hediffSet.PartIsMissing(part)) continue;
                if (hediffSet.PartOrAnyAncestorHasDirectlyAddedParts(part)) continue;

                string untrans = part.untranslatedCustomLabel ?? "";
                if (untrans.IndexOf("right thumb", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return part;
                }

                // Fallback for custom races whose right thumb uses Finger + RightHand + thumb label
                if (part.def.defName == "Finger"
                    && part.groups != null
                    && part.groups.Any(g => g != null && g.defName == "RightHand")
                    && (untrans.IndexOf("thumb", StringComparison.OrdinalIgnoreCase) >= 0
                        || (part.customLabel != null && part.customLabel.IndexOf("thumb", StringComparison.OrdinalIgnoreCase) >= 0)))
                {
                    return part;
                }
            }
            return null;
        }

        public static string GetShortAmmoLabel(AmmoDef def)
        {
            if (def == null) return "?";
            if (def.ammoClass != null)
            {
                if (!string.IsNullOrEmpty(def.ammoClass.labelShort))
                {
                    return def.ammoClass.labelShort;
                }
                if (!string.IsNullOrEmpty(def.ammoClass.label))
                {
                    string lbl = def.ammoClass.label;
                    return lbl.Length <= 8 ? lbl : lbl.Substring(0, 8);
                }
            }
            string n = def.label ?? def.defName;
            return n.Length <= 8 ? n : n.Substring(0, 8);
        }

        private static bool HasToken(string upper, string token)
        {
            if (string.IsNullOrEmpty(upper) || string.IsNullOrEmpty(token)) return false;
            int idx = 0;
            while ((idx = upper.IndexOf(token, idx, StringComparison.Ordinal)) >= 0)
            {
                bool leftBoundary = idx == 0 || !char.IsLetter(upper[idx - 1]);
                int end = idx + token.Length;
                bool rightBoundary = end == upper.Length || !char.IsLetter(upper[end]);
                if (leftBoundary && rightBoundary)
                {
                    return true;
                }
                idx += token.Length;
            }
            return false;
        }

        /// <summary>
        /// Draws a color-coded round sequence strip, merging consecutive identical-color slots into single rectangles
        /// for high performance on large drums (100-200 rounds) and zero right-edge overflow.
        /// </summary>
        public static void DrawMergedColorStrip(Rect rect, List<AmmoDef> rounds, int capacity, Color bgColor)
        {
            Widgets.DrawBoxSolid(rect, bgColor);
            if (rounds == null || rounds.Count == 0 || capacity <= 0 || rect.width <= 0f) return;

            int count = Math.Min(rounds.Count, capacity);
            float slotW = rect.width / capacity;

            int segStart = 0;
            Color curCol = GetAmmoColor(rounds[0]);

            for (int i = 1; i <= count; i++)
            {
                Color nextCol = i < count ? GetAmmoColor(rounds[i]) : default;
                if (i == count || nextCol != curCol)
                {
                    float x0 = rect.x + segStart * slotW;
                    float x1 = Mathf.Min(rect.xMax, rect.x + i * slotW);
                    float w = Mathf.Min(rect.xMax - x0, Mathf.Max(1f, x1 - x0));
                    if (w > 0f)
                    {
                        Widgets.DrawBoxSolid(new Rect(x0, rect.y, w, rect.height), curCol);
                    }
                    segStart = i;
                    curCol = nextCol;
                }
            }
        }

        public static Color GetAmmoColor(AmmoDef def)
        {
            if (def == null) return new Color(0.35f, 0.35f, 0.38f);
            string key = def.ammoClass?.defName ?? def.defName;
            if (CategoryColorCache.TryGetValue(key, out Color cached)) return cached;

            string u = key.ToUpperInvariant();
            Color col;
            if (u.Contains("FULLMETALJACKET") || HasToken(u, "FMJ") || HasToken(u, "BALL"))
                col = new Color(0.84f, 0.67f, 0.27f); // Brass Gold
            else if (u.Contains("ARMORPIERCING") || HasToken(u, "AP") || HasToken(u, "API") || u.Contains("AP_I") || u.Contains("INCENDIARYAP"))
                col = new Color(0.28f, 0.64f, 0.92f); // Steel Blue
            else if (u.Contains("SABOT"))
                col = new Color(0.68f, 0.42f, 0.95f); // Violet
            else if (u.Contains("HOLLOWPOINT") || HasToken(u, "HP") || HasToken(u, "JHP") || u.Contains("SOFTPOINT") || HasToken(u, "SP"))
                col = new Color(0.90f, 0.44f, 0.24f); // Copper Orange
            else if (u.Contains("INCENDIARY") || u.Contains("THERMITE") || u.Contains("PROMETHEUM") || u.Contains("DRAGON"))
                col = new Color(0.95f, 0.26f, 0.20f); // Flame Red
            else if (u.Contains("HIGHEXPLOSIVE") || HasToken(u, "HE") || u.Contains("FRAG") || u.Contains("AIRBURST"))
                col = new Color(0.95f, 0.56f, 0.14f); // Explosive Amber
            else if (HasToken(u, "EMP") || HasToken(u, "ION"))
                col = new Color(0.18f, 0.85f, 0.92f); // Electric Cyan
            else if (u.Contains("TRACER"))
                col = new Color(0.36f, 0.90f, 0.38f); // Tracer Green
            else if (u.Contains("BUCK") || u.Contains("BIRDSHOT") || u.Contains("FLECHETTE"))
                col = new Color(0.82f, 0.24f, 0.28f); // Shotgun Red
            else if (u.Contains("SLUG"))
                col = new Color(0.30f, 0.52f, 0.86f); // Slug Blue
            else if (u.Contains("BEANBAG") || u.Contains("RUBBER") || u.Contains("TRANQ"))
                col = new Color(0.55f, 0.80f, 0.58f); // Non-lethal Sage
            else if (u.Contains("SMOKE"))
                col = new Color(0.68f, 0.72f, 0.76f); // Smoke Grey
            else if (u.Contains("CHARGED") || u.Contains("PLASMA") || u.Contains("LASER"))
                col = new Color(0.32f, 0.78f, 0.96f); // Plasma Blue
            else
            {
                // Deterministic vibrant HSL color from hash
                uint hash = (uint)GenText.StableStringHash(key);
                float hue = (hash % 360u) / 360f;
                col = Color.HSVToRGB(hue, 0.62f, 0.85f);
            }

            // Slightly differentiate AP-I / AP-HE from plain AP
            if (HasToken(u, "API") || u.Contains("AP_I") || (u.Contains("INCENDIARY") && u.Contains("ARMOR")))
                col = new Color(0.85f, 0.28f, 0.52f); // AP-I Magenta-Red
            else if (HasToken(u, "APHE") || u.Contains("AP_HE") || (u.Contains("ARMOR") && u.Contains("EXPLOSIVE")))
                col = new Color(0.76f, 0.36f, 0.78f); // AP-HE Purple-Pink

            CategoryColorCache[key] = col;
            return col;
        }
    }
}
