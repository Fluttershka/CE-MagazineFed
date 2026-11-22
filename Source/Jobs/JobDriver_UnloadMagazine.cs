using System.Collections.Generic;
using UnityEngine;
using Verse;
using Verse.AI;

namespace CE_MagazineFed
{
    /// <summary>
    /// Unloads a Thing_CEMagazine on the map into loose AmmoThing stacks.
    /// </summary>
    public class JobDriver_UnloadMagazine : JobDriver
    {
        private Thing_CEMagazine Magazine => TargetThingA as Thing_CEMagazine;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(TargetA, job, 1, -1, null, errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedNullOrForbidden(TargetIndex.A);
            this.FailOn(() => Magazine == null || Magazine.IsEmpty);

            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.ClosestTouch);

            Toil wait = new Toil { actor = pawn };
            wait.initAction = () =>
            {
                int count = Magazine?.LoadedCount ?? 1;
                wait.defaultDuration = Mathf.Clamp(count * 3, 20, 180);
                ticksLeftThisToil = wait.defaultDuration;
            };
            wait.defaultCompleteMode = ToilCompleteMode.Delay;
            yield return wait.WithProgressBarToilDelay(TargetIndex.A);

            Toil finish = new Toil();
            finish.initAction = () =>
            {
                Magazine?.UnloadAllRounds(null);
            };
            finish.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return finish;
        }
    }
}
