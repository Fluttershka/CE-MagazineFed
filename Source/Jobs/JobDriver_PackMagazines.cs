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
        private const int MaxPickupTrips = 10;

        private Thing_CEMagazine currentPickupMag;
        private int magPickupTripsDone;
        private const int MaxMagPickupTrips = 8;

        private Thing_CEMagazine currentPackingMag;
        private AmmoPlan currentPackingPlan;
        private int totalRoundsToPackEstimate = 1;
        private int roundsPackedCount;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

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

        /// <summary>
        /// If a mag-fed weapon's inserted magazine doesn't match its plan, eject it into inventory ONLY IF
        /// either (a) we already have a fuller/matching mag in inventory to swap in, or (b) we have loose ammo
        /// (in inventory or colony) to repack it.
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

                // Check if we can actually improve/repack this mag with inventory or colony ammo
                bool canImprove = HasAnyAmmoToImprove(plan, cm.loadedRounds);
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

        private bool HasAnyAmmoToImprove(AmmoPlan plan, List<AmmoDef> currentRounds)
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
                    if ((CE_MagazineFedMod.Settings?.fetchAmmoFromStockpile ?? true) && pawn.Map != null && MagazineUtility.CountLooseAmmoInColony(pawn.Map, kv.Key) > 0)
                    {
                        return true;
                    }
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
                    }
                }

                // Queue CE's single-round reload job if weapon now has room and pawn has compatible loose ammo
                if (ca.CurMagCount < ca.MagSize && ca.TryFindAmmoInInventory(out _))
                {
                    Job reloadJob = ca.TryMakeReloadJob();
                    if (reloadJob != null)
                    {
                        pawn.jobs.jobQueue.EnqueueFirst(reloadJob, JobTag.Misc);
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
                    if (!pawn.inventory.innerContainer.TryAdd(currentPickupMag, true))
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

            // Phase 1.5: Now auto-provision any still-missing empty mags & prepare mismatched inserted mags
            yield return afterRecycleLabel;

            Toil initToil = new Toil();
            initToil.initAction = () =>
            {
                PrepareInsertedMagazinesForPacking(allowCreateNewMags: true);

                // Estimate total rounds to pack for progress bar
                var mgr = GameComponent_MagazineManager.Instance;
                int est = 0;
                if (mgr != null)
                {
                    var weapons = MagazineUtility.GetManagedWeapons(pawn);
                    foreach (var w in weapons)
                    {
                        var ca = w.TryGetComp<CompAmmoUser>();
                        if (!MagazineUtility.IsMagazineFedWeapon(ca)) continue;
                        var cfg = mgr.GetOrCreateConfig(pawn, ca);
                        if (cfg == null) continue;
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
                totalRoundsToPackEstimate = Math.Max(1, est);
            };
            initToil.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return initToil;

            // Phase 2: Loop picking up missing loose ammo from colony stockpiles
            Toil findPickupToil = new Toil();
            Toil packStartLabel = Toils_General.Label();

            findPickupToil.initAction = () =>
            {
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
                    if (!pawn.inventory.innerContainer.TryAdd(taken, true))
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

            // Phase 3: Stand and pack magazines round-by-round according to exact AmmoPlans
            yield return packStartLabel;

            Toil packMoteToil = new Toil();
            packMoteToil.initAction = () =>
            {
                if (TrySelectNextMagAndRound(out _, out _, out _, out _) && pawn.Map != null)
                {
                    MoteMaker.ThrowText(pawn.Position.ToVector3Shifted(), pawn.Map, "CEMF_Mote_PackingMag".Translate());
                }
            };
            packMoteToil.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return packMoteToil;

            Toil packOneRoundToil = new Toil { actor = pawn };
            AmmoDef pendingAmmo = null;
            bool pendingInsertFront = false;

            packOneRoundToil.initAction = () =>
            {
                packOneRoundToil.actor.pather?.StopDead();
                if (!TrySelectNextMagAndRound(out currentPackingMag, out currentPackingPlan, out pendingAmmo, out pendingInsertFront))
                {
                    currentPackingMag = null;
                    pendingAmmo = null;
                    ReadyForNextToil();
                    return;
                }
                float speed = Mathf.Max(0.25f, pawn.GetStatValue(CE_StatDefOf.ReloadSpeed));
                int baseTicks = CE_MagazineFedMod.Settings?.TicksPerRoundIdle ?? 9;
                packOneRoundToil.defaultDuration = Mathf.Max(2, Mathf.CeilToInt(baseTicks / speed));
                ticksLeftThisToil = packOneRoundToil.defaultDuration;
            };
            packOneRoundToil.defaultCompleteMode = ToilCompleteMode.Delay;
            packOneRoundToil.AddFinishAction(() =>
            {
                if (currentPackingMag == null || currentPackingMag.Destroyed || pendingAmmo == null) return;
                if (currentPackingMag.LoadedCount >= currentPackingMag.capacity) return;

                if (MagazineUtility.ConsumeLooseAmmoFromInventory(pawn, pendingAmmo, 1) == 1)
                {
                    if (pendingInsertFront)
                    {
                        currentPackingMag.rounds.Insert(0, pendingAmmo);
                    }
                    else
                    {
                        currentPackingMag.rounds.Add(pendingAmmo);
                    }
                    roundsPackedCount++;
                    MagazineUtility.TryApplyPackingThumbInjury(pawn, isFieldPacking: false);
                }
            });
            yield return packOneRoundToil.WithProgressBar(TargetIndex.A, () =>
                Mathf.Clamp01((float)roundsPackedCount / Math.Max(1, totalRoundsToPackEstimate)));

            // Continue packing until all magazines match their plans or no more planned rounds are in inventory
            yield return Toils_Jump.JumpIf(packOneRoundToil, () => TrySelectNextMagAndRound(out _, out _, out _, out _));

            // Phase 4: Re-insert best magazine into weapon & refresh inventory
            Toil finishToil = new Toil();
            finishToil.initAction = () =>
            {
                ReinsertBestMagazinesIntoWeapons();
                TryReorganizeSingleLoadWeapons();
                pawn.TryGetComp<CompInventory>()?.UpdateInventory();
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
