using System;
using System.Collections.Generic;
using CombatExtended;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace CE_MagazineFed
{
    /// <summary>
    /// Combat fallback when all magazines are empty but the pawn still carries loose compatible ammo (Requirement A4).
    /// Strictly forbids direct loose-round loading into a mag-fed gun: the pawn first hand-packs rounds slowly
    /// into a magazine, then inserts that magazine into the weapon.
    /// TargetA = Pawn, TargetB = Weapon.
    /// </summary>
    public class JobDriver_FieldPackAndReload : JobDriver
    {
        private ThingWithComps Weapon => TargetThingB as ThingWithComps;
        private CompAmmoUser CompAmmo => Weapon?.TryGetComp<CompAmmoUser>();
        private CompWeaponMagazine CompMag => Weapon?.TryGetComp<CompWeaponMagazine>();

        private Thing_CEMagazine workingMag;
        private AmmoPlan targetPlan;
        private int nextSlotToFill;
        private int roundsPackedSoFar;
        private int pendingBatchCount = 1;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref workingMag, "workingMag");
            Scribe_Values.Look(ref nextSlotToFill, "nextSlotToFill", 0);
            Scribe_Values.Look(ref roundsPackedSoFar, "roundsPackedSoFar", 0);
            Scribe_Values.Look(ref pendingBatchCount, "pendingBatchCount", 1);
        }

        private AmmoPlan ResolveTargetPlan()
        {
            if (targetPlan != null) return targetPlan;
            var ca = CompAmmo;
            if (ca == null || workingMag == null) return null;
            var cfg = GameComponent_MagazineManager.Instance?.GetOrCreateConfig(pawn, ca);
            int slotIdx = workingMag.preferredPlanIndex >= 0 ? workingMag.preferredPlanIndex : 0;
            targetPlan = cfg?.GetPlan(slotIdx);
            return targetPlan;
        }

        public override string GetReport()
        {
            if (Weapon != null)
            {
                return $"{"CEMF_Mote_FieldPacking".Translate()} ({Weapon.LabelShort})";
            }
            return base.GetReport();
        }

        private AmmoDef PickNextLooseAmmoForFieldPack()
        {
            if (CompAmmo?.CurAmmoSet?.ammoTypes == null) return null;

            // Try matching plan slot first
            var plan = ResolveTargetPlan();
            if (plan != null && nextSlotToFill >= 0 && nextSlotToFill < plan.slots.Count)
            {
                AmmoDef planned = plan.slots[nextSlotToFill];
                if (planned != null && MagazineUtility.CountLooseAmmoInInventory(pawn, planned) > 0)
                {
                    return planned;
                }
            }
            // Fallback to selected ammo
            if (CompAmmo.SelectedAmmo != null && MagazineUtility.CountLooseAmmoInInventory(pawn, CompAmmo.SelectedAmmo) > 0)
            {
                return CompAmmo.SelectedAmmo;
            }
            // Fallback to any compatible loose ammo in inventory
            foreach (var al in CompAmmo.CurAmmoSet.ammoTypes)
            {
                if (al.ammo != null && MagazineUtility.CountLooseAmmoInInventory(pawn, al.ammo) > 0)
                {
                    return al.ammo;
                }
            }
            return null;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            this.FailOnDestroyedOrNull(TargetIndex.B);
            if (pawn.MentalStateDef != MentalStateDefOf.Berserk && pawn.MentalStateDef != MentalStateDefOf.BerserkMechanoid)
            {
                this.FailOnMentalState(TargetIndex.A);
            }

            // If interrupted mid-pack while weapon has no magazine seated, re-seat workingMag if it has rounds (or seat empty back)
            AddFinishAction(cond =>
            {
                var cm = CompMag;
                if (cm != null && !cm.hasInsertedMagazine && workingMag != null && !workingMag.Destroyed
                    && pawn?.inventory?.innerContainer != null && pawn.inventory.innerContainer.Contains(workingMag))
                {
                    cm.InsertMagazine(workingMag, pawn);
                }
            });

            // Step 1: Prepare an empty/partial magazine to hand-pack
            Toil prepToil = new Toil();
            prepToil.initAction = () =>
            {
                var ca = CompAmmo;
                var cm = CompMag;
                if (ca == null || cm == null)
                {
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }

                // If the gun has an empty inserted mag, eject it to pack it; otherwise grab an empty mag from inventory
                var invMags = MagazineUtility.GetMagazinesInInventory(pawn, ca.CurAmmoSet, ca.MagSize);
                workingMag = invMags.Find(m => !m.IsFull);
                if (workingMag == null && cm.hasInsertedMagazine)
                {
                    workingMag = cm.EjectInsertedMagazine(pawn);
                }
                if (workingMag == null && (CE_MagazineFedMod.Settings?.autoProvisionEmptyMags ?? true))
                {
                    workingMag = Thing_CEMagazine.Create(ca.CurAmmoSet, ca.MagSize, 0);
                    pawn.inventory.innerContainer.TryAdd(workingMag, true);
                }
                if (workingMag == null)
                {
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }

                var mgr = GameComponent_MagazineManager.Instance;
                var cfg = mgr?.GetOrCreateConfig(pawn, ca);
                int slotIdx = workingMag.preferredPlanIndex >= 0 ? workingMag.preferredPlanIndex : 0;
                targetPlan = cfg?.GetPlan(slotIdx);
                nextSlotToFill = workingMag.LoadedCount;
                roundsPackedSoFar = 0;

                if (ca.ShouldThrowMote && pawn.Map != null)
                {
                    MoteMaker.ThrowText(pawn.Position.ToVector3Shifted(), pawn.Map, "CEMF_Mote_FieldPacking".Translate());
                }
            };
            prepToil.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return prepToil;

            // Step 2: Slowly pack rounds into workingMag (scaled by ReloadSpeed & mech speed multiplier)
            Toil packOneRoundToil = new Toil { actor = pawn };
            packOneRoundToil.initAction = () =>
            {
                if (!MoveWhileReloadUtility.CanMoveWhileReloading(packOneRoundToil.actor))
                {
                    packOneRoundToil.actor.pather?.StopDead();
                }
                int remaining = workingMag != null ? Mathf.Max(1, workingMag.capacity - workingMag.LoadedCount) : 1;
                packOneRoundToil.defaultDuration = MagazineUtility.GetPackingTicksAndBatch(pawn, isFieldPacking: true, maxBatchRounds: remaining, out pendingBatchCount);
                ticksLeftThisToil = packOneRoundToil.defaultDuration;
            };
            packOneRoundToil.defaultCompleteMode = ToilCompleteMode.Delay;
            packOneRoundToil.AddFinishAction(() =>
            {
                int batch = Math.Max(1, pendingBatchCount);
                for (int b = 0; b < batch; b++)
                {
                    if (workingMag == null || workingMag.Destroyed || workingMag.IsFull) break;
                    AmmoDef nextAmmo = PickNextLooseAmmoForFieldPack();
                    if (nextAmmo == null) break;
                    if (MagazineUtility.ConsumeLooseAmmoFromInventory(pawn, nextAmmo, 1) == 1)
                    {
                        workingMag.rounds.Add(nextAmmo);
                        nextSlotToFill = workingMag.LoadedCount;
                        roundsPackedSoFar++;
                        MagazineUtility.TryApplyPackingThumbInjury(pawn, isFieldPacking: true);
                    }
                    else
                    {
                        break;
                    }
                }
            });
            yield return packOneRoundToil.WithProgressBar(TargetIndex.A, () =>
            {
                if (workingMag == null || workingMag.capacity <= 0) return 0f;
                return (float)workingMag.LoadedCount / workingMag.capacity;
            });

            // Loop until magazine is full or no more compatible loose ammo remains
            yield return Toils_Jump.JumpIf(packOneRoundToil, () =>
                workingMag != null && !workingMag.Destroyed && !workingMag.IsFull && PickNextLooseAmmoForFieldPack() != null);

            // Step 3: Insert the freshly packed magazine into the weapon
            Toil insertWaitToil = new Toil { actor = pawn };
            insertWaitToil.initAction = () =>
            {
                if (!MoveWhileReloadUtility.CanMoveWhileReloading(insertWaitToil.actor))
                {
                    insertWaitToil.actor.pather?.StopDead();
                }
                float reloadTime = CompAmmo?.Props?.reloadTime ?? 1.5f;
                if (CE_Utility.GetPrimaryVerbPropsCE(Weapon)?.useEquipmentStatValues ?? false)
                {
                    reloadTime = Weapon.GetStatValue(CE_StatDefOf.ReloadTime);
                }
                float reloadFactor = Weapon.GetStatValue(CE_StatDefOf.CE_RangedWeapon_ReloadFactor);
                float speed = Mathf.Max(0.1f, pawn.GetStatValue(CE_StatDefOf.ReloadSpeed));
                insertWaitToil.defaultDuration = Mathf.Max(15, Mathf.CeilToInt(GenTicks.SecondsToTicks(reloadTime) * reloadFactor / speed));
                ticksLeftThisToil = insertWaitToil.defaultDuration;
            };
            insertWaitToil.defaultCompleteMode = ToilCompleteMode.Delay;
            yield return insertWaitToil.WithProgressBarToilDelay(TargetIndex.A);

            Toil insertFinishToil = new Toil();
            insertFinishToil.initAction = () =>
            {
                var cm = CompMag;
                if (cm == null || workingMag == null || workingMag.Destroyed || workingMag.IsEmpty) return;
                cm.InsertMagazine(workingMag, pawn);
                if (Weapon.def.soundInteract != null && pawn.Map != null)
                {
                    Weapon.def.soundInteract.PlayOneShot(new TargetInfo(pawn.Position, pawn.Map));
                }
            };
            insertFinishToil.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return insertFinishToil;
        }
    }
}
