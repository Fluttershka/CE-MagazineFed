using System;
using RimWorld;
using UnityEngine;
using Verse;

namespace CE_MagazineFed
{
    public class Dialog_SavePreset : Window
    {
        private string presetName;
        private readonly Action<string> onConfirm;

        public override Vector2 InitialSize => new Vector2(380f, 155f);

        public Dialog_SavePreset(string defaultName, Action<string> onConfirm)
        {
            this.presetName = defaultName ?? "Preset";
            this.onConfirm = onConfirm;
            forcePause = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 28f), "CEMF_UI_EnterPresetName".Translate());

            GUI.SetNextControlName("CEMF_PresetNameField");
            presetName = Widgets.TextField(new Rect(inRect.x, inRect.y + 34f, inRect.width, 32f), presetName);

            float btnY = inRect.yMax - 34f;
            float btnW = (inRect.width - 12f) * 0.5f;

            if (Widgets.ButtonText(new Rect(inRect.x, btnY, btnW, 32f), "CEMF_UI_Confirm".Translate())
                || (Event.current.type == EventType.KeyDown && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter)))
            {
                if (!string.IsNullOrWhiteSpace(presetName))
                {
                    onConfirm?.Invoke(presetName.Trim());
                    Close();
                }
            }
            if (Widgets.ButtonText(new Rect(inRect.x + btnW + 12f, btnY, btnW, 32f), "CEMF_UI_Cancel".Translate()))
            {
                Close();
            }
        }
    }
}
