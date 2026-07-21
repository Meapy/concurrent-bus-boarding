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
            .SetShowsErrorsInUI(false);
        internal static ConcurrentBusBoardingSettings Settings { get; private set; }

        public void OnLoad(UpdateSystem updateSystem)
        {
            Log.Info(nameof(OnLoad));
            if (GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
                Log.Info($"Current mod asset at {asset.path}");

            Settings = new ConcurrentBusBoardingSettings(this);
            Settings.RegisterInOptionsUI();
            GameManager.instance.localizationManager.AddSource("en-US", new SettingsLocale(Settings));
            AssetDatabase.global.LoadSettings("ConcurrentBusBoarding", Settings,
                new ConcurrentBusBoardingSettings(this));

            updateSystem.UpdateBefore<ConcurrentBoardingSystem, TransportCarAISystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateBefore<BoardingZoneApproachSystem, ConcurrentBoardingSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateBefore<BoardingZoneApproachSystem, TransportCarAISystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAfter<PassengerDistributionSystem, TransportCarAISystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAfter<PassengerWaitingSpreadSystem, ResidentAISystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateBefore<PassengerWaitingSpreadSystem, HumanNavigationSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAfter<BoardingHoldSystem, CarNavigationSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAt<BoardingZoneToolSystem>(SystemUpdatePhase.ToolUpdate);
            updateSystem.UpdateAt<BoardingZoneRenderSystem>(SystemUpdatePhase.Rendering);
            updateSystem.UpdateAt<BoardingZoneEditorUISystem>(SystemUpdatePhase.UIUpdate);
        }

        public void OnDispose()
        {
            Log.Info(nameof(OnDispose));
            if (Settings != null)
            {
                Settings.UnregisterInOptionsUI();
                Settings = null;
            }
        }
    }
}
