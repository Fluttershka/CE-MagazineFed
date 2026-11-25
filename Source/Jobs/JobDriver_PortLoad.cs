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
    /// Tactical single-round Port Load (侧装) for SingleLoad weapons:
    /// - Takes 1.0x the weapon's single-round reloadTime (modified by weapon ReloadFactor and pawn ReloadSpeed).
    /// - If magazine is full, ejects the chambered round (index 0) back into inventory (or ground if full).
    /// - Inserts 1 chosen compatible loose round from inventory directly into index 0 (fired next).
    /// TargetA = Pawn, TargetB = Weapon, TargetC (optional) = Ammo Thing stack in inventory.
    /// </summary>
    public class JobDriver_PortLoad : JobDriver
    {
        private ThingWithComps Weapon => TargetThingB as ThingWithComps;
        private CompAmmoUser CompAmmo => Weapon?.TryGetComp<CompAmmoUser>();
        private CompWeaponMagazine CompMag => Weapon?.TryGetComp<CompWeaponMagazine>();

        private ThingWithComps initPrimary;
        private bool reloadingEquipped;
        private bool reloadingInventory;
        private AmmoDef targetAmmoDef;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Defs.Look(ref targetAmmoDef, "targetAmmoDef");
            Scribe_Values.Look(ref reloadingEquipped, "reloadingEquipped", false);
            Scribe_Values.Look(ref reloadingInventory, "reloadingInventory", false);
            Scribe_References.Look(ref initPrimary, "initPrimary");
        }

        public override string GetReport()
        {
            if (Weapon != null && targetAmmoDef != null)
            {
                return "CEMF_Report_PortLoading".Translate(MagazineUtility.GetShortAmmoLabel(targetAmmoDef), Weapon.LabelShort);
            }
            return base.GetReport();
        }

        private bool IsInvalidState()
        {
            if (Weapon == null || CompAmmo == null || CompMag == null || targetAmmoDef == null) return true;
            if (!MagazineUtility.IsSingleLoadMultiCapacityWeapon(CompAmmo)) return true;
            if (reloadingEquipped && pawn.equipment?.Primary != Weapon) return true;
            if (reloadingInventory && (pawn.inventory == null || !pawn.inventory.innerContainer.Contains(Weapon))) return true;
            if (!reloadingEquipped && !reloadingInventory) return true;
            if (initPrimary != pawn.equipment?.Primary) return true;
            if (MagazineUtility.CountLooseAmmoInInventory(pawn, targetAmmoDef) <= 0) return true;
            return false;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            reloadingEquipped = pawn.equipment?.Primary == Weapon;
            reloadingInventory = pawn.inventory != null && pawn.inventory.innerContainer.Contains(Weapon);
            initPrimary = pawn.equipment?.Primary;

            if (targetAmmoDef == null)
            {
                targetAmmoDef = (TargetThingC?.def as AmmoDef) ?? CompMag?.pendingPortLoadAmmo;
                if (CompMag != null) CompMag.pendingPortLoadAmmo = null;
            }

            this.FailOnDespawnedOrNull(TargetIndex.A);
            this.FailOnDestroyedOrNull(TargetIndex.B);
            if (pawn.MentalStateDef != MentalStateDefOf.Berserk && pawn.MentalStateDef != MentalStateDefOf.BerserkMechanoid)
            {
                this.FailOnMentalState(TargetIndex.A);
            }
            this.FailOn(IsInvalidState);

            if (CompAmmo != null && CompAmmo.ShouldThrowMote && pawn.Map != null && targetAmmoDef != null)
            {
                MoteMaker.ThrowText(
                    pawn.Position.ToVector3Shifted(),
                    pawn.Map,
                    "CEMF_Mote_PortLoading".Translate(MagazineUtility.GetShortAmmoLabel(targetAmmoDef)));
            }

            // Wait toil: always 1.0x the weapon's single-round reloadTime
            Toil waitToil = new Toil { actor = pawn };
            waitToil.initAction = () => waitToil.actor.pather?.StopDead();
            waitToil.defaultCompleteMode = ToilCompleteMode.Delay;

            float reloadTime = CompAmmo?.Props?.reloadTime ?? 1.0f;
            if (CE_Utility.GetPrimaryVerbPropsCE(Weapon)?.useEquipmentStatValues ?? false)
            {
                reloadTime = Weapon.GetStatValue(CE_StatDefOf.ReloadTime);
            }
            float reloadFactor = Weapon.GetStatValue(CE_StatDefOf.CE_RangedWeapon_ReloadFactor);
            float pawnReloadSpeed = Mathf.Max(0.1f, pawn.GetStatValue(CE_StatDefOf.ReloadSpeed));
            waitToil.defaultDuration = Mathf.Max(10, Mathf.CeilToInt(GenTicks.SecondsToTicks(reloadTime) * reloadFactor / pawnReloadSpeed));

            yield return waitToil.WithProgressBarToilDelay(TargetIndex.A);

            Toil portLoadToil = new Toil();
            portLoadToil.initAction = () =>
            {
                var ca = CompAmmo;
                var cm = CompMag;
                if (ca == null || cm == null || targetAmmoDef == null) return;

                cm.SyncFromCompAmmo();

                // Consume 1 chosen round from inventory first (also frees inventory space if full)
                if (MagazineUtility.ConsumeLooseAmmoFromInventory(pawn, targetAmmoDef, 1) != 1)
                {
                    return;
                }

                // If magazine is full, eject chambered round (index 0) back into inventory (or ground if full)
                if (cm.loadedRounds.Count >= ca.MagSize && cm.loadedRounds.Count > 0)
                {
                    AmmoDef ejected = cm.loadedRounds[0];
                    cm.loadedRounds.RemoveAt(0);
                    if (ejected != null)
                    {
                        MagazineUtility.AddOrDropLooseAmmo(pawn, ejected, 1);
                    }
                }

                // Insert the port-loaded round directly at index 0 (next round to fire)
                cm.loadedRounds.Insert(0, targetAmmoDef);
                ca.SelectedAmmo = targetAmmoDef;
                cm.PushToCompAmmo();
                pawn.TryGetComp<CompInventory>()?.UpdateInventory();

                if (Weapon.def.soundInteract != null && pawn.Map != null)
                {
                    Weapon.def.soundInteract.PlayOneShot(new TargetInfo(pawn.Position, pawn.Map));
                }
            };
            portLoadToil.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return portLoadToil;
        }
    }
}
