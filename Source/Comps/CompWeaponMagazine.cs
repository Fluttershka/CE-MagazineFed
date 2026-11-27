using System;
using System.Collections.Generic;
using System.Linq;
using CombatExtended;
using RimWorld;
using Verse;

namespace CE_MagazineFed
{
    public class CompProperties_WeaponMagazine : CompProperties
    {
        public CompProperties_WeaponMagazine()
        {
            compClass = typeof(CompWeaponMagazine);
        }
    }

    /// <summary>
    /// Attached to all CE weapons (with CompAmmoUser).
    /// Tracks the exact ordered rounds loaded in the weapon's magazine or internal tube/cylinder,
    /// plus whether a physical magazine is currently inserted into the magwell.
    /// </summary>
    public class CompWeaponMagazine : ThingComp
    {
        /// <summary>
        /// Exact ordered rounds currently loaded in the weapon. Index 0 = next round to fire.
        /// </summary>
        public List<AmmoDef> loadedRounds = new List<AmmoDef>();

        /// <summary>
        /// For mag-fed weapons: whether a magazine body is currently seated in the gun.
        /// </summary>
        public bool hasInsertedMagazine = true;

        /// <summary>
        /// Remembers which plan slot (0..N-1) the currently inserted magazine belonged to.
        /// </summary>
        public int insertedMagPlanIndex = 0;

        public AmmoSetDef insertedMagAmmoSet;
        public int insertedMagCapacity;

        /// <summary>
        /// Persistent manual preference set when player right-click picks a specific magazine.
        /// Guides subsequent auto-reloads toward the same recipe or dominant ammo type until cleared.
        /// </summary>
        public bool hasManualMagPref = false;
        public List<AmmoDef> manualPrefAmmoDefs = new List<AmmoDef>();
        public List<int> manualPrefAmmoCounts = new List<int>();

        /// <summary>
        /// Persistent temporary ammo override for SingleLoad multi-capacity weapons via Reload right-click menu.
        /// Cleared manually ("Restore preset plan") or automatically on out-of-combat PackMagazines.
        /// </summary>
        public AmmoDef tempSingleLoadAmmo;

        /// <summary>
        /// Transient: specific magazine chosen by player from right-click menu to swap in next.
        /// </summary>
        [Unsaved]
        public Thing_CEMagazine pendingSelectedMag;

        /// <summary>
        /// Transient: true when `pendingSelectedMag` was manually chosen from the right-click menu.
        /// </summary>
        [Unsaved]
        public bool pendingManualMagChoice;

        /// <summary>
        /// Transient: whether the next reload trigger should perform a timed magazine ejection only.
        /// </summary>
        [Unsaved]
        public bool pendingEjectOnly;

        /// <summary>
        /// Transient: specific ammo chosen by player for a tactical Port Load on SingleLoad weapons.
        /// </summary>
        [Unsaved]
        public AmmoDef pendingPortLoadAmmo;

        public CompAmmoUser CompAmmo => parent.TryGetComp<CompAmmoUser>();

        public AmmoDef NextRound => loadedRounds != null && loadedRounds.Count > 0 ? loadedRounds[0] : CompAmmo?.CurrentAmmo;

        public override void PostPostMake()
        {
            base.PostPostMake();
            var ca = CompAmmo;
            bool hasRounds = ca != null && ca.CurMagCount > 0;
            hasInsertedMagazine = hasRounds || (CE_MagazineFedMod.Settings?.autoProvisionEmptyMags ?? true);
            if (hasInsertedMagazine && ca != null)
            {
                insertedMagAmmoSet = ca.CurAmmoSet;
                insertedMagCapacity = ca.MagSize;
            }
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            SyncFromCompAmmo();
        }

