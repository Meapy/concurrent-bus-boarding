using System.Collections.Generic;
using Colossal;
using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;

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
        [SettingsUIConfirmation(null,
            "Reset every customized bus boarding zone in the current city? This cannot be undone.")]
        public bool ResetAllZones
        {
            set => BoardingZoneEditorUISystem.RequestResetAllZones();
        }

        public override void SetDefaults() => OnlyShowSelectedStop = true;
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
                { m_Settings.GetOptionLabelLocaleID(nameof(ConcurrentBusBoardingSettings.ResetAllZones)),
                    "Reset all customized zones" },
                { m_Settings.GetOptionDescLocaleID(nameof(ConcurrentBusBoardingSettings.ResetAllZones)),
                    "Remove every saved per-stop zone length in the current city and return those stops to automatic sizing." }
            };
        }

        public void Unload()
        {
        }
    }
}
