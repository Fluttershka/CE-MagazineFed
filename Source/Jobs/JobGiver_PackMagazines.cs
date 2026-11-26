using System.Collections.Generic;
using CombatExtended;
using RimWorld;
using Verse;
using Verse.AI;

namespace CE_MagazineFed
{
    /// <summary>
    /// Automatically dispatches pawns during idle time (or before normal work, per settings)
    /// to organize and pack their magazines according to their configured AmmoPlans.
    /// </summary>
    public class JobGiver_PackMagazines : ThinkNode_JobGiver
    {
        private static readonly Dictionary<int, int> LastFailTick = new Dictionary<int, int>();
        private const int CooldownTicksAfterNoAmmo = 240;

        public static void ClearCooldownCache()
        {
            LastFailTick.Clear();
        }

        public static bool IsOnCooldown(Pawn pawn)
        {
            if (pawn == null || Find.TickManager == null) return false;
            int now = Find.TickManager.TicksGame;
            return LastFailTick.TryGetValue(pawn.thingIDNumber, out int lastFail)
                && now >= lastFail
                && now - lastFail < CooldownTicksAfterNoAmmo;
        }

        public static void NotifyNoProgress(Pawn pawn)
        {
            if (pawn != null && Find.TickManager != null)
            {
                LastFailTick[pawn.thingIDNumber] = Find.TickManager.TicksGame;
            }
        }

        public override float GetPriority(Pawn pawn)
        {
            var settings = CE_MagazineFedMod.Settings;
            if (settings == null || settings.packingPriority == PackingPriorityMode.ManualOnly)
            {
                return 0f;
            }
            if (!CanAttemptPacking(pawn)) return 0f;

            TimeAssignmentDef ta = pawn.timetable != null ? pawn.timetable.CurrentAssignment : TimeAssignmentDefOf.Anything;
            if (ta == TimeAssignmentDefOf.Sleep)
            {
                return 0f;
            }

            int now = Find.TickManager.TicksGame;
            if (LastFailTick.TryGetValue(pawn.thingIDNumber, out int lastFail) && now >= lastFail && now - lastFail < CooldownTicksAfterNoAmmo)
            {
                return 0f;
            }

            if (!HasActionablePackingWork(pawn))
            {
                LastFailTick[pawn.thingIDNumber] = now;
                return 0f;
            }

            if (settings.packingPriority == PackingPriorityMode.BeforeWork)
            {
                // Higher than normal Anything work (5.5f), lower than critical needs / combat reload (9.1f)
                return 6.0f;
            }

            // Default: IdleOnly — lower than JobGiver_Work (5.5f/9f) and Joy, so pawns pack when idle!
            return 1.5f;
        }

        protected override Job TryGiveJob(Pawn pawn)
        {
            var settings = CE_MagazineFedMod.Settings;
            if (settings == null || settings.packingPriority == PackingPriorityMode.ManualOnly) return null;
            if (!CanAttemptPacking(pawn)) return null;
            int now = Find.TickManager.TicksGame;
            if (LastFailTick.TryGetValue(pawn.thingIDNumber, out int lastFail) && now >= lastFail && now - lastFail < CooldownTicksAfterNoAmmo)
            {
                return null;
            }
            if (!HasActionablePackingWork(pawn))
            {
                LastFailTick[pawn.thingIDNumber] = now;
                return null;
            }
            return JobMaker.MakeJob(CEMF_DefOf.CEMF_PackMagazines, pawn);
        }

        public static bool CanAttemptPacking(Pawn pawn)
        {
            return CanAttemptPacking(pawn, allowDrafted: false);
        }

        public static bool CanAttemptPacking(Pawn pawn, bool allowDrafted)
        {
            if (pawn == null || pawn.Dead || pawn.Downed || !pawn.Spawned || pawn.Map == null) return false;
            if (!allowDrafted && pawn.Drafted) return false;
            if (pawn.InMentalState) return false;
            if (!pawn.RaceProps.IsMechanoid && (pawn.health?.capacities == null || !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation))) return false;
            if (!MagazineUtility.ShouldUseMagazineSystem(pawn)) return false;
            if (pawn.CurJobDef == CEMF_DefOf.CEMF_PackMagazines) return false;
            return true;
        }

