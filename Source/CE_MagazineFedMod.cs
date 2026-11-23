using System;
using System.Collections.Generic;
using System.Reflection;
using CombatExtended;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace CE_MagazineFed
{
    public enum FactionScope
    {
        PlayerOnly = 0,
        AllFactions = 1
    }

    public enum PackingPriorityMode
    {
        IdleOnly = 0,
        BeforeWork = 1,
        ManualOnly = 2
    }

    public class CEMFSettings : ModSettings
    {
        public FactionScope factionScope = FactionScope.PlayerOnly;
        public bool enableForMechanoids = false;
        public PackingPriorityMode packingPriority = PackingPriorityMode.IdleOnly;
        public int defaultMagCount = 4;
        public float secondsPerRoundIdle = 0.15f;
        public float fieldPackTimeMultiplier = 2.0f;
        public bool autoProvisionEmptyMags = true;
        public bool preferRecycleMapMagazines = true;
        public bool preventAutoPickupLooseAmmo = true;
        public bool fetchAmmoFromStockpile = true;
        public bool autoCleanupEmptyGroundMags = true;
        public float emptyMagCleanupSeconds = 15f;
        public bool destroyShellOnUnload = true;
        public bool showMultiColorGizmoBar = true;

        public int TicksPerRoundIdle => Mathf.Max(2, Mathf.RoundToInt(secondsPerRoundIdle * 60f));
        public int TicksPerRoundField => Mathf.Max(4, Mathf.RoundToInt(secondsPerRoundIdle * fieldPackTimeMultiplier * 60f));
        public int EmptyMagCleanupTicks => Mathf.Max(60, Mathf.RoundToInt(emptyMagCleanupSeconds * 60f));

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref factionScope, "factionScope", FactionScope.PlayerOnly);
            Scribe_Values.Look(ref enableForMechanoids, "enableForMechanoids", false);
            Scribe_Values.Look(ref packingPriority, "packingPriority", PackingPriorityMode.IdleOnly);
            Scribe_Values.Look(ref defaultMagCount, "defaultMagCount", 4);
            Scribe_Values.Look(ref secondsPerRoundIdle, "secondsPerRoundIdle", 0.15f);
            Scribe_Values.Look(ref fieldPackTimeMultiplier, "fieldPackTimeMultiplier", 2.0f);
            Scribe_Values.Look(ref autoProvisionEmptyMags, "autoProvisionEmptyMags", true);
            Scribe_Values.Look(ref preferRecycleMapMagazines, "preferRecycleMapMagazines", true);
            Scribe_Values.Look(ref preventAutoPickupLooseAmmo, "preventAutoPickupLooseAmmo", true);
            Scribe_Values.Look(ref fetchAmmoFromStockpile, "fetchAmmoFromStockpile", true);
            Scribe_Values.Look(ref autoCleanupEmptyGroundMags, "autoCleanupEmptyGroundMags", true);
            Scribe_Values.Look(ref emptyMagCleanupSeconds, "emptyMagCleanupSeconds", 15f);
            Scribe_Values.Look(ref destroyShellOnUnload, "destroyShellOnUnload", true);
            Scribe_Values.Look(ref showMultiColorGizmoBar, "showMultiColorGizmoBar", true);
        }
    }

    public class CE_MagazineFedMod : Mod
    {
        public static CEMFSettings Settings { get; private set; }
        public static CE_MagazineFedMod Instance { get; private set; }

        public CE_MagazineFedMod(ModContentPack content) : base(content)
        {
            Instance = this;
            Settings = GetSettings<CEMFSettings>();
            var harmony = new Harmony("kuwa.ce.magazinefed");
            harmony.PatchAll(Assembly.GetExecutingAssembly());
            Log.Message("[CE_MagazineFed] Initialized and Harmony patches applied.");
        }

        public override string SettingsCategory()
        {
            return "CEMF_ModName".Translate();
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);

            listing.Label("CEMF_Settings_FactionScope".Translate());
            if (listing.RadioButton("CEMF_Settings_FactionScope_PlayerOnly".Translate(), Settings.factionScope == FactionScope.PlayerOnly))
            {
                Settings.factionScope = FactionScope.PlayerOnly;
            }
            if (listing.RadioButton("CEMF_Settings_FactionScope_AllFactions".Translate(), Settings.factionScope == FactionScope.AllFactions))
            {
                Settings.factionScope = FactionScope.AllFactions;
            }
            listing.CheckboxLabeled("CEMF_Settings_EnableForMechanoids".Translate(), ref Settings.enableForMechanoids);

            listing.Gap(10f);
            listing.Label("CEMF_Settings_PackingPriority".Translate());
            if (listing.RadioButton("CEMF_Settings_PackingPriority_IdleOnly".Translate(), Settings.packingPriority == PackingPriorityMode.IdleOnly))
            {
                Settings.packingPriority = PackingPriorityMode.IdleOnly;
            }
            if (listing.RadioButton("CEMF_Settings_PackingPriority_BeforeWork".Translate(), Settings.packingPriority == PackingPriorityMode.BeforeWork))
            {
                Settings.packingPriority = PackingPriorityMode.BeforeWork;
            }
            if (listing.RadioButton("CEMF_Settings_PackingPriority_ManualOnly".Translate(), Settings.packingPriority == PackingPriorityMode.ManualOnly))
            {
                Settings.packingPriority = PackingPriorityMode.ManualOnly;
            }

            listing.Gap(12f);
            listing.Label("CEMF_Settings_DefaultMagCount".Translate(Settings.defaultMagCount));
            Settings.defaultMagCount = Mathf.RoundToInt(listing.Slider(Settings.defaultMagCount, 1f, 12f));

            listing.Label("CEMF_Settings_TicksPerRound".Translate(Settings.secondsPerRoundIdle.ToString("0.00")));
            Settings.secondsPerRoundIdle = (float)Math.Round(listing.Slider(Settings.secondsPerRoundIdle, 0.05f, 1.0f), 2);

            listing.Label("CEMF_Settings_FieldPackMult".Translate(Settings.fieldPackTimeMultiplier.ToString("0.0")));
            Settings.fieldPackTimeMultiplier = (float)Math.Round(listing.Slider(Settings.fieldPackTimeMultiplier, 1.0f, 5.0f), 1);

            listing.Gap(10f);
            listing.CheckboxLabeled("CEMF_Settings_AutoProvision".Translate(), ref Settings.autoProvisionEmptyMags);
            listing.CheckboxLabeled("CEMF_Settings_PreferRecycleMapMags".Translate(), ref Settings.preferRecycleMapMagazines);
            listing.CheckboxLabeled("CEMF_Settings_PreventAutoPickupLooseAmmo".Translate(), ref Settings.preventAutoPickupLooseAmmo);
            listing.CheckboxLabeled("CEMF_Settings_FetchFromStockpile".Translate(), ref Settings.fetchAmmoFromStockpile);
            listing.CheckboxLabeled("CEMF_Settings_AutoCleanupEmptyMags".Translate(), ref Settings.autoCleanupEmptyGroundMags);
            if (Settings.autoCleanupEmptyGroundMags)
            {
                listing.Label("CEMF_Settings_EmptyCleanupSeconds".Translate(Mathf.RoundToInt(Settings.emptyMagCleanupSeconds)));
                Settings.emptyMagCleanupSeconds = Mathf.Round(listing.Slider(Settings.emptyMagCleanupSeconds, 3f, 120f));
            }
            listing.CheckboxLabeled("CEMF_Settings_DestroyShellOnUnload".Translate(), ref Settings.destroyShellOnUnload);
            listing.CheckboxLabeled("CEMF_Settings_ShowMultiColorBar".Translate(), ref Settings.showMultiColorGizmoBar);

            listing.End();
            base.DoSettingsWindowContents(inRect);
        }
    }

    [StaticConstructorOnStartup]
    public static class CEMF_StartupInjector
    {
        static CEMF_StartupInjector()
        {
            int added = 0;
            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (def.comps == null) continue;
                bool hasAmmoUser = false;
                bool hasWeaponMag = false;
                for (int i = 0; i < def.comps.Count; i++)
                {
                    var cp = def.comps[i];
                    if (cp is CompProperties_AmmoUser) hasAmmoUser = true;
                    if (cp is CompProperties_WeaponMagazine) hasWeaponMag = true;
                }
                if (hasAmmoUser && !hasWeaponMag)
                {
                    def.comps.Add(new CompProperties_WeaponMagazine());
                    added++;
                }
            }
            if (added > 0)
            {
                Log.Message($"[CE_MagazineFed] Fallback-injected CompProperties_WeaponMagazine into {added} additional weapon defs.");
            }
        }
    }
}