        /// <summary>
        /// Reconciles `loadedRounds` with `CompAmmoUser.CurMagCount` in case an external mod,
        /// mid-save load, weapon generator, or dev-mode command changed CurMagCount directly.
        /// </summary>
        public void SyncFromCompAmmo()
        {
            var ca = CompAmmo;
            if (ca == null || !ca.UseAmmo || !ca.HasMagazine) return;
            if (loadedRounds == null) loadedRounds = new List<AmmoDef>();

            int target = ca.CurMagCount;
            AmmoDef fill = ca.CurrentAmmo ?? ca.SelectedAmmo ?? ca.CurAmmoSet?.ammoTypes?.FirstOrDefault()?.ammo;

            // Sanitize any stale rounds that no longer belong to CurAmmoSet (e.g. caliber/underbarrel switch)
            if (ca.CurAmmoSet?.ammoTypes != null && fill != null)
            {
                for (int i = 0; i < loadedRounds.Count; i++)
                {
                    AmmoDef r = loadedRounds[i];
                    if (r == null || !IsValidForSet(ca.CurAmmoSet, r))
                    {
                        loadedRounds[i] = fill;
                    }
                }
            }

            if (loadedRounds.Count < target)
            {
                int diff = target - loadedRounds.Count;
                for (int i = 0; i < diff; i++)
                {
                    if (fill != null) loadedRounds.Add(fill);
                }
            }
            else if (loadedRounds.Count > target)
            {
                int remove = loadedRounds.Count - target;
                loadedRounds.RemoveRange(0, remove);
            }

            if (MagazineUtility.IsMagazineFedWeapon(ca) && loadedRounds.Count > 0 && !hasInsertedMagazine)
            {
                hasInsertedMagazine = true;
                insertedMagAmmoSet = ca.CurAmmoSet;
                insertedMagCapacity = ca.MagSize;
            }

            if (loadedRounds.Count > 0 && loadedRounds[0] != null && ca.CurrentAmmo != loadedRounds[0])
            {
                ca.CurrentAmmo = loadedRounds[0];
            }
        }

        /// <summary>
        /// Pushes `loadedRounds` state into `CompAmmoUser.CurMagCount` and `CompAmmoUser.CurrentAmmo`.
        /// </summary>
        public void PushToCompAmmo()
        {
            var ca = CompAmmo;
            if (ca == null) return;
            if (loadedRounds == null) loadedRounds = new List<AmmoDef>();

            if (loadedRounds.Count > 0 && loadedRounds[0] != null)
            {
                ca.CurrentAmmo = loadedRounds[0];
            }
            ca.CurMagCount = loadedRounds.Count;
        }

        /// <summary>
        /// Consumes `count` rounds from the front of `loadedRounds` after firing.
        /// </summary>
        public void ConsumeFiredRounds(int count)
        {
            if (loadedRounds == null) loadedRounds = new List<AmmoDef>();
            if (count <= 0) count = 1;
            int remove = Math.Min(count, loadedRounds.Count);
            if (remove > 0)
            {
                loadedRounds.RemoveRange(0, remove);
            }

            var ca = CompAmmo;
            if (ca != null && loadedRounds.Count > 0 && loadedRounds[0] != null)
            {
                ca.CurrentAmmo = loadedRounds[0];
            }
        }

        /// <summary>
        /// Ejects the currently inserted magazine as a physical Thing_CEMagazine into pawn inventory (or ground).
        /// </summary>
        public Thing_CEMagazine EjectInsertedMagazine(Pawn holder)
        {
            var ca = CompAmmo;
            if (ca == null || !MagazineUtility.IsMagazineFedWeapon(ca)) return null;
            SyncFromCompAmmo();

            if (!hasInsertedMagazine && loadedRounds.Count == 0) return null;

            AmmoSetDef set = insertedMagAmmoSet ?? ca.CurAmmoSet;
            int cap = insertedMagCapacity > 0 ? insertedMagCapacity : ca.MagSize;
            var mag = Thing_CEMagazine.Create(set, cap, insertedMagPlanIndex);
            mag.rounds.AddRange(loadedRounds);

            loadedRounds.Clear();
            hasInsertedMagazine = false;
            ca.CurMagCount = 0;

            if (holder != null)
            {
                bool added = holder.inventory?.innerContainer != null && holder.inventory.innerContainer.TryAdd(mag, true);
                if (!added && holder.MapHeld != null)
                {
                    GenPlace.TryPlaceThing(mag, holder.PositionHeld, holder.MapHeld, ThingPlaceMode.Near);
                }
                holder.TryGetComp<CompInventory>()?.UpdateInventory();
            }
            else if (parent.MapHeld != null)
            {
                GenPlace.TryPlaceThing(mag, parent.PositionHeld, parent.MapHeld, ThingPlaceMode.Near);
            }

            return mag;
        }

