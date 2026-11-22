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
    /// Fast tactical magazine swap: ejects the currently inserted magazine (preserving remaining rounds)
    /// into inventory and inserts a loaded compatible magazine from inventory.
    /// TargetA = Pawn, TargetB = Weapon, TargetC (optional) = specific Thing_CEMagazine to insert.
    /// </summary>
    public class JobDriver_ReloadMagazine : JobDriver
    {
        private ThingWithComps Weapon => TargetThingB as ThingWithComps;
        private Thing_CEMagazine TargetMag => TargetThingC as Thing_CEMagazine;
        private CompAmmoUser CompAmmo => Weapon?.TryGetComp<CompAmmoUser>();
        private CompWeaponMagazine CompMag => Weapon?.TryGetComp<CompWeaponMagazine>();

        private ThingWithComps initPrimary;
        private bool reloadingEquipped;
        private bool reloadingInventory;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

        public override string GetReport()
        {
            if (Weapon != null)
            {
                return $"{"CEMF_Mote_SwappingMag".Translate()} ({Weapon.LabelShort})";
            }
            return base.GetReport();
        }

        private bool IsInvalidState()
        {
            if (Weapon == null || CompAmmo == null || CompMag == null) return true;
            if (reloadingEquipped && pawn.equipment?.Primary != Weapon) return true;
            if (reloadingInventory && (pawn.inventory == null || !pawn.inventory.innerContainer.Contains(Weapon))) return true;
            if (initPrimary != pawn.equipment?.Primary) return true;
            if (TargetMag != null && (TargetMag.Destroyed || TargetMag.IsEmpty || !pawn.inventory.innerContainer.Contains(TargetMag)))
            {
                return !MagazineUtility.HasLoadedMagazineInInventory(pawn, CompAmmo.CurAmmoSet, CompAmmo.MagSize);
            }
            return !MagazineUtility.HasLoadedMagazineInInventory(pawn, CompAmmo.CurAmmoSet, CompAmmo.MagSize);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            reloadingEquipped = pawn.equipment?.Primary == Weapon;
            reloadingInventory = pawn.inventory != null && pawn.inventory.innerContainer.Contains(Weapon);
            initPrimary = pawn.equipment?.Primary;

            this.FailOnDespawnedOrNull(TargetIndex.A);
            this.FailOnDestroyedOrNull(TargetIndex.B);
            if (pawn.MentalStateDef != MentalStateDefOf.Berserk && pawn.MentalStateDef != MentalStateDefOf.BerserkMechanoid)
            {
                this.FailOnMentalState(TargetIndex.A);
            }
            this.FailOn(IsInvalidState);

            if (CompAmmo != null && CompAmmo.ShouldThrowMote && pawn.Map != null)
            {
                MoteMaker.ThrowText(pawn.Position.ToVector3Shifted(), pawn.Map, "CEMF_Mote_SwappingMag".Translate());
            }

            // Wait toil matching CE weapon reload time
            Toil waitToil = new Toil { actor = pawn };
            waitToil.initAction = () => waitToil.actor.pather?.StopDead();
            waitToil.defaultCompleteMode = ToilCompleteMode.Delay;

            float reloadTime = CompAmmo?.Props?.reloadTime ?? 1.5f;
            if (CE_Utility.GetPrimaryVerbPropsCE(Weapon)?.useEquipmentStatValues ?? false)
            {
                reloadTime = Weapon.GetStatValue(CE_StatDefOf.ReloadTime);
            }
            float reloadFactor = Weapon.GetStatValue(CE_StatDefOf.CE_RangedWeapon_ReloadFactor);
            float pawnReloadSpeed = Mathf.Max(0.1f, pawn.GetStatValue(CE_StatDefOf.ReloadSpeed));
            waitToil.defaultDuration = Mathf.Max(15, Mathf.CeilToInt(GenTicks.SecondsToTicks(reloadTime) * reloadFactor / pawnReloadSpeed));

            yield return waitToil.WithProgressBarToilDelay(TargetIndex.A);

            Toil swapToil = new Toil();
            swapToil.initAction = () =>
            {
                var ca = CompAmmo;
                var cm = CompMag;
                if (ca == null || cm == null) return;

                Thing_CEMagazine magToInsert = null;
                if (TargetMag != null && !TargetMag.Destroyed && !TargetMag.IsEmpty && pawn.inventory.innerContainer.Contains(TargetMag))
                {
                    magToInsert = TargetMag;
                }
                else
                {
                    magToInsert = MagazineUtility.FindBestLoadedMagazine(pawn, ca.CurAmmoSet, ca.MagSize, ca.SelectedAmmo);
                }

                if (magToInsert == null) return;

                cm.InsertMagazine(magToInsert, pawn);
                cm.pendingSelectedMag = null;

                if (Weapon.def.soundInteract != null && pawn.Map != null)
                {
                    Weapon.def.soundInteract.PlayOneShot(new TargetInfo(pawn.Position, pawn.Map));
                }
            };
            swapToil.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return swapToil;
        }
    }
}
