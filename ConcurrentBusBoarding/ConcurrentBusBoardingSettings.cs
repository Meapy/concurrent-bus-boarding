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
    [SettingsUIGroupOrder(TransportGroup, DisplayGroup)]
    [SettingsUIShowGroupName(TransportGroup, DisplayGroup)]
    public sealed class ConcurrentBusBoardingSettings : ModSetting
    {
        internal const string MainSection = "Main";
        internal const string TransportGroup = "Transport";
        internal const string DisplayGroup = "Display";

        public ConcurrentBusBoardingSettings(IMod mod) : base(mod)
        {
        }

        [SettingsUISection(MainSection, TransportGroup)]
        [SettingsUISlider(min = 50f, max = 200f, step = 5f, unit = "%")]
        public int PublicTransportAttractiveness { get; set; }

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
        [SettingsUISlider(min = 5f, max = 60f, step = 1f, unit = "%")]
        public int OverlayOpacity { get; set; }

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
            PublicTransportAttractiveness = 100;
            OnlyShowSelectedStop = true;
            GlobalOverlayRed = 38;
            GlobalOverlayGreen = 140;
            GlobalOverlayBlue = 242;
            GlobalOverlayAlpha = 71;
            OverlayOpacity = 18;
        }

        internal UnityColor GetGlobalOverlayColor()
        {
            if (GlobalOverlayRed == 0 && GlobalOverlayGreen == 0 && GlobalOverlayBlue == 0)
                return new UnityColor(0.15f, 0.55f, 0.95f, GetOverlayAlpha());
            return new UnityColor(
                math.clamp(GlobalOverlayRed, 0, 255) / 255f,
                math.clamp(GlobalOverlayGreen, 0, 255) / 255f,
                math.clamp(GlobalOverlayBlue, 0, 255) / 255f,
                GetOverlayAlpha());
        }

        internal float GetOverlayAlpha()
        {
            int opacity = OverlayOpacity;
            if (opacity < 5 || opacity > 60)
                opacity = 18;
            return opacity / 100f;
        }

        internal bool SetGlobalOverlayColor(string rgb)
        {
            if (rgb == null || (rgb.Length != 6 && rgb.Length != 8) ||
                !byte.TryParse(rgb.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                    out byte red) ||
                !byte.TryParse(rgb.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                    out byte green) ||
                !byte.TryParse(rgb.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                    out byte blue))
                return false;
            GlobalOverlayRed = red;
            GlobalOverlayGreen = green;
            GlobalOverlayBlue = blue;
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
                { m_Settings.GetOptionGroupLocaleID(ConcurrentBusBoardingSettings.TransportGroup),
                    "Public transport" },
                { m_Settings.GetOptionGroupLocaleID(ConcurrentBusBoardingSettings.DisplayGroup), "Overlay" },
                { m_Settings.GetOptionLabelLocaleID(
                        nameof(ConcurrentBusBoardingSettings.PublicTransportAttractiveness)),
                    "Public transport attractiveness" },
                { m_Settings.GetOptionDescLocaleID(
                        nameof(ConcurrentBusBoardingSettings.PublicTransportAttractiveness)),
                    "Adjust how strongly residents prefer passenger public transport when choosing a route. 100% keeps the vanilla cost; higher values make public transport more attractive." },
                { m_Settings.GetOptionLabelLocaleID(nameof(ConcurrentBusBoardingSettings.OnlyShowSelectedStop)),
                    "Only show the selected stop" },
                { m_Settings.GetOptionDescLocaleID(nameof(ConcurrentBusBoardingSettings.OnlyShowSelectedStop)),
                    "Hide boarding-zone overlays until a bus stop is selected. The zone remains visible while editing it on the map." },
                { m_Settings.GetOptionLabelLocaleID(nameof(ConcurrentBusBoardingSettings.ChooseGlobalOverlayColor)),
                    "Global overlay colour" },
                { m_Settings.GetOptionDescLocaleID(nameof(ConcurrentBusBoardingSettings.ChooseGlobalOverlayColor)),
                    "Choose the fallback colour for stops explicitly using global colour or without a valid line. All colours keep the global opacity." },
                { m_Settings.GetOptionLabelLocaleID(nameof(ConcurrentBusBoardingSettings.OverlayOpacity)),
                    "Overlay opacity" },
                { m_Settings.GetOptionDescLocaleID(nameof(ConcurrentBusBoardingSettings.OverlayOpacity)),
                    "Set boarding-zone opacity. Lower percentages make every overlay more transparent." },
                { m_Settings.GetOptionLabelLocaleID(nameof(ConcurrentBusBoardingSettings.ResetAllZones)),
                    "Reset all customized zones" },
                { m_Settings.GetOptionDescLocaleID(nameof(ConcurrentBusBoardingSettings.ResetAllZones)),
                    "Remove every saved per-stop zone length in the current city and return those stops to automatic sizing." },
                { m_Settings.GetOptionLabelLocaleID(nameof(ConcurrentBusBoardingSettings.ResetAllZoneColors)),
                    "Reset all stop overlay colours" },
                { m_Settings.GetOptionDescLocaleID(nameof(ConcurrentBusBoardingSettings.ResetAllZoneColors)),
                    "Return every bus stop in the current city to its line colour without changing customized zone lengths." }
            };
        }

        public void Unload()
        {
        }
    }
}
