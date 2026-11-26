using System;
using System.Collections.Generic;
using System.Linq;
using CombatExtended;
using Verse;

namespace CE_MagazineFed
{
    public class AmmoPatternSegment : IExposable
    {
        public AmmoDef ammoDef;
        public int count = 1;

        public AmmoPatternSegment() { }

        public AmmoPatternSegment(AmmoDef ammoDef, int count)
        {
            this.ammoDef = ammoDef;
            this.count = Math.Max(1, count);
        }

        public void ExposeData()
        {
            Scribe_Defs.Look(ref ammoDef, "ammoDef");
            Scribe_Values.Look(ref count, "count", 1);
        }
    }

    /// <summary>
    /// Ordered ammo arrangement for a single magazine or a single-load internal tube/cylinder.
    /// Index 0 = first round fired (top of magazine / next in chamber).
    /// Index capacity - 1 = last round fired (bottom of magazine).
    /// </summary>
    public class AmmoPlan : IExposable
    {
        public AmmoSetDef ammoSet;
        public int capacity;
        public List<AmmoDef> slots = new List<AmmoDef>();

        public AmmoPlan() { }

        public AmmoPlan(AmmoSetDef ammoSet, int capacity, AmmoDef fillAmmo = null)
        {
            this.ammoSet = ammoSet;
            this.capacity = Math.Max(1, capacity);
            AmmoDef defaultAmmo = fillAmmo ?? GetDefaultAmmo();
            slots = new List<AmmoDef>(this.capacity);
            for (int i = 0; i < this.capacity; i++)
            {
                slots.Add(defaultAmmo);
            }
        }

        public AmmoDef GetDefaultAmmo()
        {
            if (ammoSet?.ammoTypes != null && ammoSet.ammoTypes.Count > 0)
            {
                return ammoSet.ammoTypes[0].ammo;
            }
            return null;
        }

        public void Normalize()
        {
            if (slots == null) slots = new List<AmmoDef>();
            if (capacity < 1) capacity = 1;
            AmmoDef fallback = GetDefaultAmmo();
            for (int i = 0; i < slots.Count; i++)
            {
                if (slots[i] == null || !IsValidAmmoForSet(slots[i]))
                {
                    slots[i] = fallback;
                }
            }
            while (slots.Count < capacity)
            {
                slots.Add(fallback);
            }
            if (slots.Count > capacity)
            {
                slots.RemoveRange(capacity, slots.Count - capacity);
            }
        }

        public bool IsValidAmmoForSet(AmmoDef def)
        {
            if (def == null || ammoSet?.ammoTypes == null) return false;
            for (int i = 0; i < ammoSet.ammoTypes.Count; i++)
            {
                if (ammoSet.ammoTypes[i].ammo == def) return true;
            }
            return false;
        }

        public void FillAll(AmmoDef def)
        {
            if (def == null) return;
            Normalize();
            for (int i = 0; i < capacity; i++)
            {
                slots[i] = def;
            }
        }

        public void ReverseOrder()
        {
            Normalize();
            slots.Reverse();
        }

        public void ApplySegments(List<AmmoPatternSegment> segments, bool cyclic)
        {
            Normalize();
            if (segments == null || segments.Count == 0) return;

            var validSegs = new List<AmmoPatternSegment>();
            for (int i = 0; i < segments.Count; i++)
            {
                if (segments[i]?.ammoDef != null && segments[i].count > 0 && IsValidAmmoForSet(segments[i].ammoDef))
                {
                    validSegs.Add(segments[i]);
                }
            }
            if (validSegs.Count == 0) return;

            if (cyclic)
            {
                int idx = 0;
                while (idx < capacity)
                {
                    for (int s = 0; s < validSegs.Count && idx < capacity; s++)
                    {
                        int c = validSegs[s].count;
                        for (int k = 0; k < c && idx < capacity; k++)
                        {
                            slots[idx++] = validSegs[s].ammoDef;
                        }
                    }
                }
            }
            else
            {
                int idx = 0;
                for (int s = 0; s < validSegs.Count && idx < capacity; s++)
                {
                    int c = validSegs[s].count;
                    for (int k = 0; k < c && idx < capacity; k++)
                    {
                        slots[idx++] = validSegs[s].ammoDef;
                    }
                }
                // Fill remaining slots with last segment's ammo
                AmmoDef tail = validSegs[validSegs.Count - 1].ammoDef;
                while (idx < capacity)
                {
                    slots[idx++] = tail;
                }
            }
        }

