using Verse;

namespace CE_MagazineFed
{
    /// <summary>
    /// Optional DefModExtension allowing third-party mod authors to declare a weapon's default
    /// feeding mode in XML without requiring a C# reference.
    /// Priority: Player Mod Settings Override > FeedModeExtension > CE reloadOneAtATime auto-detection.
    /// </summary>
    public class FeedModeExtension : DefModExtension
    {
        /// <summary>
        /// Default feeding mode for this weapon:
        /// - Auto (default): infer from CE CompProperties_AmmoUser.reloadOneAtATime
        /// - MagazineFed: detachable physical magazines
        /// - SingleLoad: single-round tube/cylinder loading with ordered ammo plan
        /// - VanillaCE: leave untouched by CE Magazine Fed (keep vanilla CE behavior)
        /// </summary>
        public WeaponFeedModeOverride defaultFeedMode = WeaponFeedModeOverride.Auto;

        /// <summary>
        /// When defaultFeedMode differs from CE's original reloadOneAtATime:
        /// - true (default): also sync reloadOneAtATime and convert reloadTime
        ///   (SingleLoad -> MagazineFed: orig * magSize * 0.9; MagazineFed -> SingleLoad: orig / magSize; rounded to 1 decimal).
        /// - false: keep the weapon's original CE reloadOneAtATime and reloadTime unchanged.
        /// Ignored if overrideReloadTime > 0.
        /// </summary>
        public bool autoAdjustReloadTime = true;

        /// <summary>
        /// Optional explicit reloadTime override (in seconds) used when this extension's
        /// defaultFeedMode is active (i.e. not overridden by the player in Mod Settings).
        /// Values <= 0 are ignored.
        /// </summary>
        public float overrideReloadTime = -1f;
    }
}
