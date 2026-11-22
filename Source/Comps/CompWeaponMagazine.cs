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
        /// Transient: specific magazine chosen by player from right-click menu to swap in next.
        /// </summary>
        [Unsaved]
        public Thing_CEMagazine pendingSelectedMag;

        public CompAmmoUser CompAmmo => parent.TryGetComp<CompAmmoUser>();

        public AmmoDef NextRound => loadedRounds != null && loadedRounds.Count > 0 ? loadedRounds[0] : CompAmmo?.CurrentAmmo;

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

            if (loadedRounds.Count < target)
            {
                int diff = target - loadedRounds.Count;
                for (int i = 0; i < diff; i++)
                {
                    if (fill != null) loadedRounds.Add(fill);
                }
                if (MagazineUtility.IsMagazineFedWeapon(ca) && target > 0)
                {
                    hasInsertedMagazine = true;
                    insertedMagAmmoSet = ca.CurAmmoSet;
                    insertedMagCapacity = ca.MagSize;
                }
            }
            else if (loadedRounds.Count > target)
            {
                int remove = loadedRounds.Count - target;
                loadedRounds.RemoveRange(0, remove);
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
            holder?.TryGetComp<CompInventory>()?.UpdateInventory();
            return true;
        }

        /// <summary>
        /// Determines the next AmmoDef to load into a single-load multi-capacity weapon (e.g. pump shotgun)
        /// according to the pawn's AmmoPlan, and whether it should be inserted at index 0 (front top-up) or appended.
        /// </summary>
        public bool TryGetNextSingleLoadPlannedAmmo(Pawn pawn, out AmmoDef desiredAmmo, out bool insertAtFront)
        {
            desiredAmmo = null;
            insertAtFront = false;

            var ca = CompAmmo;
            if (ca == null || !MagazineUtility.IsSingleLoadMultiCapacityWeapon(ca)) return false;
            SyncFromCompAmmo();

            if (loadedRounds.Count >= ca.MagSize) return false;

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

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Collections.Look(ref loadedRounds, "loadedRounds", LookMode.Def);
            Scribe_Values.Look(ref hasInsertedMagazine, "hasInsertedMagazine", true);
            Scribe_Values.Look(ref insertedMagPlanIndex, "insertedMagPlanIndex", 0);
            Scribe_Defs.Look(ref insertedMagAmmoSet, "insertedMagAmmoSet");
            Scribe_Values.Look(ref insertedMagCapacity, "insertedMagCapacity", 0);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (loadedRounds == null) loadedRounds = new List<AmmoDef>();
                loadedRounds.RemoveAll(r => r == null);
            }
        }
    }
}
