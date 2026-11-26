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
        private bool ejectOnly;

        private bool IsEjectOnly => ejectOnly || (job != null && job.count == 1 && TargetMag == null);

        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref reloadingEquipped, "reloadingEquipped", false);
            Scribe_Values.Look(ref reloadingInventory, "reloadingInventory", false);
            Scribe_Values.Look(ref ejectOnly, "ejectOnly", false);
            Scribe_References.Look(ref initPrimary, "initPrimary");
        }

        public override string GetReport()
        {
            if (Weapon != null)
            {
                string key = IsEjectOnly ? "CEMF_Mote_EjectingMag" : "CEMF_Mote_SwappingMag";
                return $"{key.Translate()} ({Weapon.LabelShort})";
            }
            return base.GetReport();
        }

        private bool IsInvalidState()
        {
            if (Weapon == null || CompAmmo == null || CompMag == null) return true;
            if (reloadingEquipped && pawn.equipment?.Primary != Weapon) return true;
            if (reloadingInventory && (pawn.inventory == null || !pawn.inventory.innerContainer.Contains(Weapon))) return true;
            if (!reloadingEquipped && !reloadingInventory) return true;
            if (initPrimary != pawn.equipment?.Primary) return true;
            if (IsEjectOnly)
            {
                return !CompMag.hasInsertedMagazine && CompAmmo.CurMagCount <= 0;
            }
            if (TargetMag != null && (TargetMag.Destroyed || TargetMag.IsEmpty || !pawn.inventory.innerContainer.Contains(TargetMag)))
            {
                return !MagazineUtility.HasLoadedMagazineInInventory(pawn, CompAmmo.CurAmmoSet, CompAmmo.MagSize);
            }
            return !MagazineUtility.HasLoadedMagazineInInventory(pawn, CompAmmo.CurAmmoSet, CompAmmo.MagSize);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            if (!reloadingEquipped && !reloadingInventory)
            {
                reloadingEquipped = pawn.equipment?.Primary == Weapon;
                reloadingInventory = pawn.inventory != null && pawn.inventory.innerContainer.Contains(Weapon);
                initPrimary = pawn.equipment?.Primary;
            }
            if (job != null && job.count == 1 && TargetMag == null)
            {
                ejectOnly = true;
            }

            this.FailOnDespawnedOrNull(TargetIndex.A);
            this.FailOnDestroyedOrNull(TargetIndex.B);
            if (pawn.MentalStateDef != MentalStateDefOf.Berserk && pawn.MentalStateDef != MentalStateDefOf.BerserkMechanoid)
            {
                this.FailOnMentalState(TargetIndex.A);
            }
            this.FailOn(IsInvalidState);

            if (CompAmmo != null && CompAmmo.ShouldThrowMote && pawn.Map != null)
            {
                string moteKey = IsEjectOnly ? "CEMF_Mote_EjectingMag" : "CEMF_Mote_SwappingMag";
                MoteMaker.ThrowText(pawn.Position.ToVector3Shifted(), pawn.Map, moteKey.Translate());
            }

            // Wait toil matching CE weapon reload time (or 0.35x for magazine ejection)
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
            float mult = IsEjectOnly ? 0.35f : 1.0f;
            int minTicks = IsEjectOnly ? 12 : 15;
            waitToil.defaultDuration = Mathf.Max(minTicks, Mathf.CeilToInt(GenTicks.SecondsToTicks(reloadTime * mult) * reloadFactor / pawnReloadSpeed));

            yield return waitToil.WithProgressBarToilDelay(TargetIndex.A);

            Toil swapToil = new Toil();
            swapToil.initAction = () =>
            {
                var ca = CompAmmo;
                var cm = CompMag;
                if (ca == null || cm == null) return;

                if (IsEjectOnly)
                {
                    if (cm.hasInsertedMagazine || ca.CurMagCount > 0)
                    {
                        cm.EjectInsertedMagazine(pawn);
                        if (Weapon.def.soundInteract != null && pawn.Map != null)
                        {
                            Weapon.def.soundInteract.PlayOneShot(new TargetInfo(pawn.Position, pawn.Map));
                        }
                    }
                    return;
                }

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