        public AmmoPlan Clone()
        {
            Normalize();
            var copy = new AmmoPlan
            {
                ammoSet = ammoSet,
                capacity = capacity,
                slots = new List<AmmoDef>(slots)
            };
            return copy;
        }

        public void CopyFrom(AmmoPlan other)
        {
            if (other == null) return;
            other.Normalize();
            Normalize();
            for (int i = 0; i < capacity; i++)
            {
                int srcIdx = i < other.slots.Count ? i : other.slots.Count - 1;
                AmmoDef src = other.slots[srcIdx];
                if (IsValidAmmoForSet(src))
                {
                    slots[i] = src;
                }
            }
        }

        public bool MatchesExactly(List<AmmoDef> actualRounds)
        {
            Normalize();
            if (actualRounds == null || actualRounds.Count != capacity) return false;
            for (int i = 0; i < capacity; i++)
            {
                if (actualRounds[i] != slots[i]) return false;
            }
            return true;
        }

        /// <summary>
        /// Checks if actualRounds is a valid suffix of slots (i.e. normal firing state where front rounds were fired).
        /// </summary>
        public bool IsSuffixMatch(List<AmmoDef> actualRounds)
        {
            Normalize();
            if (actualRounds == null || actualRounds.Count == 0 || actualRounds.Count > capacity) return false;
            int offset = capacity - actualRounds.Count;
            for (int i = 0; i < actualRounds.Count; i++)
            {
                if (actualRounds[i] != slots[offset + i]) return false;
            }
            return true;
        }

        /// <summary>
        /// Checks if actualRounds is a valid prefix of slots (e.g. partially loaded from empty).
        /// </summary>
        public bool IsPrefixMatch(List<AmmoDef> actualRounds)
        {
            Normalize();
            if (actualRounds == null || actualRounds.Count == 0 || actualRounds.Count > capacity) return false;
            for (int i = 0; i < actualRounds.Count; i++)
            {
                if (actualRounds[i] != slots[i]) return false;
            }
            return true;
        }

        public Dictionary<AmmoDef, int> GetCountsByDef()
        {
            Normalize();
            var dict = new Dictionary<AmmoDef, int>();
            for (int i = 0; i < slots.Count; i++)
            {
                var def = slots[i];
                if (def == null) continue;
                if (dict.TryGetValue(def, out int cur)) dict[def] = cur + 1;
                else dict[def] = 1;
            }
            return dict;
        }

        public string GetCompactSummary()
        {
            var counts = GetCountsByDef();
            if (counts.Count == 0) return "";
            return string.Join(", ", counts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value}x{MagazineUtility.GetShortAmmoLabel(kv.Key)}"));
        }

