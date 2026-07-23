using System.Collections.Generic;
using Colossal;
using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;

namespace ConcurrentBusBoarding
{
    [FileLocation("ConcurrentBusBoarding")]
    [SettingsUIGroupOrder("ColorPicker")]
    [SettingsUIShowGroupName("ColorPicker")]
    public sealed class ColorPickerSettings : ModSetting
    {
        internal const string MainSection = "Main";
        internal const string DisplayGroup = "Color";

        public ColorPickerSettings(IMod mod) : base(mod)
        {
        }

        [SettingsUISection(MainSection, DisplayGroup)]
        public bool UseDefaultOverlayColor { get; set; }

        [SettingsUISection(MainSection, DisplayGroup)]
        public string DefaultOverlayColorHex { get; set; }

        public override void SetDefaults()
        {
            UseDefaultOverlayColor = true;
            DefaultOverlayColorHex = "#2f8fe8";
        }
    }

    internal sealed class ColorPickerLocale : IDictionarySource
    {
        private readonly ColorPickerSettings m_Settings;

        internal ColorPickerLocale(ColorPickerSettings settings)
        {
            m_Settings = settings;
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors,
            Dictionary<string, int> indexCounts)
        {
            return new Dictionary<string, string>
            {
                { m_Settings.GetSettingsLocaleID(), "Concurrent Bus Boarding Color" },
                { m_Settings.GetOptionTabLocaleID(ColorPickerSettings.MainSection), "Main" },
                { m_Settings.GetOptionGroupLocaleID(ColorPickerSettings.DisplayGroup), "Color" },
                { m_Settings.GetOptionLabelLocaleID(nameof(ColorPickerSettings.UseDefaultOverlayColor)),
                    "Use custom default overlay color" },
                { m_Settings.GetOptionDescLocaleID(nameof(ColorPickerSettings.UseDefaultOverlayColor)),
                    "Enable a custom default tint for boarding-zone overlays. Individual bus stops can override this color." },
                { m_Settings.GetOptionLabelLocaleID(nameof(ColorPickerSettings.DefaultOverlayColorHex)),
                    "Default overlay color" },
                { m_Settings.GetOptionDescLocaleID(nameof(ColorPickerSettings.DefaultOverlayColorHex)),
                    "Pick the default tint used for boarding-zone overlays. Individual bus stops can override this color." }
            };
        }

        public void Unload()
        {
        }
    }
}