        /// <summary>
        /// Inserts a physical Thing_CEMagazine from inventory into this weapon, destroying the inventory entity.
        /// </summary>
        public bool InsertMagazine(Thing_CEMagazine mag, Pawn holder)
        {
            var ca = CompAmmo;
            if (ca == null || mag == null || mag.Destroyed) return false;
            if (!mag.Matches(ca.CurAmmoSet, ca.MagSize)) return false;

            // Eject any currently seated magazine first
            if (hasInsertedMagazine || loadedRounds.Count > 0)
            {
                EjectInsertedMagazine(holder);
            }

            loadedRounds.Clear();
            loadedRounds.AddRange(mag.rounds);
            hasInsertedMagazine = true;
            insertedMagPlanIndex = mag.preferredPlanIndex >= 0 ? mag.preferredPlanIndex : 0;
            insertedMagAmmoSet = mag.ammoSet;
            insertedMagCapacity = mag.capacity;

            // Remove magazine entity from inventory/map since it now lives inside the weapon
            if (mag.holdingOwner != null)
            {
                mag.holdingOwner.Remove(mag);
            }
            if (!mag.Destroyed)
            {
                mag.Destroy(DestroyMode.Vanish);
            }

            PushToCompAmmo();
            AmmoDef dom = MagazineUtility.GetPrimaryDominantAmmo(loadedRounds);
            if (dom != null)
            {
                ca.SelectedAmmo = dom;
            }
            holder?.TryGetComp<CompInventory>()?.UpdateInventory();
            return true;
        }

        public void SetManualMagPreference(Pawn holder, Thing_CEMagazine mag)
        {
            if (mag == null)
            {
                ClearManualMagPreference();
                return;
            }

            if (manualPrefAmmoDefs == null) manualPrefAmmoDefs = new List<AmmoDef>();
            if (manualPrefAmmoCounts == null) manualPrefAmmoCounts = new List<int>();
            manualPrefAmmoDefs.Clear();
            manualPrefAmmoCounts.Clear();

            var comp = MagazineUtility.ResolveMagazineComposition(holder, mag);
            foreach (var kv in comp)
            {
                if (kv.Key != null && kv.Value > 0)
                {
                    manualPrefAmmoDefs.Add(kv.Key);
                    manualPrefAmmoCounts.Add(kv.Value);
                }
            }

            hasManualMagPref = manualPrefAmmoDefs.Count > 0;
            var ca = CompAmmo;
            if (ca != null)
            {
                AmmoDef dom = MagazineUtility.GetPrimaryDominantAmmo(mag.rounds) ?? mag.NextRound;
                if (dom != null)
                {
                    ca.SelectedAmmo = dom;
                }
            }
        }

        public void ClearManualMagPreference()
        {
            hasManualMagPref = false;
            manualPrefAmmoDefs?.Clear();
            manualPrefAmmoCounts?.Clear();
        }

        public Dictionary<AmmoDef, int> GetManualPrefComposition()
        {
            if (!hasManualMagPref || manualPrefAmmoDefs == null || manualPrefAmmoCounts == null || manualPrefAmmoDefs.Count == 0)
            {
                return null;
            }

            var dict = new Dictionary<AmmoDef, int>();
            int n = Math.Min(manualPrefAmmoDefs.Count, manualPrefAmmoCounts.Count);
            for (int i = 0; i < n; i++)
            {
                AmmoDef def = manualPrefAmmoDefs[i];
                int cnt = manualPrefAmmoCounts[i];
                if (def != null && cnt > 0)
                {
                    if (dict.TryGetValue(def, out int existing)) dict[def] = existing + cnt;
                    else dict[def] = cnt;
                }
            }
            return dict.Count > 0 ? dict : null;
        }

        public string GetManualPrefCompactSummary()
        {
            var comp = GetManualPrefComposition();
            if (comp == null || comp.Count == 0) return "";
            if (comp.Count == 1)
            {
                return MagazineUtility.GetShortAmmoLabel(comp.First().Key);
            }
            return string.Join("+", comp.OrderByDescending(kv => kv.Value).Take(3).Select(kv => $"{kv.Value}{MagazineUtility.GetShortAmmoLabel(kv.Key)}"));
        }

        public void ClearTempSingleLoadAmmo()
        {
            tempSingleLoadAmmo = null;
        }

