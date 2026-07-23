using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;
using Game.Simulation;
using Game.Tools;
using Game.UI;

namespace ConcurrentBusBoarding
{
    public sealed class Mod : IMod
    {
        internal static readonly ILog Log = LogManager
            .GetLogger($"{nameof(ConcurrentBusBoarding)}.{nameof(Mod)}")
            .SetShowsErrorsInUI(true);
        internal static ConcurrentBusBoardingSettings Settings { get; private set; }

        public void OnLoad(UpdateSystem updateSystem)
        {
            CrashBreadcrumbs.Start();
            CrashBreadcrumbs.Write("mod-onload before-settings");
            Log.Info(nameof(OnLoad));
            if (GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
                Log.Info($"Current mod asset at {asset.path}");

            Settings = new ConcurrentBusBoardingSettings(this);
            Settings.RegisterInOptionsUI();
            ColorPickerSettings colorPickerSettings = new ColorPickerSettings(this);
            colorPickerSettings.RegisterInOptionsUI();
            GameManager.instance.localizationManager.AddSource("en-US", new SettingsLocale(Settings));
            GameManager.instance.localizationManager.AddSource("en-US", new ColorPickerLocale(colorPickerSettings));
            AssetDatabase.global.LoadSettings("ConcurrentBusBoarding", Settings,
                new ConcurrentBusBoardingSettings(this));
            AssetDatabase.global.LoadSettings("ConcurrentBusBoarding.Color", colorPickerSettings,
                new ColorPickerSettings(this));
            CrashBreadcrumbs.Write("mod-onload after-settings");
            // ponytail: no approach/front-position or passenger-spread system; native traffic owns movement.
            updateSystem.UpdateBefore<ConcurrentBoardingSystem, TransportCarAISystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAfter<RouteHandoffSystem, TransportCarAISystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAfter<PassengerDistributionSystem, TransportCarAISystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAfter<BoardingHoldSystem, CarNavigationSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAt<BoardingZoneToolSystem>(SystemUpdatePhase.ToolUpdate);
            updateSystem.UpdateAt<BoardingZoneRenderSystem>(SystemUpdatePhase.Rendering);
            updateSystem.UpdateAt<BoardingZoneEditorUISystem>(SystemUpdatePhase.UIUpdate);
            CrashBreadcrumbs.Write("mod-onload rear-zone-boarding-systems-registered");
        }

        public void OnDispose()
        {
            CrashBreadcrumbs.Write("mod-dispose");
            Log.Info(nameof(OnDispose));
            if (Settings != null)
            {
                Settings.UnregisterInOptionsUI();
                Settings = null;
            }
            CrashBreadcrumbs.Stop();
        }
    }
}
