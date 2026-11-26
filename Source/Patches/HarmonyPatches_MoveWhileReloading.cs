using System;
using System.Collections.Generic;
using System.Reflection;
using CombatExtended;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace CE_MagazineFed
{
    public static class MoveWhileReloadUtility
    {
        private static bool? cachedCEMoveReloadActive;
        private static bool? cachedRunAndGunActive;

        public static bool IsCEMoveReloadModActive
        {
            get
            {
                if (!cachedCEMoveReloadActive.HasValue)
                {
                    cachedCEMoveReloadActive =
                        ModsConfig.IsActive("nkm.moveWhileReloading")
                        || ModsConfig.IsActive("himawari.moveWhileReloading")
                        || ModLister.HasActiveModWithName("CE - Move while reloading")
                        || ModLister.HasActiveModWithName("CE - Move while reloading(Continue)")
                        || AccessTools.TypeByName("CEMoveReload.HarmonyPatches") != null;
                }
                return cachedCEMoveReloadActive.Value;
            }
        }

        public static bool IsRunAndGunActive
        {
            get
            {
                if (!cachedRunAndGunActive.HasValue)
                {
                    cachedRunAndGunActive =
                        ModsConfig.IsActive("roolo.RunAndGun")
                        || ModsConfig.IsActive("roolo.RunAndGun.kotobike")
                        || ModsConfig.IsActive("MemeGoddess.RunAndGun")
                        || ModLister.HasActiveModWithName("RunAndGun")
                        || ModLister.HasActiveModWithName("RunAndGun - Continued");
                }
                return cachedRunAndGunActive.Value;
            }
        }

        public static bool IsFeatureGloballyActive()
        {
            var settings = CE_MagazineFedMod.Settings;
            var mode = settings?.moveWhileReloadingMode ?? MoveWhileReloadingMode.AutoWithMod;
            if (mode == MoveWhileReloadingMode.Disabled) return false;
            if (mode == MoveWhileReloadingMode.AlwaysEnabled) return true;
            return IsCEMoveReloadModActive;
        }

        public static bool CanMoveWhileReloading(Pawn pawn)
        {
            return pawn != null && IsFeatureGloballyActive() && MagazineUtility.ShouldUseMagazineSystem(pawn);
        }

        /// <summary>
        /// True when `pawn` is currently executing a magazine reload, port load, field-pack,
        /// Phase-3 magazine packing, or (in standalone mode without CE - Move while reloading) a vanilla CE reload.
        /// </summary>
        public static bool IsManagedMovableJobActive(Pawn pawn)
        {
            if (!CanMoveWhileReloading(pawn)) return false;
            JobDef curDef = pawn.CurJobDef;
            if (curDef == null) return false;

            if (curDef == CEMF_DefOf.CEMF_ReloadMagazine
                || curDef == CEMF_DefOf.CEMF_PortLoad
                || curDef == CEMF_DefOf.CEMF_FieldPackAndReload)
            {
                return true;
            }

            if (curDef == CEMF_DefOf.CEMF_PackMagazines)
            {
                return pawn.jobs?.curDriver is JobDriver_PackMagazines packDriver && packDriver.IsInPackingPhase;
            }

            // In standalone AlwaysEnabled mode without CE - Move while reloading installed,
            // also allow moving during CE_JobDefOf.ReloadWeapon (e.g. single-load shotguns).
            // When CE - Move while reloading IS installed, leave CE_JobDefOf.ReloadWeapon untouched to avoid double speed penalty.
            if (curDef == CE_JobDefOf.ReloadWeapon && !IsCEMoveReloadModActive)
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Computes the movement speed multiplier (0..1) while reloading or packing.
        /// Default formula matches CE - Move while reloading: 1 - Clamp(weaponMass / (MassUtility.Capacity(pawn) / 2), 0, 1).
        /// </summary>
        public static float GetMoveSpeedFactor(Pawn pawn)
        {
            var settings = CE_MagazineFedMod.Settings;
            if (settings != null && settings.moveSpeedPenaltyMode == MoveSpeedPenaltyMode.FixedMultiplier)
            {
                return settings.EffectiveFixedMoveSpeed;
            }

            Thing weapon = ResolveReferenceWeapon(pawn);
            if (weapon == null)
            {
                return 0.85f;
            }

            float weaponMass = weapon.GetStatValue(StatDefOf.Mass);
            float carryWeight = Math.Max(0.0001f, MassUtility.Capacity(pawn));
            float penalty = Mathf.Clamp(weaponMass / (carryWeight / 2f) * 100f, 0f, 100f);
            return Mathf.Clamp01((100f - penalty) / 100f);
        }

        private static Thing ResolveReferenceWeapon(Pawn pawn)
        {
            if (pawn == null) return null;
            Thing targetB = pawn.CurJob?.targetB.Thing;
            if (targetB != null && (targetB.def.IsRangedWeapon || targetB.TryGetComp<CompAmmoUser>() != null))
            {
                return targetB;
            }
            if (pawn.equipment?.Primary != null)
            {
                return pawn.equipment.Primary;
            }
            var managed = MagazineUtility.GetManagedWeapons(pawn);
            return managed.Count > 0 ? managed[0] : null;
        }

        /// <summary>
        /// Starts a reload/pack job while seamlessly preserving the pawn's active walking path
        /// if move-while-reloading is enabled and the pawn was already moving.
        /// </summary>
        public static bool StartJobPreservingMove(Pawn pawn, Job job, bool useOrderedJob, bool cancelBusyStances = true)
        {
            if (pawn?.jobs == null || job == null) return false;

            IntVec3 resumeDest = IntVec3.Invalid;
            if (CanMoveWhileReloading(pawn) && pawn.pather != null && pawn.pather.Moving && pawn.pather.Destination.Cell.IsValid)
            {
                resumeDest = pawn.pather.Destination.Cell;
            }

            bool started;
            if (useOrderedJob)
            {
                started = pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            }
            else
            {
                pawn.jobs.StartJob(job, JobCondition.InterruptForced, null, pawn.CurJob?.def != job.def, cancelBusyStances);
                started = pawn.CurJob == job;
            }

            if (started && resumeDest.IsValid && pawn.Position != resumeDest && pawn.pather != null && !pawn.pather.Moving)
            {
                pawn.pather.StartPath(resumeDest, PathEndMode.OnCell);
            }
            return started;
        }
    }

    // =========================================================================
    // 1. Standalone CE JobDriver_Reload support (only when CEMoveReload is NOT active)
    // =========================================================================

    [HarmonyPatch(typeof(CombatExtended.JobDriver_Reload), "MakeNewToils")]
    public static class Patch_CE_JobDriver_Reload_MakeNewToils_Move
    {
        public static void Postfix(CombatExtended.JobDriver_Reload __instance, ref IEnumerable<Toil> __result)
        {
            if (MoveWhileReloadUtility.IsCEMoveReloadModActive) return;
            if (__instance?.pawn == null || !MoveWhileReloadUtility.CanMoveWhileReloading(__instance.pawn)) return;

            var list = new List<Toil>(__result);
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].defaultCompleteMode == ToilCompleteMode.Delay)
                {
                    list[i].initAction = null;
                    break;
                }
            }
            __result = list;
        }
    }

    // =========================================================================
    // 1b. Preserve active walking path when clicking Reload on single-load / vanilla CE weapons
    // =========================================================================

    [HarmonyPatch(typeof(CompAmmoUser), nameof(CompAmmoUser.TryStartReload))]
    public static class Patch_CompAmmoUser_TryStartReload_PreserveMove
    {
        public static void Prefix(CompAmmoUser __instance, out IntVec3 __state)
        {
            __state = IntVec3.Invalid;
            Pawn wielder = __instance?.Wielder;
            if (wielder == null || !MoveWhileReloadUtility.CanMoveWhileReloading(wielder)) return;

            Pawn_PathFollower pather = wielder.pather;
            if (pather != null && pather.Moving && pather.Destination.Cell.IsValid)
            {
                __state = pather.Destination.Cell;
            }
        }

        public static void Postfix(CompAmmoUser __instance, IntVec3 __state)
        {
            if (!__state.IsValid) return;
            Pawn wielder = __instance?.Wielder;
            if (wielder == null || wielder.Dead || wielder.Downed || !wielder.Spawned) return;
            if (wielder.Position == __state || wielder.pather == null || wielder.pather.Moving) return;

            bool isMovableReload = MoveWhileReloadUtility.IsManagedMovableJobActive(wielder)
                || (wielder.CurJobDef == CE_JobDefOf.ReloadWeapon && MoveWhileReloadUtility.CanMoveWhileReloading(wielder));
            if (isMovableReload)
            {
                wielder.pather.StartPath(__state, PathEndMode.OnCell);
            }
        }
    }

    // =========================================================================
    // 2. Movement speed penalty via clean Postfix on Pawn.TicksPerMove
    // =========================================================================

    [HarmonyPatch(typeof(Pawn), "TicksPerMove", new[] { typeof(bool) })]
    public static class Patch_Pawn_TicksPerMove_MoveWhileReloading
    {
        public static void Postfix(Pawn __instance, ref float __result)
        {
            if (__instance == null || __instance.debugMaxMoveSpeed) return;
            if (!MoveWhileReloadUtility.IsManagedMovableJobActive(__instance)) return;

            float factor = MoveWhileReloadUtility.GetMoveSpeedFactor(__instance);
            if (factor <= 0.001f)
            {
                __result = 450f;
            }
            else if (factor < 0.999f)
            {
                __result = Mathf.Clamp(__result / factor, 1f, 450f);
            }
        }
    }

    // =========================================================================
    // 3. Keep reloading/packing when player issues a drafted Goto order
    //    Runs at Priority.First so CEMoveReload's catch-all Prefix doesn't cancel our jobs.
    // =========================================================================

    [HarmonyPatch(typeof(FloatMenuOptionProvider_DraftedMove), nameof(FloatMenuOptionProvider_DraftedMove.PawnGotoAction))]
    public static class Patch_DraftedMove_PawnGotoAction_MoveWhileReloading
    {
        [HarmonyPriority(Priority.First)]
        public static bool Prefix(IntVec3 clickCell, Pawn pawn, IntVec3 gotoLoc)
        {
            if (pawn == null || pawn.Map == null) return true;
            if (!MoveWhileReloadUtility.IsManagedMovableJobActive(pawn)) return true;

            if (pawn.Position == gotoLoc)
            {
                FleckMaker.Static(gotoLoc, pawn.Map, FleckDefOf.FeedbackGoto);
                return false;
            }

            if (pawn.Map.reachability.CanReach(pawn.Position, gotoLoc, PathEndMode.OnCell, TraverseParms.For(pawn, Danger.Deadly, TraverseMode.PassDoors)))
            {
                if (KeyBindingDefOf.QueueOrder.IsDownEvent && pawn.pather != null && pawn.pather.Moving)
                {
                    Job gotoJob = JobMaker.MakeJob(JobDefOf.Goto, gotoLoc);
                    if (pawn.Map.exitMapGrid.IsExitCell(clickCell))
                    {
                        gotoJob.exitMapOnArrival = !pawn.IsColonyMech;
                    }
                    PlayerKnowledgeDatabase.KnowledgeDemonstrated(ConceptDefOf.QueueOrders, KnowledgeAmount.NoteTaught);
                    if (gotoJob.TryMakePreToilReservations(pawn, errorOnFailed: true))
                    {
                        pawn.jobs.jobQueue.EnqueueLast(gotoJob, JobTag.Misc);
                    }
                }
                else
                {
                    pawn.jobs?.ClearQueuedJobs();
                    pawn.pather?.StartPath(gotoLoc, PathEndMode.OnCell);
                }

                FleckMaker.Static(gotoLoc, pawn.Map, FleckDefOf.FeedbackGoto);
            }

            return false;
        }
    }

    // =========================================================================
    // 4. Chain Shift-queued Goto waypoints while reloading/packing on the move
    // =========================================================================

    [HarmonyPatch(typeof(Pawn_PathFollower), "PatherArrived")]
    public static class Patch_Pawn_PathFollower_PatherArrived_MoveWhileReloading
    {
        [HarmonyPriority(Priority.First)]
        public static bool Prefix(Pawn ___pawn)
        {
            if (___pawn == null || !MoveWhileReloadUtility.IsManagedMovableJobActive(___pawn)) return true;

            var queue = ___pawn.jobs?.jobQueue;
            if (queue == null || queue.Count == 0) return true;

            for (int i = 0; i < queue.Count; i++)
            {
                Job queued = queue[i].job;
                if (queued != null && queued.def == JobDefOf.Goto && queued.targetA.Cell.IsValid)
                {
                    IntVec3 dest = queued.targetA.Cell;
                    ___pawn.jobs.EndCurrentOrQueuedJob(queued, JobCondition.Succeeded);
                    ___pawn.pather.StartPath(dest, PathEndMode.OnCell);
                    return false;
                }
            }
            return true;
        }
    }

    // =========================================================================
    // 5. Preserve walking destination smoothly when reload/pack finishes mid-stride
    //    Uses Prefix/Postfix __state without replacing vanilla EndCurrentJob.
    // =========================================================================

    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.EndCurrentJob))]
    public static class Patch_Pawn_JobTracker_EndCurrentJob_MoveWhileReloading
    {
        public static void Prefix(JobCondition condition, bool startNewJob, Pawn ___pawn, out IntVec3 __state)
        {
            __state = IntVec3.Invalid;
            if (!startNewJob || condition != JobCondition.Succeeded || ___pawn == null) return;
            if (!MoveWhileReloadUtility.IsManagedMovableJobActive(___pawn)) return;

            Pawn_PathFollower pather = ___pawn.pather;
            if (pather != null && pather.Moving && pather.Destination.Cell.IsValid)
            {
                __state = pather.Destination.Cell;
            }
        }

        public static void Postfix(Pawn_JobTracker __instance, bool startNewJob, Pawn ___pawn, IntVec3 __state)
        {
            if (!__state.IsValid || !startNewJob || ___pawn == null) return;
            if (___pawn.Dead || ___pawn.Downed || !___pawn.Spawned || ___pawn.Position == __state) return;

            JobDef curDef = __instance.curJob?.def;
            if (curDef == null
                || curDef == JobDefOf.Wait_MaintainPosture
                || curDef == JobDefOf.Wait
                || curDef == JobDefOf.Wait_Combat)
            {
                Job gotoJob = JobMaker.MakeJob(JobDefOf.Goto, __state);
                gotoJob.playerForced = ___pawn.Drafted;
                __instance.StartJob(gotoJob, JobCondition.InterruptForced, null, resumeCurJobAfterwards: false, cancelBusyStances: false);
            }
        }
    }

    // =========================================================================
    // 6. Do not interrupt moving reload/pack with CE Suppression RunForCover
    // =========================================================================

    [HarmonyPatch(typeof(SuppressionUtility), nameof(SuppressionUtility.GetRunForCoverJob))]
    public static class Patch_SuppressionUtility_GetRunForCoverJob_MoveWhileReloading
    {
        public static void Postfix(Pawn pawn, ref Job __result)
        {
            if (__result?.def != CE_JobDefOf.RunForCover) return;
            if (pawn?.pather?.Moving == true && MoveWhileReloadUtility.IsManagedMovableJobActive(pawn))
            {
                __result = null;
            }
        }
    }

    // =========================================================================
    // 7. RunAndGun soft compatibility (standalone mode only; CEMoveReload handles when active)
    // =========================================================================

    [HarmonyPatch(typeof(Verb), "TryCastNextBurstShot")]
    public static class Patch_Verb_TryCastNextBurstShot_MoveWhileReloading
    {
        public static bool Prepare()
        {
            return MoveWhileReloadUtility.IsRunAndGunActive;
        }

        public static bool Prefix(Verb __instance)
        {
            if (MoveWhileReloadUtility.IsCEMoveReloadModActive) return true;
            Pawn pawn = __instance?.CasterPawn;
            if (pawn == null || !MoveWhileReloadUtility.CanMoveWhileReloading(pawn)) return true;

            var comp = pawn.equipment?.Primary?.GetComp<CompAmmoUser>();
            if (comp == null) return true;

            if (pawn.pather != null && pawn.pather.Moving && !comp.CanBeFiredNow)
            {
                comp.TryStartReload();
                return false;
            }
            return true;
        }
    }

    // =========================================================================
    // 8. Achtung! soft compatibility (drag-move without cancelling reload/pack)
    // =========================================================================

    [HarmonyPatch]
    public static class Patch_Achtung_Colonist_OrderTo_MoveWhileReloading
    {
        private static FieldInfo fieldPawn;
        private static FieldInfo fieldLastOrder;
        private static MethodInfo methodUpdateOrderPos;

        public static bool Prepare()
        {
            Type colonistType = AccessTools.TypeByName("AchtungMod.Colonist");
            if (colonistType == null) return false;
            fieldPawn = AccessTools.Field(colonistType, "pawn");
            fieldLastOrder = AccessTools.Field(colonistType, "lastOrder");
            methodUpdateOrderPos = AccessTools.Method(colonistType, "UpdateOrderPos", new[] { typeof(Vector3) });
            return fieldPawn != null && fieldLastOrder != null && methodUpdateOrderPos != null
                && AccessTools.Method(colonistType, "OrderTo", new[] { typeof(Vector3) }) != null;
        }

        public static MethodBase TargetMethod()
        {
            Type colonistType = AccessTools.TypeByName("AchtungMod.Colonist");
            return colonistType != null ? AccessTools.Method(colonistType, "OrderTo", new[] { typeof(Vector3) }) : null;
        }

        [HarmonyPriority(Priority.First)]
        public static bool Prefix(object __instance, Vector3 pos)
        {
            if (__instance == null || fieldPawn == null) return true;
            Pawn pawn = fieldPawn.GetValue(__instance) as Pawn;
            if (pawn == null || pawn.Map == null || !MoveWhileReloadUtility.IsManagedMovableJobActive(pawn)) return true;

            IntVec3 dest = (IntVec3)methodUpdateOrderPos.Invoke(__instance, new object[] { pos });
            IntVec3 lastOrder = (IntVec3)fieldLastOrder.GetValue(__instance);
            if ((dest.IsValid && !lastOrder.IsValid) || lastOrder != dest)
            {
                fieldLastOrder.SetValue(__instance, dest);
                if (dest.IsValid && pawn.jobs != null && pawn.jobs.IsCurrentJobPlayerInterruptible())
                {
                    if (KeyBindingDefOf.QueueOrder.IsDownEvent && pawn.pather != null && pawn.pather.Moving)
                    {
                        Job job = JobMaker.MakeJob(JobDefOf.Goto, dest);
                        job.playerForced = true;
                        job.collideWithPawns = false;
                        if (pawn.Map.exitMapGrid.IsExitCell(dest))
                        {
                            job.exitMapOnArrival = !pawn.IsColonyMech;
                        }
                        if (job.TryMakePreToilReservations(pawn, errorOnFailed: true))
                        {
                            pawn.jobs.jobQueue.EnqueueLast(job, JobTag.Misc);
                        }
                    }
                    else
                    {
                        pawn.jobs.ClearQueuedJobs();
                        pawn.pather?.StartPath(dest, PathEndMode.OnCell);
                    }
                }
            }
            return false;
        }
    }
}
