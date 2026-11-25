using System;
using System.Collections.Generic;
using System.Linq;
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

    public enum WeaponFeedModeOverride
    {
        Auto = 0,
        MagazineFed = 1,
        SingleLoad = 2,
        VanillaCE = 3
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

        /// <summary>
        /// Per-weapon feeding mode override keyed by weapon ThingDef.defName. Only non-Auto entries are stored.
        /// </summary>
        public Dictionary<string, WeaponFeedModeOverride> weaponFeedOverrides = new Dictionary<string, WeaponFeedModeOverride>();
        private List<string> scribeOverrideKeys;
        private List<WeaponFeedModeOverride> scribeOverrideValues;

        public int TicksPerRoundIdle => Mathf.Max(2, Mathf.RoundToInt(secondsPerRoundIdle * 60f));
        public int TicksPerRoundField => Mathf.Max(4, Mathf.RoundToInt(secondsPerRoundIdle * fieldPackTimeMultiplier * 60f));
        public int EmptyMagCleanupTicks => Mathf.Max(60, Mathf.RoundToInt(emptyMagCleanupSeconds * 60f));

        public WeaponFeedModeOverride GetWeaponOverride(ThingDef def)
        {
            if (def == null || weaponFeedOverrides == null) return WeaponFeedModeOverride.Auto;
            return weaponFeedOverrides.TryGetValue(def.defName, out var mode) ? mode : WeaponFeedModeOverride.Auto;
        }

        public void SetWeaponOverride(ThingDef def, WeaponFeedModeOverride mode)
        {
            if (def == null) return;
            if (weaponFeedOverrides == null) weaponFeedOverrides = new Dictionary<string, WeaponFeedModeOverride>();
            if (mode == WeaponFeedModeOverride.Auto)
            {
                weaponFeedOverrides.Remove(def.defName);
            }
            else
            {
                weaponFeedOverrides[def.defName] = mode;
            }
            MagazineUtility.ApplyWeaponFeedOverride(def);
        }

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
            Scribe_Collections.Look(ref weaponFeedOverrides, "weaponFeedOverrides", LookMode.Value, LookMode.Value, ref scribeOverrideKeys, ref scribeOverrideValues);

            if (Scribe.mode == LoadSaveMode.PostLoadInit && weaponFeedOverrides == null)
            {
                weaponFeedOverrides = new Dictionary<string, WeaponFeedModeOverride>();
            }
        }
    }

    public class CE_MagazineFedMod : Mod
    {
        public static CEMFSettings Settings { get; private set; }
        public static CE_MagazineFedMod Instance { get; private set; }

        // Settings UI state
        private int activeTab = 0; // 0 = General, 1 = Weapons
        private Vector2 generalScroll;
        private Vector2 weaponListScroll;
        private string selectedModFilter = null; // null = All mods, "__MODIFIED__" = only overridden
        private int selectedModeFilter = -1; // -1 = All, 0 = MagFed, 1 = SingleLoad, 2 = VanillaCE, 3 = OverriddenOnly
        private string searchQuery = "";

        private struct WeaponEntry
        {
            public ThingDef def;
            public string modName;
            public string searchBlob;
            public int magSize;
            public string caliberLabel;
            public WeaponFeedModeOverride defaultMode;
            public bool defaultFromXml;
        }

        private List<WeaponEntry> cachedWeapons;
        private List<(string modName, int count)> cachedModSources;

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

        private void EnsureWeaponCache()
        {
            if (cachedWeapons != null) return;
            cachedWeapons = new List<WeaponEntry>();
            var modCounts = new Dictionary<string, int>();

            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (!MagazineUtility.IsEligibleWeaponDef(def, out var props, out var defMode)) continue;

                string modName = def.modContentPack?.Name ?? "Core";
                string cal = props.ammoSet != null ? props.ammoSet.LabelCap.RawText : "";
                string label = def.LabelCap.RawText ?? def.defName;

                MagazineUtility.GetDefaultFeedMode(def, out bool fromXml);
                cachedWeapons.Add(new WeaponEntry
                {
                    def = def,
                    modName = modName,
                    searchBlob = (label + " " + def.defName + " " + cal + " " + modName).ToLowerInvariant(),
                    magSize = props.magazineSize,
                    caliberLabel = cal,
                    defaultMode = defMode,
                    defaultFromXml = fromXml
                });

                if (modCounts.TryGetValue(modName, out int c)) modCounts[modName] = c + 1;
                else modCounts[modName] = 1;
            }

            cachedWeapons.Sort((a, b) =>
            {
                int cmp = string.Compare(a.modName, b.modName, StringComparison.OrdinalIgnoreCase);
                if (cmp != 0) return cmp;
                return string.Compare(a.def.label ?? a.def.defName, b.def.label ?? b.def.defName, StringComparison.OrdinalIgnoreCase);
            });

            cachedModSources = modCounts
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => (kv.Key, kv.Value))
                .ToList();
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            // Top tab buttons
            float tabH = 32f;
            float tabW = 190f;
            Rect tab0 = new Rect(inRect.x, inRect.y, tabW, tabH);
            Rect tab1 = new Rect(tab0.xMax + 8f, inRect.y, tabW + 30f, tabH);

            DrawTabButton(tab0, "CEMF_Settings_TabGeneral".Translate(), activeTab == 0, () => activeTab = 0);
            int overrideCount = Settings.weaponFeedOverrides?.Count ?? 0;
            string tab1Lbl = overrideCount > 0
                ? $"{"CEMF_Settings_TabWeapons".Translate()} ({overrideCount})"
                : "CEMF_Settings_TabWeapons".Translate().RawText;
            DrawTabButton(tab1, tab1Lbl, activeTab == 1, () => activeTab = 1);

            Widgets.DrawLineHorizontal(inRect.x, tab0.yMax + 4f, inRect.width);
            Rect contentRect = new Rect(inRect.x, tab0.yMax + 10f, inRect.width, inRect.height - tabH - 12f);

            if (activeTab == 0)
            {
                DrawGeneralSettings(contentRect);
            }
            else
            {
                DrawWeaponSettings(contentRect);
            }

            base.DoSettingsWindowContents(inRect);
        }

        private static void DrawTabButton(Rect rect, string label, bool selected, Action onClick)
        {
            Color bg = selected ? new Color(0.24f, 0.28f, 0.34f, 1f) : new Color(0.14f, 0.16f, 0.19f, 0.95f);
            Widgets.DrawBoxSolid(rect, Mouse.IsOver(rect) && !selected ? new Color(0.20f, 0.23f, 0.27f, 1f) : bg);
            if (selected)
            {
                Widgets.DrawBoxSolid(new Rect(rect.x, rect.yMax - 3f, rect.width, 3f), new Color(0.85f, 0.65f, 0.25f));
            }
            GUI.color = selected ? Color.white : new Color(0.75f, 0.78f, 0.82f);
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(rect, label);
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
            if (Widgets.ButtonInvisible(rect)) onClick();
        }

        private void DrawGeneralSettings(Rect inRect)
        {
            Rect viewRect = new Rect(0f, 0f, inRect.width - 20f, 640f);
            Widgets.BeginScrollView(inRect, ref generalScroll, viewRect);

            Listing_Standard listing = new Listing_Standard();
            listing.Begin(viewRect);

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
            Widgets.EndScrollView();
        }

        private void DrawWeaponSettings(Rect inRect)
        {
            EnsureWeaponCache();

            float y = inRect.y;

            // Filter Row 1: Mod Source Dropdown | Mode Filter Dropdown | Search Box
            float modBtnW = 280f;
            float modeBtnW = 165f;
            float searchW = inRect.width - modBtnW - modeBtnW - 16f;

            string modBtnLabel = selectedModFilter == null
                ? "CEMF_Settings_FilterModAll".Translate(cachedWeapons.Count).RawText
                : (selectedModFilter == "__MODIFIED__"
                    ? "CEMF_Settings_FilterModModified".Translate(Settings.weaponFeedOverrides?.Count ?? 0).RawText
                    : selectedModFilter);

            if (Widgets.ButtonText(new Rect(inRect.x, y, modBtnW, 28f), $"{modBtnLabel} ▼"))
            {
                var opts = new List<FloatMenuOption>
                {
                    new FloatMenuOption("CEMF_Settings_FilterModAll".Translate(cachedWeapons.Count), () => { selectedModFilter = null; weaponListScroll = Vector2.zero; }),
                    new FloatMenuOption("CEMF_Settings_FilterModModified".Translate(Settings.weaponFeedOverrides?.Count ?? 0), () => { selectedModFilter = "__MODIFIED__"; weaponListScroll = Vector2.zero; })
                };
                foreach (var src in cachedModSources)
                {
                    string captured = src.modName;
                    opts.Add(new FloatMenuOption($"{src.modName} ({src.count})", () => { selectedModFilter = captured; weaponListScroll = Vector2.zero; }));
                }
                Find.WindowStack.Add(new FloatMenu(opts));
            }

            string modeBtnLabel = selectedModeFilter switch
            {
                0 => "CEMF_FeedMode_MagFed".Translate().RawText,
                1 => "CEMF_FeedMode_SingleLoad".Translate().RawText,
                2 => "CEMF_FeedMode_VanillaCE".Translate().RawText,
                3 => "CEMF_Settings_FilterModeOverridden".Translate().RawText,
                _ => "CEMF_Settings_FilterModeAll".Translate().RawText
            };
            if (Widgets.ButtonText(new Rect(inRect.x + modBtnW + 8f, y, modeBtnW, 28f), $"{modeBtnLabel} ▼"))
            {
                var opts = new List<FloatMenuOption>
                {
                    new FloatMenuOption("CEMF_Settings_FilterModeAll".Translate(), () => { selectedModeFilter = -1; weaponListScroll = Vector2.zero; }),
                    new FloatMenuOption("CEMF_FeedMode_MagFed".Translate(), () => { selectedModeFilter = 0; weaponListScroll = Vector2.zero; }),
                    new FloatMenuOption("CEMF_FeedMode_SingleLoad".Translate(), () => { selectedModeFilter = 1; weaponListScroll = Vector2.zero; }),
                    new FloatMenuOption("CEMF_FeedMode_VanillaCE".Translate(), () => { selectedModeFilter = 2; weaponListScroll = Vector2.zero; }),
                    new FloatMenuOption("CEMF_Settings_FilterModeOverridden".Translate(), () => { selectedModeFilter = 3; weaponListScroll = Vector2.zero; })
                };
                Find.WindowStack.Add(new FloatMenu(opts));
            }

            Rect searchRect = new Rect(inRect.x + modBtnW + modeBtnW + 16f, y, searchW, 28f);
            searchQuery = Widgets.TextField(searchRect, searchQuery ?? "");
            if (string.IsNullOrEmpty(searchQuery))
            {
                GUI.color = new Color(0.6f, 0.63f, 0.68f, 0.7f);
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(new Rect(searchRect.x + 6f, searchRect.y, searchRect.width - 10f, searchRect.height), "CEMF_Settings_SearchWeapons".Translate());
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;
            }
            y += 34f;

            // Compute filtered list
            string q = (searchQuery ?? "").Trim().ToLowerInvariant();
            var filtered = new List<WeaponEntry>();
            for (int i = 0; i < cachedWeapons.Count; i++)
            {
                var w = cachedWeapons[i];
                var ov = Settings.GetWeaponOverride(w.def);
                var eff = ov == WeaponFeedModeOverride.Auto ? w.defaultMode : ov;

                if (selectedModFilter == "__MODIFIED__")
                {
                    if (ov == WeaponFeedModeOverride.Auto) continue;
                }
                else if (selectedModFilter != null && w.modName != selectedModFilter)
                {
                    continue;
                }

                if (selectedModeFilter == 0 && eff != WeaponFeedModeOverride.MagazineFed) continue;
                if (selectedModeFilter == 1 && eff != WeaponFeedModeOverride.SingleLoad) continue;
                if (selectedModeFilter == 2 && eff != WeaponFeedModeOverride.VanillaCE) continue;
                if (selectedModeFilter == 3 && ov == WeaponFeedModeOverride.Auto) continue;

                if (q.Length > 0 && !w.searchBlob.Contains(q)) continue;
                filtered.Add(w);
            }

            // Filter Row 2: Match count + Batch action buttons for current filtered weapons
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.76f, 0.80f, 0.86f);
            Widgets.Label(new Rect(inRect.x, y + 4f, 230f, 20f), "CEMF_Settings_ShowingWeapons".Translate(filtered.Count, cachedWeapons.Count));
            GUI.color = Color.white;

            float batchW = 104f;
            float bx = inRect.xMax - batchW * 4f - 18f;
            if (Widgets.ButtonText(new Rect(bx, y, batchW, 24f), "CEMF_Settings_BatchAuto".Translate()))
            {
                for (int i = 0; i < filtered.Count; i++) Settings.SetWeaponOverride(filtered[i].def, WeaponFeedModeOverride.Auto);
            }
            if (Widgets.ButtonText(new Rect(bx + (batchW + 6f), y, batchW, 24f), "CEMF_Settings_BatchMagFed".Translate()))
            {
                for (int i = 0; i < filtered.Count; i++) Settings.SetWeaponOverride(filtered[i].def, WeaponFeedModeOverride.MagazineFed);
            }
            if (Widgets.ButtonText(new Rect(bx + (batchW + 6f) * 2f, y, batchW, 24f), "CEMF_Settings_BatchSingleLoad".Translate()))
            {
                for (int i = 0; i < filtered.Count; i++) Settings.SetWeaponOverride(filtered[i].def, WeaponFeedModeOverride.SingleLoad);
            }
            if (Widgets.ButtonText(new Rect(bx + (batchW + 6f) * 3f, y, batchW, 24f), "CEMF_Settings_BatchVanilla".Translate()))
            {
                for (int i = 0; i < filtered.Count; i++) Settings.SetWeaponOverride(filtered[i].def, WeaponFeedModeOverride.VanillaCE);
            }
            Text.Font = GameFont.Small;
            y += 30f;

            // Virtualized ScrollView for filtered weapons
            Rect listOuter = new Rect(inRect.x, y, inRect.width, inRect.yMax - y);
            Widgets.DrawBoxSolid(listOuter, new Color(0.09f, 0.10f, 0.12f, 0.95f));

            float rowH = 36f;
            float totalH = Mathf.Max(listOuter.height - 4f, filtered.Count * (rowH + 2f) + 4f);
            Rect viewRect = new Rect(0f, 0f, listOuter.width - 20f, totalH);

            Widgets.BeginScrollView(new Rect(listOuter.x + 2f, listOuter.y + 2f, listOuter.width - 4f, listOuter.height - 4f), ref weaponListScroll, viewRect);

            float minVisY = weaponListScroll.y - rowH;
            float maxVisY = weaponListScroll.y + listOuter.height + rowH;

            for (int i = 0; i < filtered.Count; i++)
            {
                float rowY = 2f + i * (rowH + 2f);
                if (rowY < minVisY || rowY > maxVisY) continue;

                var entry = filtered[i];
                var ov = Settings.GetWeaponOverride(entry.def);
                var eff = ov == WeaponFeedModeOverride.Auto ? entry.defaultMode : ov;

                Rect row = new Rect(2f, rowY, viewRect.width - 4f, rowH);
                Color rowBg = (i % 2 == 0) ? new Color(0.14f, 0.15f, 0.17f, 0.95f) : new Color(0.12f, 0.13f, 0.15f, 0.95f);
                Widgets.DrawBoxSolid(row, rowBg);
                if (ov != WeaponFeedModeOverride.Auto)
                {
                    Widgets.DrawBoxSolid(new Rect(row.x, row.y, 4f, row.height), new Color(0.92f, 0.68f, 0.22f));
                }

                // Weapon icon
                Widgets.ThingIcon(new Rect(row.x + 8f, row.y + 3f, 30f, 30f), entry.def);

                // Mode toggle buttons on right (4 buttons)
                float btnAutoW = 54f;
                float btnModeW = 76f;
                float gap = 4f;
                float rightBlockW = btnAutoW + btnModeW * 3f + gap * 3f + 6f;
                float btnStartX = row.xMax - rightBlockW;

                // Weapon Name & Meta on left
                float infoW = btnStartX - (row.x + 44f) - 6f;
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
                Widgets.Label(new Rect(row.x + 44f, row.y + 1f, infoW, 20f), entry.def.LabelCap);

                Text.Font = GameFont.Tiny;
                GUI.color = new Color(0.66f, 0.70f, 0.76f);
                var liveProps = entry.def.GetCompProperties<CompProperties_AmmoUser>();
                float curReload = liveProps?.reloadTime ?? 0f;
                float origReload = MagazineUtility.TryGetOriginalAmmoProps(entry.def, out var origProps) ? origProps.reloadTime : curReload;
                bool reloadChanged = Math.Abs(curReload - origReload) > 0.005f;
                string reloadStr = reloadChanged
                    ? $"{origReload:0.##}s → {curReload:0.0}s"
                    : $"{curReload:0.##}s";
                string meta = $"{entry.modName}  |  {entry.caliberLabel} ({entry.magSize})  |  {reloadStr}";
                Widgets.Label(new Rect(row.x + 44f, row.y + 19f, infoW, 16f), meta);
                GUI.color = Color.white;

                string defaultLabel = entry.defaultFromXml
                    ? $"{GetModeLabel(entry.defaultMode)} (XML)"
                    : GetModeLabel(entry.defaultMode);
                TooltipHandler.TipRegion(new Rect(row.x + 6f, row.y, infoW + 38f, row.height),
                    $"{entry.def.LabelCap} ({entry.def.defName})\nMod: {entry.modName}\n{entry.caliberLabel} × {entry.magSize}\n{"CEMF_Settings_DefaultModeTip".Translate(defaultLabel)}\n{"CEMF_Settings_ReloadTimeTip".Translate(reloadStr)}");

                // 4 segmented buttons: Auto | MagFed | SingleLoad | Vanilla
                float by = row.y + 5f;
                float bh = 26f;
                DrawModeButton(new Rect(btnStartX, by, btnAutoW, bh), "CEMF_FeedMode_Auto".Translate(),
                    ov == WeaponFeedModeOverride.Auto, new Color(0.32f, 0.36f, 0.42f),
                    () => Settings.SetWeaponOverride(entry.def, WeaponFeedModeOverride.Auto));

                float x1 = btnStartX + btnAutoW + gap;
                DrawModeButton(new Rect(x1, by, btnModeW, bh), "CEMF_FeedMode_MagFed".Translate(),
                    eff == WeaponFeedModeOverride.MagazineFed && ov != WeaponFeedModeOverride.Auto,
                    ov == WeaponFeedModeOverride.Auto && eff == WeaponFeedModeOverride.MagazineFed ? new Color(0.42f, 0.34f, 0.18f) : new Color(0.68f, 0.48f, 0.14f),
                    () => Settings.SetWeaponOverride(entry.def, WeaponFeedModeOverride.MagazineFed),
                    ov == WeaponFeedModeOverride.Auto && eff == WeaponFeedModeOverride.MagazineFed);

                float x2 = x1 + btnModeW + gap;
                DrawModeButton(new Rect(x2, by, btnModeW, bh), "CEMF_FeedMode_SingleLoad".Translate(),
                    eff == WeaponFeedModeOverride.SingleLoad && ov != WeaponFeedModeOverride.Auto,
                    ov == WeaponFeedModeOverride.Auto && eff == WeaponFeedModeOverride.SingleLoad ? new Color(0.16f, 0.36f, 0.40f) : new Color(0.18f, 0.56f, 0.62f),
                    () => Settings.SetWeaponOverride(entry.def, WeaponFeedModeOverride.SingleLoad),
                    ov == WeaponFeedModeOverride.Auto && eff == WeaponFeedModeOverride.SingleLoad);

                float x3 = x2 + btnModeW + gap;
                DrawModeButton(new Rect(x3, by, btnModeW, bh), "CEMF_FeedMode_VanillaCE".Translate(),
                    ov == WeaponFeedModeOverride.VanillaCE,
                    ov == WeaponFeedModeOverride.Auto && eff == WeaponFeedModeOverride.VanillaCE ? new Color(0.34f, 0.24f, 0.24f) : new Color(0.45f, 0.30f, 0.30f),
                    () => Settings.SetWeaponOverride(entry.def, WeaponFeedModeOverride.VanillaCE),
                    ov == WeaponFeedModeOverride.Auto && eff == WeaponFeedModeOverride.VanillaCE);

                Text.Font = GameFont.Small;
            }

            Widgets.EndScrollView();
        }

        private static void DrawModeButton(Rect rect, string label, bool active, Color activeColor, Action onClick, bool softActive = false)
        {
            Color bg = active
                ? activeColor
                : (softActive ? activeColor : new Color(0.18f, 0.20f, 0.23f, 0.92f));
            if (Mouse.IsOver(rect) && !active)
            {
                bg = new Color(bg.r + 0.08f, bg.g + 0.08f, bg.b + 0.08f, 1f);
            }
            Widgets.DrawBoxSolid(rect, bg);
            if (active)
            {
                GUI.color = new Color(0.95f, 0.95f, 0.95f, 0.85f);
                Widgets.DrawBox(rect, 1);
            }
            GUI.color = (active || softActive) ? Color.white : new Color(0.70f, 0.74f, 0.78f);
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(rect, label);
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
            if (Widgets.ButtonInvisible(rect)) onClick();
        }

        private static string GetModeLabel(WeaponFeedModeOverride mode)
        {
            return mode switch
            {
                WeaponFeedModeOverride.MagazineFed => "CEMF_FeedMode_MagFed".Translate().RawText,
                WeaponFeedModeOverride.SingleLoad => "CEMF_FeedMode_SingleLoad".Translate().RawText,
                WeaponFeedModeOverride.VanillaCE => "CEMF_FeedMode_VanillaCE".Translate().RawText,
                _ => "CEMF_FeedMode_Auto".Translate().RawText
            };
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
            MagazineUtility.CacheOriginalWeaponPropsAndApplyOverrides();
            LongEventHandler.ExecuteWhenFinished(MagazineUtility.CacheOriginalWeaponPropsAndApplyOverrides);
        }
    }
}
