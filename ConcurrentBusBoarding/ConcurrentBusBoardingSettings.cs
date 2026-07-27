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

        // Holding a bus at a stop takes slightly longer than the game's own boarding, and the game
        // uses stop waiting times when deciding whether residents choose a line. If a busy network
        // ever loses passengers, turning this off is the first thing to try - it takes effect
        // immediately and leaves the overlays, zone editor and stuck-stop repair working.
        [SettingsUISection(MainSection, TransportGroup)]
        public bool EnableConcurrentBoarding { get; set; } = true;

        [SettingsUISection(MainSection, TransportGroup)]
        [SettingsUISlider(min = 50f, max = 200f, step = 5f, unit = "%")]
        public int PublicTransportAttractiveness { get; set; } = 100;

        [SettingsUISection(MainSection, TransportGroup)]
        // Defaults to 100 so the mod never rewrites native pathfind costs, or forces the citywide
        // route-edge refresh that goes with them, unless the player opts in.
        [SettingsUISlider(min = 100f, max = 200f, step = 5f, unit = "%")]
        public int BusAttractiveness { get; set; } = 100;

        [SettingsUISection(MainSection, TransportGroup)]
        [SettingsUIButton]
        [SettingsUIConfirmation(null,
            "Clear stuck boarding state from every bus stop and bus in this city? Use this if a city saved with an earlier version has stops that no longer board passengers.")]
        public bool RepairBoardingState
        {
            set => BoardingRepairSystem.RequestRepair();
        }

        [SettingsUISection(MainSection, TransportGroup)]
        [SettingsUIButton]
        public bool ReportBusLines
        {
            set => LineDiagnosticsSystem.RequestReport();
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
            EnableConcurrentBoarding = true;
            PublicTransportAttractiveness = 100;
            BusAttractiveness = 100;
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
                { m_Settings.GetOptionLabelLocaleID(nameof(ConcurrentBusBoardingSettings.EnableConcurrentBoarding)),
                    "Enable concurrent boarding" },
                { m_Settings.GetOptionDescLocaleID(nameof(ConcurrentBusBoardingSettings.EnableConcurrentBoarding)),
                    "Let a second bus board alongside the first when two are at the same stop. Turn off to leave every bus entirely to the game while keeping the overlays and zone editor. Takes effect immediately, so you can compare your city with it on and off." },
                { m_Settings.GetOptionLabelLocaleID(nameof(ConcurrentBusBoardingSettings.BusAttractiveness)),
                    "Extra bus attractiveness" },
                { m_Settings.GetOptionDescLocaleID(nameof(ConcurrentBusBoardingSettings.BusAttractiveness)),
                    "Make residents prefer buses specifically, on top of the general public transport setting above. This applies only to route costs used exclusively by bus lines, so other transport types are unaffected. 100% keeps the vanilla bus cost." },
                { m_Settings.GetOptionLabelLocaleID(nameof(ConcurrentBusBoardingSettings.ReportBusLines)),
                    "Write bus line report to log" },
                { m_Settings.GetOptionDescLocaleID(nameof(ConcurrentBusBoardingSettings.ReportBusLines)),
                    "Write one line per bus route to the mod log: vehicles running, waiting passengers, average wait, boarding success rate, and any stop reserved by a bus that is not releasing it. Changes nothing in the city." },
                { m_Settings.GetOptionLabelLocaleID(nameof(ConcurrentBusBoardingSettings.RepairBoardingState)),
                    "Repair stuck bus stops" },
                { m_Settings.GetOptionDescLocaleID(nameof(ConcurrentBusBoardingSettings.RepairBoardingState)),
                    "Clear boarding state left behind in this city by an earlier version: stops still reserved for a bus that has gone, and buses that can no longer accept passengers or depart. Runs automatically once each time a city loads." },
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
