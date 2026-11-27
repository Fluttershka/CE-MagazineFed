using System;
using System.Collections.Generic;
using System.Linq;
using CombatExtended;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace CE_MagazineFed
{
    public class GameComponent_MagazineManager : GameComponent
    {
        private Dictionary<int, PawnAmmoManagementData> pawnDataMap = new Dictionary<int, PawnAmmoManagementData>();
        private List<int> scribeKeys;
        private List<PawnAmmoManagementData> scribeValues;

        public List<AmmoPlanPreset> presets = new List<AmmoPlanPreset>();

        private const int CleanupCheckIntervalTicks = 240;
        private const int MechScheduleIntervalTicks = 120;

        public static GameComponent_MagazineManager Instance => Current.Game?.GetComponent<GameComponent_MagazineManager>();

        public GameComponent_MagazineManager(Game game)
        {
            JobGiver_PackMagazines.ClearCooldownCache();
        }

        public override void GameComponentTick()
        {
            base.GameComponentTick();
            var settings = CE_MagazineFedMod.Settings;
            if (settings == null || Find.TickManager == null) return;

            int now = Find.TickManager.TicksGame;
            var maps = Find.Maps;
            if (maps == null) return;

            // Low-frequency staggered safe dispatcher for player-controlled mechanoids
            if (settings.enableForMechanoids && now % MechScheduleIntervalTicks == 0)
            {
                int slotPhase = (now / MechScheduleIntervalTicks) & 1;
                for (int m = 0; m < maps.Count; m++)
                {
                    Map map = maps[m];
                    if (map?.mapPawns == null) continue;
                    var factionPawns = map.mapPawns.PawnsInFaction(Faction.OfPlayer);
                    if (factionPawns == null) continue;

                    for (int i = 0; i < factionPawns.Count; i++)
                    {
                        Pawn p = factionPawns[i];
                        if (p == null || p.RaceProps == null || !p.RaceProps.IsMechanoid) continue;
                        if ((p.thingIDNumber & 1) != slotPhase) continue;
                        TryDispatchMechanoidAmmoJob(p);
                    }
                }
            }

            if (!settings.autoCleanupEmptyGroundMags || CEMF_DefOf.CEMF_Magazine == null) return;
            if (now % CleanupCheckIntervalTicks != 0) return;
            int delayTicks = settings.EmptyMagCleanupTicks;

            for (int m = 0; m < maps.Count; m++)
            {
                Map map = maps[m];
                if (map?.listerThings == null) continue;
                var mags = map.listerThings.ThingsOfDef(CEMF_DefOf.CEMF_Magazine);
                if (mags == null || mags.Count == 0) continue;

                // Iterate backwards because we may destroy items
                for (int i = mags.Count - 1; i >= 0; i--)
                {
                    if (!(mags[i] is Thing_CEMagazine mag) || mag.Destroyed || !mag.Spawned) continue;
                    if (!mag.IsEmpty)
                    {
                        mag.emptyOnGroundTick = -1;
                        continue;
                    }
                    // Never destroy an empty mag that someone is actively reserving/walking to pick up
                    if (map.reservationManager != null && map.reservationManager.IsReserved(mag))
                    {
                        mag.emptyOnGroundTick = now;
                        continue;
                    }
                    if (mag.emptyOnGroundTick < 0)
                    {
                        mag.emptyOnGroundTick = now;
                        continue;
                    }
                    if (now - mag.emptyOnGroundTick >= delayTicks)
                    {
                        mag.Destroy(DestroyMode.Vanish);
                    }
                }
            }
        }

        /// <summary>
        /// Finds the best reachable, unforbidden, reservable compatible magazine on the pawn's map.
        /// Prioritizes magazines that already contain rounds, then closest distance.
        /// </summary>
        public static Thing_CEMagazine FindBestCompatibleMapMagazine(Pawn pawn, AmmoSetDef ammoSet, int capacity)
        {
            if (pawn?.Map == null || ammoSet == null || CEMF_DefOf.CEMF_Magazine == null) return null;
            var compInv = pawn.TryGetComp<CompInventory>();
            var list = pawn.Map.listerThings.ThingsOfDef(CEMF_DefOf.CEMF_Magazine);
            if (list == null || list.Count == 0) return null;

            Thing_CEMagazine best = null;
            float bestScore = float.MinValue;

            for (int i = 0; i < list.Count; i++)
            {
                if (!(list[i] is Thing_CEMagazine mag) || mag.Destroyed || !mag.Spawned) continue;
                if (!mag.Matches(ammoSet, capacity)) continue;
                if (mag.IsForbidden(pawn) || mag.IsBurning()) continue;
                if (mag.designatedForUnload) continue; // Don't steal mags the player explicitly marked to unload
                if (!pawn.CanReserveAndReach(mag, PathEndMode.ClosestTouch, Danger.Some)) continue;
                if (compInv != null && (!compInv.CanFitInInventory(mag, out int fit) || fit <= 0)) continue;

                float dist = pawn.Position.DistanceTo(mag.Position);
                // Prefer loaded magazines strongly, then closer ones
                float score = mag.LoadedCount * 100f - dist;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = mag;
                }
            }
            return best;
        }

        public PawnAmmoManagementData GetOrCreatePawnData(Pawn pawn)
        {
            if (pawn == null) return null;
            if (!pawnDataMap.TryGetValue(pawn.thingIDNumber, out var data) || data == null)
            {
                data = new PawnAmmoManagementData();
                pawnDataMap[pawn.thingIDNumber] = data;
            }
            return data;
        }

        public PawnWeaponAmmoConfig GetOrCreateConfig(Pawn pawn, CompAmmoUser compAmmo)
        {
            if (pawn == null || compAmmo == null || compAmmo.CurAmmoSet == null) return null;
            bool isSingle = MagazineUtility.IsSingleLoadMultiCapacityWeapon(compAmmo);
            var data = GetOrCreatePawnData(pawn);
            int? initialOverride = isSingle ? null : (int?)MagazineUtility.ComputeInitialMagCountForPawn(pawn, compAmmo);
            return data.GetOrCreateConfig(compAmmo.CurAmmoSet, compAmmo.MagSize, isSingle, compAmmo.CurrentAmmo ?? compAmmo.SelectedAmmo, initialOverride);
        }

        public PawnWeaponAmmoConfig GetConfig(Pawn pawn, AmmoSetDef ammoSet, int capacity, bool isSingleLoad)
        {
            if (pawn == null || ammoSet == null) return null;
            if (!pawnDataMap.TryGetValue(pawn.thingIDNumber, out var data) || data == null) return null;
            return data.GetConfig(ammoSet, capacity, isSingleLoad);
        }

        public List<AmmoPlanPreset> GetPresetsFor(AmmoSetDef ammoSet)
        {
            if (presets == null) presets = new List<AmmoPlanPreset>();
            return presets.Where(p => p?.plan != null && p.plan.ammoSet == ammoSet).ToList();
        }

        public void SavePreset(string name, AmmoPlan plan)
        {
            if (string.IsNullOrWhiteSpace(name) || plan == null) return;
            if (presets == null) presets = new List<AmmoPlanPreset>();
            presets.RemoveAll(p => p != null && p.name == name && p.plan?.ammoSet == plan.ammoSet);
            presets.Add(new AmmoPlanPreset(name.Trim(), plan));
        }

        public void DeletePreset(AmmoPlanPreset preset)
        {
            presets?.Remove(preset);
        }

        /// <summary>
        /// Ensures the pawn has physical empty magazines provisioned up to `config.targetTotalMags`
        /// (accounting for any magazine currently inserted in a matching weapon), and removes excess completely empty magazines
        /// if the player reduced targetTotalMags. Respects CE carry bulk & weight limits to avoid overburdening.
        /// </summary>
        public void EnsureMagazinesForConfig(Pawn pawn, PawnWeaponAmmoConfig config, bool allowCreateNew = true, bool reservePackingHeadroom = false, bool allowLooseAmmoBulkReclaim = false)
        {
            if (pawn == null || config == null || config.isSingleLoad) return;
            if (!MagazineUtility.ShouldUseMagazineSystem(pawn)) return;

            config.EnsurePlansMatchCount();

            // Gather all matching mag-fed weapons on this pawn that currently have an inserted magazine
            var insertedComps = new List<CompWeaponMagazine>();
            var weapons = MagazineUtility.GetManagedWeapons(pawn);
            for (int i = 0; i < weapons.Count; i++)
            {
                var ca = weapons[i].TryGetComp<CompAmmoUser>();
                if (!MagazineUtility.IsMagazineFedWeapon(ca)) continue;
                if (ca.CurAmmoSet == config.ammoSet && ca.MagSize == config.capacity)
                {
                    var cm = weapons[i].TryGetComp<CompWeaponMagazine>();
                    if (cm != null && cm.hasInsertedMagazine)
                    {
                        insertedComps.Add(cm);
                    }
                }
            }

            int insertedCount = insertedComps.Count;
            if (config.targetTotalMags < insertedCount)
            {
                config.targetTotalMags = insertedCount;
                config.EnsurePlansMatchCount();
            }

            // Normalize unique plan slot indices across all inserted magazines first
            var usedIndices = new HashSet<int>();
            for (int i = 0; i < insertedComps.Count; i++)
            {
                int idx = insertedComps[i].insertedMagPlanIndex;
                if (idx >= 0 && idx < config.targetTotalMags && usedIndices.Add(idx))
                {
                    // Valid unique slot kept
                }
                else
                {
                    insertedComps[i].insertedMagPlanIndex = -1;
                }
            }
            for (int i = 0; i < insertedComps.Count; i++)
            {
                if (insertedComps[i].insertedMagPlanIndex < 0)
                {
                    for (int slot = 0; slot < config.targetTotalMags; slot++)
                    {
                        if (usedIndices.Add(slot))
                        {
                            insertedComps[i].insertedMagPlanIndex = slot;
                            break;
                        }
                    }
                }
            }

            var invMags = MagazineUtility.GetMagazinesInInventory(pawn, config.ammoSet, config.capacity);
            int totalOwned = insertedCount + invMags.Count;

            // Normalize preferredPlanIndex assignments among inventory magazines
            for (int i = 0; i < invMags.Count; i++)
            {
                int idx = invMags[i].preferredPlanIndex;
                if (idx >= 0 && idx < config.targetTotalMags && !usedIndices.Contains(idx))
                {
                    usedIndices.Add(idx);
                }
                else
                {
                    invMags[i].preferredPlanIndex = -1;
                }
            }
            // Assign unassigned invMags to free plan slots
            for (int i = 0; i < invMags.Count; i++)
            {
                if (invMags[i].preferredPlanIndex < 0)
                {
                    for (int slot = 0; slot < config.targetTotalMags; slot++)
                    {
                        if (!usedIndices.Contains(slot))
                        {
                            invMags[i].preferredPlanIndex = slot;
                            usedIndices.Add(slot);
                            break;
                        }
                    }
                }
            }

            // Provision missing empty magazines (only when allowed, auto-provision enabled, and carry space permits)
            if (allowCreateNew && (CE_MagazineFedMod.Settings?.autoProvisionEmptyMags ?? true) && totalOwned < config.targetTotalMags)
            {
                var compInv = pawn.TryGetComp<CompInventory>();
                float availBulk = float.MaxValue;
                float availWeight = float.MaxValue;
                float remainingReclaimBulk = 0f;
                if (compInv != null)
                {
                    compInv.UpdateInventory();
                    availBulk = Mathf.Max(0f, compInv.capacityBulk - compInv.currentBulk);
                    availWeight = Mathf.Max(0f, compInv.capacityWeight - compInv.currentWeight);
                    if (allowLooseAmmoBulkReclaim)
                    {
                        remainingReclaimBulk = MagazineUtility.ComputeCompatibleLooseAmmoBulkInInventory(pawn, config.ammoSet);
                    }
                }

                float magBulk = Mathf.Max(0.01f, MagazineUtility.EstimateFullMagazineBulk(config.ammoSet, config.capacity));
                float emptyMass = Mathf.Max(0.01f, MagazineUtility.EstimateEmptyMagazineMass(config.capacity));
                float headroom = reservePackingHeadroom
                    ? Mathf.Min(magBulk * 0.5f, MagazineUtility.GetCaliberReferenceBulk(config.ammoSet) * Mathf.Min(config.capacity, 10))
                    : 0f;

                int needed = config.targetTotalMags - totalOwned;
                for (int k = 0; k < needed; k++)
                {
                    if (compInv != null)
                    {
                        float reclaimHere = Mathf.Min(remainingReclaimBulk, magBulk);
                        float effectiveAvailBulk = availBulk + reclaimHere;
                        if (effectiveAvailBulk + 0.001f < magBulk + headroom || availWeight + 0.001f < emptyMass)
                        {
                            break;
                        }
                        remainingReclaimBulk -= reclaimHere;
                        availBulk = effectiveAvailBulk - magBulk;
                        availWeight -= emptyMass;
                    }

                    int freeSlot = -1;
                    for (int slot = 0; slot < config.targetTotalMags; slot++)
                    {
                        if (!usedIndices.Contains(slot))
                        {
                            freeSlot = slot;
                            usedIndices.Add(slot);
                            break;
                        }
                    }
                    var emptyMag = Thing_CEMagazine.Create(config.ammoSet, config.capacity, freeSlot);
                    if (!pawn.inventory.innerContainer.TryAdd(emptyMag, true))
                    {
                        emptyMag.Destroy(DestroyMode.Vanish);
                        break;
                    }
                }
                compInv?.UpdateInventory();
            }
            // Remove excess EMPTY magazines if player lowered targetTotalMags
            else if (totalOwned > config.targetTotalMags)
            {
                int excess = totalOwned - config.targetTotalMags;
                for (int i = invMags.Count - 1; i >= 0 && excess > 0; i--)
                {
                    if (invMags[i].IsEmpty && invMags[i].preferredPlanIndex < 0)
                    {
                        pawn.inventory.innerContainer.Remove(invMags[i]);
                        invMags[i].Destroy(DestroyMode.Vanish);
                        excess--;
                    }
                }
                if (excess > 0)
                {
                    for (int i = invMags.Count - 1; i >= 0 && excess > 0; i--)
                    {
                        if (!invMags[i].Destroyed && invMags[i].IsEmpty)
                        {
                            pawn.inventory.innerContainer.Remove(invMags[i]);
                            invMags[i].Destroy(DestroyMode.Vanish);
                            excess--;
                        }
                    }
                }
                pawn.TryGetComp<CompInventory>()?.UpdateInventory();
            }
        }

        /// <summary>
        /// Checks whether a pawn has any magazine or single-load weapon that needs packing/reorganizing.
        /// </summary>
        public bool PawnNeedsPacking(Pawn pawn, out Dictionary<AmmoDef, int> missingAmmoNeeded)
        {
            missingAmmoNeeded = new Dictionary<AmmoDef, int>();
            if (pawn == null || !MagazineUtility.ShouldUseMagazineSystem(pawn)) return false;

            var weapons = MagazineUtility.GetManagedWeapons(pawn);
            if (weapons.Count == 0) return false;

            bool needsWork = false;
            var processedConfigs = new HashSet<PawnWeaponAmmoConfig>();

            for (int w = 0; w < weapons.Count; w++)
            {
                var compAmmo = weapons[w].TryGetComp<CompAmmoUser>();
                var compMag = weapons[w].TryGetComp<CompWeaponMagazine>();
                if (compAmmo == null || compAmmo.CurAmmoSet == null) continue;

                var config = GetOrCreateConfig(pawn, compAmmo);
                if (config == null) continue;
                bool firstForConfig = processedConfigs.Add(config);

                if (!config.isSingleLoad)
                {
                    if (firstForConfig)
                    {
                        // Count inserted magazines across all matching weapons sharing this config
                        int inGun = 0;
                        var usedSlots = new HashSet<int>();
                        for (int k = 0; k < weapons.Count; k++)
                        {
                            var kCa = weapons[k].TryGetComp<CompAmmoUser>();
                            if (!MagazineUtility.IsMagazineFedWeapon(kCa) || kCa.CurAmmoSet != config.ammoSet || kCa.MagSize != config.capacity) continue;
                            var kCm = weapons[k].TryGetComp<CompWeaponMagazine>();
                            if (kCm != null && kCm.hasInsertedMagazine)
                            {
                                inGun++;
                                if (kCm.insertedMagPlanIndex >= 0 && kCm.insertedMagPlanIndex < config.targetTotalMags)
                                {
                                    usedSlots.Add(kCm.insertedMagPlanIndex);
                                }
                            }
                        }

                        var invMags = MagazineUtility.GetMagazinesInInventory(pawn, config.ammoSet, config.capacity);
                        for (int i = 0; i < invMags.Count; i++)
                        {
                            if (invMags[i].preferredPlanIndex >= 0 && invMags[i].preferredPlanIndex < config.targetTotalMags)
                            {
                                usedSlots.Add(invMags[i].preferredPlanIndex);
                            }
                        }

                        if ((invMags.Count + inGun) < config.targetTotalMags)
                        {
                            bool autoProvision = CE_MagazineFedMod.Settings?.autoProvisionEmptyMags ?? true;
                            int maxFitNew = autoProvision
                                ? MagazineUtility.CountMaxNewMagazinesFit(pawn, config.ammoSet, config.capacity, includeCompatibleLooseAmmoReclaim: true)
                                : 0;
                            bool canRecycle = (CE_MagazineFedMod.Settings?.preferRecycleMapMagazines ?? true)
                                              && FindBestCompatibleMapMagazine(pawn, config.ammoSet, config.capacity) != null;

                            if (maxFitNew > 0 || canRecycle)
                            {
                                needsWork = true;
                            }

                            int unprovisionedSlots = Math.Min(config.targetTotalMags - (invMags.Count + inGun), Math.Max(maxFitNew, canRecycle ? 1 : 0));
                            int addedDemand = 0;
                            for (int slot = 0; slot < config.targetTotalMags && addedDemand < unprovisionedSlots; slot++)
                            {
                                if (usedSlots.Add(slot))
                                {
                                    AccumulateMissingAmmo(config.GetPlan(slot), null, missingAmmoNeeded);
                                    addedDemand++;
                                }
                            }
                        }

                        // Check each inventory mag against its plan (once per config)
                        for (int i = 0; i < invMags.Count; i++)
                        {
                            var mag = invMags[i];
                            int slotIdx = mag.preferredPlanIndex >= 0 && mag.preferredPlanIndex < config.targetTotalMags ? mag.preferredPlanIndex : 0;
                            var plan = config.GetPlan(slotIdx);
                            if (!plan.MatchesExactly(mag.rounds))
                            {
                                needsWork = true;
                                AccumulateMissingAmmo(plan, mag.rounds, missingAmmoNeeded);
                            }
                        }
                    }

                    // Check THIS weapon's inserted magazine against its plan (runs for every weapon including sidearms)
                    if (compMag != null && compMag.hasInsertedMagazine)
                    {
                        compMag.SyncFromCompAmmo();
                        int slotIdx = compMag.insertedMagPlanIndex >= 0 && compMag.insertedMagPlanIndex < config.targetTotalMags ? compMag.insertedMagPlanIndex : 0;
                        var plan = config.GetPlan(slotIdx);
                        if (!plan.MatchesExactly(compMag.loadedRounds))
                        {
                            needsWork = true;
                            AccumulateMissingAmmo(plan, compMag.loadedRounds, missingAmmoNeeded);
                        }
                    }
                    else if (compMag != null && !compMag.hasInsertedMagazine)
                    {
                        // Missing inserted mag: needs 1 full plan
                        needsWork = true;
                        AccumulateMissingAmmo(config.GetPlan(0), null, missingAmmoNeeded);
                    }
                }
                else
                {
                    // Single-load multi-capacity weapon (e.g. pump shotgun):
                    // Ensure pawn carries enough planned ammo in inventory or can restore after Port Load / tactical override
                    if (compMag != null)
                    {
                        compMag.SyncFromCompAmmo();
                        var plan = config.GetPlan(0);
                        if (!plan.MatchesExactly(compMag.loadedRounds))
                        {
                            AccumulateMissingAmmo(plan, compMag.loadedRounds, missingAmmoNeeded);
                            if (MagazineUtility.CanRestoreSingleLoadWeapon(pawn, compAmmo, compMag, plan, includeStockpile: true))
                            {
                                needsWork = true;
                            }
                        }
                    }
                }
            }

            // Subtract ammo already in pawn's loose inventory
            var keys = missingAmmoNeeded.Keys.ToList();
            for (int i = 0; i < keys.Count; i++)
            {
                var ad = keys[i];
                int inInv = MagazineUtility.CountLooseAmmoInInventory(pawn, ad);
                int stillNeeded = missingAmmoNeeded[ad] - inInv;
                if (stillNeeded <= 0)
                {
                    missingAmmoNeeded.Remove(ad);
                }
                else
                {
                    missingAmmoNeeded[ad] = stillNeeded;
                }
            }

            return needsWork;
        }

        public static bool IsHostileThreatNear(Pawn pawn, float radius)
        {
            if (pawn == null || !pawn.Spawned || pawn.Map?.attackTargetsCache == null) return false;
            var targets = pawn.Map.attackTargetsCache.GetPotentialTargetsFor(pawn);
            if (targets == null) return false;

            for (int i = 0; i < targets.Count; i++)
            {
                IAttackTarget t = targets[i];
                if (t?.Thing != null && t.Thing.Spawned && !t.ThreatDisabled(pawn) && t.Thing.Position.InHorDistOf(pawn.Position, radius))
                {
                    return true;
                }
            }
            return false;
        }

        public static bool IsEscortOverseerSafeForPacking(Pawn mech, Pawn overseer)
        {
            if (mech == null || overseer == null || overseer.Dead || overseer.Downed || overseer.Drafted) return false;
            if (!overseer.Spawned || overseer.Map != mech.Map) return false;
            if (Find.TickManager.TicksGame - overseer.LastAttackTargetTick < 240) return false;
            if (IsHostileThreatNear(overseer, 35f)) return false;
            return true;
        }

        private void TryDispatchMechanoidAmmoJob(Pawn mech)
        {
            if (!MagazineUtility.IsPlayerControlledMechanoid(mech)) return;
            if (!JobGiver_PackMagazines.CanAttemptPacking(mech)) return;

            var weapons = MagazineUtility.GetManagedWeapons(mech);
            if (weapons.Count == 0) return;

            JobDef curDef = mech.CurJobDef;
            if (curDef == CEMF_DefOf.CEMF_ReloadMagazine
                || curDef == CEMF_DefOf.CEMF_FieldPackAndReload
                || curDef == CE_JobDefOf.ReloadWeapon)
            {
                return;
            }
            if (mech.stances?.FullBodyBusy == true) return;
            if (mech.jobs?.jobQueue != null && mech.jobs.jobQueue.Count > 0) return;

            // Energy / charging / shutdown guard
            if (ModsConfig.BiotechActive)
            {
                if (RestUtility.IsCharging(mech) || curDef == JobDefOf.MechCharge || curDef == JobDefOf.SelfShutdown)
                {
                    return;
                }
                if (mech.needs?.energy != null && (mech.needs.energy.IsLowEnergySelfShutdown || mech.needs.energy.IsSelfShutdown))
                {
                    return;
                }
            }

            // Caravan / Lord duty guard
            if (CaravanFormingUtility.IsFormingCaravan(mech) || mech.GetLord() != null) return;

            // Combat safety guard
            if (Find.TickManager.TicksGame - mech.LastAttackTargetTick < 300) return;
            if (curDef == JobDefOf.AttackStatic
                || curDef == JobDefOf.AttackMelee
                || curDef == JobDefOf.Wait_Combat
                || curDef == JobDefOf.Flee
                || curDef == JobDefOf.FleeAndCower
                || curDef == JobDefOf.FleeAndCowerShort)
            {
                return;
            }
            if (IsHostileThreatNear(mech, 35f)) return;

            // Biotech work mode & current-job interruptibility check
            var settings = CE_MagazineFedMod.Settings;
            MechWorkModeDef workMode = ModsConfig.BiotechActive ? MechanitorUtility.GetMechWorkMode(mech) : null;
            if (ModsConfig.BiotechActive && (workMode == MechWorkModeDefOf.Recharge || workMode == MechWorkModeDefOf.SelfShutdown))
            {
                return;
            }

            bool isIdleOrPatrol = curDef == null
                || curDef == JobDefOf.GotoWander
                || curDef == JobDefOf.Wait_Wander
                || curDef == JobDefOf.Wait
                || curDef == JobDefOf.Wait_MaintainPosture;

            if (ModsConfig.BiotechActive && workMode == MechWorkModeDefOf.Escort)
            {
                if (!(settings?.allowEscortMechPacking ?? true)) return;
                Pawn overseer = MechanitorUtility.GetOverseer(mech);
                if (!IsEscortOverseerSafeForPacking(mech, overseer)) return;

                bool escortIdleNearOverseer = isIdleOrPatrol
                    || ((curDef == JobDefOf.Follow || curDef == JobDefOf.FollowClose)
                        && mech.Position.InHorDistOf(overseer.Position, 12f)
                        && (overseer.pather == null || !overseer.pather.Moving));
                if (!escortIdleNearOverseer) return;
            }
            else
            {
                if (!isIdleOrPatrol)
                {
                    bool canInterruptWork = settings != null
                        && settings.packingPriority == PackingPriorityMode.BeforeWork
                        && mech.CurJob != null
                        && !mech.CurJob.playerForced
                        && curDef.playerInterruptible
                        && mech.carryTracker?.CarriedThing == null
                        && curDef != JobDefOf.BeatFire
                        && curDef != JobDefOf.Rescue
                        && curDef != JobDefOf.TendPatient;
                    if (!canInterruptWork) return;
                }
            }

            // Tier 1: Auto-pack magazines (unless ManualOnly)
            if (settings != null && settings.packingPriority != PackingPriorityMode.ManualOnly)
            {
                if (!JobGiver_PackMagazines.IsOnCooldown(mech))
                {
                    if (JobGiver_PackMagazines.HasActionablePackingWork(mech))
                    {
                        Job packJob = JobMaker.MakeJob(CEMF_DefOf.CEMF_PackMagazines, mech);
                        packJob.playerForced = false;
                        mech.jobs.StartJob(packJob, JobCondition.InterruptForced, null, false, true, null, JobTag.Misc, false, false);
                        return;
                    }
                    if (PawnNeedsPacking(mech, out _))
                    {
                        JobGiver_PackMagazines.NotifyNoProgress(mech);
                    }
                }
            }

            // Tier 2: Undrafted post-combat tactical reload (swap to a fuller magazine / top up single-load tube)
            for (int i = 0; i < weapons.Count; i++)
            {
                ThingWithComps weapon = weapons[i];
                var ca = weapon.TryGetComp<CompAmmoUser>();
                if (ca == null) continue;

                if (MagazineUtility.IsMagazineFedWeapon(ca))
                {
                    if (ca.CurMagCount < ca.MagSize)
                    {
                        var bestMag = MagazineUtility.FindBestLoadedMagazine(mech, ca, minLoadedCountExclusive: ca.CurMagCount);
                        if (bestMag != null)
                        {
                            Job swapJob = JobMaker.MakeJob(CEMF_DefOf.CEMF_ReloadMagazine, mech, weapon, bestMag);
                            swapJob.playerForced = false;
                            mech.jobs.StartJob(swapJob, JobCondition.InterruptForced, null, false, true, null, JobTag.Misc, false, false);
                            return;
                        }
                    }
                }
                else if (MagazineUtility.IsSingleLoadMultiCapacityWeapon(ca))
                {
                    if (ca.CurMagCount < ca.MagSize && ca.TryFindAmmoInInventory(out _))
                    {
                        Job reloadJob = ca.TryMakeReloadJob();
                        if (reloadJob != null)
                        {
                            reloadJob.playerForced = false;
                            mech.jobs.StartJob(reloadJob, JobCondition.InterruptForced, null, false, true, null, JobTag.Misc, false, false);
                            return;
                        }
                    }
                }
            }
        }

        private static void AccumulateMissingAmmo(AmmoPlan plan, List<AmmoDef> currentRounds, Dictionary<AmmoDef, int> needed)
        {
            var planCounts = plan.GetCountsByDef();
            var curCounts = new Dictionary<AmmoDef, int>();
            if (currentRounds != null)
            {
                for (int i = 0; i < currentRounds.Count; i++)
                {
                    var d = currentRounds[i];
                    if (d == null) continue;
                    if (curCounts.TryGetValue(d, out int c)) curCounts[d] = c + 1;
                    else curCounts[d] = 1;
                }
            }

            foreach (var kv in planCounts)
            {
                curCounts.TryGetValue(kv.Key, out int have);
                int diff = kv.Value - have;
                if (diff > 0)
                {
                    if (needed.TryGetValue(kv.Key, out int existing)) needed[kv.Key] = existing + diff;
                    else needed[kv.Key] = diff;
                }
            }
        }

        private void PruneStalePawnData()
        {
            if (pawnDataMap == null || pawnDataMap.Count == 0) return;

            var validIds = new HashSet<int>();
            var allKnown = PawnsFinder.AllMapsWorldAndTemporary_AliveOrDead;
            if (allKnown != null)
            {
                for (int i = 0; i < allKnown.Count; i++)
                {
                    Pawn p = allKnown[i];
                    if (p != null && (!p.Dead || p.Corpse != null))
                    {
                        validIds.Add(p.thingIDNumber);
                    }
                }
            }

            var staleKeys = new List<int>();
            foreach (var kv in pawnDataMap)
            {
                if (kv.Value == null || kv.Value.configs == null || kv.Value.configs.Count == 0 || !validIds.Contains(kv.Key))
                {
                    staleKeys.Add(kv.Key);
                }
            }
            for (int i = 0; i < staleKeys.Count; i++)
            {
                pawnDataMap.Remove(staleKeys[i]);
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                PruneStalePawnData();
            }
            Scribe_Collections.Look(ref pawnDataMap, "pawnDataMap", LookMode.Value, LookMode.Deep, ref scribeKeys, ref scribeValues);
            Scribe_Collections.Look(ref presets, "presets", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (pawnDataMap == null) pawnDataMap = new Dictionary<int, PawnAmmoManagementData>();
                if (presets == null) presets = new List<AmmoPlanPreset>();
                presets.RemoveAll(p => p == null || p.plan == null || p.plan.ammoSet == null);
            }
        }
    }
}
