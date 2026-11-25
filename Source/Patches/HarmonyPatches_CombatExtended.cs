using System;
using System.Collections.Generic;
using System.Linq;
using CombatExtended;
using CombatExtended.AI;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace CE_MagazineFed
{
    internal static class CE_Reflection
    {
        public static readonly AccessTools.FieldRef<CompAmmoUser, int> LastReloadJobTick =
            AccessTools.FieldRefAccess<CompAmmoUser, int>("_lastReloadJobTick");

        public static readonly FastInvokeHandler DoOutOfAmmoAction =
            MethodInvoker.GetHandler(AccessTools.Method(typeof(CompAmmoUser), "DoOutOfAmmoAction"));

        public static readonly AccessTools.FieldRef<CompAmmoUser, AmmoDef> CurrentAmmoInt =
            AccessTools.FieldRefAccess<CompAmmoUser, AmmoDef>("currentAmmoInt");

        public static readonly System.Reflection.FieldInfo RgStanceField =
            AccessTools.Field(typeof(CompAmmoUser), "rgStance");

        public static Type GetRgStance() => RgStanceField?.GetValue(null) as Type;
    }

    // =========================================================================
    // 1. Shooting: sync mixed ammo order before shot & pop after shot
    // =========================================================================

    [HarmonyPatch(typeof(CompAmmoUser), nameof(CompAmmoUser.TryPrepareShot))]
    public static class Patch_CompAmmoUser_TryPrepareShot
    {
        public static void Prefix(CompAmmoUser __instance)
        {
            if (!MagazineUtility.IsManagedWeapon(__instance)) return;
            var cm = __instance.parent.TryGetComp<CompWeaponMagazine>();
            if (cm == null) return;
            cm.SyncFromCompAmmo();
            if (cm.loadedRounds.Count > 0 && cm.loadedRounds[0] != null)
            {
                __instance.CurrentAmmo = cm.loadedRounds[0];
            }
        }
    }

    [HarmonyPatch(typeof(CompAmmoUser), nameof(CompAmmoUser.Notify_ShotFired))]
    public static class Patch_CompAmmoUser_Notify_ShotFired
    {
        public static void Postfix(CompAmmoUser __instance, int ammoConsumedPerShot)
        {
            if (!MagazineUtility.IsManagedWeapon(__instance)) return;
            var cm = __instance.parent.TryGetComp<CompWeaponMagazine>();
            if (cm == null) return;
            cm.ConsumeFiredRounds(ammoConsumedPerShot <= 0 ? 1 : ammoConsumedPerShot);
        }
    }

    [HarmonyPatch(typeof(CompAmmoUser), nameof(CompAmmoUser.ResetAmmoCount))]
    public static class Patch_CompAmmoUser_ResetAmmoCount
    {
        public static void Postfix(CompAmmoUser __instance)
        {
            var cm = __instance.parent.TryGetComp<CompWeaponMagazine>();
            if (cm == null || !__instance.UseAmmo || !__instance.HasMagazine) return;
            cm.loadedRounds.Clear();
            AmmoDef fill = __instance.CurrentAmmo ?? __instance.SelectedAmmo ?? __instance.CurAmmoSet?.ammoTypes?.FirstOrDefault()?.ammo;
            if (fill != null)
            {
                for (int i = 0; i < __instance.CurMagCount; i++)
                {
                    cm.loadedRounds.Add(fill);
                }
            }
            if (MagazineUtility.IsMagazineFedWeapon(__instance))
            {
                cm.hasInsertedMagazine = true;
                cm.insertedMagAmmoSet = __instance.CurAmmoSet;
                cm.insertedMagCapacity = __instance.MagSize;
            }
        }
    }

    // =========================================================================
    // 2. Ammo availability & FullMagazine checks for mag-fed and mixed weapons
    // =========================================================================

    [HarmonyPatch(typeof(CompAmmoUser), nameof(CompAmmoUser.HasAmmo), MethodType.Getter)]
    public static class Patch_CompAmmoUser_HasAmmo
    {
        public static bool Prefix(CompAmmoUser __instance, ref bool __result)
        {
            if (!MagazineUtility.IsMagazineFedWeapon(__instance) || !MagazineUtility.ShouldUseMagazineSystem(__instance))
            {
                return true;
            }
            Pawn holder = __instance.Holder;
            __result = MagazineUtility.HasLoadedMagazineInInventory(holder, __instance.CurAmmoSet, __instance.MagSize)
                       || MagazineUtility.HasCompatibleLooseAmmoInInventory(holder, __instance.CurAmmoSet);
            return false;
        }
    }

    [HarmonyPatch(typeof(CompAmmoUser), nameof(CompAmmoUser.MagsLeft), MethodType.Getter)]
    public static class Patch_CompAmmoUser_MagsLeft
    {
        public static bool Prefix(CompAmmoUser __instance, ref int __result)
        {
            if (!MagazineUtility.IsMagazineFedWeapon(__instance) || !MagazineUtility.ShouldUseMagazineSystem(__instance))
            {
                return true;
            }
            __result = MagazineUtility.TotalRoundsInInventoryMagazines(__instance.Holder, __instance.CurAmmoSet, __instance.MagSize);
            return false;
        }
    }

    [HarmonyPatch(typeof(CompAmmoUser), nameof(CompAmmoUser.MagsLeftReadOnly), MethodType.Getter)]
    public static class Patch_CompAmmoUser_MagsLeftReadOnly
    {
        public static bool Prefix(CompAmmoUser __instance, ref int __result)
        {
            if (!MagazineUtility.IsMagazineFedWeapon(__instance) || !MagazineUtility.ShouldUseMagazineSystem(__instance))
            {
                return true;
            }
            __result = MagazineUtility.TotalRoundsInInventoryMagazines(__instance.Holder, __instance.CurAmmoSet, __instance.MagSize);
            return false;
        }
    }

    [HarmonyPatch(typeof(CompAmmoUser), nameof(CompAmmoUser.FullMagazine), MethodType.Getter)]
    public static class Patch_CompAmmoUser_FullMagazine
    {
        public static bool Prefix(CompAmmoUser __instance, ref bool __result)
        {
            if (!MagazineUtility.IsManagedWeapon(__instance) || !MagazineUtility.ShouldUseMagazineSystem(__instance))
            {
                return true;
            }
            // Mixed magazines / mixed single-load tubes can have CurrentAmmo != SelectedAmmo while completely full!
            __result = __instance.HasMagazine && __instance.CurMagCount >= __instance.MagSize;
            return false;
        }
    }

    // =========================================================================
    // 3. Unloading: eject magazine intact (mag-fed) OR unload mixed rounds accurately (single-load)
    // =========================================================================

    [HarmonyPatch(typeof(CompAmmoUser), nameof(CompAmmoUser.TryUnload), new[] { typeof(Thing), typeof(bool) }, new[] { ArgumentType.Out, ArgumentType.Normal })]
    public static class Patch_CompAmmoUser_TryUnload
    {
        public static bool Prefix(CompAmmoUser __instance, out Thing droppedAmmo, bool forceUnload, ref bool __result)
        {
            droppedAmmo = null;
            if (!MagazineUtility.ShouldUseMagazineSystem(__instance)) return true;

            var cm = __instance.parent.TryGetComp<CompWeaponMagazine>();
            if (cm == null) return true;

            // Case A: Magazine-fed weapon -> eject physical magazine intact!
            if (MagazineUtility.IsMagazineFedWeapon(__instance))
            {
                if (!__instance.HasMagazine || __instance.Holder == null)
                {
                    __result = false;
                    return false;
                }
                if (cm.hasInsertedMagazine || __instance.CurMagCount > 0)
                {
                    cm.EjectInsertedMagazine(__instance.Holder);
                }
                __result = true;
                return false;
            }

            // Case B: Single-load multi-capacity weapon -> unload every distinct AmmoDef in loadedRounds accurately
            if (MagazineUtility.IsSingleLoadMultiCapacityWeapon(__instance))
            {
                if (!__instance.HasMagazine || __instance.Holder == null)
                {
                    __result = false;
                    return false;
                }
                if (!__instance.UseAmmo || __instance.CurMagCount == 0)
                {
                    __result = true;
                    return false;
                }
                if (!forceUnload && __instance.turret == null)
                {
                    // During normal top-up reload, do NOT unload existing shells from pump shotgun tube!
                    __result = true;
                    return false;
                }

                cm.SyncFromCompAmmo();
                var counts = new Dictionary<AmmoDef, int>();
                for (int i = 0; i < cm.loadedRounds.Count; i++)
                {
                    var ad = cm.loadedRounds[i];
                    if (ad == null) continue;
                    if (counts.TryGetValue(ad, out int cur)) counts[ad] = cur + 1;
                    else counts[ad] = 1;
                }
                cm.loadedRounds.Clear();
                __instance.CurMagCount = 0;

                foreach (var kv in counts)
                {
                    MagazineUtility.AddOrDropLooseAmmo(__instance.Holder, kv.Key, kv.Value);
                }
                __result = true;
                return false;
            }

            return true;
        }
    }

    // =========================================================================
    // 4. Reload Dispatch: Mag Swap vs Field-Pack (Requirement A4) vs Single-Load Plan (#5)
    // =========================================================================

    [HarmonyPatch(typeof(CompAmmoUser), nameof(CompAmmoUser.TryMakeReloadJob))]
    public static class Patch_CompAmmoUser_TryMakeReloadJob
    {
        public static bool Prefix(CompAmmoUser __instance, ref Job __result)
        {
            if (!MagazineUtility.IsMagazineFedWeapon(__instance) || !MagazineUtility.ShouldUseMagazineSystem(__instance))
            {
                return true;
            }
            Pawn holder = __instance.Holder;
            if (holder == null)
            {
                __result = null;
                return false;
            }

            var cm = __instance.parent.TryGetComp<CompWeaponMagazine>();
            Thing_CEMagazine chosen = cm?.pendingSelectedMag;
            if (chosen != null && (chosen.Destroyed || chosen.IsEmpty || !holder.inventory.innerContainer.Contains(chosen)))
            {
                chosen = null;
            }
            if (chosen == null)
            {
                chosen = MagazineUtility.FindBestLoadedMagazine(holder, __instance.CurAmmoSet, __instance.MagSize, __instance.SelectedAmmo);
            }

            if (chosen != null)
            {
                __result = JobMaker.MakeJob(CEMF_DefOf.CEMF_ReloadMagazine, holder, __instance.parent, chosen);
                return false;
            }

            // No loaded magazine available -> if loose ammo exists, use slow field-packing (A4)!
            if (MagazineUtility.HasCompatibleLooseAmmoInInventory(holder, __instance.CurAmmoSet))
            {
                __result = JobMaker.MakeJob(CEMF_DefOf.CEMF_FieldPackAndReload, holder, __instance.parent);
                return false;
            }

            __result = null;
            return false;
        }
    }

    [HarmonyPatch(typeof(CompAmmoUser), nameof(CompAmmoUser.TryStartReload))]
    public static class Patch_CompAmmoUser_TryStartReload
    {
        public static bool Prefix(CompAmmoUser __instance)
        {
            if (!MagazineUtility.IsMagazineFedWeapon(__instance) || !MagazineUtility.ShouldUseMagazineSystem(__instance))
            {
                return true;
            }
            Pawn wielder = __instance.Wielder;
            if (wielder == null || !__instance.IsEquippedGun) return false;
            if (wielder.jobs?.curDriver is IJobDriver_Tactical) return false;
            Type rgStance = CE_Reflection.GetRgStance();
            if (rgStance != null && wielder.stances?.curStance != null && wielder.stances.curStance.GetType() == rgStance)
            {
                return false;
            }

            JobDef curDef = wielder.jobs?.curJob?.def;
            if (curDef == CEMF_DefOf.CEMF_ReloadMagazine || curDef == CEMF_DefOf.CEMF_FieldPackAndReload || curDef == CE_JobDefOf.ReloadWeapon)
            {
                return false;
            }

            int lastTick = CE_Reflection.LastReloadJobTick(__instance);
            if (lastTick == GenTicks.TicksGame) return false;

            if (!__instance.HasAmmo)
            {
                CE_Reflection.DoOutOfAmmoAction(__instance, Array.Empty<object>());
                return false;
            }

            Job job = __instance.TryMakeReloadJob();
            if (job != null)
            {
                CE_Reflection.LastReloadJobTick(__instance) = GenTicks.TicksGame;
                job.playerForced = true;
                wielder.jobs.StartJob(job, JobCondition.InterruptForced, null, wielder.CurJob?.def != job.def, true);
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(CompAmmoUser), nameof(CompAmmoUser.TryFindAmmoInInventory))]
    public static class Patch_CompAmmoUser_TryFindAmmoInInventory
    {
        public static bool Prefix(CompAmmoUser __instance, out Thing ammoThing, ref bool __result)
        {
            ammoThing = null;
            if (!MagazineUtility.ShouldUseMagazineSystem(__instance)) return true;

            // Mag-fed weapons NEVER load loose bullets directly!
            if (MagazineUtility.IsMagazineFedWeapon(__instance))
            {
                __result = false;
                return false;
            }

            // Single-load multi-capacity weapons (e.g. pump shotgun): pick exact next planned shell!
            if (MagazineUtility.IsSingleLoadMultiCapacityWeapon(__instance))
            {
                Pawn holder = __instance.Holder;
                var compInv = __instance.CompInventory;
                var cm = __instance.parent.TryGetComp<CompWeaponMagazine>();
                if (holder == null || compInv == null || cm == null) return true;

                if (cm.TryGetNextSingleLoadPlannedAmmo(holder, out AmmoDef desired, out _))
                {
                    var ammoList = compInv.ammoList;
                    for (int i = 0; i < ammoList.Count; i++)
                    {
                        if (ammoList[i].def == desired && ammoList[i].stackCount > 0)
                        {
                            ammoThing = ammoList[i];
                            __instance.SelectedAmmo = desired;
                            __result = true;
                            return false;
                        }
                    }
                }

                // Fallback: any compatible loose ammo in inventory so pawn isn't helpless if out of planned shell type
                foreach (var al in __instance.CurAmmoSet.ammoTypes)
                {
                    for (int i = 0; i < compInv.ammoList.Count; i++)
                    {
                        if (compInv.ammoList[i].def == al.ammo && compInv.ammoList[i].stackCount > 0)
                        {
                            ammoThing = compInv.ammoList[i];
                            __instance.SelectedAmmo = al.ammo;
                            __result = true;
                            return false;
                        }
                    }
                }
                __result = false;
                return false;
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(CompAmmoUser), nameof(CompAmmoUser.LoadAmmo))]
    public static class Patch_CompAmmoUser_LoadAmmo
    {
        public static bool Prefix(CompAmmoUser __instance, Thing ammo, bool emptyMag)
        {
            if (!MagazineUtility.ShouldUseMagazineSystem(__instance)) return true;

            // Single-load multi-capacity weapons (e.g. pump shotgun): insert round into exact firing-order slot!
            if (MagazineUtility.IsSingleLoadMultiCapacityWeapon(__instance))
            {
                Pawn holder = __instance.Holder;
                var cm = __instance.parent.TryGetComp<CompWeaponMagazine>();
                if (holder == null || cm == null) return true;

                cm.SyncFromCompAmmo();
                if (cm.loadedRounds.Count >= __instance.MagSize) return false;

                bool fromInv = false;
                Thing ammoThing = ammo;
                if (ammoThing == null)
                {
                    if (!__instance.TryFindAmmoInInventory(out ammoThing))
                    {
                        CE_Reflection.DoOutOfAmmoAction(__instance, Array.Empty<object>());
                        return false;
                    }
                    fromInv = true;
                }

                AmmoDef loadedDef = ammoThing.def as AmmoDef;
                if (loadedDef == null) return true;

                bool insertAtFront = false;
                if (cm.TryGetNextSingleLoadPlannedAmmo(holder, out AmmoDef planned, out bool front) && planned == loadedDef)
                {
                    insertAtFront = front;
                }

                // Consume 1 shell
                if (ammoThing.stackCount > 1)
                {
                    ammoThing.stackCount--;
                }
                else
                {
                    if (fromInv || ammoThing.holdingOwner != null)
                    {
                        ammoThing.holdingOwner?.Remove(ammoThing);
                    }
                    if (!ammoThing.Destroyed)
                    {
                        ammoThing.Destroy(DestroyMode.Vanish);
                    }
                }

                if (insertAtFront)
                {
                    cm.loadedRounds.Insert(0, loadedDef);
                }
                else
                {
                    cm.loadedRounds.Add(loadedDef);
                }

                cm.PushToCompAmmo();
                __instance.CompInventory?.UpdateInventory();

                if (__instance.parent.def.soundInteract != null && holder.MapHeld != null)
                {
                    __instance.parent.def.soundInteract.PlayOneShot(new TargetInfo(holder.PositionHeld, holder.MapHeld));
                }
                return false;
            }

            return true;
        }
    }

    // =========================================================================
    // 5. JobGiver_CheckReload: use loaded magazines for opportunistic & auto reload
    // =========================================================================

    [HarmonyPatch(typeof(JobGiver_CheckReload), nameof(JobGiver_CheckReload.TryGiveJob))]
    public static class Patch_JobGiver_CheckReload_TryGiveJob
    {
        public static bool Prefix(Pawn pawn, ref Job __result)
        {
            if (pawn == null || !MagazineUtility.ShouldUseMagazineSystem(pawn)) return true;

            var weapons = new List<ThingWithComps>();
            ThingWithComps primary = pawn.equipment?.Primary;
            CompAmmoUser primaryComp = primary?.TryGetComp<CompAmmoUser>();

            if (pawn.Drafted)
            {
                if (primaryComp == null || !MagazineUtility.IsMagazineFedWeapon(primaryComp))
                {
                    return true;
                }
                if (!primaryComp.IsOpportunisticReloadActive)
                {
                    __result = null;
                    return false;
                }
                if (Find.TickManager.TicksGame - pawn.LastAttackTargetTick < primaryComp.MinimalTicksAfterFight)
                {
                    __result = null;
                    return false;
                }
                if (pawn.Map != null)
                {
                    foreach (Pawn other in pawn.Map.mapPawns.AllPawnsSpawned)
                    {
                        if (other.Position.InHorDistOf(pawn.Position, primaryComp.SafeDistanceToReload)
                            && !other.IsPsychologicallyInvisible()
                            && other.HostileTo(pawn))
                        {
                            __result = null;
                            return false;
                        }
                    }
                }
                weapons.Add(primary);
            }
            else
            {
                if (primaryComp != null && MagazineUtility.IsManagedWeapon(primaryComp))
                {
                    weapons.Add(primary);
                }
                var compInv = pawn.TryGetComp<CompInventory>();
                if (compInv != null)
                {
                    foreach (var w in compInv.rangedWeaponList)
                    {
                        var ca = w.TryGetComp<CompAmmoUser>();
                        if (MagazineUtility.IsManagedWeapon(ca) && !weapons.Contains(w))
                        {
                            weapons.Add(w);
                        }
                    }
                }
            }

            // Only override if pawn actually has a mag-fed weapon to check; single-load weapons can run vanilla CheckReload cleanly
            bool anyMagFed = weapons.Any(w => MagazineUtility.IsMagazineFedWeapon(w.TryGetComp<CompAmmoUser>()));
            if (!anyMagFed) return true;

            for (int i = 0; i < weapons.Count; i++)
            {
                ThingWithComps weapon = weapons[i];
                var ca = weapon.TryGetComp<CompAmmoUser>();
                if (ca == null) continue;

                if (MagazineUtility.IsMagazineFedWeapon(ca))
                {
                    if ((Controller.settings.OpportunisticReloadMode == OpportunisticReloadMode.Any
                        || (Controller.settings.OpportunisticReloadMode == OpportunisticReloadMode.DraftedOnly && pawn.Drafted))
                        && ca.CurMagCount > ca.TryReloadOn)
                    {
                        continue;
                    }

                    if (ca.CurMagCount < ca.MagSize)
                    {
                        var bestMag = MagazineUtility.FindBestLoadedMagazine(pawn, ca.CurAmmoSet, ca.MagSize, ca.SelectedAmmo);
                        if (bestMag != null && bestMag.LoadedCount > ca.CurMagCount)
                        {
                            __result = JobMaker.MakeJob(CEMF_DefOf.CEMF_ReloadMagazine, pawn, weapon, bestMag);
                            return false;
                        }
                    }
                }
                else if (MagazineUtility.IsSingleLoadMultiCapacityWeapon(ca))
                {
                    if (ca.CurMagCount < ca.MagSize && ca.TryFindAmmoInInventory(out _))
                    {
                        __result = ca.TryMakeReloadJob();
                        return false;
                    }
                }
            }

            __result = null;
            return false;
        }
    }

    [HarmonyPatch(typeof(JobGiver_CheckReload), nameof(JobGiver_CheckReload.GetPriority))]
    public static class Patch_JobGiver_CheckReload_GetPriority
    {
        public static bool Prefix(Pawn pawn, ref float __result)
        {
            if (pawn == null || pawn.Drafted || !MagazineUtility.ShouldUseMagazineSystem(pawn)) return true;

            var weapons = MagazineUtility.GetManagedWeapons(pawn);
            bool anyMagFed = weapons.Any(w => MagazineUtility.IsMagazineFedWeapon(w.TryGetComp<CompAmmoUser>()));
            if (!anyMagFed) return true;

            for (int i = 0; i < weapons.Count; i++)
            {
                var ca = weapons[i].TryGetComp<CompAmmoUser>();
                if (ca == null) continue;
                if (MagazineUtility.IsMagazineFedWeapon(ca))
                {
                    if (ca.CurMagCount < ca.MagSize)
                    {
                        var bestMag = MagazineUtility.FindBestLoadedMagazine(pawn, ca.CurAmmoSet, ca.MagSize, ca.SelectedAmmo);
                        if (bestMag != null && bestMag.LoadedCount > ca.CurMagCount)
                        {
                            __result = 9.1f;
                            return false;
                        }
                    }
                }
                else if (MagazineUtility.IsSingleLoadMultiCapacityWeapon(ca))
                {
                    if (ca.CurMagCount < ca.MagSize && ca.TryFindAmmoInInventory(out _))
                    {
                        __result = 9.1f;
                        return false;
                    }
                }
            }

            __result = 0f;
            return false;
        }
    }

    // =========================================================================
    // 6. UI Integration: Command_Reload menu, GizmoAmmoStatus strip, Manage Gizmo
    // =========================================================================

    [HarmonyPatch(typeof(Command_Reload), "BuildAmmoOptions")]
    public static class Patch_Command_Reload_BuildAmmoOptions
    {
        public static bool Prefix(Command_Reload __instance, ref List<FloatMenuOption> __result)
        {
            CompAmmoUser ca = __instance.compAmmo;
            if (ca == null || !MagazineUtility.ShouldUseMagazineSystem(ca)) return true;
            if (!MagazineUtility.IsMagazineFedWeapon(ca)) return true;

            Pawn wielder = ca.Wielder;
            var cm = ca.parent.TryGetComp<CompWeaponMagazine>();
            var options = new List<FloatMenuOption>();

            if (wielder != null)
            {
                var loadedMags = MagazineUtility.GetLoadedMagazinesInInventory(wielder, ca.CurAmmoSet, ca.MagSize);
                if (loadedMags.Count > 0)
                {
                    for (int i = 0; i < loadedMags.Count; i++)
                    {
                        Thing_CEMagazine mag = loadedMags[i];
                        string summary = mag.GetCompactAmmoSummary();
                        string label = mag.preferredPlanIndex >= 0
                            ? "CEMF_Menu_InsertMag".Translate(mag.preferredPlanIndex + 1, mag.LoadedCount, mag.capacity, summary)
                            : "CEMF_Menu_InsertLooseMag".Translate(mag.LoadedCount, mag.capacity, summary);
                        options.Add(new FloatMenuOption(label, () =>
                        {
                            if (cm != null) cm.pendingSelectedMag = mag;
                            ca.TryStartReload();
                        }));
                    }
                }
                else if (MagazineUtility.HasCompatibleLooseAmmoInInventory(wielder, ca.CurAmmoSet))
                {
                    options.Add(new FloatMenuOption("CEMF_Menu_FieldPack".Translate(), () =>
                    {
                        ca.TryStartReload();
                    }));
                }
                else
                {
                    options.Add(new FloatMenuOption("CEMF_Menu_NoMagOrAmmo".Translate(), null));
                }

                if (cm != null && (cm.hasInsertedMagazine || ca.CurMagCount > 0))
                {
                    options.Add(new FloatMenuOption("CEMF_Menu_EjectMag".Translate(), () =>
                    {
                        ca.TryUnload(forceUnload: true);
                    }));
                }

                options.Add(new FloatMenuOption("CEMF_Menu_OpenManager".Translate(), () =>
                {
                    Find.WindowStack.Add(new Window_MagazineManager(wielder, ca.parent));
                }));
            }

            __result = options;
            return false;
        }

        public static void Postfix(Command_Reload __instance, List<FloatMenuOption> __result)
        {
            CompAmmoUser ca = __instance.compAmmo;
            if (ca == null || __result == null || !MagazineUtility.ShouldUseMagazineSystem(ca)) return;
            if (MagazineUtility.IsSingleLoadMultiCapacityWeapon(ca) && ca.Wielder != null)
            {
                __result.Add(new FloatMenuOption("CEMF_Menu_OpenManager".Translate(), () =>
                {
                    Find.WindowStack.Add(new Window_MagazineManager(ca.Wielder, ca.parent));
                }));
            }
        }
    }

    /// <summary>
    /// Command_Action that groups across multiple selected pawns on the gizmo bar without opening duplicate windows.
    /// </summary>
    internal class Command_SingleWindowAction : Command_Action
    {
        public override bool InheritInteractionsFrom(Gizmo other) => false;
    }

    /// <summary>
    /// Tactical Port Load gizmo supporting both single-pawn and multi-select drafted pawns without per-frame allocations.
    /// </summary>
    internal class Command_PortLoad : Command_Action
    {
        public CompAmmoUser compAmmo;
        private List<CompAmmoUser> groupedUsers;

        public override bool GroupsWith(Gizmo other)
        {
            return other is Command_PortLoad;
        }

        public override bool InheritInteractionsFrom(Gizmo other) => false;

        public override void MergeWith(Gizmo other)
        {
            if (other is Command_PortLoad otherCmd && otherCmd.compAmmo != null)
            {
                if (groupedUsers == null)
                {
                    groupedUsers = new List<CompAmmoUser> { compAmmo };
                }
                if (!groupedUsers.Contains(otherCmd.compAmmo))
                {
                    groupedUsers.Add(otherCmd.compAmmo);
                }
            }
        }

        public override void ProcessInput(Event ev)
        {
            base.ProcessInput(ev);
            var users = groupedUsers ?? (compAmmo != null ? new List<CompAmmoUser> { compAmmo } : new List<CompAmmoUser>());
            var draftedUsers = new List<CompAmmoUser>();
            for (int i = 0; i < users.Count; i++)
            {
                var u = users[i];
                if (u?.Wielder != null && u.Wielder.Drafted && !u.Wielder.Dead && u.parent != null && !u.parent.Destroyed)
                {
                    draftedUsers.Add(u);
                }
            }

            if (draftedUsers.Count == 0) return;

            // Aggregate compatible loose ammo across selected drafted SingleLoad weapon wielders
            var ammoTotals = new Dictionary<AmmoDef, int>();
            var ammoOrder = new List<AmmoDef>();

            for (int uIdx = 0; uIdx < draftedUsers.Count; uIdx++)
            {
                var ca = draftedUsers[uIdx];
                var container = ca.Wielder.inventory?.innerContainer;
                if (ca.CurAmmoSet?.ammoTypes == null || container == null) continue;

                foreach (var link in ca.CurAmmoSet.ammoTypes)
                {
                    AmmoDef ad = link.ammo;
                    if (ad == null) continue;
                    int count = 0;
                    for (int i = 0; i < container.Count; i++)
                    {
                        if (container[i].def == ad && container[i].stackCount > 0)
                        {
                            count += container[i].stackCount;
                        }
                    }
                    if (count > 0)
                    {
                        if (ammoTotals.TryGetValue(ad, out int cur))
                        {
                            ammoTotals[ad] = cur + count;
                        }
                        else
                        {
                            ammoTotals[ad] = count;
                            ammoOrder.Add(ad);
                        }
                    }
                }
            }

            var opts = new List<FloatMenuOption>();
            bool singleUser = draftedUsers.Count == 1;
            CompAmmoUser primaryCa = draftedUsers[0];
            var primaryCm = primaryCa.parent.TryGetComp<CompWeaponMagazine>();
            primaryCm?.SyncFromCompAmmo();
            bool primaryFull = primaryCa.CurMagCount >= primaryCa.MagSize;
            AmmoDef primaryChambered = primaryCm?.NextRound;

            for (int i = 0; i < ammoOrder.Count; i++)
            {
                AmmoDef capturedAmmo = ammoOrder[i];
                int totalCount = ammoTotals[capturedAmmo];
                string shortName = MagazineUtility.GetShortAmmoLabel(capturedAmmo);

                string label;
                if (singleUser)
                {
                    string chamberTag = (primaryChambered == capturedAmmo && primaryCa.CurMagCount > 0)
                        ? "CEMF_PortLoad_ChamberTag".Translate().RawText
                        : "";
                    label = (primaryFull
                        ? "CEMF_PortLoad_OptionFull".Translate(shortName, totalCount).RawText
                        : "CEMF_PortLoad_Option".Translate(shortName, totalCount).RawText) + chamberTag;
                }
                else
                {
                    label = "CEMF_PortLoad_Option".Translate(shortName, totalCount).RawText;
                }

                opts.Add(new FloatMenuOption(label, () =>
                {
                    for (int uIdx = 0; uIdx < draftedUsers.Count; uIdx++)
                    {
                        var ca = draftedUsers[uIdx];
                        Pawn wielder = ca.Wielder;
                        if (wielder == null || wielder.Dead || !wielder.Drafted || ca.parent == null || ca.parent.Destroyed) continue;
                        if (ca.CurAmmoSet?.ammoTypes == null || !ca.CurAmmoSet.ammoTypes.Any(l => l.ammo == capturedAmmo)) continue;

                        Thing sample = null;
                        var container = wielder.inventory?.innerContainer;
                        if (container != null)
                        {
                            for (int k = 0; k < container.Count; k++)
                            {
                                if (container[k].def == capturedAmmo && container[k].stackCount > 0)
                                {
                                    sample = container[k];
                                    break;
                                }
                            }
                        }
                        if (sample == null) continue;

                        var cm = ca.parent.TryGetComp<CompWeaponMagazine>();
                        if (cm != null) cm.pendingPortLoadAmmo = capturedAmmo;
                        Job job = JobMaker.MakeJob(CEMF_DefOf.CEMF_PortLoad, wielder, ca.parent, sample);
                        job.playerForced = true;
                        wielder.jobs.StartJob(job, JobCondition.InterruptForced, null, wielder.CurJob?.def != job.def, true);
                    }
                }, capturedAmmo));
            }

            if (opts.Count == 0)
            {
                opts.Add(new FloatMenuOption("CEMF_PortLoad_NoAmmo".Translate(), null));
            }
            Find.WindowStack.Add(new FloatMenu(opts));
        }
    }

    [HarmonyPatch(typeof(CompAmmoUser), nameof(CompAmmoUser.CompGetGizmosExtra))]
    public static class Patch_CompAmmoUser_CompGetGizmosExtra
    {
        public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> gizmos, CompAmmoUser __instance)
        {
            foreach (var g in gizmos)
            {
                yield return g;
            }

            if (__instance.Wielder != null
                && (__instance.Wielder.IsColonistPlayerControlled || __instance.Wielder.IsColonyMech)
                && MagazineUtility.ShouldUseMagazineSystem(__instance.Wielder)
                && MagazineUtility.IsManagedWeapon(__instance))
            {
                yield return new Command_SingleWindowAction
                {
                    defaultLabel = "CEMF_Gizmo_ManageLabel".Translate(),
                    defaultDesc = "CEMF_Gizmo_ManageDesc".Translate(),
                    icon = CEMF_Textures.ManageMags,
                    action = () =>
                    {
                        Find.WindowStack.Add(new Window_MagazineManager(__instance.Wielder, __instance.parent));
                    }
                };

                // Port Load gizmo for SingleLoad multi-capacity weapons (only shown when Drafted)
                if (__instance.Wielder.Drafted && MagazineUtility.IsSingleLoadMultiCapacityWeapon(__instance))
                {
                    Pawn wielder = __instance.Wielder;
                    var portCmd = new Command_PortLoad
                    {
                        compAmmo = __instance,
                        defaultLabel = "CEMF_Gizmo_PortLoadLabel".Translate(),
                        defaultDesc = "CEMF_Gizmo_PortLoadDesc".Translate(),
                        icon = CEMF_Textures.PortLoad,
                        action = () => { }
                    };

                    if (!MagazineUtility.HasCompatibleLooseAmmoInInventory(wielder, __instance.CurAmmoSet))
                    {
                        portCmd.Disable("CEMF_PortLoad_NoAmmo".Translate());
                    }

                    yield return portCmd;
                }
            }
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
    public static class Patch_Pawn_GetGizmos
    {
        public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> gizmos, Pawn __instance)
        {
            foreach (var g in gizmos)
            {
                yield return g;
            }

            if (__instance == null || (!__instance.IsColonistPlayerControlled && !__instance.IsColonyMech)) yield break;
            if (!MagazineUtility.ShouldUseMagazineSystem(__instance)) yield break;

            // If primary weapon is already a managed weapon, CompAmmoUser already yielded the gizmo
            var primaryComp = __instance.equipment?.Primary?.TryGetComp<CompAmmoUser>();
            if (MagazineUtility.IsManagedWeapon(primaryComp)) yield break;

            // Otherwise, if pawn carries any managed weapon in inventory, still show the Manage Magazines gizmo!
            var managed = MagazineUtility.GetManagedWeapons(__instance);
            if (managed.Count > 0)
            {
                yield return new Command_SingleWindowAction
                {
                    defaultLabel = "CEMF_Gizmo_ManageLabel".Translate(),
                    defaultDesc = "CEMF_Gizmo_ManageDesc".Translate(),
                    icon = CEMF_Textures.ManageMags,
                    action = () =>
                    {
                        Find.WindowStack.Add(new Window_MagazineManager(__instance, managed[0]));
                    }
                };
            }
        }
    }

    [HarmonyPatch(typeof(GizmoAmmoStatus), nameof(GizmoAmmoStatus.GizmoOnGUI))]
    public static class Patch_GizmoAmmoStatus_GizmoOnGUI
    {
        public static void Postfix(GizmoAmmoStatus __instance, Vector2 topLeft, float maxWidth)
        {
            if (!(CE_MagazineFedMod.Settings?.showMultiColorGizmoBar ?? true)) return;
            var ca = __instance.compAmmo;
            if (ca == null || !MagazineUtility.IsManagedWeapon(ca)) return;

            var cm = ca.parent.TryGetComp<CompWeaponMagazine>();
            if (cm == null) return;
            cm.SyncFromCompAmmo();

            if (cm.loadedRounds.Count == 0 || ca.MagSize <= 0) return;

            // Draw sleek 5px color-coded firing-order strip along bottom edge of gizmo
            float width = __instance.GetWidth(maxWidth);
            Rect stripRect = new Rect(topLeft.x + 8f, topLeft.y + 66f, width - 16f, 5f);
            Widgets.DrawBoxSolid(stripRect, new Color(0.08f, 0.08f, 0.1f, 0.9f));

            float slotW = stripRect.width / ca.MagSize;
            for (int i = 0; i < cm.loadedRounds.Count && i < ca.MagSize; i++)
            {
                Rect seg = new Rect(stripRect.x + i * slotW, stripRect.y, Mathf.Max(1f, slotW), stripRect.height);
                Widgets.DrawBoxSolid(seg, MagazineUtility.GetAmmoColor(cm.loadedRounds[i]));
            }
        }
    }

    [HarmonyPatch(typeof(GizmoAmmoStatus), nameof(GizmoAmmoStatus.GetTooltip))]
    public static class Patch_GizmoAmmoStatus_GetTooltip
    {
        public static void Postfix(GizmoAmmoStatus __instance, ref string __result)
        {
            var ca = __instance.compAmmo;
            if (ca == null || !MagazineUtility.IsManagedWeapon(ca) || !MagazineUtility.ShouldUseMagazineSystem(ca)) return;

            var cm = ca.parent.TryGetComp<CompWeaponMagazine>();
            if (cm == null) return;
            cm.SyncFromCompAmmo();

            string extra = "\n\n";
            if (cm.NextRound != null)
            {
                extra += "CEMF_Gizmo_NextRound".Translate(cm.NextRound.LabelCap) + "\n";
                extra += "CEMF_MagInspectContents".Translate(cm.GetLoadedBreakdownSummary()) + "\n";
            }

            if (MagazineUtility.IsMagazineFedWeapon(ca) && ca.Holder != null)
            {
                var loadedMags = MagazineUtility.GetLoadedMagazinesInInventory(ca.Holder, ca.CurAmmoSet, ca.MagSize);
                int totalRounds = loadedMags.Sum(m => m.LoadedCount);
                extra += "CEMF_Gizmo_SpareMags".Translate(loadedMags.Count, totalRounds);
            }
            else if (MagazineUtility.IsSingleLoadMultiCapacityWeapon(ca) && cm.NextRound != null)
            {
                extra += "CEMF_Gizmo_SingleLoadPlan".Translate(cm.NextRound.LabelCap);
            }

            __result += extra;
        }
    }

    // =========================================================================
    // 7. CE Loadout & HoldTracker Integration (never drop mags, count rounds in mags!)
    // =========================================================================

    [HarmonyPatch]
    public static class Patch_Utility_HoldTracker_GetStorageByThingDef
    {
        public static System.Reflection.MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("CombatExtended.Utility_HoldTracker");
            return type != null ? AccessTools.Method(type, "GetStorageByThingDef", new[] { typeof(Pawn) }) : null;
        }

        public static void Postfix(Pawn pawn, Dictionary<ThingDef, Integer> __result)
        {
            if (pawn?.inventory?.innerContainer == null || __result == null) return;

            // Remove CEMF_Magazine from raw ThingDef excess tracking so CE Loadout never drops magazines
            if (CEMF_DefOf.CEMF_Magazine != null && __result.ContainsKey(CEMF_DefOf.CEMF_Magazine))
            {
                __result.Remove(CEMF_DefOf.CEMF_Magazine);
            }

            // Count rounds stored inside inventory magazines toward their AmmoDef totals
            var container = pawn.inventory.innerContainer;
            for (int i = 0; i < container.Count; i++)
            {
                if (container[i] is Thing_CEMagazine mag && mag.rounds != null)
                {
                    for (int r = 0; r < mag.rounds.Count; r++)
                    {
                        var ad = mag.rounds[r];
                        if (ad == null) continue;
                        if (__result.TryGetValue(ad, out var integer)) integer.value += 1;
                        else __result[ad] = new Integer(1);
                    }
                }
            }
        }
    }

    [HarmonyPatch]
    public static class Patch_Utility_HoldTracker_GetExcessThing
    {
        public static System.Reflection.MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("CombatExtended.Utility_HoldTracker");
            return type != null ? AccessTools.Method(type, "GetExcessThing") : null;
        }

        public static void Postfix(ref Thing dropThing, ref int dropCount, ref bool __result)
        {
            if (__result && dropThing is Thing_CEMagazine)
            {
                dropThing = null;
                dropCount = 0;
                __result = false;
            }
        }
    }

    // =========================================================================
    // 8. Prevent auto-picking up loose ammo for magazine-fed weapons
    // =========================================================================

    [HarmonyPatch(typeof(JobGiver_TakeAndEquip), nameof(JobGiver_TakeAndEquip.GetPriority))]
    public static class Patch_JobGiver_TakeAndEquip_GetPriority
    {
        public static bool Prefix(Pawn pawn, ref float __result)
        {
            if (MagazineUtility.ShouldBlockTakeAndEquipLooseAmmo(pawn))
            {
                __result = 0f;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(JobGiver_TakeAndEquip), "TryGiveJob")]
    public static class Patch_JobGiver_TakeAndEquip_TryGiveJob
    {
        public static bool Prefix(Pawn pawn, ref Job __result)
        {
            if (MagazineUtility.ShouldBlockTakeAndEquipLooseAmmo(pawn))
            {
                __result = null;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(JobGiver_UpdateLoadout), "FindPickup")]
    public static class Patch_JobGiver_UpdateLoadout_FindPickup
    {
        public static bool Prefix(Pawn pawn, LoadoutSlot curSlot, ref Thing curThing, ref Pawn curCarrier)
        {
            if (curSlot?.thingDef != null && MagazineUtility.IsLooseAmmoBlockedForAutoPickup(pawn, curSlot.thingDef))
            {
                curThing = null;
                curCarrier = null;
                return false;
            }
            return true;
        }

        public static void Postfix(Pawn pawn, ref Thing curThing, ref Pawn curCarrier)
        {
            if (curThing != null && MagazineUtility.IsLooseAmmoBlockedForAutoPickup(pawn, curThing.def))
            {
                curThing = null;
                curCarrier = null;
            }
        }
    }

    [HarmonyPatch(typeof(CompAmmoUser), nameof(CompAmmoUser.TryPickupAmmo))]
    public static class Patch_CompAmmoUser_TryPickupAmmo
    {
        public static bool Prefix(CompAmmoUser __instance, ref bool __result)
        {
            if (!(CE_MagazineFedMod.Settings?.preventAutoPickupLooseAmmo ?? true)) return true;
            if (!MagazineUtility.IsMagazineFedWeapon(__instance) || !MagazineUtility.ShouldUseMagazineSystem(__instance))
            {
                return true;
            }
            // Do not auto-pickup loose ammo for mag-fed weapons when out of ammo
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(CompMechAmmo), nameof(CompMechAmmo.TryMakeAmmoJob))]
    public static class Patch_CompMechAmmo_TryMakeAmmoJob
    {
        public static bool Prefix(CompMechAmmo __instance, bool forced)
        {
            if (forced) return true;
            if (!(CE_MagazineFedMod.Settings?.preventAutoPickupLooseAmmo ?? true)) return true;
            if (__instance?.ParentPawn == null || !MagazineUtility.ShouldUseMagazineSystem(__instance.ParentPawn)) return true;
            if (MagazineUtility.IsMagazineFedWeapon(__instance.AmmoUser))
            {
                return false;
            }
            return true;
        }
    }

    // =========================================================================
    // 9. NPC / Enemy Spawn Magazine Packing (when FactionScope == AllFactions)
    // =========================================================================

    [HarmonyPatch(typeof(LoadoutPropertiesExtension), nameof(LoadoutPropertiesExtension.GenerateLoadoutFor))]
    public static class Patch_LoadoutPropertiesExtension_GenerateLoadoutFor
    {
        public static void Postfix(Pawn pawn)
        {
            if (CE_MagazineFedMod.Settings?.factionScope != FactionScope.AllFactions) return;
            MagazineUtility.PackNpcInventoryIntoMagazines(pawn);
        }
    }

    // =========================================================================
    // 10. Magazine Bulk base value = fixed full-magazine bulk (no extra offset line)
    // =========================================================================

    [HarmonyPatch(typeof(StatWorker), nameof(StatWorker.GetBaseValueFor))]
    public static class Patch_StatWorker_GetBaseValueFor
    {
        public static void Postfix(StatDef ___stat, StatRequest request, ref float __result)
        {
            if (___stat == CE_StatDefOf.Bulk && request.HasThing && request.Thing is Thing_CEMagazine mag)
            {
                __result = mag.GetFullMagazineBulk();
            }
        }
    }
}
