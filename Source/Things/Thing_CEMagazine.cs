using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CombatExtended;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace CE_MagazineFed
{
    public class Thing_CEMagazine : ThingWithComps
    {
        public AmmoSetDef ammoSet;
        public int capacity = 30;
        /// <summary>
        /// Ordered rounds inside this magazine. Index 0 = next round fired (top of magazine).
        /// </summary>
        public List<AmmoDef> rounds = new List<AmmoDef>();
        /// <summary>
        /// Preferred plan index (0..N-1) assigned to this magazine in its owner's config; -1 if unassigned.
        /// </summary>
        public int preferredPlanIndex = -1;

        /// <summary>
        /// If true, hauling colonists will automatically batch-unload this magazine into loose ammo.
        /// </summary>
        public bool designatedForUnload = false;

        /// <summary>
        /// Tick when this empty magazine was first seen unowned on the ground; used for delayed auto-cleanup.
        /// </summary>
        public int emptyOnGroundTick = -1;

        private Graphic cachedCustomGraphic;
        private int cachedGraphicCapacity = -1;
        private AmmoSetDef cachedGraphicAmmoSet;

        public int LoadedCount => rounds?.Count ?? 0;
        public bool IsEmpty => LoadedCount == 0;
        public bool IsFull => LoadedCount >= capacity;
        public AmmoDef NextRound => LoadedCount > 0 ? rounds[0] : null;

        public static Thing_CEMagazine Create(AmmoSetDef ammoSet, int capacity, int planIndex = -1)
        {
            var mag = (Thing_CEMagazine)ThingMaker.MakeThing(CEMF_DefOf.CEMF_Magazine);
            mag.ammoSet = ammoSet;
            mag.capacity = Math.Max(1, capacity);
            mag.rounds = new List<AmmoDef>(mag.capacity);
            mag.preferredPlanIndex = planIndex;
            return mag;
        }

        public bool Matches(AmmoSetDef set, int cap)
        {
            return ammoSet == set && capacity == cap;
        }

        public override Graphic Graphic
        {
            get
            {
                if (cachedCustomGraphic == null || cachedGraphicCapacity != capacity || cachedGraphicAmmoSet != ammoSet)
                {
                    cachedGraphicCapacity = capacity;
                    cachedGraphicAmmoSet = ammoSet;
                    string texPath = CEMF_Textures.GetMagazineTexPath(ammoSet, capacity);
                    cachedCustomGraphic = GraphicDatabase.Get<Graphic_Single>(texPath, ShaderDatabase.Cutout, new Vector2(0.75f, 0.75f), Color.white);
                }
                return cachedCustomGraphic ?? base.Graphic;
            }
        }

        public override string LabelNoCount
        {
            get
            {
                string cal = ammoSet != null ? ammoSet.LabelCap.RawText : "Unknown";
                string baseLbl = "CEMF_MagLabelFormat".Translate(cal, LoadedCount, capacity);
                if (IsEmpty)
                {
                    return $"{baseLbl} [{"CEMF_MagEmpty".Translate()}]";
                }
                string summary = GetCompactAmmoSummary();
                if (!string.IsNullOrEmpty(summary))
                {
                    return $"{baseLbl} [{summary}]";
                }
                return baseLbl;
            }
        }

        public Dictionary<AmmoDef, int> GetAmmoCounts()
        {
            var dict = new Dictionary<AmmoDef, int>();
            if (rounds == null) return dict;
            for (int i = 0; i < rounds.Count; i++)
            {
                var ad = rounds[i];
                if (ad == null) continue;
                if (dict.TryGetValue(ad, out int cur)) dict[ad] = cur + 1;
                else dict[ad] = 1;
            }
            return dict;
        }

        public string GetCompactAmmoSummary()
        {
            var counts = GetAmmoCounts();
            if (counts.Count == 0) return "";
            if (counts.Count == 1)
            {
                var first = counts.First();
                return MagazineUtility.GetShortAmmoLabel(first.Key);
            }
            return string.Join("+", counts.OrderByDescending(kv => kv.Value).Take(3).Select(kv => $"{kv.Value}{MagazineUtility.GetShortAmmoLabel(kv.Key)}"));
        }

        public override string GetInspectString()
        {
            StringBuilder sb = new StringBuilder();
            string baseStr = base.GetInspectString();
            if (!string.IsNullOrEmpty(baseStr))
            {
                sb.AppendLine(baseStr);
            }
            sb.AppendLine("CEMF_MagInspectCaliber".Translate(ammoSet != null ? ammoSet.LabelCap.RawText : "-"));
            sb.AppendLine("CEMF_MagInspectLoaded".Translate(LoadedCount, capacity));
            if (NextRound != null)
            {
                sb.AppendLine("CEMF_MagInspectNext".Translate(NextRound.LabelCap));
                var counts = GetAmmoCounts();
                string breakdown = string.Join(", ", counts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value}x {MagazineUtility.GetShortAmmoLabel(kv.Key)}"));
                sb.AppendLine("CEMF_MagInspectContents".Translate(breakdown));
            }
            if (designatedForUnload && !IsEmpty)
            {
                sb.AppendLine("CEMF_MagMarkedForUnload".Translate());
            }
            return sb.ToString().TrimEndNewlines();
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (var g in base.GetGizmos())
            {
                yield return g;
            }

            if (!Spawned) yield break;

            if (!IsEmpty)
            {
                yield return new Command_Toggle
                {
                    defaultLabel = "CEMF_Gizmo_MarkUnloadLabel".Translate(),
                    defaultDesc = "CEMF_Gizmo_MarkUnloadDesc".Translate(),
                    icon = CEMF_Textures.UnloadMagIcon,
                    isActive = () => designatedForUnload,
                    toggleAction = () =>
                    {
                        designatedForUnload = !designatedForUnload;
                        if (designatedForUnload && this.IsForbidden(Faction.OfPlayer))
                        {
                            this.SetForbidden(false, false);
                        }
                    }
                };
            }
            else
            {
                yield return new Command_Action
                {
                    defaultLabel = "CEMF_Gizmo_RecycleEmptyLabel".Translate(),
                    defaultDesc = "CEMF_Gizmo_RecycleEmptyDesc".Translate(),
                    icon = CEMF_Textures.RecycleEmptyIcon,
                    action = () =>
                    {
                        if (!Destroyed && IsEmpty)
                        {
                            Destroy(DestroyMode.Vanish);
                        }
                    }
                };
            }
        }

        public override IEnumerable<FloatMenuOption> GetFloatMenuOptions(Pawn selPawn)
        {
            foreach (var opt in base.GetFloatMenuOptions(selPawn))
            {
                yield return opt;
            }
            if (selPawn != null && selPawn.IsColonistPlayerControlled && Spawned)
            {
                if (IsEmpty)
                {
                    yield return new FloatMenuOption("CEMF_CannotUnloadEmpty".Translate(), null);
                }
                else if (selPawn.CanReserveAndReach(this, PathEndMode.ClosestTouch, Danger.Deadly))
                {
                    yield return new FloatMenuOption("CEMF_UnloadMagazine".Translate(LabelNoCount), () =>
                    {
                        Job job = JobMaker.MakeJob(CEMF_DefOf.CEMF_UnloadMagazine, this);
                        selPawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                    });
                }
            }
        }

        /// <summary>
        /// Unloads all rounds in this magazine into a pawn's inventory or onto the map at this magazine's position.
        /// </summary>
        public void UnloadAllRounds(Pawn toPawn = null)
        {
            if (rounds == null || rounds.Count == 0) return;
            var counts = GetAmmoCounts();
            rounds.Clear();

            foreach (var kv in counts)
            {
                if (toPawn != null)
                {
                    MagazineUtility.AddOrDropLooseAmmo(toPawn, kv.Key, kv.Value);
                }
                else if (MapHeld != null)
                {
                    int rem = kv.Value;
                    while (rem > 0)
                    {
                        int stack = Math.Min(rem, kv.Key.stackLimit);
                        Thing t = ThingMaker.MakeThing(kv.Key);
                        t.stackCount = stack;
                        GenPlace.TryPlaceThing(t, PositionHeld, MapHeld, ThingPlaceMode.Near);
                        rem -= stack;
                    }
                }
            }
        }

        public float GetEmptyMassDelta()
        {
            // Base ThingDef mass is 0.12f; adjust realistically by magazine capacity
            float targetEmpty = Mathf.Clamp(0.05f + capacity * 0.004f, 0.06f, 0.65f);
            return targetEmpty - 0.12f;
        }

        public float GetLoadedRoundsMass()
        {
            if (rounds == null || rounds.Count == 0) return 0f;
            float sum = 0f;
            for (int i = 0; i < rounds.Count; i++)
            {
                if (rounds[i] != null)
                {
                    sum += rounds[i].GetStatValueAbstract(StatDefOf.Mass);
                }
            }
            return sum;
        }

        public float GetCaliberReferenceBulk()
        {
            if (ammoSet?.ammoTypes != null)
            {
                for (int i = 0; i < ammoSet.ammoTypes.Count; i++)
                {
                    var ad = ammoSet.ammoTypes[i]?.ammo;
                    if (ad != null)
                    {
                        return ad.GetStatValueAbstract(CE_StatDefOf.Bulk);
                    }
                }
            }
            if (rounds != null && rounds.Count > 0 && rounds[0] != null)
            {
                return rounds[0].GetStatValueAbstract(CE_StatDefOf.Bulk);
            }
            return 0.02f;
        }

        /// <summary>
        /// Constant full-magazine bulk (independent of current loaded round count).
        /// Used directly as the Bulk base value via StatWorker.GetBaseValueFor.
        /// </summary>
        public float GetFullMagazineBulk()
        {
            float emptyShellBulk = Mathf.Clamp(0.12f + capacity * 0.009f, 0.15f, 1.8f);
            float fullRoundsBulk = GetCaliberReferenceBulk() * capacity * 0.85f;
            return emptyShellBulk + fullRoundsBulk;
        }

        public float GetLoadedRoundsMarketValue()
        {
            if (rounds == null || rounds.Count == 0) return 0f;
            float sum = 0f;
            for (int i = 0; i < rounds.Count; i++)
            {
                if (rounds[i] != null)
                {
                    sum += rounds[i].GetStatValueAbstract(StatDefOf.MarketValue);
                }
            }
            return sum;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Defs.Look(ref ammoSet, "ammoSet");
            Scribe_Values.Look(ref capacity, "capacity", 30);
            Scribe_Collections.Look(ref rounds, "rounds", LookMode.Def);
            Scribe_Values.Look(ref preferredPlanIndex, "preferredPlanIndex", -1);
            Scribe_Values.Look(ref designatedForUnload, "designatedForUnload", false);
            Scribe_Values.Look(ref emptyOnGroundTick, "emptyOnGroundTick", -1);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (rounds == null) rounds = new List<AmmoDef>();
                rounds.RemoveAll(r => r == null);
                if (capacity < 1) capacity = Math.Max(1, rounds.Count);
            }
        }
    }

    public class StatPart_MagazineMassBulk : StatPart
    {
        public override void TransformValue(StatRequest req, ref float val)
        {
            if (TryGetDelta(req, out float delta))
            {
                val = Mathf.Max(0.01f, val + delta);
            }
        }

        public override string ExplanationPart(StatRequest req)
        {
            if (TryGetDelta(req, out float delta) && Mathf.Abs(delta) > 0.0001f)
            {
                return "CEMF_StatsReport_MagContents".Translate() + ": " + parentStat.ValueToString(delta, ToStringNumberSense.Offset);
            }
            return null;
        }

        private bool TryGetDelta(StatRequest req, out float delta)
        {
            delta = 0f;
            if (!req.HasThing || !(req.Thing is Thing_CEMagazine mag)) return false;

            if (parentStat == StatDefOf.Mass)
            {
                delta = mag.GetEmptyMassDelta() + mag.GetLoadedRoundsMass();
                return true;
            }
            return false;
        }
    }

    public class StatPart_MagazineMarketValue : StatPart
    {
        public override void TransformValue(StatRequest req, ref float val)
        {
            if (req.HasThing && req.Thing is Thing_CEMagazine mag)
            {
                val = Mathf.Max(0f, val + mag.GetLoadedRoundsMarketValue());
            }
        }

        public override string ExplanationPart(StatRequest req)
        {
            if (req.HasThing && req.Thing is Thing_CEMagazine mag)
            {
                float ammoVal = mag.GetLoadedRoundsMarketValue();
                if (ammoVal > 0.0001f)
                {
                    return "CEMF_StatsReport_MagMarketValue".Translate() + ": " + parentStat.ValueToString(ammoVal, ToStringNumberSense.Offset);
                }
            }
            return null;
        }
    }
}