        /// <summary>
        /// True if `pawn` can pack or reorder at least one magazine using ONLY loose ammo & magazines
        /// already carried in inventory (no stockpile fetching or map magazine recycling).
        /// Used for drafted move-while-packing.
        /// </summary>
        public static bool HasInventoryOnlyPackingWork(Pawn pawn)
        {
            if (pawn == null || pawn.Dead || pawn.inventory?.innerContainer == null) return false;
            var mgr = GameComponent_MagazineManager.Instance;
            if (mgr == null) return false;

            var weapons = MagazineUtility.GetManagedWeapons(pawn);
            var checkedConfigs = new HashSet<PawnWeaponAmmoConfig>();

            for (int i = 0; i < weapons.Count; i++)
            {
                var ca = weapons[i].TryGetComp<CompAmmoUser>();
                if (!MagazineUtility.IsMagazineFedWeapon(ca)) continue;
                var cfg = mgr.GetOrCreateConfig(pawn, ca);
                if (cfg == null || cfg.isSingleLoad) continue;

                if (checkedConfigs.Add(cfg))
                {
                    var invMags = MagazineUtility.GetMagazinesInInventory(pawn, cfg.ammoSet, cfg.capacity);
                    for (int m = 0; m < invMags.Count; m++)
                    {
                        var mag = invMags[m];
                        int slot = mag.preferredPlanIndex >= 0 && mag.preferredPlanIndex < cfg.targetTotalMags ? mag.preferredPlanIndex : 0;
                        var plan = cfg.GetPlan(slot);
                        if (!plan.MatchesExactly(mag.rounds) && CanProgressMagFromInventoryOnly(pawn, plan, mag.rounds))
                        {
                            return true;
                        }
                    }

                    // Check if a missing magazine can be provisioned and packed immediately from inventory loose ammo
                    int inGun = 0;
                    var usedSlots = new HashSet<int>();
                    for (int w = 0; w < weapons.Count; w++)
                    {
                        var wCa = weapons[w].TryGetComp<CompAmmoUser>();
                        if (!MagazineUtility.IsMagazineFedWeapon(wCa) || wCa.CurAmmoSet != cfg.ammoSet || wCa.MagSize != cfg.capacity) continue;
                        var wCm = weapons[w].TryGetComp<CompWeaponMagazine>();
                        if (wCm != null && wCm.hasInsertedMagazine)
                        {
                            inGun++;
                            if (wCm.insertedMagPlanIndex >= 0 && wCm.insertedMagPlanIndex < cfg.targetTotalMags) usedSlots.Add(wCm.insertedMagPlanIndex);
                        }
                    }
                    for (int m = 0; m < invMags.Count; m++)
                    {
                        if (invMags[m].preferredPlanIndex >= 0 && invMags[m].preferredPlanIndex < cfg.targetTotalMags) usedSlots.Add(invMags[m].preferredPlanIndex);
                    }
                    if ((invMags.Count + inGun) < cfg.targetTotalMags
                        && (CE_MagazineFedMod.Settings?.autoProvisionEmptyMags ?? true)
                        && MagazineUtility.CanFitNewMagazine(pawn, cfg.ammoSet, cfg.capacity, includeCompatibleLooseAmmoReclaim: true))
                    {
                        int freeSlot = 0;
                        for (int s = 0; s < cfg.targetTotalMags; s++)
                        {
                            if (!usedSlots.Contains(s))
                            {
                                freeSlot = s;
                                break;
                            }
                        }
                        if (CanProgressMagFromInventoryOnly(pawn, cfg.GetPlan(freeSlot), null))
                        {
                            return true;
                        }
                    }
                }

                // Check this weapon's inserted magazine
                var cm = weapons[i].TryGetComp<CompWeaponMagazine>();
                if (cm != null && cm.hasInsertedMagazine)
                {
                    cm.SyncFromCompAmmo();
                    int slot = cm.insertedMagPlanIndex >= 0 && cm.insertedMagPlanIndex < cfg.targetTotalMags ? cm.insertedMagPlanIndex : 0;
                    var plan = cfg.GetPlan(slot);
                    if (!plan.MatchesExactly(cm.loadedRounds) && CanProgressMagFromInventoryOnly(pawn, plan, cm.loadedRounds))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool CanProgressMagFromInventoryOnly(Pawn pawn, AmmoPlan plan, List<AmmoDef> curRounds)
        {
            plan.Normalize();
            var needed = plan.GetCountsByDef();
            var cur = new Dictionary<AmmoDef, int>();
            if (curRounds != null)
            {
                for (int i = 0; i < curRounds.Count; i++)
                {
                    var d = curRounds[i];
                    if (d == null) continue;
                    if (cur.TryGetValue(d, out int c)) cur[d] = c + 1;
                    else cur[d] = 1;
                }
            }

            bool sameSet = true;
            foreach (var kv in needed)
            {
                if (!cur.TryGetValue(kv.Key, out int c) || c != kv.Value)
                {
                    sameSet = false;
                    break;
                }
            }
            if (sameSet && curRounds != null && curRounds.Count == plan.capacity)
            {
                return true;
            }

            foreach (var kv in needed)
            {
                cur.TryGetValue(kv.Key, out int have);
                if (kv.Value > have && MagazineUtility.CountLooseAmmoInInventory(pawn, kv.Key) > 0)
                {
                    return true;
                }
            }
            return false;
        }

        public static bool HasActionablePackingWork(Pawn pawn)
        {
            var mgr = GameComponent_MagazineManager.Instance;
            if (mgr == null) return false;

            if (!mgr.PawnNeedsPacking(pawn, out var missingFromInventory))
            {
                return false;
            }

            // If we have work that doesn't require extra colony ammo (e.g. loose ammo already in inventory,
            // or mags just need reordering, or empty mags need provisioning), it's immediately actionable!
            var weapons = MagazineUtility.GetManagedWeapons(pawn);
            for (int i = 0; i < weapons.Count; i++)
            {
                var ca = weapons[i].TryGetComp<CompAmmoUser>();
                if (ca == null) continue;
                var cfg = mgr.GetOrCreateConfig(pawn, ca);
                if (cfg == null) continue;

                if (!cfg.isSingleLoad)
                {
                    var invMags = MagazineUtility.GetMagazinesInInventory(pawn, cfg.ammoSet, cfg.capacity);
                    var cm = weapons[i].TryGetComp<CompWeaponMagazine>();
                    int inGun = (cm != null && cm.hasInsertedMagazine) ? 1 : 0;
                    if ((invMags.Count + inGun) < cfg.targetTotalMags)
                    {
                        bool autoProvision = CE_MagazineFedMod.Settings?.autoProvisionEmptyMags ?? true;
                        bool canFitDirectly = autoProvision && MagazineUtility.CanFitNewMagazine(pawn, cfg.ammoSet, cfg.capacity, includeCompatibleLooseAmmoReclaim: false);
                        bool canRecycle = (CE_MagazineFedMod.Settings?.preferRecycleMapMagazines ?? true)
                                          && GameComponent_MagazineManager.FindBestCompatibleMapMagazine(pawn, cfg.ammoSet, cfg.capacity) != null;
                        if (canFitDirectly || canRecycle)
                        {
                            return true;
                        }

                        if (autoProvision && MagazineUtility.CanFitNewMagazine(pawn, cfg.ammoSet, cfg.capacity, includeCompatibleLooseAmmoReclaim: true))
                        {
                            // Deferred tight magazine: actionable only if we have or can fetch ammo for its slot
                            var usedSlots = new HashSet<int>();
                            if (cm != null && cm.hasInsertedMagazine && cm.insertedMagPlanIndex >= 0) usedSlots.Add(cm.insertedMagPlanIndex);
                            for (int m = 0; m < invMags.Count; m++)
                            {
                                if (invMags[m].preferredPlanIndex >= 0) usedSlots.Add(invMags[m].preferredPlanIndex);
                            }
                            int freeSlot = 0;
                            for (int s = 0; s < cfg.targetTotalMags; s++)
                            {
                                if (!usedSlots.Contains(s))
                                {
                                    freeSlot = s;
                                    break;
                                }
                            }
                            if (CanProgressMag(pawn, cfg.GetPlan(freeSlot), null))
                            {
                                return true;
                            }
                        }
                    }

                    // Check if any mag can be packed/reordered using loose ammo already in inventory
                    for (int m = 0; m < invMags.Count; m++)
                    {
                        var mag = invMags[m];
                        int slot = mag.preferredPlanIndex >= 0 && mag.preferredPlanIndex < cfg.targetTotalMags ? mag.preferredPlanIndex : 0;
                        var plan = cfg.GetPlan(slot);
                        if (!plan.MatchesExactly(mag.rounds))
                        {
                            if (CanProgressMag(pawn, plan, mag.rounds)) return true;
                        }
                    }

                    // Check each equipped/carried weapon sharing this config (supports dual/sidearm same-caliber guns)
                    for (int w = 0; w < weapons.Count; w++)
                    {
                        var wCa = weapons[w].TryGetComp<CompAmmoUser>();
                        if (!MagazineUtility.IsMagazineFedWeapon(wCa) || wCa.CurAmmoSet != cfg.ammoSet || wCa.MagSize != cfg.capacity) continue;

                        var wCm = weapons[w].TryGetComp<CompWeaponMagazine>();
                        if (wCm != null && wCm.hasInsertedMagazine)
                        {
                            wCm.SyncFromCompAmmo();
                            int slot = wCm.insertedMagPlanIndex >= 0 && wCm.insertedMagPlanIndex < cfg.targetTotalMags ? wCm.insertedMagPlanIndex : 0;
                            var plan = cfg.GetPlan(slot);
                            if (!plan.MatchesExactly(wCm.loadedRounds) && CanProgressMag(pawn, plan, wCm.loadedRounds))
                            {
                                return true;
                            }
                        }
                    }
                }
                else
                {
                    // Single-load multi-capacity: only actionable via PackMagazines if we need to fetch missing planned ammo from stockpile
                    if (missingFromInventory.Count > 0 && (CE_MagazineFedMod.Settings?.fetchAmmoFromStockpile ?? true))
                    {
                        if (AnyAmmoReachableOnMap(pawn, missingFromInventory)) return true;
                    }
                }
            }

            return false;
        }

        private static bool CanProgressMag(Pawn pawn, AmmoPlan plan, List<AmmoDef> curRounds)
        {
            plan.Normalize();
            var needed = plan.GetCountsByDef();
            var cur = new Dictionary<AmmoDef, int>();
            if (curRounds != null)
            {
                for (int i = 0; i < curRounds.Count; i++)
                {
                    var d = curRounds[i];
                    if (d == null) continue;
                    if (cur.TryGetValue(d, out int c)) cur[d] = c + 1;
                    else cur[d] = 1;
                }
            }

            // Reordering existing rounds requires zero new ammo
            bool sameSet = true;
            foreach (var kv in needed)
            {
                if (!cur.TryGetValue(kv.Key, out int c) || c != kv.Value)
                {
                    sameSet = false;
                    break;
                }
            }
            if (sameSet && curRounds != null && curRounds.Count == plan.capacity)
            {
                return true;
            }

            var compInv = pawn.TryGetComp<CompInventory>();
            foreach (var kv in needed)
            {
                cur.TryGetValue(kv.Key, out int have);
                if (kv.Value > have)
                {
                    if (MagazineUtility.CountLooseAmmoInInventory(pawn, kv.Key) > 0) return true;
                    if ((CE_MagazineFedMod.Settings?.fetchAmmoFromStockpile ?? true) && pawn.Map != null)
                    {
                        if (compInv != null && (!compInv.CanFitInInventory(kv.Key, out int fit) || fit <= 0))
                        {
                            continue;
                        }
                        Thing t = GenClosest.ClosestThingReachable(
                            pawn.Position,
                            pawn.Map,
                            ThingRequest.ForDef(kv.Key),
                            PathEndMode.ClosestTouch,
                            TraverseParms.For(pawn, Danger.Some),
                            9999f,
                            x => !x.IsForbidden(pawn)
                                 && !x.IsBurning()
                                 && !(x is AmmoThing at && at.IsCookingOff)
                                 && pawn.CanReserve(x, 10, 1));
                        if (t != null) return true;
                    }
                }
            }
            return false;
        }

        private static bool AnyAmmoReachableOnMap(Pawn pawn, Dictionary<AmmoDef, int> missing)
        {
            if (pawn.Map == null) return false;
            var compInv = pawn.TryGetComp<CompInventory>();
            foreach (var kv in missing)
            {
                if (kv.Value <= 0 || kv.Key == null) continue;
                if (compInv != null && (!compInv.CanFitInInventory(kv.Key, out int fit) || fit <= 0)) continue;
                Thing t = GenClosest.ClosestThingReachable(
                    pawn.Position,
                    pawn.Map,
                    ThingRequest.ForDef(kv.Key),
                    PathEndMode.ClosestTouch,
                    TraverseParms.For(pawn, Danger.Some),
                    9999f,
                    x => !x.IsForbidden(pawn)
                         && !x.IsBurning()
                         && !(x is AmmoThing at && at.IsCookingOff)
                         && pawn.CanReserve(x, 10, 1));
                if (t != null) return true;
            }
            return false;
        }
    }
}