        public void ExposeData()
        {
            Scribe_Defs.Look(ref ammoSet, "ammoSet");
            Scribe_Values.Look(ref capacity, "capacity", 1);
            Scribe_Collections.Look(ref slots, "slots", LookMode.Def);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                Normalize();
            }
        }
    }

    public class AmmoPlanPreset : IExposable
    {
        public string name;
        public AmmoPlan plan;

        public AmmoPlanPreset() { }

        public AmmoPlanPreset(string name, AmmoPlan plan)
        {
            this.name = name;
            this.plan = plan.Clone();
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref name, "name", "Preset");
            Scribe_Deep.Look(ref plan, "plan");
        }
    }

    /// <summary>
    /// Configuration for a specific (AmmoSetDef, capacity, isSingleLoad) weapon class on a pawn.
    /// </summary>
    public class PawnWeaponAmmoConfig : IExposable
    {
        public AmmoSetDef ammoSet;
        public int capacity;
        public bool isSingleLoad;
        public int targetTotalMags = 4;
        public List<AmmoPlan> magPlans = new List<AmmoPlan>();

        public PawnWeaponAmmoConfig() { }

        public PawnWeaponAmmoConfig(AmmoSetDef ammoSet, int capacity, bool isSingleLoad, AmmoDef defaultAmmo = null, int initialMagCount = 4)
        {
            this.ammoSet = ammoSet;
            this.capacity = Math.Max(1, capacity);
            this.isSingleLoad = isSingleLoad;
            this.targetTotalMags = isSingleLoad ? 1 : Math.Max(1, initialMagCount);
            EnsurePlansMatchCount(defaultAmmo);
        }

        public bool Matches(AmmoSetDef set, int cap, bool singleLoad)
        {
            return ammoSet == set && capacity == cap && isSingleLoad == singleLoad;
        }

        public void EnsurePlansMatchCount(AmmoDef defaultAmmo = null)
        {
            if (magPlans == null) magPlans = new List<AmmoPlan>();
            if (isSingleLoad)
            {
                targetTotalMags = 1;
            }
            else
            {
                targetTotalMags = Math.Max(1, Math.Min(20, targetTotalMags));
            }

            while (magPlans.Count < targetTotalMags)
            {
                AmmoPlan template = magPlans.Count > 0 ? magPlans[magPlans.Count - 1].Clone() : new AmmoPlan(ammoSet, capacity, defaultAmmo);
                template.ammoSet = ammoSet;
                template.capacity = capacity;
                template.Normalize();
                magPlans.Add(template);
            }
            if (magPlans.Count > targetTotalMags)
            {
                magPlans.RemoveRange(targetTotalMags, magPlans.Count - targetTotalMags);
            }
            for (int i = 0; i < magPlans.Count; i++)
            {
                if (magPlans[i] == null)
                {
                    magPlans[i] = new AmmoPlan(ammoSet, capacity, defaultAmmo);
                }
                magPlans[i].ammoSet = ammoSet;
                magPlans[i].capacity = capacity;
                magPlans[i].Normalize();
            }
        }

        public AmmoPlan GetPlan(int index)
        {
            EnsurePlansMatchCount();
            if (index < 0 || index >= magPlans.Count) index = 0;
            return magPlans[index];
        }

        public Dictionary<AmmoDef, int> GetTotalAmmoRequirement()
        {
            EnsurePlansMatchCount();
            var total = new Dictionary<AmmoDef, int>();
            for (int i = 0; i < magPlans.Count; i++)
            {
                var sub = magPlans[i].GetCountsByDef();
                foreach (var kv in sub)
                {
                    if (total.TryGetValue(kv.Key, out int cur)) total[kv.Key] = cur + kv.Value;
                    else total[kv.Key] = kv.Value;
                }
            }
            return total;
        }

        public void ExposeData()
        {
            Scribe_Defs.Look(ref ammoSet, "ammoSet");
            Scribe_Values.Look(ref capacity, "capacity", 1);
            Scribe_Values.Look(ref isSingleLoad, "isSingleLoad", false);
            Scribe_Values.Look(ref targetTotalMags, "targetTotalMags", 4);
            Scribe_Collections.Look(ref magPlans, "magPlans", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                EnsurePlansMatchCount();
            }
        }
    }

    public class PawnAmmoManagementData : IExposable
    {
        public List<PawnWeaponAmmoConfig> configs = new List<PawnWeaponAmmoConfig>();

        public PawnWeaponAmmoConfig GetConfig(AmmoSetDef ammoSet, int capacity, bool isSingleLoad)
        {
            if (configs == null) configs = new List<PawnWeaponAmmoConfig>();
            for (int i = 0; i < configs.Count; i++)
            {
                if (configs[i] != null && configs[i].Matches(ammoSet, capacity, isSingleLoad))
                {
                    configs[i].EnsurePlansMatchCount();
                    return configs[i];
                }
            }
            return null;
        }

        public PawnWeaponAmmoConfig GetOrCreateConfig(AmmoSetDef ammoSet, int capacity, bool isSingleLoad, AmmoDef defaultAmmo = null, int? initialMagCountOverride = null)
        {
            var existing = GetConfig(ammoSet, capacity, isSingleLoad);
            if (existing != null) return existing;

            int initMags = isSingleLoad ? 1 : (initialMagCountOverride ?? (CE_MagazineFedMod.Settings?.defaultMagCount ?? 4));
            var created = new PawnWeaponAmmoConfig(ammoSet, capacity, isSingleLoad, defaultAmmo, initMags);
            configs.Add(created);
            return created;
        }

        public void ExposeData()
        {
            Scribe_Collections.Look(ref configs, "configs", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && configs == null)
            {
                configs = new List<PawnWeaponAmmoConfig>();
            }
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                configs.RemoveAll(c => c == null || c.ammoSet == null);
            }
        }
    }
}
