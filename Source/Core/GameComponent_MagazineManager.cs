using System;
using System.Collections.Generic;
using System.Linq;
using CombatExtended;
using RimWorld;
using Verse;

namespace CE_MagazineFed
{
    public class GameComponent_MagazineManager : GameComponent
    {
        private Dictionary<int, PawnAmmoManagementData> pawnDataMap = new Dictionary<int, PawnAmmoManagementData>();
        private List<int> scribeKeys;
        private List<PawnAmmoManagementData> scribeValues;

        public List<AmmoPlanPreset> presets = new List<AmmoPlanPreset>();

        public static GameComponent_MagazineManager Instance => Current.Game?.GetComponent<GameComponent_MagazineManager>();

        public GameComponent_MagazineManager(Game game) { }

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
            return data.GetOrCreateConfig(compAmmo.CurAmmoSet, compAmmo.MagSize, isSingle, compAmmo.CurrentAmmo ?? compAmmo.SelectedAmmo);
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
        /// if the player reduced targetTotalMags.
        /// </summary>
        public void EnsureMagazinesForConfig(Pawn pawn, PawnWeaponAmmoConfig config)
        {
            if (pawn == null || config == null || config.isSingleLoad) return;
            if (!(CE_MagazineFedMod.Settings?.autoProvisionEmptyMags ?? true)) return;
            if (!MagazineUtility.ShouldUseMagazineSystem(pawn)) return;

            config.EnsurePlansMatchCount();

            // Check if a matching weapon currently has an inserted magazine
            int insertedPlanIndex = -1;
            int insertedCount = 0;
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
                        insertedCount++;
                        if (cm.insertedMagPlanIndex < 0 || cm.insertedMagPlanIndex >= config.targetTotalMags)
                        {
                            cm.insertedMagPlanIndex = 0;
                        }
                        insertedPlanIndex = cm.insertedMagPlanIndex;
                    }
                }
            }

            var invMags = MagazineUtility.GetMagazinesInInventory(pawn, config.ammoSet, config.capacity);
            int totalOwned = insertedCount + invMags.Count;

            // Normalize preferredPlanIndex assignments among owned magazines
            var usedIndices = new HashSet<int>();
            if (insertedPlanIndex >= 0 && insertedPlanIndex < config.targetTotalMags)
            {
                usedIndices.Add(insertedPlanIndex);
            }
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

            // Provision missing empty magazines
            if (totalOwned < config.targetTotalMags)
            {
                int needed = config.targetTotalMags - totalOwned;
                for (int k = 0; k < needed; k++)
                {
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
                pawn.TryGetComp<CompInventory>()?.UpdateInventory();
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
                if (config == null || !processedConfigs.Add(config)) continue;

                if (!config.isSingleLoad)
                {
                    // Check if we need empty mags provisioned
                    var invMags = MagazineUtility.GetMagazinesInInventory(pawn, config.ammoSet, config.capacity);
                    int inGun = (compMag != null && compMag.hasInsertedMagazine) ? 1 : 0;
                    if ((CE_MagazineFedMod.Settings?.autoProvisionEmptyMags ?? true) && (invMags.Count + inGun) < config.targetTotalMags)
                    {
                        needsWork = true;
                    }

                    // Check each inventory mag against its plan
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

                    // Also check if gun's inserted magazine doesn't match its plan (e.g. partially fired)
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
                    // Ensure pawn carries enough planned ammo in inventory to top up the tube if needed
                    if (compMag != null)
                    {
                        compMag.SyncFromCompAmmo();
                        var plan = config.GetPlan(0);
                        if (!plan.MatchesExactly(compMag.loadedRounds))
                        {
                            AccumulateMissingAmmo(plan, compMag.loadedRounds, missingAmmoNeeded);
                            if (missingAmmoNeeded.Count > 0)
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

        public override void ExposeData()
        {
            base.ExposeData();
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
