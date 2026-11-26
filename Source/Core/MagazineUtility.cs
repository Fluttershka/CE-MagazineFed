using System;
using System.Collections.Generic;
using System.Linq;
using CombatExtended;
using RimWorld;
using UnityEngine;
using Verse;

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
                return pawn.IsColonyMech || pawn.Faction == Faction.OfPlayer;
            }

            if (!pawn.RaceProps.Humanlike) return false;

            if (scope == FactionScope.AllFactions)
            {
                return true;
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

            // ~0.4% per round during idle packing (~11% per full 30-rd mag), ~0.8% per round under hurried combat field-packing
            float chance = isFieldPacking ? 0.008f : 0.004f;
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