        /// <summary>
        /// Determines the next AmmoDef to load into a single-load multi-capacity weapon (e.g. pump shotgun)
        /// according to the pawn's AmmoPlan (or active right-click tactical override), and whether it should be inserted at index 0.
        /// </summary>
        public bool TryGetNextSingleLoadPlannedAmmo(Pawn pawn, out AmmoDef desiredAmmo, out bool insertAtFront)
        {
            desiredAmmo = null;
            insertAtFront = false;

            var ca = CompAmmo;
            if (ca == null || !MagazineUtility.IsSingleLoadMultiCapacityWeapon(ca)) return false;
            // Only player-controlled / colony pawns have custom mixed-ammo plans; avoid creating save entries for transient NPCs
            if (pawn == null || (pawn.Faction != Faction.OfPlayer && !pawn.IsColonist && !pawn.IsColonyMech)) return false;
            SyncFromCompAmmo();

            if (loadedRounds.Count >= ca.MagSize) return false;

            if (tempSingleLoadAmmo != null)
            {
                if (!IsValidForSet(ca.CurAmmoSet, tempSingleLoadAmmo))
                {
                    tempSingleLoadAmmo = null;
                }
                else if (MagazineUtility.CountLooseAmmoInInventory(pawn, tempSingleLoadAmmo) > 0)
                {
                    desiredAmmo = tempSingleLoadAmmo;
                    insertAtFront = false;
                    return true;
                }
            }

            var mgr = GameComponent_MagazineManager.Instance;
            var config = mgr?.GetOrCreateConfig(pawn, ca);
            if (config == null) return false;

            var plan = config.GetPlan(0);
            plan.Normalize();

            if (loadedRounds.Count == 0 || plan.IsPrefixMatch(loadedRounds))
            {
                // Filling from empty or continuing a prefix: append next slot
                desiredAmmo = plan.slots[loadedRounds.Count];
                insertAtFront = false;
                return desiredAmmo != null;
            }

            if (plan.IsSuffixMatch(loadedRounds))
            {
                // Normal post-fire top-up: missing front rounds are indices 0..(missing-1),
                // push index (missing-1) to front so full sequence restores exact firing order!
                int missing = plan.capacity - loadedRounds.Count;
                desiredAmmo = plan.slots[missing - 1];
                insertAtFront = true;
                return desiredAmmo != null;
            }

            // Fallback: use slot at current count
            int idx = Math.Min(loadedRounds.Count, plan.capacity - 1);
            desiredAmmo = plan.slots[idx];
            insertAtFront = false;
            return desiredAmmo != null;
        }

        public string GetLoadedBreakdownSummary()
        {
            SyncFromCompAmmo();
            if (loadedRounds == null || loadedRounds.Count == 0) return "CEMF_MagEmpty".Translate();
            var dict = new Dictionary<AmmoDef, int>();
            for (int i = 0; i < loadedRounds.Count; i++)
            {
                var ad = loadedRounds[i];
                if (ad == null) continue;
                if (dict.TryGetValue(ad, out int cur)) dict[ad] = cur + 1;
                else dict[ad] = 1;
            }
            return string.Join(", ", dict.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value}x {MagazineUtility.GetShortAmmoLabel(kv.Key)}"));
        }

        private static bool IsValidForSet(AmmoSetDef set, AmmoDef def)
        {
            if (set?.ammoTypes == null || def == null) return false;
            for (int i = 0; i < set.ammoTypes.Count; i++)
            {
                if (set.ammoTypes[i].ammo == def) return true;
            }
            return false;
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Collections.Look(ref loadedRounds, "loadedRounds", LookMode.Def);
            Scribe_Values.Look(ref hasInsertedMagazine, "hasInsertedMagazine", true, true);
            Scribe_Values.Look(ref insertedMagPlanIndex, "insertedMagPlanIndex", 0);
            Scribe_Defs.Look(ref insertedMagAmmoSet, "insertedMagAmmoSet");
            Scribe_Values.Look(ref insertedMagCapacity, "insertedMagCapacity", 0);
            Scribe_Values.Look(ref hasManualMagPref, "hasManualMagPref", false);
            Scribe_Collections.Look(ref manualPrefAmmoDefs, "manualPrefAmmoDefs", LookMode.Def);
            Scribe_Collections.Look(ref manualPrefAmmoCounts, "manualPrefAmmoCounts", LookMode.Value);
            Scribe_Defs.Look(ref tempSingleLoadAmmo, "tempSingleLoadAmmo");

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (loadedRounds == null) loadedRounds = new List<AmmoDef>();
                loadedRounds.RemoveAll(r => r == null);

                if (manualPrefAmmoDefs == null) manualPrefAmmoDefs = new List<AmmoDef>();
                if (manualPrefAmmoCounts == null) manualPrefAmmoCounts = new List<int>();
                int pairCount = Math.Min(manualPrefAmmoDefs.Count, manualPrefAmmoCounts.Count);
                var cleanDefs = new List<AmmoDef>(pairCount);
                var cleanCounts = new List<int>(pairCount);
                for (int i = 0; i < pairCount; i++)
                {
                    if (manualPrefAmmoDefs[i] != null && manualPrefAmmoCounts[i] > 0)
                    {
                        cleanDefs.Add(manualPrefAmmoDefs[i]);
                        cleanCounts.Add(manualPrefAmmoCounts[i]);
                    }
                }
                manualPrefAmmoDefs = cleanDefs;
                manualPrefAmmoCounts = cleanCounts;
                if (manualPrefAmmoDefs.Count == 0)
                {
                    hasManualMagPref = false;
                }
            }
        }
    }
}
