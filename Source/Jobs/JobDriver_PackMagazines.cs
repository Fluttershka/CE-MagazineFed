using System;
using System.Collections.Generic;
using System.Linq;
using CombatExtended;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace CE_MagazineFed
{
    /// <summary>
    /// Idle / ordered job that ensures a pawn's magazines match their AmmoPlans:
    /// 1. Provisions/cleans empty magazines to match targetTotalMags
    /// 2. Unloads mismatched rounds back to inventory so they can be reused
    /// 3. Fetches missing loose ammo from colony stockpiles (if enabled & inventory is short)
    /// 4. Packs each magazine round-by-round into exact AmmoPlan firing order
    /// 5. Ensures the pawn's weapon has a loaded magazine seated at the end
    /// </summary>
    public class JobDriver_PackMagazines : JobDriver
    {
        private Thing currentPickupThing;
        private int currentPickupCount;
        private int pickupTripsDone;
        private const int MaxPickupTrips = 20;

        private Thing_CEMagazine currentPickupMag;
        private int magPickupTripsDone;
        private const int MaxMagPickupTrips = 8;

        private Thing_CEMagazine currentPackingMag;
        private AmmoPlan currentPackingPlan;
        private int totalRoundsToPackEstimate = 1;
        private int roundsPackedCount;
        private int roundsPackedThisWave;
        private int pendingBatchCount = 1;
        private bool madeAnyProgress;
        private bool isInPackingPhase;

        public bool IsInPackingPhase => isInPackingPhase;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref currentPickupThing, "currentPickupThing");
            Scribe_Values.Look(ref currentPickupCount, "currentPickupCount", 0);
            Scribe_Values.Look(ref pickupTripsDone, "pickupTripsDone", 0);
            Scribe_References.Look(ref currentPickupMag, "currentPickupMag");
            Scribe_Values.Look(ref magPickupTripsDone, "magPickupTripsDone", 0);
            Scribe_References.Look(ref currentPackingMag, "currentPackingMag");
            Scribe_Values.Look(ref totalRoundsToPackEstimate, "totalRoundsToPackEstimate", 1);
            Scribe_Values.Look(ref roundsPackedCount, "roundsPackedCount", 0);
            Scribe_Values.Look(ref roundsPackedThisWave, "roundsPackedThisWave", 0);
            Scribe_Values.Look(ref pendingBatchCount, "pendingBatchCount", 1);
            Scribe_Values.Look(ref madeAnyProgress, "madeAnyProgress", false);
            Scribe_Values.Look(ref isInPackingPhase, "isInPackingPhase", false);
        }

        public override string GetReport()
        {
            if (currentPickupMag != null && !currentPickupMag.Destroyed)
            {
                return $"{"CEMF_Mote_PackingMag".Translate()} ({currentPickupMag.LabelShort})";
            }
            if (currentPickupThing != null && !currentPickupThing.Destroyed)
            {
                return $"{"CEMF_Mote_PackingMag".Translate()} ({currentPickupThing.LabelShort})";
            }
            return "CEMF_Mote_PackingMag".Translate();
        }

        private bool FindNextMapMagazineToRecycle(out Thing_CEMagazine mapMag)
        {
            mapMag = null;
            if (pawn == null || pawn.Drafted) return false;
            if (!(CE_MagazineFedMod.Settings?.preferRecycleMapMagazines ?? true)) return false;
            if (magPickupTripsDone >= MaxMagPickupTrips) return false;
            if (pawn.Map == null) return false;

            var mgr = GameComponent_MagazineManager.Instance;
            if (mgr == null) return false;

            var weapons = MagazineUtility.GetManagedWeapons(pawn);
            var checkedConfigs = new HashSet<PawnWeaponAmmoConfig>();

            for (int w = 0; w < weapons.Count; w++)
            {
                var ca = weapons[w].TryGetComp<CompAmmoUser>();
                if (!MagazineUtility.IsMagazineFedWeapon(ca)) continue;

                var cfg = mgr.GetOrCreateConfig(pawn, ca);
                if (cfg == null || !checkedConfigs.Add(cfg)) continue;

                var invMags = MagazineUtility.GetMagazinesInInventory(pawn, cfg.ammoSet, cfg.capacity);
                var cm = weapons[w].TryGetComp<CompWeaponMagazine>();
                int inGun = (cm != null && cm.hasInsertedMagazine) ? 1 : 0;
                if ((invMags.Count + inGun) >= cfg.targetTotalMags) continue;

                var found = GameComponent_MagazineManager.FindBestCompatibleMapMagazine(pawn, cfg.ammoSet, cfg.capacity);
                if (found != null)
                {
                    mapMag = found;
                    return true;
                }
            }
            return false;
        }

        private bool FindNextStockpilePickup(out Thing ammoStack, out int takeCount)
        {
            ammoStack = null;
            takeCount = 0;
            if (pawn == null || pawn.Drafted) return false;
            if (!(CE_MagazineFedMod.Settings?.fetchAmmoFromStockpile ?? true)) return false;
            if (pickupTripsDone >= MaxPickupTrips) return false;
            if (pawn.Map == null) return false;

            var mgr = GameComponent_MagazineManager.Instance;
            if (mgr == null) return false;
            mgr.PawnNeedsPacking(pawn, out var missingMap);
            if (missingMap == null || missingMap.Count == 0) return false;

            var compInv = pawn.TryGetComp<CompInventory>();
            if (compInv == null) return false;
            compInv.UpdateInventory();

            foreach (var kv in missingMap)
            {
                AmmoDef ammoDef = kv.Key;
                int need = kv.Value;
                if (ammoDef == null || need <= 0) continue;

                // Check if at least 1 round can fit in inventory
                if (!compInv.CanFitInInventory(ammoDef, out int fitTest) || fitTest <= 0)
                {
                    continue;
                }

                Thing found = GenClosest.ClosestThingReachable(
                    pawn.Position,
                    pawn.Map,
                    ThingRequest.ForDef(ammoDef),
                    PathEndMode.ClosestTouch,
                    TraverseParms.For(pawn, Danger.Some),
                    9999f,
                    t => !t.IsForbidden(pawn)
                         && !t.IsBurning()
                         && !(t is AmmoThing at && at.IsCookingOff)
                         && pawn.CanReserve(t, 10, 1));

                if (found != null && compInv.CanFitInInventory(found, out int canFit) && canFit > 0)
                {
                    ammoStack = found;
                    takeCount = Math.Min(Math.Min(need, found.stackCount), canFit);
                    if (takeCount > 0) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Checks if a magazine can accept at least one planned round right now from pawn inventory.
        /// </summary>
        private bool TrySelectNextMagAndRound(out Thing_CEMagazine mag, out AmmoPlan plan, out AmmoDef nextAmmo, out bool insertAtFront)
        {
            mag = null;
            plan = null;
            nextAmmo = null;
            insertAtFront = false;

            var mgr = GameComponent_MagazineManager.Instance;
            if (mgr == null) return false;

            var weapons = MagazineUtility.GetManagedWeapons(pawn);
            var checkedConfigs = new HashSet<PawnWeaponAmmoConfig>();

            for (int w = 0; w < weapons.Count; w++)
            {
                var ca = weapons[w].TryGetComp<CompAmmoUser>();
                if (!MagazineUtility.IsMagazineFedWeapon(ca)) continue;

                var cfg = mgr.GetOrCreateConfig(pawn, ca);
                if (cfg == null || !checkedConfigs.Add(cfg)) continue;

                var invMags = MagazineUtility.GetMagazinesInInventory(pawn, cfg.ammoSet, cfg.capacity);
                invMags.Sort((a, b) =>
                {
                    int pa = a.preferredPlanIndex >= 0 ? a.preferredPlanIndex : 999;
                    int pb = b.preferredPlanIndex >= 0 ? b.preferredPlanIndex : 999;
                    return pa.CompareTo(pb);
                });

                for (int i = 0; i < invMags.Count; i++)
                {
                    var m = invMags[i];
                    int slotIdx = m.preferredPlanIndex >= 0 && m.preferredPlanIndex < cfg.targetTotalMags ? m.preferredPlanIndex : 0;
                    var p = cfg.GetPlan(slotIdx);
                    p.Normalize();

                    if (p.MatchesExactly(m.rounds)) continue;

                    // If rounds do not form either a prefix or suffix of the plan, unload mismatched rounds to inventory ONLY
                    // if we actually have the round needed for slot 0 (either in inventory or inside this mag itself),
                    // so we never strip a loaded mag down to 0/N when slot 0's ammo is out of stock.
                    if (m.rounds.Count > 0 && !p.IsPrefixMatch(m.rounds) && !p.IsSuffixMatch(m.rounds))
                    {
                        AmmoDef firstSlot = p.slots[0];
                        bool canStartPlan = firstSlot != null && (MagazineUtility.CountLooseAmmoInInventory(pawn, firstSlot) > 0 || m.rounds.Contains(firstSlot));
                        if (!canStartPlan) continue;
                        m.UnloadAllRounds(pawn);
                        madeAnyProgress = true;
                    }

                    if (m.rounds.Count >= p.capacity) continue;

                    AmmoDef needed;
                    bool atFront;
                    if (m.rounds.Count == 0 || p.IsPrefixMatch(m.rounds))
                    {
                        needed = p.slots[m.rounds.Count];
                        atFront = false;
                    }
                    else
                    {
                        // Suffix match: insert missing front rounds in reverse order so firing order is exact
                        int missing = p.capacity - m.rounds.Count;
                        needed = p.slots[missing - 1];
                        atFront = true;
                    }

                    if (needed != null && MagazineUtility.CountLooseAmmoInInventory(pawn, needed) > 0)
                    {
                        mag = m;
                        plan = p;
                        nextAmmo = needed;
                        insertAtFront = atFront;
                        return true;
                    }
                }
            }
            return false;
        }

        private void EnsureEmptyMagazinesProvisioned(bool reservePackingHeadroom, bool allowLooseAmmoBulkReclaim)
        {
            var mgr = GameComponent_MagazineManager.Instance;
            if (mgr == null) return;

            var weapons = MagazineUtility.GetManagedWeapons(pawn);
            var processed = new HashSet<PawnWeaponAmmoConfig>();
            for (int i = 0; i < weapons.Count; i++)
            {
                var ca = weapons[i].TryGetComp<CompAmmoUser>();
                if (!MagazineUtility.IsMagazineFedWeapon(ca)) continue;
                var cfg = mgr.GetOrCreateConfig(pawn, ca);
                if (cfg == null || !processed.Add(cfg)) continue;

                int before = MagazineUtility.GetMagazinesInInventory(pawn, cfg.ammoSet, cfg.capacity).Count;
                mgr.EnsureMagazinesForConfig(pawn, cfg, allowCreateNew: true, reservePackingHeadroom: reservePackingHeadroom, allowLooseAmmoBulkReclaim: allowLooseAmmoBulkReclaim);
                int after = MagazineUtility.GetMagazinesInInventory(pawn, cfg.ammoSet, cfg.capacity).Count;
                if (after != before) madeAnyProgress = true;
            }
        }

        private AmmoPlan ResolvePlanForMag(Thing_CEMagazine mag)
        {
            if (mag == null) return null;
            var mgr = GameComponent_MagazineManager.Instance;
            if (mgr == null) return null;
            var weapons = MagazineUtility.GetManagedWeapons(pawn);
            for (int i = 0; i < weapons.Count; i++)
            {
                var ca = weapons[i].TryGetComp<CompAmmoUser>();
                if (!MagazineUtility.IsMagazineFedWeapon(ca)) continue;
                if (ca.CurAmmoSet != mag.ammoSet || ca.MagSize != mag.capacity) continue;
                var cfg = mgr.GetOrCreateConfig(pawn, ca);
                if (cfg != null)
                {
                    int slotIdx = mag.preferredPlanIndex >= 0 && mag.preferredPlanIndex < cfg.targetTotalMags ? mag.preferredPlanIndex : 0;
                    return cfg.GetPlan(slotIdx);
                }
            }
            return null;
        }

        private int CountConsecutivePackableRounds(Thing_CEMagazine mag, AmmoPlan plan, int maxCheck)
        {
            if (mag == null || plan == null) return 0;
            plan.Normalize();
            int simCount = mag.rounds.Count;
            bool isPrefix = simCount == 0 || plan.IsPrefixMatch(mag.rounds);
            bool isSuffix = !isPrefix && plan.IsSuffixMatch(mag.rounds);
            if (!isPrefix && !isSuffix) return 0;

            var available = new Dictionary<AmmoDef, int>();
            int limit = Math.Min(maxCheck, plan.capacity - simCount);
            int count = 0;

            for (int k = 0; k < limit; k++)
            {
                AmmoDef needed = isPrefix
                    ? plan.slots[simCount]
                    : plan.slots[(plan.capacity - simCount) - 1];
                if (needed == null) break;

                if (!available.TryGetValue(needed, out int have))
                {
                    have = MagazineUtility.CountLooseAmmoInInventory(pawn, needed);
                }
                if (have <= 0) break;

                available[needed] = have - 1;
                count++;
                simCount++;
            }
            return count;
        }

        private bool ShouldAbortMechPacking()
        {
            if (pawn == null || pawn.RaceProps == null || !pawn.RaceProps.IsMechanoid) return false;
            if (pawn.Drafted)
            {
                bool allowDraftedMovePack = job != null && job.playerForced && MoveWhileReloadUtility.CanMoveWhileReloading(pawn);
                if (!allowDraftedMovePack) return true;
            }

            if (ModsConfig.BiotechActive)
            {
                if (RestUtility.IsCharging(pawn)) return true;
                if (pawn.needs?.energy != null && (pawn.needs.energy.IsLowEnergySelfShutdown || pawn.needs.energy.IsSelfShutdown))
                {
                    return true;
                }
            }

            if (job != null && job.playerForced) return false;

            if (ModsConfig.BiotechActive)
            {
                MechWorkModeDef mode = MechanitorUtility.GetMechWorkMode(pawn);
                if (mode == MechWorkModeDefOf.Recharge || mode == MechWorkModeDefOf.SelfShutdown)
                {
                    return true;
                }
                if (mode == MechWorkModeDefOf.Escort)
                {
                    if (!(CE_MagazineFedMod.Settings?.allowEscortMechPacking ?? true)) return true;
                    Pawn overseer = MechanitorUtility.GetOverseer(pawn);
                    if (overseer == null || overseer.Dead || overseer.Downed || overseer.Drafted || !overseer.Spawned || overseer.Map != pawn.Map)
                    {
                        return true;
                    }
                    if (Find.TickManager.TicksGame - overseer.LastAttackTargetTick < 180)
                    {
                        return true;
                    }
                    if (pawn.IsHashIntervalTick(30) && GameComponent_MagazineManager.IsHostileThreatNear(overseer, 35f))
                    {
                        return true;
                    }
                }
            }

            if (pawn.IsHashIntervalTick(30) && GameComponent_MagazineManager.IsHostileThreatNear(pawn, 30f))
            {
                return true;
            }
            return false;
        }

        /// <summary>
        /// Called right before standing to pack rounds (after stockpile ammo pickup is already finished):
        /// ejects a weapon's inserted magazine into inventory ONLY IF the pawn currently has loose ammo in inventory
        /// (or existing rounds inside the mag) to immediately repack/improve it.
        /// </summary>
        private void PrepareInsertedMagazinesForPacking(bool allowCreateNewMags)
        {
            var mgr = GameComponent_MagazineManager.Instance;
            if (mgr == null) return;

            var weapons = MagazineUtility.GetManagedWeapons(pawn);
            for (int i = 0; i < weapons.Count; i++)
            {
                var ca = weapons[i].TryGetComp<CompAmmoUser>();
                if (!MagazineUtility.IsMagazineFedWeapon(ca)) continue;

                var cfg = mgr.GetOrCreateConfig(pawn, ca);
                if (cfg == null) continue;

                mgr.EnsureMagazinesForConfig(pawn, cfg, allowCreateNewMags);

                var cm = weapons[i].TryGetComp<CompWeaponMagazine>();
                if (cm == null || !cm.hasInsertedMagazine) continue;

                cm.SyncFromCompAmmo();
                int slotIdx = cm.insertedMagPlanIndex >= 0 && cm.insertedMagPlanIndex < cfg.targetTotalMags ? cm.insertedMagPlanIndex : 0;
                var plan = cfg.GetPlan(slotIdx);
                if (plan.MatchesExactly(cm.loadedRounds)) continue;

                // Only eject now if the pawn already holds loose ammo in inventory (or can reorder existing rounds)
                bool canImprove = HasInventoryAmmoToImprove(plan, cm.loadedRounds);
                if (canImprove)
                {
                    cm.EjectInsertedMagazine(pawn);
                }
            }

            // Re-normalize magazine slot indices & count after any ejections
            for (int i = 0; i < weapons.Count; i++)
            {
                var ca = weapons[i].TryGetComp<CompAmmoUser>();
                if (!MagazineUtility.IsMagazineFedWeapon(ca)) continue;
                var cfg = mgr.GetOrCreateConfig(pawn, ca);
                if (cfg != null) mgr.EnsureMagazinesForConfig(pawn, cfg, allowCreateNewMags);
            }
        }

        private bool HasInventoryAmmoToImprove(AmmoPlan plan, List<AmmoDef> currentRounds)
        {
            plan.Normalize();
            var needed = plan.GetCountsByDef();
            var cur = new Dictionary<AmmoDef, int>();
            if (currentRounds != null)
            {
                for (int i = 0; i < currentRounds.Count; i++)
                {
                    var d = currentRounds[i];
                    if (d == null) continue;
                    if (cur.TryGetValue(d, out int c)) cur[d] = c + 1;
                    else cur[d] = 1;
                }
            }

            // If we have all rounds already inside just out of order, we can reorganize for free!
            bool sameMultiset = needed.Count == cur.Count && needed.All(kv => cur.TryGetValue(kv.Key, out int c) && c == kv.Value);
            if (sameMultiset) return true;

            foreach (var kv in needed)
            {
                cur.TryGetValue(kv.Key, out int have);
                if (kv.Value > have)
                {
                    if (MagazineUtility.CountLooseAmmoInInventory(pawn, kv.Key) > 0) return true;
                }
            }
            return false;
        }

        private void TryReorganizeSingleLoadWeapons()
        {
            var mgr = GameComponent_MagazineManager.Instance;
            if (mgr == null) return;

            var weapons = MagazineUtility.GetManagedWeapons(pawn);
            for (int i = 0; i < weapons.Count; i++)
            {
                var ca = weapons[i].TryGetComp<CompAmmoUser>();
                var cm = weapons[i].TryGetComp<CompWeaponMagazine>();
                if (ca == null || cm == null || !MagazineUtility.IsSingleLoadMultiCapacityWeapon(ca)) continue;

                cm.SyncFromCompAmmo();
                var cfg = mgr.GetOrCreateConfig(pawn, ca);
                if (cfg == null) continue;

                var plan = cfg.GetPlan(0);
                plan.Normalize();
                if (plan.MatchesExactly(cm.loadedRounds)) continue;

                // If mismatched (not prefix and not suffix), only unload if pawn has slot 0 ammo available
                if (cm.loadedRounds.Count > 0 && !plan.IsPrefixMatch(cm.loadedRounds) && !plan.IsSuffixMatch(cm.loadedRounds))
                {
                    AmmoDef first = plan.slots[0];
                    if (first != null && (MagazineUtility.CountLooseAmmoInInventory(pawn, first) > 0 || cm.loadedRounds.Contains(first)))
                    {
                        ca.TryUnload(forceUnload: true);
                        madeAnyProgress = true;
                    }
                }

                // Queue CE's single-round reload job if weapon now has room and pawn has compatible loose ammo
                if (ca.CurMagCount < ca.MagSize && ca.TryFindAmmoInInventory(out _))
                {
                    Job reloadJob = ca.TryMakeReloadJob();
                    if (reloadJob != null)
                    {
                        pawn.jobs.jobQueue.EnqueueFirst(reloadJob, JobTag.Misc);
                        madeAnyProgress = true;
                    }
                    break;
                }
            }
        }

        private void ReinsertBestMagazinesIntoWeapons()
        {
            var mgr = GameComponent_MagazineManager.Instance;
            if (mgr == null) return;

            var weapons = MagazineUtility.GetManagedWeapons(pawn);
            for (int i = 0; i < weapons.Count; i++)
            {
                var ca = weapons[i].TryGetComp<CompAmmoUser>();
                var cm = weapons[i].TryGetComp<CompWeaponMagazine>();
                if (ca == null || cm == null || !MagazineUtility.IsMagazineFedWeapon(ca)) continue;

                if (!cm.hasInsertedMagazine || cm.loadedRounds.Count == 0)
                {
                    var best = MagazineUtility.FindBestLoadedMagazine(pawn, ca.CurAmmoSet, ca.MagSize, null);
                    if (best != null)
                    {
                        cm.InsertMagazine(best, pawn);
                    }
                    else
                    {
                        // At least seat an empty magazine back in if we had ejected it
                        var anyEmpty = MagazineUtility.GetMagazinesInInventory(pawn, ca.CurAmmoSet, ca.MagSize).FirstOrDefault();
                        if (anyEmpty != null && !cm.hasInsertedMagazine)
                        {
                            cm.InsertMagazine(anyEmpty, pawn);
                        }
                    }
                }
            }
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            this.FailOnMentalState(TargetIndex.A);
            if (pawn?.RaceProps != null && pawn.RaceProps.IsMechanoid)
            {
                this.FailOn(ShouldAbortMechPacking);
            }

            // Ensure weapons always have a magazine seated back even if the job is interrupted!
            AddFinishAction(cond =>
            {
                ReinsertBestMagazinesIntoWeapons();
            });

            // Phase 1: Normalize existing mags (without creating free mags yet) and recycle compatible map mags FIRST
            Toil initCountersToil = new Toil();
            initCountersToil.initAction = () =>
            {
                pickupTripsDone = 0;
                magPickupTripsDone = 0;
                roundsPackedCount = 0;
                roundsPackedThisWave = 0;
                pendingBatchCount = 1;
                madeAnyProgress = false;
                isInPackingPhase = false;
            };
            initCountersToil.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return initCountersToil;

            Toil findMapMagToil = new Toil();
            Toil afterRecycleLabel = Toils_General.Label();

            findMapMagToil.initAction = () =>
            {
                currentPickupMag = null;
                if (FindNextMapMagazineToRecycle(out var foundMag))
                {
                    currentPickupMag = foundMag;
                    job.SetTarget(TargetIndex.C, foundMag);
                    if (!pawn.Reserve(foundMag, job, 1, -1))
                    {
                        currentPickupMag = null;
                        JumpToToil(afterRecycleLabel);
                    }
                }
                else
                {
                    JumpToToil(afterRecycleLabel);
                }
            };
            findMapMagToil.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return findMapMagToil;

            Toil gotoMapMagToil = Toils_Goto.GotoThing(TargetIndex.C, PathEndMode.ClosestTouch)
                .FailOnDespawnedNullOrForbidden(TargetIndex.C);
            yield return gotoMapMagToil;

            Toil pickupMapMagToil = new Toil();
            pickupMapMagToil.initAction = () =>
            {
                magPickupTripsDone++;
                if (currentPickupMag != null && pawn.Map?.reservationManager != null)
                {
                    pawn.Map.reservationManager.Release(currentPickupMag, pawn, job);
                }
                if (currentPickupMag != null && !currentPickupMag.Destroyed && currentPickupMag.Spawned)
                {
                    currentPickupMag.designatedForUnload = false;
                    currentPickupMag.emptyOnGroundTick = -1;
                    currentPickupMag.preferredPlanIndex = -1;

                    currentPickupMag.DeSpawn(DestroyMode.Vanish);
                    if (pawn.inventory.innerContainer.TryAdd(currentPickupMag, true))
                    {
                        madeAnyProgress = true;
                    }
                    else
                    {
                        GenPlace.TryPlaceThing(currentPickupMag, pawn.Position, pawn.Map, ThingPlaceMode.Near);
                    }
                    pawn.TryGetComp<CompInventory>()?.UpdateInventory();
                }
                currentPickupMag = null;
            };
            pickupMapMagToil.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return pickupMapMagToil;

            yield return Toils_Jump.JumpIf(findMapMagToil, () => magPickupTripsDone < MaxMagPickupTrips && FindNextMapMagazineToRecycle(out _));

            // Phase 1.5: Provision any missing empty mags into inventory (keeping weapon's inserted magazine seated during stockpile walk!)
            yield return afterRecycleLabel;

            Toil initToil = new Toil();
            initToil.initAction = () =>
            {
                EnsureEmptyMagazinesProvisioned(reservePackingHeadroom: true, allowLooseAmmoBulkReclaim: false);
            };
            initToil.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return initToil;

            // Phase 2: Loop picking up missing loose ammo from colony stockpiles
            Toil findPickupToil = new Toil();
            Toil packStartLabel = Toils_General.Label();

            findPickupToil.initAction = () =>
            {
                isInPackingPhase = false;
                currentPickupThing = null;
                currentPickupCount = 0;
                if (FindNextStockpilePickup(out var stack, out int count))
                {
                    currentPickupThing = stack;
                    currentPickupCount = count;
                    job.SetTarget(TargetIndex.B, stack);
                    job.count = count;
                    if (!pawn.Reserve(stack, job, 10, count))
                    {
                        currentPickupThing = null;
                        JumpToToil(packStartLabel);
                    }
                }
                else
                {
                    JumpToToil(packStartLabel);
                }
            };
            findPickupToil.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return findPickupToil;

            Toil gotoAmmoToil = Toils_Goto.GotoThing(TargetIndex.B, PathEndMode.ClosestTouch)
                .FailOnDespawnedNullOrForbidden(TargetIndex.B);
            yield return gotoAmmoToil;

            Toil pickupAmmoToil = new Toil();
            pickupAmmoToil.initAction = () =>
            {
                pickupTripsDone++;
                if (currentPickupThing == null || currentPickupThing.Destroyed || !currentPickupThing.Spawned)
                {
                    return;
                }
                var compInv = pawn.TryGetComp<CompInventory>();
                int take = Math.Min(currentPickupCount, currentPickupThing.stackCount);
                if (compInv != null && compInv.CanFitInInventory(currentPickupThing, out int fit))
                {
                    take = Math.Min(take, fit);
                }
                if (take > 0)
                {
                    Thing taken = currentPickupThing.SplitOff(take);
                    if (pawn.inventory.innerContainer.TryAdd(taken, true))
                    {
                        madeAnyProgress = true;
                    }
                    else
                    {
                        GenPlace.TryPlaceThing(taken, pawn.Position, pawn.Map, ThingPlaceMode.Near);
                    }
                    compInv?.UpdateInventory();
                }
                if (pawn.Map?.reservationManager != null)
                {
                    pawn.Map.reservationManager.Release(currentPickupThing, pawn, job);
                }
                currentPickupThing = null;
            };
            pickupAmmoToil.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return pickupAmmoToil;

            // Loop back to check if more ammo types need picking up
            yield return Toils_Jump.JumpIf(findPickupToil, () => pickupTripsDone < MaxPickupTrips && FindNextStockpilePickup(out _, out _));

            // Phase 3: Now that pawn has finished fetching ammo, eject mismatched inserted mags (if improvable) and pack round-by-round
            yield return packStartLabel;

            Toil packMoteToil = new Toil();
            packMoteToil.initAction = () =>
            {
                roundsPackedThisWave = 0;
                EnsureEmptyMagazinesProvisioned(reservePackingHeadroom: false, allowLooseAmmoBulkReclaim: true);
                PrepareInsertedMagazinesForPacking(allowCreateNewMags: false);
                isInPackingPhase = true;

                // Estimate total rounds to pack for progress bar
                var mgr = GameComponent_MagazineManager.Instance;
                int est = 0;
                if (mgr != null)
                {
                    var weapons = MagazineUtility.GetManagedWeapons(pawn);
                    var seen = new HashSet<PawnWeaponAmmoConfig>();
                    foreach (var w in weapons)
                    {
                        var ca = w.TryGetComp<CompAmmoUser>();
                        if (!MagazineUtility.IsMagazineFedWeapon(ca)) continue;
                        var cfg = mgr.GetOrCreateConfig(pawn, ca);
                        if (cfg == null || !seen.Add(cfg)) continue;
                        foreach (var m in MagazineUtility.GetMagazinesInInventory(pawn, cfg.ammoSet, cfg.capacity))
                        {
                            int slot = m.preferredPlanIndex >= 0 && m.preferredPlanIndex < cfg.targetTotalMags ? m.preferredPlanIndex : 0;
                            var plan = cfg.GetPlan(slot);
                            if (!plan.MatchesExactly(m.rounds))
                            {
                                est += Math.Max(1, plan.capacity - m.rounds.Count);
                            }
                        }
                    }
                }
                totalRoundsToPackEstimate = Math.Max(1, roundsPackedCount + est);

                if (TrySelectNextMagAndRound(out _, out _, out _, out _) && pawn.Map != null)
                {
                    MoteMaker.ThrowText(pawn.Position.ToVector3Shifted(), pawn.Map, "CEMF_Mote_PackingMag".Translate());
                }
            };
            packMoteToil.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return packMoteToil;

            Toil packOneRoundToil = new Toil { actor = pawn };

            packOneRoundToil.initAction = () =>
            {
                if (!MoveWhileReloadUtility.CanMoveWhileReloading(packOneRoundToil.actor))
                {
                    packOneRoundToil.actor.pather?.StopDead();
                }
                if (!TrySelectNextMagAndRound(out currentPackingMag, out currentPackingPlan, out _, out _))
                {
                    currentPackingMag = null;
                    pendingBatchCount = 1;
                    ReadyForNextToil();
                    return;
                }
                int maxPackable = Math.Max(1, CountConsecutivePackableRounds(currentPackingMag, currentPackingPlan, 25));
                packOneRoundToil.defaultDuration = MagazineUtility.GetPackingTicksAndBatch(pawn, isFieldPacking: false, maxBatchRounds: maxPackable, out pendingBatchCount);
                ticksLeftThisToil = packOneRoundToil.defaultDuration;
            };
            packOneRoundToil.defaultCompleteMode = ToilCompleteMode.Delay;
            packOneRoundToil.AddFinishAction(() =>
            {
                if (currentPackingMag == null || currentPackingMag.Destroyed) return;
                AmmoPlan plan = currentPackingPlan ?? ResolvePlanForMag(currentPackingMag);
                if (plan == null) return;
                plan.Normalize();

                int batch = Math.Max(1, pendingBatchCount);
                for (int b = 0; b < batch; b++)
                {
                    if (currentPackingMag.Destroyed || currentPackingMag.LoadedCount >= currentPackingMag.capacity) break;

                    AmmoDef nextAmmo;
                    bool atFront;
                    if (currentPackingMag.rounds.Count == 0 || plan.IsPrefixMatch(currentPackingMag.rounds))
                    {
                        nextAmmo = plan.slots[currentPackingMag.rounds.Count];
                        atFront = false;
                    }
                    else if (plan.IsSuffixMatch(currentPackingMag.rounds))
                    {
                        int missing = plan.capacity - currentPackingMag.rounds.Count;
                        nextAmmo = plan.slots[missing - 1];
                        atFront = true;
                    }
                    else
                    {
                        break;
                    }

                    if (nextAmmo == null || MagazineUtility.ConsumeLooseAmmoFromInventory(pawn, nextAmmo, 1) != 1) break;

                    if (atFront)
                    {
                        currentPackingMag.rounds.Insert(0, nextAmmo);
                    }
                    else
                    {
                        currentPackingMag.rounds.Add(nextAmmo);
                    }
                    roundsPackedCount++;
                    roundsPackedThisWave++;
                    madeAnyProgress = true;
                    MagazineUtility.TryApplyPackingThumbInjury(pawn, isFieldPacking: false);
                }
            });
            yield return packOneRoundToil.WithProgressBar(TargetIndex.A, () =>
                Mathf.Clamp01((float)roundsPackedCount / Math.Max(1, totalRoundsToPackEstimate)));

            // Continue packing until all magazines match their plans or no more planned rounds are in inventory
            yield return Toils_Jump.JumpIf(packOneRoundToil, () => TrySelectNextMagAndRound(out _, out _, out _, out _));

            // Re-seat best magazines and refresh bulk BEFORE checking for another pickup wave
            Toil waveSeatToil = new Toil();
            waveSeatToil.initAction = () =>
            {
                ReinsertBestMagazinesIntoWeapons();
                pawn.TryGetComp<CompInventory>()?.UpdateInventory();
            };
            waveSeatToil.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return waveSeatToil;

            // If packing rounds freed carry bulk for tight inventories, loop back for another pickup wave
            yield return Toils_Jump.JumpIf(findPickupToil, () =>
                roundsPackedThisWave > 0
                && pickupTripsDone < MaxPickupTrips
                && FindNextStockpilePickup(out _, out _));

            // Phase 4: Re-insert best magazine into weapon & refresh inventory
            Toil finishToil = new Toil();
            finishToil.initAction = () =>
            {
                ReinsertBestMagazinesIntoWeapons();
                TryReorganizeSingleLoadWeapons();
                pawn.TryGetComp<CompInventory>()?.UpdateInventory();
                if (!madeAnyProgress && roundsPackedCount <= 0)
                {
                    JobGiver_PackMagazines.NotifyNoProgress(pawn);
                }
                if (pawn.equipment?.Primary?.def?.soundInteract != null && pawn.Map != null && roundsPackedCount > 0)
                {
                    pawn.equipment.Primary.def.soundInteract.PlayOneShot(new TargetInfo(pawn.Position, pawn.Map));
                }
            };
            finishToil.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return finishToil;
        }
    }
}
