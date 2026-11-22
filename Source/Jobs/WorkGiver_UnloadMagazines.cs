using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace CE_MagazineFed
{
    /// <summary>
    /// Dispatches hauling colonists to batch-unload any Thing_CEMagazine marked with designatedForUnload.
    /// </summary>
    public class WorkGiver_UnloadMagazines : WorkGiver_Scanner
    {
        public override ThingRequest PotentialWorkThingRequest => ThingRequest.ForDef(CEMF_DefOf.CEMF_Magazine);

        public override PathEndMode PathEndMode => PathEndMode.ClosestTouch;

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
        {
            if (pawn?.Map == null || CEMF_DefOf.CEMF_Magazine == null) yield break;
            var list = pawn.Map.listerThings.ThingsOfDef(CEMF_DefOf.CEMF_Magazine);
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] is Thing_CEMagazine mag && mag.designatedForUnload && !mag.IsEmpty)
                {
                    yield return mag;
                }
            }
        }

        public override bool ShouldSkip(Pawn pawn, bool forced = false)
        {
            if (pawn?.Map == null || CEMF_DefOf.CEMF_Magazine == null) return true;
            var list = pawn.Map.listerThings.ThingsOfDef(CEMF_DefOf.CEMF_Magazine);
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] is Thing_CEMagazine mag && mag.designatedForUnload && !mag.IsEmpty)
                {
                    return false;
                }
            }
            return true;
        }

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            if (!(t is Thing_CEMagazine mag)) return false;
            if (!mag.designatedForUnload || mag.IsEmpty) return false;
            if (mag.IsForbidden(pawn) || mag.IsBurning()) return false;
            return pawn.CanReserve(mag, 1, -1, null, forced);
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            return JobMaker.MakeJob(CEMF_DefOf.CEMF_UnloadMagazine, t);
        }
    }
}
