using System.Collections.Generic;
using System.Globalization;
using Colossal;
using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;
using Unity.Mathematics;
using UnityColor = UnityEngine.Color;

namespace ConcurrentBusBoarding
{
    [FileLocation("ConcurrentBusBoarding")]
    [SettingsUIGroupOrder(DisplayGroup)]
    [SettingsUIShowGroupName(DisplayGroup)]
    public sealed class ConcurrentBusBoardingSettings : ModSetting
    {
        internal const string MainSection = "Main";
        internal const string DisplayGroup = "Display";

        public ConcurrentBusBoardingSettings(IMod mod) : base(mod)
        {
        }

        [SettingsUISection(MainSection, DisplayGroup)]
        public bool OnlyShowSelectedStop { get; set; }

        [SettingsUISection(MainSection, DisplayGroup)]
        [SettingsUIButton]
        public bool ChooseGlobalOverlayColor
        {
            set { }
        }

        [SettingsUIHidden]
        public int GlobalOverlayRed { get; set; }

        [SettingsUIHidden]
        public int GlobalOverlayGreen { get; set; }

        [SettingsUIHidden]
        public int GlobalOverlayBlue { get; set; }

        [SettingsUIHidden]
        public int GlobalOverlayAlpha { get; set; }

        [SettingsUISection(MainSection, DisplayGroup)]
        [SettingsUIButton]
        [SettingsUIConfirmation(null,
            "Reset every customized bus boarding zone in the current city? This cannot be undone.")]
        public bool ResetAllZones
        {
            set => BoardingZoneEditorUISystem.RequestResetAllZones();
        }

        [SettingsUISection(MainSection, DisplayGroup)]
        [SettingsUIButton]
        [SettingsUIConfirmation(null,
            "Reset every bus stop overlay colour to the global colour? This cannot be undone.")]
        public bool ResetAllZoneColors
        {
            set => BoardingZoneEditorUISystem.RequestResetAllZoneColors();
        }

        public override void SetDefaults()
        {
            OnlyShowSelectedStop = true;
            GlobalOverlayRed = 38;
            GlobalOverlayGreen = 140;
            GlobalOverlayBlue = 242;
            GlobalOverlayAlpha = 71;
        }

        internal UnityColor GetGlobalOverlayColor()
        {
            if (GlobalOverlayRed == 0 && GlobalOverlayGreen == 0 &&
                GlobalOverlayBlue == 0 && GlobalOverlayAlpha == 0)
                return new UnityColor(0.15f, 0.55f, 0.95f, 0.28f);
            return new UnityColor(
                math.clamp(GlobalOverlayRed, 0, 255) / 255f,
                math.clamp(GlobalOverlayGreen, 0, 255) / 255f,
                math.clamp(GlobalOverlayBlue, 0, 255) / 255f,
                math.clamp(GlobalOverlayAlpha, 0, 255) / 255f);
        }

        internal bool SetGlobalOverlayColor(string rgba)
        {
            if (rgba == null || rgba.Length != 8 ||
                !byte.TryParse(rgba.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                    out byte red) ||
                !byte.TryParse(rgba.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                    out byte green) ||
                !byte.TryParse(rgba.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                    out byte blue) ||
                !byte.TryParse(rgba.Substring(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                    out byte alpha))
                return false;
            GlobalOverlayRed = red;
            GlobalOverlayGreen = green;
            GlobalOverlayBlue = blue;
            GlobalOverlayAlpha = alpha;
            ApplyAndSave();
            return true;
        }
    }

    internal sealed class SettingsLocale : IDictionarySource
    {
        private readonly ConcurrentBusBoardingSettings m_Settings;

        internal SettingsLocale(ConcurrentBusBoardingSettings settings)
        {
            m_Settings = settings;
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors,
            Dictionary<string, int> indexCounts)
        {
            return new Dictionary<string, string>
            {
                { m_Settings.GetSettingsLocaleID(), "Concurrent Bus Boarding" },
                { m_Settings.GetOptionTabLocaleID(ConcurrentBusBoardingSettings.MainSection), "Main" },
                { m_Settings.GetOptionGroupLocaleID(ConcurrentBusBoardingSettings.DisplayGroup), "Overlay" },
                { m_Settings.GetOptionLabelLocaleID(nameof(ConcurrentBusBoardingSettings.OnlyShowSelectedStop)),
                    "Only show the selected stop" },
                { m_Settings.GetOptionDescLocaleID(nameof(ConcurrentBusBoardingSettings.OnlyShowSelectedStop)),
                    "Hide boarding-zone overlays until a bus stop is selected. The zone remains visible while editing it on the map." },
                { m_Settings.GetOptionLabelLocaleID(nameof(ConcurrentBusBoardingSettings.ChooseGlobalOverlayColor)),
                    "Global overlay colour" },
                { m_Settings.GetOptionDescLocaleID(nameof(ConcurrentBusBoardingSettings.ChooseGlobalOverlayColor)),
                    "Choose the default boarding-zone overlay colour with the colour wheel. Stops using their line colour retain this colour's transparency." },
                { m_Settings.GetOptionLabelLocaleID(nameof(ConcurrentBusBoardingSettings.ResetAllZones)),
                    "Reset all customized zones" },
                { m_Settings.GetOptionDescLocaleID(nameof(ConcurrentBusBoardingSettings.ResetAllZones)),
                    "Remove every saved per-stop zone length in the current city and return those stops to automatic sizing." },
                { m_Settings.GetOptionLabelLocaleID(nameof(ConcurrentBusBoardingSettings.ResetAllZoneColors)),
                    "Reset all stop overlay colours" },
                { m_Settings.GetOptionDescLocaleID(nameof(ConcurrentBusBoardingSettings.ResetAllZoneColors)),
                    "Return every bus stop in the current city to the global overlay colour without changing customized zone lengths." }
            };
        }

        public void Unload()
        {
        }
    }
}
