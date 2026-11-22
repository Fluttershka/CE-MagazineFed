using System;
using System.Collections.Generic;
using System.Linq;
using CombatExtended;
using RimWorld;
using UnityEngine;
using Verse;

namespace CE_MagazineFed
{
    public static class MagazineUtility
    {
        private static readonly Dictionary<string, Color> CategoryColorCache = new Dictionary<string, Color>();

        public static bool ShouldUseMagazineSystem(Pawn pawn)
        {
            if (pawn == null || pawn.Dead) return false;
            if (pawn.inventory == null || pawn.inventory.innerContainer == null) return false;
            if (pawn.TryGetComp<CompInventory>() == null) return false;

            var scope = CE_MagazineFedMod.Settings?.factionScope ?? FactionScope.PlayerOnly;
            if (scope == FactionScope.AllFactions)
            {
                return pawn.RaceProps != null && (pawn.RaceProps.Humanlike || pawn.RaceProps.IsMechanoid);
            }
            return pawn.IsColonistPlayerControlled || pawn.Faction == Faction.OfPlayer;
        }

        public static bool ShouldUseMagazineSystem(CompAmmoUser compAmmo)
        {
            if (compAmmo == null) return false;
            if (compAmmo.turret != null) return false;
            if (compAmmo.parent is Building_AutoloaderCE) return false;
            return ShouldUseMagazineSystem(compAmmo.Holder);
        }

        /// <summary>
        /// True if this weapon uses detachable magazines (MagSize > 1, not reloadOneAtATime, standard single-round AmmoDefs).
        /// </summary>
        public static bool IsMagazineFedWeapon(CompAmmoUser compAmmo)
        {
            if (compAmmo == null || !compAmmo.UseAmmo) return false;
            if (compAmmo.turret != null || compAmmo.parent is Building_AutoloaderCE) return false;
            if (compAmmo.Props == null || compAmmo.Props.reloadOneAtATime) return false;
            if (compAmmo.MagSize <= 1) return false;
            if (compAmmo.CurAmmoSet?.ammoTypes == null || compAmmo.CurAmmoSet.ammoTypes.Count == 0) return false;

            // Exclude special multi-charge packs / fuel canisters (e.g. flamethrower tanks, reusable packs)
            var types = compAmmo.CurAmmoSet.ammoTypes;
            for (int i = 0; i < types.Count; i++)
            {
                var ad = types[i].ammo;
                if (ad == null) continue;
                if (ad.ammoCount > 1 || ad.HasComp(typeof(CompApparelReloadable)))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// True if this weapon reloads one round at a time into a multi-round internal magazine/tube/cylinder
        /// (e.g., pump shotgun, revolver, lever-action, internal-mag bolt action). Requirement #5.
        /// </summary>
        public static bool IsSingleLoadMultiCapacityWeapon(CompAmmoUser compAmmo)
        {
            if (compAmmo == null || !compAmmo.UseAmmo) return false;
            if (compAmmo.turret != null || compAmmo.parent is Building_AutoloaderCE) return false;
            if (compAmmo.Props == null || !compAmmo.Props.reloadOneAtATime) return false;
            if (compAmmo.MagSize <= 1) return false;
            if (compAmmo.CurAmmoSet?.ammoTypes == null || compAmmo.CurAmmoSet.ammoTypes.Count == 0) return false;

            var types = compAmmo.CurAmmoSet.ammoTypes;
            for (int i = 0; i < types.Count; i++)
            {
                var ad = types[i].ammo;
                if (ad == null) continue;
                if (ad.ammoCount > 1 || ad.HasComp(typeof(CompApparelReloadable)))
                {
                    return false;
                }
            }
            return true;
        }

        public static bool IsManagedWeapon(CompAmmoUser compAmmo)
        {
            return IsMagazineFedWeapon(compAmmo) || IsSingleLoadMultiCapacityWeapon(compAmmo);
        }

        /// <summary>
        /// True if the pawn's currently equipped primary weapon is a magazine-fed weapon.
        /// </summary>
        public static bool HasEquippedMagFedWeapon(Pawn pawn)
        {
            var primary = pawn?.equipment?.Primary;
            if (primary == null) return false;
            return IsMagazineFedWeapon(primary.TryGetComp<CompAmmoUser>());
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

        public static Thing_CEMagazine FindBestLoadedMagazine(Pawn pawn, AmmoSetDef ammoSet, int capacity, AmmoDef preferredAmmo = null)
        {
            var loaded = GetLoadedMagazinesInInventory(pawn, ammoSet, capacity);
            if (loaded.Count == 0) return null;

            if (preferredAmmo != null)
            {
                // Prefer a mag whose top round or majority matches preferredAmmo, weighted by fullness
                Thing_CEMagazine best = null;
                int bestScore = int.MinValue;
                for (int i = 0; i < loaded.Count; i++)
                {
                    var m = loaded[i];
                    int score = m.LoadedCount * 10;
                    if (m.NextRound == preferredAmmo) score += 200;
                    if (m.IsFull) score += 50;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = m;
                    }
                }
                return best;
            }
            return loaded[0];
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

                // Pack into full magazines (plus a final partial mag if any left)
                int idx = 0;
                int magSlot = 0;
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

        public static Color GetAmmoColor(AmmoDef def)
        {
            if (def == null) return new Color(0.35f, 0.35f, 0.38f);
            string key = def.ammoClass?.defName ?? def.defName;
            if (CategoryColorCache.TryGetValue(key, out Color cached)) return cached;

            string u = key.ToUpperInvariant();
            Color col;
            if (u.Contains("FULLMETALJACKET") || u.Contains("FMJ") || u.Contains("BALL"))
                col = new Color(0.84f, 0.67f, 0.27f); // Brass Gold
            else if (u.Contains("ARMORPIERCING") || u.Contains("AP_I") || u.Contains("API") || u.Contains("INCENDIARYAP"))
                col = new Color(0.28f, 0.64f, 0.92f); // Steel Blue
            else if (u.Contains("SABOT"))
                col = new Color(0.68f, 0.42f, 0.95f); // Violet
            else if (u.Contains("HOLLOWPOINT") || u.Contains("HP") || u.Contains("SOFTPOINT"))
                col = new Color(0.90f, 0.44f, 0.24f); // Copper Orange
            else if (u.Contains("INCENDIARY") || u.Contains("THERMITE") || u.Contains("PROMETHEUM") || u.Contains("DRAGON"))
                col = new Color(0.95f, 0.26f, 0.20f); // Flame Red
            else if (u.Contains("HIGHEXPLOSIVE") || u.Contains("HE") || u.Contains("FRAG") || u.Contains("AIRBURST"))
                col = new Color(0.95f, 0.56f, 0.14f); // Explosive Amber
            else if (u.Contains("EMP") || u.Contains("ION"))
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
            if (u.Contains("API") || u.Contains("AP_I") || u.Contains("INCENDIARY") && u.Contains("ARMOR"))
                col = new Color(0.85f, 0.28f, 0.52f); // AP-I Magenta-Red
            else if (u.Contains("APHE") || u.Contains("AP_HE") || (u.Contains("ARMOR") && u.Contains("EXPLOSIVE")))
                col = new Color(0.76f, 0.36f, 0.78f); // AP-HE Purple-Pink

            CategoryColorCache[key] = col;
            return col;
        }
    }
}
