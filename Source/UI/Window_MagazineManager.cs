using System;
using System.Collections.Generic;
using System.Linq;
using CombatExtended;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace CE_MagazineFed
{
    public class Window_MagazineManager : Window
    {
        private Pawn selectedPawn;
        private ThingWithComps selectedWeapon;
        private int selectedMagIndex;
        private AmmoDef selectedBrushAmmo;

        // Clipboard for copy/paste across magazines or pawns
        private static AmmoPlan clipboardPlan;

        // Segment / cyclic pattern builder state
        private readonly List<AmmoPatternSegment> segments = new List<AmmoPatternSegment>();
        private AmmoSetDef segmentsCaliber;

        // Scroll positions
        private Vector2 pawnListScroll;
        private Vector2 magListScroll;
        private Vector2 slotGridScroll;
        private Vector2 segmentScroll;
        private Vector2 paletteScroll;

        // Drag-paint state
        private bool isPaintingSlots;

        public override Vector2 InitialSize => new Vector2(1120f, 730f);

        public Window_MagazineManager(Pawn initialPawn = null, ThingWithComps initialWeapon = null)
        {
            doCloseX = true;
            draggable = true;
            resizeable = false;
            absorbInputAroundWindow = false;
            preventCameraMotion = false;

            selectedPawn = initialPawn;
            selectedWeapon = initialWeapon;
            EnsureValidSelection();
        }

        private List<Pawn> GetEligiblePawns()
        {
            var pawns = new List<Pawn>();
            Map map = selectedPawn?.MapHeld ?? Find.CurrentMap;
            if (map != null)
            {
                foreach (Pawn p in map.mapPawns.FreeColonists)
                {
                    if (!p.Dead) pawns.Add(p);
                }
                foreach (Pawn p in map.mapPawns.SpawnedColonyMechs)
                {
                    if (!p.Dead && MagazineUtility.GetManagedWeapons(p).Count > 0) pawns.Add(p);
                }
            }
            if (selectedPawn != null && !selectedPawn.Dead && !pawns.Contains(selectedPawn))
            {
                pawns.Insert(0, selectedPawn);
            }
            return pawns;
        }

        private void EnsureValidSelection()
        {
            var pawns = GetEligiblePawns();
            if (selectedPawn == null || selectedPawn.Dead)
            {
                selectedPawn = pawns.FirstOrDefault(p => MagazineUtility.GetManagedWeapons(p).Count > 0) ?? pawns.FirstOrDefault();
            }

            if (selectedPawn == null)
            {
                selectedWeapon = null;
                return;
            }

            var weapons = MagazineUtility.GetManagedWeapons(selectedPawn);
            if (selectedWeapon == null || !weapons.Contains(selectedWeapon))
            {
                selectedWeapon = weapons.FirstOrDefault();
                selectedMagIndex = 0;
            }

            if (selectedWeapon != null)
            {
                var ca = selectedWeapon.TryGetComp<CompAmmoUser>();
                if (ca?.CurAmmoSet?.ammoTypes != null && ca.CurAmmoSet.ammoTypes.Count > 0)
                {
                    if (selectedBrushAmmo == null || !ca.CurAmmoSet.ammoTypes.Any(a => a.ammo == selectedBrushAmmo))
                    {
                        selectedBrushAmmo = ca.CurrentAmmo ?? ca.CurAmmoSet.ammoTypes[0].ammo;
                    }
                    if (segmentsCaliber != ca.CurAmmoSet)
                    {
                        ResetDefaultSegments(ca.CurAmmoSet);
                    }
                }
            }
        }

        private void ResetDefaultSegments(AmmoSetDef ammoSet)
        {
            segmentsCaliber = ammoSet;
            segments.Clear();
            if (ammoSet?.ammoTypes == null || ammoSet.ammoTypes.Count == 0) return;

            segments.Add(new AmmoPatternSegment(ammoSet.ammoTypes[0].ammo, 3));
            if (ammoSet.ammoTypes.Count > 1)
            {
                segments.Add(new AmmoPatternSegment(ammoSet.ammoTypes[1].ammo, 1));
            }
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (Event.current.type == EventType.MouseUp)
            {
                isPaintingSlots = false;
            }

            EnsureValidSelection();

            // Dark tactical frame backdrop
            GUI.DrawTexture(inRect, CEMF_Textures.DarkPanelBg);

            // 1. Header Banner (62px)
            Rect headerRect = new Rect(inRect.x, inRect.y, inRect.width, 62f);
            DrawHeader(headerRect);

            // 2. Main 3-column body below header
            float bodyY = headerRect.yMax + 8f;
            float bodyH = inRect.yMax - bodyY - 4f;

            float leftW = 210f;
            Rect leftRect = new Rect(inRect.x + 6f, bodyY, leftW, bodyH);
            DrawColonistSidebar(leftRect);

            float rightAreaX = leftRect.xMax + 8f;
            float rightAreaW = inRect.xMax - rightAreaX - 6f;

            if (selectedPawn == null)
            {
                DrawCenteredMessage(new Rect(rightAreaX, bodyY, rightAreaW, bodyH), "CEMF_UI_NoWeapon".Translate());
                return;
            }

            var weapons = MagazineUtility.GetManagedWeapons(selectedPawn);
            if (weapons.Count == 0)
            {
                DrawCenteredMessage(new Rect(rightAreaX, bodyY, rightAreaW, bodyH), "CEMF_UI_NoWeapon".Translate());
                return;
            }

            // Weapon Tabs Strip (36px)
            Rect tabRect = new Rect(rightAreaX, bodyY, rightAreaW, 36f);
            DrawWeaponTabs(tabRect, weapons);

            float contentY = tabRect.yMax + 8f;
            float contentH = inRect.yMax - contentY - 4f;

            var compAmmo = selectedWeapon?.TryGetComp<CompAmmoUser>();
            var compMag = selectedWeapon?.TryGetComp<CompWeaponMagazine>();
            var mgr = GameComponent_MagazineManager.Instance;
            if (compAmmo == null || mgr == null) return;

            var config = mgr.GetOrCreateConfig(selectedPawn, compAmmo);
            if (config == null) return;
            config.EnsurePlansMatchCount(compAmmo.CurrentAmmo);
            if (selectedMagIndex < 0 || selectedMagIndex >= config.magPlans.Count)
            {
                selectedMagIndex = 0;
            }

            // Middle Column: Magazine Roster OR Single-Load Tube Info (275px)
            float midW = 278f;
            Rect midRect = new Rect(rightAreaX, contentY, midW, contentH);
            DrawMiddleColumn(midRect, compAmmo, compMag, config, mgr);

            // Right Column: Visual Bullet Sequence Editor
            float editorX = midRect.xMax + 8f;
            float editorW = inRect.xMax - editorX - 6f;
            Rect editorRect = new Rect(editorX, contentY, editorW, contentH);
            DrawSequenceEditor(editorRect, compAmmo, compMag, config, mgr);
        }

        private void DrawHeader(Rect rect)
        {
            if (CEMF_Textures.HeaderBanner != null && CEMF_Textures.HeaderBanner != BaseContent.BadTex)
            {
                GUI.DrawTexture(rect, CEMF_Textures.HeaderBanner, ScaleMode.ScaleAndCrop);
            }
            else
            {
                Widgets.DrawBoxSolid(rect, new Color(0.09f, 0.12f, 0.16f, 1f));
            }

            // Icon
            Rect iconRect = new Rect(rect.x + 12f, rect.y + 9f, 44f, 44f);
            GUI.DrawTexture(iconRect, CEMF_Textures.ManageMags, ScaleMode.ScaleToFit);

            // Title & Subtitle
            Text.Font = GameFont.Medium;
            GUI.color = Color.white;
            Widgets.Label(new Rect(iconRect.xMax + 10f, rect.y + 6f, 520f, 30f), "CEMF_UI_Title".Translate());

            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.75f, 0.82f, 0.90f);
            string sub = selectedPawn != null
                ? $"{selectedPawn.LabelShortCap}  |  {(selectedWeapon != null ? selectedWeapon.LabelCap : "CEMF_UI_NoWeapon".Translate().RawText)}"
                : "";
            Widgets.Label(new Rect(iconRect.xMax + 10f, rect.y + 36f, 540f, 20f), sub);
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            // "Pack Now" Action Button (leave space for top-right close X)
            if (selectedPawn != null && !selectedPawn.Dead)
            {
                Rect packBtnRect = new Rect(rect.xMax - 196f, rect.y + 13f, 152f, 36f);
                TooltipHandler.TipRegion(packBtnRect, "CEMF_UI_PackNowTip".Translate());
                bool clicked = DrawIconButton(packBtnRect, CEMF_Textures.PackNow, "CEMF_UI_PackNow".Translate(), new Color(0.18f, 0.34f, 0.24f, 0.95f));
                if (clicked)
                {
                    OrderPackNow(selectedPawn);
                }
            }
        }

        private void OrderPackNow(Pawn pawn)
        {
            if (pawn == null || !JobGiver_PackMagazines.CanAttemptPacking(pawn))
            {
                Messages.Message("CEMF_UI_PackNowNotNeeded".Translate(pawn?.LabelShortCap ?? ""), MessageTypeDefOf.RejectInput, false);
                return;
            }
            var mgr = GameComponent_MagazineManager.Instance;
            if (mgr != null)
            {
                foreach (var w in MagazineUtility.GetManagedWeapons(pawn))
                {
                    var ca = w.TryGetComp<CompAmmoUser>();
                    if (MagazineUtility.IsMagazineFedWeapon(ca))
                    {
                        var cfg = mgr.GetOrCreateConfig(pawn, ca);
                        if (cfg != null) mgr.EnsureMagazinesForConfig(pawn, cfg);
                    }
                }
            }
            if (!JobGiver_PackMagazines.HasActionablePackingWork(pawn))
            {
                Messages.Message("CEMF_UI_PackNowNotNeeded".Translate(pawn.LabelShortCap), MessageTypeDefOf.NeutralEvent, false);
                return;
            }
            Job job = JobMaker.MakeJob(CEMF_DefOf.CEMF_PackMagazines, pawn);
            pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            Messages.Message("CEMF_UI_PackNowOrdered".Translate(pawn.LabelShortCap), pawn, MessageTypeDefOf.PositiveEvent, false);
        }

        private void DrawColonistSidebar(Rect rect)
        {
            DrawPanelBox(rect);

            Rect titleRect = new Rect(rect.x + 8f, rect.y + 6f, rect.width - 16f, 24f);
            Text.Font = GameFont.Small;
            GUI.color = new Color(0.86f, 0.90f, 0.96f);
            Widgets.Label(titleRect, "CEMF_UI_Colonists".Translate());
            GUI.color = Color.white;
            Widgets.DrawLineHorizontal(rect.x + 6f, titleRect.yMax + 2f, rect.width - 12f);

            var pawns = GetEligiblePawns();
            Rect listOuter = new Rect(rect.x + 4f, titleRect.yMax + 6f, rect.width - 8f, rect.height - 38f);
            float rowH = 46f;
            Rect viewRect = new Rect(0f, 0f, listOuter.width - 16f, Mathf.Max(listOuter.height, pawns.Count * (rowH + 3f)));

            Widgets.BeginScrollView(listOuter, ref pawnListScroll, viewRect);
            float curY = 0f;
            var mgr = GameComponent_MagazineManager.Instance;

            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn p = pawns[i];
                Rect row = new Rect(0f, curY, viewRect.width, rowH);
                bool isSel = p == selectedPawn;

                GUI.DrawTexture(row, isSel ? CEMF_Textures.CardSelectedBg : CEMF_Textures.CardBg);
                if (Mouse.IsOver(row))
                {
                    Widgets.DrawHighlight(row);
                }

                var weapons = MagazineUtility.GetManagedWeapons(p);
                bool hasManaged = weapons.Count > 0;
                bool needsPack = hasManaged && mgr != null && mgr.PawnNeedsPacking(p, out _);

                // Status indicator bar on left edge
                Color statusCol = !hasManaged
                    ? new Color(0.35f, 0.38f, 0.42f)
                    : (needsPack ? new Color(0.95f, 0.68f, 0.18f) : new Color(0.28f, 0.80f, 0.42f));
                Widgets.DrawBoxSolid(new Rect(row.x, row.y, 4f, row.height), statusCol);

                // Pawn name
                Text.Font = GameFont.Small;
                GUI.color = hasManaged ? Color.white : new Color(0.62f, 0.65f, 0.70f);
                Widgets.Label(new Rect(row.x + 10f, row.y + 3f, row.width - 14f, 22f), p.LabelShortCap);

                // Equipped managed weapon short text
                Text.Font = GameFont.Tiny;
                GUI.color = new Color(0.70f, 0.76f, 0.84f);
                string wText = hasManaged ? weapons[0].LabelShortCap : "-";
                Widgets.Label(new Rect(row.x + 10f, row.y + 24f, row.width - 14f, 18f), wText);
                GUI.color = Color.white;
                Text.Font = GameFont.Small;

                if (Widgets.ButtonInvisible(row))
                {
                    selectedPawn = p;
                    selectedWeapon = weapons.FirstOrDefault();
                    selectedMagIndex = 0;
                    selectedBrushAmmo = null;
                }

                curY += rowH + 3f;
            }
            Widgets.EndScrollView();
        }

        private void DrawWeaponTabs(Rect rect, List<ThingWithComps> weapons)
        {
            float tabW = Mathf.Min(260f, (rect.width - (weapons.Count - 1) * 6f) / Math.Max(1, weapons.Count));
            float x = rect.x;
            for (int i = 0; i < weapons.Count; i++)
            {
                var w = weapons[i];
                var ca = w.TryGetComp<CompAmmoUser>();
                bool isSel = w == selectedWeapon;
                bool isSingle = MagazineUtility.IsSingleLoadMultiCapacityWeapon(ca);

                Rect tab = new Rect(x, rect.y, tabW, rect.height);
                GUI.DrawTexture(tab, isSel ? CEMF_Textures.CardSelectedBg : CEMF_Textures.CardBg);
                if (isSel)
                {
                    Widgets.DrawBoxSolid(new Rect(tab.x, tab.yMax - 3f, tab.width, 3f), isSingle ? new Color(0.22f, 0.80f, 0.85f) : new Color(0.95f, 0.66f, 0.18f));
                }
                else if (Mouse.IsOver(tab))
                {
                    Widgets.DrawHighlight(tab);
                }

                // Weapon icon
                Widgets.ThingIcon(new Rect(tab.x + 4f, tab.y + 4f, 28f, 28f), w);

                Text.Font = GameFont.Tiny;
                GUI.color = Color.white;
                string title = $"{w.LabelShortCap} ({ca.CurMagCount}/{ca.MagSize})";
                Widgets.Label(new Rect(tab.x + 36f, tab.y + 2f, tab.width - 40f, 16f), title);

                GUI.color = isSingle ? new Color(0.45f, 0.90f, 0.95f) : new Color(0.96f, 0.78f, 0.36f);
                string modeStr = isSingle ? "CEMF_UI_Mode_SingleLoad".Translate() : "CEMF_UI_Mode_MagFed".Translate();
                string calStr = ca.CurAmmoSet != null ? ca.CurAmmoSet.LabelCap.RawText : "";
                Widgets.Label(new Rect(tab.x + 36f, tab.y + 17f, tab.width - 40f, 16f), $"[{modeStr}] {calStr}");
                GUI.color = Color.white;
                Text.Font = GameFont.Small;

                if (Widgets.ButtonInvisible(tab))
                {
                    selectedWeapon = w;
                    selectedMagIndex = 0;
                    selectedBrushAmmo = null;
                }

                x += tabW + 6f;
            }
        }

        private void DrawMiddleColumn(Rect rect, CompAmmoUser compAmmo, CompWeaponMagazine compMag, PawnWeaponAmmoConfig config, GameComponent_MagazineManager mgr)
        {
            DrawPanelBox(rect);

            if (config.isSingleLoad)
            {
                DrawSingleLoadMiddlePanel(rect, compAmmo, compMag, config);
                return;
            }

            float pad = 8f;
            float y = rect.y + pad;

            // Total magazine count control
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(rect.x + pad, y, rect.width - pad * 2f, 24f), "CEMF_UI_TotalMags".Translate());
            y += 26f;

            float ctrlW = rect.width - pad * 2f;
            Rect minusBtn = new Rect(rect.x + pad, y, 36f, 28f);
            Rect countBox = new Rect(minusBtn.xMax + 6f, y, ctrlW - 84f, 28f);
            Rect plusBtn = new Rect(countBox.xMax + 6f, y, 36f, 28f);

            if (Widgets.ButtonText(minusBtn, "-") && config.targetTotalMags > 1)
            {
                config.targetTotalMags--;
                config.EnsurePlansMatchCount(compAmmo.CurrentAmmo);
                mgr.EnsureMagazinesForConfig(selectedPawn, config);
                if (selectedMagIndex >= config.targetTotalMags) selectedMagIndex = config.targetTotalMags - 1;
            }

            Widgets.DrawBoxSolid(countBox, new Color(0.08f, 0.10f, 0.13f, 1f));
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(countBox, config.targetTotalMags.ToString());
            Text.Anchor = TextAnchor.UpperLeft;

            if (Widgets.ButtonText(plusBtn, "+") && config.targetTotalMags < 20)
            {
                config.targetTotalMags++;
                config.EnsurePlansMatchCount(compAmmo.CurrentAmmo);
                mgr.EnsureMagazinesForConfig(selectedPawn, config);
            }
            y += 32f;

            // Mass / Bulk preview
            EstimatePlannedWeightBulk(config, out float estMass, out float estBulk);
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.72f, 0.78f, 0.85f);
            Widgets.Label(new Rect(rect.x + pad, y, ctrlW, 30f),
                "CEMF_UI_SparePreview".Translate(Math.Max(0, config.targetTotalMags - 1), estMass.ToString("0.00"), estBulk.ToString("0.00")));
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            y += 32f;

            Widgets.DrawLineHorizontal(rect.x + 6f, y, rect.width - 12f);
            y += 6f;

            // Gather live actual magazines for status mapping
            var invMags = MagazineUtility.GetMagazinesInInventory(selectedPawn, config.ammoSet, config.capacity);
            compMag?.SyncFromCompAmmo();

            // Bottom buttons height = 66px
            float bottomH = 68f;
            Rect listOuter = new Rect(rect.x + 6f, y, rect.width - 12f, rect.yMax - y - bottomH - 6f);
            float cardH = 64f;
            Rect viewRect = new Rect(0f, 0f, listOuter.width - 16f, Mathf.Max(listOuter.height, config.targetTotalMags * (cardH + 4f)));

            Widgets.BeginScrollView(listOuter, ref magListScroll, viewRect);
            float curY = 0f;
            Texture2D magTex = CEMF_Textures.GetMagazineIcon(config.capacity);

            for (int i = 0; i < config.targetTotalMags; i++)
            {
                AmmoPlan plan = config.GetPlan(i);
                List<AmmoDef> actualRounds = GetActualRoundsForSlot(i, compMag, invMags, out bool inGun, out bool exists);

                Rect card = new Rect(0f, curY, viewRect.width, cardH);
                bool isSel = i == selectedMagIndex;
                GUI.DrawTexture(card, isSel ? CEMF_Textures.CardSelectedBg : CEMF_Textures.CardBg);
                if (isSel)
                {
                    Widgets.DrawBoxSolid(new Rect(card.x, card.y, 4f, card.height), new Color(0.96f, 0.70f, 0.20f));
                }
                else if (Mouse.IsOver(card))
                {
                    Widgets.DrawHighlight(card);
                }

                // Mag icon
                GUI.DrawTexture(new Rect(card.x + 8f, card.y + 8f, 34f, 34f), magTex, ScaleMode.ScaleToFit);

                // Label + location badge
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
                string magLbl = "CEMF_UI_MagSlotLabel".Translate(i + 1);
                Widgets.Label(new Rect(card.x + 46f, card.y + 4f, 95f, 22f), magLbl);

                Text.Font = GameFont.Tiny;
                string locBadge = inGun ? "CEMF_UI_MagInGun".Translate() : (exists ? "CEMF_UI_MagInBag".Translate() : "CEMF_UI_MagMissing".Translate());
                GUI.color = inGun ? new Color(0.96f, 0.72f, 0.24f) : (exists ? new Color(0.55f, 0.78f, 0.96f) : new Color(0.55f, 0.55f, 0.58f));
                Text.Anchor = TextAnchor.UpperRight;
                Widgets.Label(new Rect(card.x + 135f, card.y + 6f, card.width - 142f, 18f), locBadge);
                Text.Anchor = TextAnchor.UpperLeft;

                // Status text
                string statusText;
                Color statusColor;
                if (!exists)
                {
                    statusText = "CEMF_UI_MagMissing".Translate();
                    statusColor = new Color(0.58f, 0.60f, 0.64f);
                }
                else if (plan.MatchesExactly(actualRounds))
                {
                    statusText = "CEMF_UI_MagReady".Translate();
                    statusColor = new Color(0.32f, 0.86f, 0.45f);
                }
                else if (actualRounds.Count < config.capacity)
                {
                    statusText = "CEMF_UI_MagNeedPack".Translate(actualRounds.Count, config.capacity);
                    statusColor = new Color(0.95f, 0.76f, 0.24f);
                }
                else
                {
                    statusText = "CEMF_UI_MagMismatch".Translate(actualRounds.Count, config.capacity);
                    statusColor = new Color(0.95f, 0.48f, 0.24f);
                }
                GUI.color = statusColor;
                Widgets.Label(new Rect(card.x + 46f, card.y + 24f, card.width - 52f, 16f), statusText);
                GUI.color = Color.white;

                // Mini Dual Strip: Plan (top 6px) + Actual (bottom 5px)
                DrawColorStrip(new Rect(card.x + 8f, card.y + 45f, card.width - 16f, 6f), plan.slots, config.capacity);
                DrawColorStrip(new Rect(card.x + 8f, card.y + 53f, card.width - 16f, 5f), actualRounds, config.capacity);

                Text.Font = GameFont.Small;

                if (Widgets.ButtonInvisible(card))
                {
                    selectedMagIndex = i;
                }

                curY += cardH + 4f;
            }
            Widgets.EndScrollView();

            // Bottom quick action buttons
            float bY = rect.yMax - bottomH;
            float halfW = (rect.width - 18f) * 0.5f;
            if (Widgets.ButtonText(new Rect(rect.x + 6f, bY, halfW, 28f), "CEMF_UI_CopyPlan".Translate()))
            {
                clipboardPlan = config.GetPlan(selectedMagIndex).Clone();
            }
            bool canPaste = clipboardPlan != null && clipboardPlan.ammoSet == config.ammoSet;
            GUI.color = canPaste ? Color.white : new Color(0.55f, 0.55f, 0.55f);
            if (Widgets.ButtonText(new Rect(rect.x + 12f + halfW, bY, halfW, 28f), "CEMF_UI_PastePlan".Translate()) && canPaste)
            {
                config.GetPlan(selectedMagIndex).CopyFrom(clipboardPlan);
            }
            GUI.color = Color.white;

            if (Widgets.ButtonText(new Rect(rect.x + 6f, bY + 33f, rect.width - 12f, 28f), "CEMF_UI_SyncToAllMags".Translate()))
            {
                var src = config.GetPlan(selectedMagIndex);
                for (int i = 0; i < config.magPlans.Count; i++)
                {
                    if (i != selectedMagIndex)
                    {
                        config.magPlans[i].CopyFrom(src);
                    }
                }
            }
        }

        private void DrawSingleLoadMiddlePanel(Rect rect, CompAmmoUser compAmmo, CompWeaponMagazine compMag, PawnWeaponAmmoConfig config)
        {
            float pad = 10f;
            float y = rect.y + pad;
            float w = rect.width - pad * 2f;

            // Shotgun tube banner icon
            Rect artRect = new Rect(rect.x + (rect.width - 132f) * 0.5f, y, 132f, 132f);
            GUI.DrawTexture(artRect, CEMF_Textures.TubeShotgun, ScaleMode.ScaleToFit);
            y = artRect.yMax + 8f;

            Text.Font = GameFont.Small;
            GUI.color = new Color(0.45f, 0.90f, 0.95f);
            Text.Anchor = TextAnchor.UpperCenter;
            Widgets.Label(new Rect(rect.x + pad, y, w, 24f), "CEMF_UI_SingleLoadHeader".Translate());
            Text.Anchor = TextAnchor.UpperLeft;
            y += 26f;

            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.80f, 0.85f, 0.90f);
            Widgets.Label(new Rect(rect.x + pad, y, w, 68f), "CEMF_UI_SingleLoadDesc".Translate());
            y += 74f;

            Widgets.DrawLineHorizontal(rect.x + 8f, y, rect.width - 16f);
            y += 10f;

            compMag?.SyncFromCompAmmo();
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
            Widgets.Label(new Rect(rect.x + pad, y, w, 24f), "CEMF_UI_SingleLoadCurrent".Translate(compAmmo.CurMagCount, compAmmo.MagSize));
            y += 26f;

            if (compMag != null)
            {
                DrawColorStrip(new Rect(rect.x + pad, y, w, 14f), compMag.loadedRounds, compAmmo.MagSize);
                y += 20f;
                Text.Font = GameFont.Tiny;
                GUI.color = new Color(0.75f, 0.82f, 0.88f);
                Widgets.Label(new Rect(rect.x + pad, y, w, 40f), "CEMF_MagInspectContents".Translate(compMag.GetLoadedBreakdownSummary()));
            }

            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }

        private void DrawSequenceEditor(Rect rect, CompAmmoUser compAmmo, CompWeaponMagazine compMag, PawnWeaponAmmoConfig config, GameComponent_MagazineManager mgr)
        {
            DrawPanelBox(rect);

            float pad = 10f;
            float innerW = rect.width - pad * 2f;
            float y = rect.y + pad;

            AmmoPlan plan = config.GetPlan(config.isSingleLoad ? 0 : selectedMagIndex);
            plan.Normalize();

            List<AmmoDef> actualRounds;
            if (config.isSingleLoad)
            {
                compMag?.SyncFromCompAmmo();
                actualRounds = compMag?.loadedRounds ?? new List<AmmoDef>();
            }
            else
            {
                var invMags = MagazineUtility.GetMagazinesInInventory(selectedPawn, config.ammoSet, config.capacity);
                actualRounds = GetActualRoundsForSlot(selectedMagIndex, compMag, invMags, out _, out _);
            }

            // ---- Section A: Ammo Palette ----
            Text.Font = GameFont.Small;
            GUI.color = new Color(0.88f, 0.92f, 0.98f);
            Widgets.Label(new Rect(rect.x + pad, y, innerW, 22f), "CEMF_UI_AmmoPalette".Translate());
            GUI.color = Color.white;
            y += 24f;

            var ammoLinks = compAmmo.CurAmmoSet?.ammoTypes ?? new List<AmmoLink>();
            float cardW = 128f;
            float cardH = 54f;
            Rect palOuter = new Rect(rect.x + pad, y, innerW, cardH + 16f);
            Rect palView = new Rect(0f, 0f, Mathf.Max(innerW, ammoLinks.Count * (cardW + 5f)), cardH);

            Widgets.BeginScrollView(palOuter, ref paletteScroll, palView);
            float px = 0f;
            Map map = selectedPawn?.MapHeld;
            for (int i = 0; i < ammoLinks.Count; i++)
            {
                AmmoDef ad = ammoLinks[i].ammo;
                if (ad == null) continue;
                bool isSel = ad == selectedBrushAmmo;
                Color col = MagazineUtility.GetAmmoColor(ad);

                Rect pCard = new Rect(px, 0f, cardW, cardH);
                GUI.DrawTexture(pCard, isSel ? CEMF_Textures.CardSelectedBg : CEMF_Textures.CardBg);
                Widgets.DrawBoxSolid(new Rect(pCard.x, pCard.y, pCard.width, 5f), col);
                if (isSel)
                {
                    GUI.color = col;
                    Widgets.DrawBox(pCard, 2);
                    GUI.color = Color.white;
                }
                else if (Mouse.IsOver(pCard))
                {
                    Widgets.DrawHighlight(pCard);
                }

                Widgets.ThingIcon(new Rect(pCard.x + 4f, pCard.y + 10f, 24f, 24f), ad);

                Text.Font = GameFont.Tiny;
                GUI.color = Color.white;
                Widgets.Label(new Rect(pCard.x + 30f, pCard.y + 8f, pCard.width - 34f, 18f), MagazineUtility.GetShortAmmoLabel(ad));

                int invCnt = MagazineUtility.CountLooseAmmoInInventory(selectedPawn, ad);
                int colCnt = MagazineUtility.CountLooseAmmoInColony(map, ad);
                GUI.color = new Color(0.72f, 0.78f, 0.84f);
                Widgets.Label(new Rect(pCard.x + 6f, pCard.y + 33f, pCard.width - 10f, 18f), "CEMF_UI_StockInfo".Translate(invCnt, colCnt));
                GUI.color = Color.white;
                Text.Font = GameFont.Small;

                TooltipHandler.TipRegion(pCard, ad.LabelCap + "\n" + (ad.description ?? ""));

                if (Widgets.ButtonInvisible(pCard))
                {
                    selectedBrushAmmo = ad;
                }
                px += cardW + 5f;
            }
            Widgets.EndScrollView();
            y = palOuter.yMax + 6f;

            // ---- Section B: Visual Bullet Slot Grid ----
            Text.Font = GameFont.Small;
            GUI.color = new Color(0.95f, 0.84f, 0.56f);
            Widgets.Label(new Rect(rect.x + pad, y, innerW, 22f), "CEMF_UI_SequenceHeader".Translate(plan.capacity));
            y += 20f;
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.68f, 0.74f, 0.82f);
            Widgets.Label(new Rect(rect.x + pad, y, innerW, 18f), "CEMF_UI_SequenceHint".Translate());
            GUI.color = Color.white;
            y += 20f;

            float gridHeight = 192f;
            Rect gridOuter = new Rect(rect.x + pad, y, innerW, gridHeight);
            Widgets.DrawBoxSolid(gridOuter, new Color(0.08f, 0.10f, 0.13f, 0.95f));

            int cols = plan.capacity <= 12 ? Math.Max(6, plan.capacity) : (plan.capacity <= 40 ? 10 : (plan.capacity <= 90 ? 15 : 20));
            int rows = Mathf.CeilToInt((float)plan.capacity / cols);
            float cellGap = 3f;
            float availW = gridOuter.width - 20f;
            float cellW = (availW - (cols + 1) * cellGap) / cols;
            float cellH = Mathf.Clamp(cellW * 0.95f, 26f, 44f);
            float totalH = rows * (cellH + cellGap) + cellGap;

            Rect gridView = new Rect(0f, 0f, availW, Mathf.Max(gridOuter.height - 4f, totalH));
            Widgets.BeginScrollView(new Rect(gridOuter.x + 2f, gridOuter.y + 2f, gridOuter.width - 4f, gridOuter.height - 4f), ref slotGridScroll, gridView);

            for (int idx = 0; idx < plan.capacity; idx++)
            {
                int r = idx / cols;
                int c = idx % cols;
                Rect cell = new Rect(cellGap + c * (cellW + cellGap), cellGap + r * (cellH + cellGap), cellW, cellH);

                AmmoDef plannedDef = plan.slots[idx];
                AmmoDef actualDef = idx < actualRounds.Count ? actualRounds[idx] : null;
                Color bgCol = MagazineUtility.GetAmmoColor(plannedDef);

                Widgets.DrawBoxSolid(cell, new Color(bgCol.r * 0.82f, bgCol.g * 0.82f, bgCol.b * 0.82f, 0.95f));

                // Top bullet-tip accent stripe
                Widgets.DrawBoxSolid(new Rect(cell.x, cell.y, cell.width, 4f), bgCol);

                // Slot index top-left
                Text.Font = GameFont.Tiny;
                GUI.color = new Color(0.08f, 0.08f, 0.10f, 0.90f);
                Widgets.Label(new Rect(cell.x + 2f, cell.y + 3f, cell.width - 4f, 15f), (idx + 1).ToString());

                // Short ammo label centered
                if (cellW >= 32f)
                {
                    Text.Anchor = TextAnchor.MiddleCenter;
                    GUI.color = Color.white;
                    string shortLbl = MagazineUtility.GetShortAmmoLabel(plannedDef);
                    if (shortLbl.Length > 5 && cellW < 46f) shortLbl = shortLbl.Substring(0, 5);
                    Widgets.Label(new Rect(cell.x + 1f, cell.y + 11f, cell.width - 2f, cell.height - 15f), shortLbl);
                    Text.Anchor = TextAnchor.UpperLeft;
                }

                // Bottom match indicator pip (green = actual matches planned; dark = empty/mismatch)
                bool slotMatches = actualDef != null && actualDef == plannedDef;
                Color pipCol = slotMatches ? new Color(0.20f, 0.95f, 0.35f) : (actualDef == null ? new Color(0.12f, 0.14f, 0.18f) : new Color(0.95f, 0.25f, 0.22f));
                Widgets.DrawBoxSolid(new Rect(cell.x + 2f, cell.yMax - 4f, cell.width - 4f, 3f), pipCol);

                GUI.color = Color.white;

                if (Mouse.IsOver(cell))
                {
                    Widgets.DrawBox(cell, 1);
                    string actualStr = actualDef != null ? actualDef.LabelCap.RawText : "CEMF_UI_EmptySlot".Translate().RawText;
                    string plannedStr = plannedDef != null ? plannedDef.LabelCap.RawText : "-";
                    TooltipHandler.TipRegion(cell, "CEMF_UI_SlotTooltip".Translate(idx + 1, plannedStr, actualStr));

                    if (Event.current.type == EventType.MouseDown && Event.current.button == 0)
                    {
                        isPaintingSlots = true;
                        if (selectedBrushAmmo != null) plan.slots[idx] = selectedBrushAmmo;
                        Event.current.Use();
                    }
                    else if (isPaintingSlots && Event.current.type == EventType.MouseDrag && Event.current.button == 0)
                    {
                        if (selectedBrushAmmo != null) plan.slots[idx] = selectedBrushAmmo;
                        Event.current.Use();
                    }
                    else if (Event.current.type == EventType.MouseDown && Event.current.button == 1 && ammoLinks.Count > 0)
                    {
                        int curI = ammoLinks.FindIndex(a => a.ammo == plannedDef);
                        int nextI = (curI + 1) % ammoLinks.Count;
                        plan.slots[idx] = ammoLinks[nextI].ammo;
                        Event.current.Use();
                    }
                }
            }
            Widgets.EndScrollView();
            Text.Font = GameFont.Small;

            y = gridOuter.yMax + 6f;

            // Comparison Bars: Planned vs Actual
            Text.Font = GameFont.Tiny;
            float lblW = 104f;
            Widgets.Label(new Rect(rect.x + pad, y, lblW, 16f), "CEMF_UI_PlanBar".Translate());
            DrawColorStrip(new Rect(rect.x + pad + lblW, y + 2f, innerW - lblW, 12f), plan.slots, plan.capacity);
            y += 18f;

            Widgets.Label(new Rect(rect.x + pad, y, lblW, 16f), "CEMF_UI_ActualBar".Translate());
            DrawColorStrip(new Rect(rect.x + pad + lblW, y + 2f, innerW - lblW, 12f), actualRounds, plan.capacity);
            y += 22f;

            // ---- Section C: Quick Tools + Presets Row ----
            Text.Font = GameFont.Small;
            float toolBtnW = (innerW - 18f) * 0.25f;
            if (Widgets.ButtonText(new Rect(rect.x + pad, y, toolBtnW, 28f), "CEMF_UI_FillAll".Translate()))
            {
                if (selectedBrushAmmo != null) plan.FillAll(selectedBrushAmmo);
            }
            if (Widgets.ButtonText(new Rect(rect.x + pad + (toolBtnW + 6f), y, toolBtnW, 28f), "CEMF_UI_ReverseOrder".Translate()))
            {
                plan.ReverseOrder();
            }
            if (Widgets.ButtonText(new Rect(rect.x + pad + (toolBtnW + 6f) * 2f, y, toolBtnW, 28f), "CEMF_UI_SavePreset".Translate()))
            {
                string defName = $"{compAmmo.CurAmmoSet.LabelCap} ({plan.GetCompactSummary()})";
                Find.WindowStack.Add(new Dialog_SavePreset(defName, name =>
                {
                    mgr.SavePreset(name, plan);
                    Messages.Message("CEMF_UI_PresetSaved".Translate(name), MessageTypeDefOf.PositiveEvent, false);
                }));
            }
            if (Widgets.ButtonText(new Rect(rect.x + pad + (toolBtnW + 6f) * 3f, y, toolBtnW, 28f), "CEMF_UI_LoadPreset".Translate() + " ▼"))
            {
                OpenPresetFloatMenu(mgr, compAmmo.CurAmmoSet, plan);
            }
            y += 34f;

            // ---- Section D: Segment & Cyclic Pattern Builder ----
            float summaryH = 22f;
            float segBoxH = rect.yMax - y - summaryH - 8f;
            Rect segBox = new Rect(rect.x + pad, y, innerW, segBoxH);
            Widgets.DrawBoxSolid(segBox, new Color(0.09f, 0.11f, 0.15f, 0.95f));

            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.85f, 0.90f, 0.96f);
            Widgets.Label(new Rect(segBox.x + 8f, segBox.y + 4f, 220f, 18f), "CEMF_UI_SegmentBuilder".Translate());
            GUI.color = Color.white;

            // Top-right segment action buttons
            if (Widgets.ButtonText(new Rect(segBox.xMax - 178f, segBox.y + 3f, 88f, 22f), "CEMF_UI_AddSegment".Translate()))
            {
                AmmoDef def = selectedBrushAmmo ?? ammoLinks.FirstOrDefault()?.ammo;
                if (def != null) segments.Add(new AmmoPatternSegment(def, 1));
            }
            if (Widgets.ButtonText(new Rect(segBox.xMax - 84f, segBox.y + 3f, 78f, 22f), "CEMF_UI_ClearSegments".Translate()))
            {
                segments.Clear();
            }

            // Scrollable segment list
            Rect segListOuter = new Rect(segBox.x + 6f, segBox.y + 28f, segBox.width - 12f, segBox.height - 64f);
            float segRowH = 26f;
            Rect segView = new Rect(0f, 0f, segListOuter.width - 16f, Mathf.Max(segListOuter.height, segments.Count * (segRowH + 3f)));
            Widgets.BeginScrollView(segListOuter, ref segmentScroll, segView);
            float sy = 0f;
            int removeIdx = -1;
            int moveUpIdx = -1;
            int moveDownIdx = -1;

            for (int i = 0; i < segments.Count; i++)
            {
                var seg = segments[i];
                Rect sRow = new Rect(0f, sy, segView.width, segRowH);
                Widgets.DrawBoxSolid(sRow, new Color(0.14f, 0.17f, 0.21f, 0.95f));
                Widgets.DrawBoxSolid(new Rect(sRow.x, sRow.y, 6f, sRow.height), MagazineUtility.GetAmmoColor(seg.ammoDef));

                // Ammo selector dropdown button
                Rect ammoBtn = new Rect(sRow.x + 10f, sRow.y + 2f, 210f, 22f);
                if (Widgets.ButtonText(ammoBtn, (seg.ammoDef != null ? seg.ammoDef.LabelCap.RawText : "-") + " ▼"))
                {
                    int captured = i;
                    var opts = ammoLinks.Select(al => new FloatMenuOption(al.ammo.LabelCap, () => segments[captured].ammoDef = al.ammo)).ToList();
                    Find.WindowStack.Add(new FloatMenu(opts));
                }

                // Quantity stepper
                float qx = ammoBtn.xMax + 10f;
                if (Widgets.ButtonText(new Rect(qx, sRow.y + 2f, 26f, 22f), "-5")) seg.count = Math.Max(1, seg.count - 5);
                if (Widgets.ButtonText(new Rect(qx + 28f, sRow.y + 2f, 24f, 22f), "-")) seg.count = Math.Max(1, seg.count - 1);
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(new Rect(qx + 54f, sRow.y + 2f, 38f, 22f), $"x {seg.count}");
                Text.Anchor = TextAnchor.UpperLeft;
                if (Widgets.ButtonText(new Rect(qx + 94f, sRow.y + 2f, 24f, 22f), "+")) seg.count = Math.Min(plan.capacity, seg.count + 1);
                if (Widgets.ButtonText(new Rect(qx + 120f, sRow.y + 2f, 26f, 22f), "+5")) seg.count = Math.Min(plan.capacity, seg.count + 5);

                // Reorder & remove
                float rx = sRow.xMax - 84f;
                if (Widgets.ButtonText(new Rect(rx, sRow.y + 2f, 24f, 22f), "↑") && i > 0) moveUpIdx = i;
                if (Widgets.ButtonText(new Rect(rx + 27f, sRow.y + 2f, 24f, 22f), "↓") && i < segments.Count - 1) moveDownIdx = i;
                if (Widgets.ButtonText(new Rect(rx + 54f, sRow.y + 2f, 26f, 22f), "×")) removeIdx = i;

                sy += segRowH + 3f;
            }
            Widgets.EndScrollView();

            if (removeIdx >= 0) segments.RemoveAt(removeIdx);
            else if (moveUpIdx > 0)
            {
                var tmp = segments[moveUpIdx - 1];
                segments[moveUpIdx - 1] = segments[moveUpIdx];
                segments[moveUpIdx] = tmp;
            }
            else if (moveDownIdx >= 0 && moveDownIdx < segments.Count - 1)
            {
                var tmp = segments[moveDownIdx + 1];
                segments[moveDownIdx + 1] = segments[moveDownIdx];
                segments[moveDownIdx] = tmp;
            }

            // Apply buttons at bottom of segment box
            Text.Font = GameFont.Small;
            float applyW = (segBox.width - 18f) * 0.5f;
            float applyY = segBox.yMax - 30f;
            if (Widgets.ButtonText(new Rect(segBox.x + 6f, applyY, applyW, 25f), "CEMF_UI_ApplySequential".Translate()))
            {
                plan.ApplySegments(segments, cyclic: false);
            }
            if (Widgets.ButtonText(new Rect(segBox.x + 12f + applyW, applyY, applyW, 25f), "CEMF_UI_ApplyCyclic".Translate()))
            {
                plan.ApplySegments(segments, cyclic: true);
            }

            // Bottom total ammo requirement summary
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.76f, 0.84f, 0.92f);
            var req = config.GetTotalAmmoRequirement();
            string reqStr = string.Join(", ", req.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value}x {MagazineUtility.GetShortAmmoLabel(kv.Key)}"));
            Widgets.Label(new Rect(rect.x + pad, segBox.yMax + 4f, innerW, 20f), "CEMF_UI_SummaryNeed".Translate(reqStr));
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }

        private void OpenPresetFloatMenu(GameComponent_MagazineManager mgr, AmmoSetDef ammoSet, AmmoPlan targetPlan)
        {
            var list = mgr.GetPresetsFor(ammoSet);
            var opts = new List<FloatMenuOption>();
            if (list.Count == 0)
            {
                opts.Add(new FloatMenuOption("CEMF_UI_NoPresets".Translate(), null));
            }
            else
            {
                foreach (var preset in list)
                {
                    var captured = preset;
                    opts.Add(new FloatMenuOption(
                        $"{captured.name} ({captured.plan.GetCompactSummary()})",
                        () => targetPlan.CopyFrom(captured.plan),
                        MenuOptionPriority.Default,
                        null, null, 28f,
                        r =>
                        {
                            if (Widgets.ButtonText(new Rect(r.x + 2f, r.y + 3f, 24f, r.height - 6f), "×"))
                            {
                                mgr.DeletePreset(captured);
                                return true;
                            }
                            return false;
                        }));
                }
            }
            Find.WindowStack.Add(new FloatMenu(opts));
        }

        private static List<AmmoDef> GetActualRoundsForSlot(int slotIndex, CompWeaponMagazine compMag, List<Thing_CEMagazine> invMags, out bool inGun, out bool exists)
        {
            inGun = false;
            exists = false;
            if (compMag != null && compMag.hasInsertedMagazine && compMag.insertedMagPlanIndex == slotIndex)
            {
                inGun = true;
                exists = true;
                return compMag.loadedRounds ?? new List<AmmoDef>();
            }
            for (int i = 0; i < invMags.Count; i++)
            {
                if (invMags[i].preferredPlanIndex == slotIndex)
                {
                    exists = true;
                    return invMags[i].rounds ?? new List<AmmoDef>();
                }
            }
            return new List<AmmoDef>();
        }

        private static void EstimatePlannedWeightBulk(PawnWeaponAmmoConfig config, out float totalMass, out float totalBulk)
        {
            totalMass = 0f;
            totalBulk = 0f;
            float emptyMass = Mathf.Clamp(0.05f + config.capacity * 0.004f, 0.06f, 0.65f);
            float emptyBulk = Mathf.Clamp(0.12f + config.capacity * 0.009f, 0.15f, 1.8f);

            // Spare mags in inventory (targetTotalMags - 1) + inserted mag body
            totalMass += emptyMass * config.targetTotalMags;
            totalBulk += emptyBulk * Math.Max(0, config.targetTotalMags - 1);

            var req = config.GetTotalAmmoRequirement();
            foreach (var kv in req)
            {
                totalMass += kv.Key.GetStatValueAbstract(StatDefOf.Mass) * kv.Value;
                totalBulk += kv.Key.GetStatValueAbstract(CE_StatDefOf.Bulk) * 0.85f * kv.Value;
            }
        }

        private static void DrawColorStrip(Rect rect, List<AmmoDef> rounds, int capacity)
        {
            Widgets.DrawBoxSolid(rect, new Color(0.07f, 0.08f, 0.10f, 1f));
            if (rounds == null || rounds.Count == 0 || capacity <= 0) return;

            float slotW = rect.width / capacity;
            int count = Math.Min(rounds.Count, capacity);
            for (int i = 0; i < count; i++)
            {
                Rect seg = new Rect(rect.x + i * slotW, rect.y, Mathf.Max(1f, slotW), rect.height);
                Widgets.DrawBoxSolid(seg, MagazineUtility.GetAmmoColor(rounds[i]));
            }
        }

        private static void DrawPanelBox(Rect rect)
        {
            Widgets.DrawBoxSolid(rect, new Color(0.12f, 0.14f, 0.18f, 0.96f));
            GUI.color = new Color(0.28f, 0.34f, 0.42f, 0.85f);
            Widgets.DrawBox(rect, 1);
            GUI.color = Color.white;
        }

        private static bool DrawIconButton(Rect rect, Texture2D icon, string label, Color bg)
        {
            Widgets.DrawBoxSolid(rect, Mouse.IsOver(rect) ? new Color(bg.r + 0.08f, bg.g + 0.10f, bg.b + 0.08f, 1f) : bg);
            GUI.color = new Color(0.45f, 0.72f, 0.52f, 0.9f);
            Widgets.DrawBox(rect, 1);
            GUI.color = Color.white;

            if (icon != null && icon != BaseContent.BadTex)
            {
                GUI.DrawTexture(new Rect(rect.x + 6f, rect.y + 4f, 28f, 28f), icon, ScaleMode.ScaleToFit);
            }
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(new Rect(rect.x + 34f, rect.y, rect.width - 38f, rect.height), label);
            Text.Anchor = TextAnchor.UpperLeft;
            return Widgets.ButtonInvisible(rect);
        }

        private static void DrawCenteredMessage(Rect rect, string message)
        {
            DrawPanelBox(rect);
            Text.Anchor = TextAnchor.MiddleCenter;
            GUI.color = new Color(0.75f, 0.80f, 0.86f);
            Widgets.Label(rect, message);
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
        }
    }
}
