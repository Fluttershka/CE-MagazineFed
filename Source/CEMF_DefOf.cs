using CombatExtended;
using RimWorld;
using UnityEngine;
using Verse;

namespace CE_MagazineFed
{
    [DefOf]
    public static class CEMF_DefOf
    {
        public static ThingDef CEMF_Magazine;
        public static JobDef CEMF_ReloadMagazine;
        public static JobDef CEMF_FieldPackAndReload;
        public static JobDef CEMF_PackMagazines;
        public static JobDef CEMF_UnloadMagazine;
        public static JobDef CEMF_PortLoad;

        static CEMF_DefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(CEMF_DefOf));
        }
    }

    [StaticConstructorOnStartup]
    public static class CEMF_Textures
    {
        public static readonly Texture2D ManageMags = ContentFinder<Texture2D>.Get("UI/Buttons/CEMF_ManageMags", false) ?? BaseContent.BadTex;
        public static readonly Texture2D PackNow = ContentFinder<Texture2D>.Get("UI/Buttons/CEMF_PackNow", false) ?? BaseContent.BadTex;
        public static readonly Texture2D PortLoad = ContentFinder<Texture2D>.Get("UI/Buttons/CEMF_PortLoad", false) ?? ManageMags;

        public static readonly Texture2D MagPistolSmall = ContentFinder<Texture2D>.Get("UI/Icons/CEMF_Mag_Pistol_Small", false) ?? BaseContent.BadTex;
        public static readonly Texture2D MagPistolMed = ContentFinder<Texture2D>.Get("UI/Icons/CEMF_Mag_Pistol_Med", false) ?? BaseContent.BadTex;
        public static readonly Texture2D MagPistolDrum = ContentFinder<Texture2D>.Get("UI/Icons/CEMF_Mag_Pistol_Drum", false) ?? BaseContent.BadTex;
        public static readonly Texture2D MagRifleSmall = ContentFinder<Texture2D>.Get("UI/Icons/CEMF_Mag_Rifle_Small", false) ?? BaseContent.BadTex;
        public static readonly Texture2D MagRifleMed = ContentFinder<Texture2D>.Get("UI/Icons/CEMF_Mag_Rifle_Med", false) ?? BaseContent.BadTex;
        public static readonly Texture2D MagRifleDrum = ContentFinder<Texture2D>.Get("UI/Icons/CEMF_Mag_Rifle_Drum", false) ?? BaseContent.BadTex;

        // Legacy aliases kept for backwards compatibility
        public static readonly Texture2D MagPistol = MagPistolSmall;
        public static readonly Texture2D MagRifle = MagRifleMed;
        public static readonly Texture2D MagDrum = MagRifleDrum;

        public static readonly Texture2D TubeShotgun = ContentFinder<Texture2D>.Get("UI/Icons/CEMF_Tube_Shotgun", false) ?? BaseContent.BadTex;
        public static readonly Texture2D HeaderBanner = ContentFinder<Texture2D>.Get("UI/Backgrounds/CEMF_HeaderBanner", false) ?? BaseContent.BadTex;
        public static readonly Texture2D UnloadMagIcon = ContentFinder<Texture2D>.Get("UI/Buttons/CE_AutoLoader_Drop", false) ?? ManageMags;
        public static readonly Texture2D RecycleEmptyIcon = ContentFinder<Texture2D>.Get("UI/Designators/Deconstruct", false) ?? ManageMags;

        public static readonly Texture2D SolidWhite = SolidColorMaterials.NewSolidColorTexture(Color.white);
        public static readonly Texture2D DarkPanelBg = SolidColorMaterials.NewSolidColorTexture(new Color(0.11f, 0.12f, 0.13f, 0.97f));
        public static readonly Texture2D CardBg = SolidColorMaterials.NewSolidColorTexture(new Color(0.15f, 0.16f, 0.18f, 0.96f));
        public static readonly Texture2D CardSelectedBg = SolidColorMaterials.NewSolidColorTexture(new Color(0.23f, 0.26f, 0.29f, 0.98f));
        public static readonly Texture2D BorderColor = SolidColorMaterials.NewSolidColorTexture(new Color(0.34f, 0.36f, 0.39f, 0.92f));

        public static string GetMagazineTexPath(AmmoSetDef ammoSet, int capacity)
        {
            if (MagazineUtility.IsPistolCaliber(ammoSet))
            {
                if (capacity <= 18) return "UI/Icons/CEMF_Mag_Pistol_Small";
                if (capacity >= 50) return "UI/Icons/CEMF_Mag_Pistol_Drum";
                return "UI/Icons/CEMF_Mag_Pistol_Med";
            }

            if (capacity <= 18) return "UI/Icons/CEMF_Mag_Rifle_Small";
            if (capacity >= 50) return "UI/Icons/CEMF_Mag_Rifle_Drum";
            return "UI/Icons/CEMF_Mag_Rifle_Med";
        }

        public static Texture2D GetMagazineIcon(AmmoSetDef ammoSet, int capacity)
        {
            if (MagazineUtility.IsPistolCaliber(ammoSet))
            {
                if (capacity <= 18) return MagPistolSmall;
                if (capacity >= 50) return MagPistolDrum;
                return MagPistolMed;
            }

            if (capacity <= 18) return MagRifleSmall;
            if (capacity >= 50) return MagRifleDrum;
            return MagRifleMed;
        }

        public static Texture2D GetMagazineIcon(int capacity)
        {
            return GetMagazineIcon(null, capacity);
        }
    }
}
