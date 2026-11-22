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
        public static readonly Texture2D MagPistol = ContentFinder<Texture2D>.Get("UI/Icons/CEMF_Mag_Pistol", false) ?? BaseContent.BadTex;
        public static readonly Texture2D MagRifle = ContentFinder<Texture2D>.Get("UI/Icons/CEMF_Mag_Rifle", false) ?? BaseContent.BadTex;
        public static readonly Texture2D MagDrum = ContentFinder<Texture2D>.Get("UI/Icons/CEMF_Mag_Drum", false) ?? BaseContent.BadTex;
        public static readonly Texture2D TubeShotgun = ContentFinder<Texture2D>.Get("UI/Icons/CEMF_Tube_Shotgun", false) ?? BaseContent.BadTex;
        public static readonly Texture2D HeaderBanner = ContentFinder<Texture2D>.Get("UI/Backgrounds/CEMF_HeaderBanner", false) ?? BaseContent.BadTex;

        public static readonly Texture2D SolidWhite = SolidColorMaterials.NewSolidColorTexture(Color.white);
        public static readonly Texture2D DarkPanelBg = SolidColorMaterials.NewSolidColorTexture(new Color(0.11f, 0.12f, 0.13f, 0.97f));
        public static readonly Texture2D CardBg = SolidColorMaterials.NewSolidColorTexture(new Color(0.15f, 0.16f, 0.18f, 0.96f));
        public static readonly Texture2D CardSelectedBg = SolidColorMaterials.NewSolidColorTexture(new Color(0.23f, 0.26f, 0.29f, 0.98f));
        public static readonly Texture2D BorderColor = SolidColorMaterials.NewSolidColorTexture(new Color(0.34f, 0.36f, 0.39f, 0.92f));

        public static Texture2D GetMagazineIcon(int capacity)
        {
            if (capacity <= 18) return MagPistol;
            if (capacity >= 55) return MagDrum;
            return MagRifle;
        }
    }
}
