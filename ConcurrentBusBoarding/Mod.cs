using System;
using System.Reflection;
using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using Game;
using Game.Modding;
using Game.Pathfind;
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
            GameManager.instance.localizationManager.AddSource("en-US", new SettingsLocale(Settings));
            AssetDatabase.global.LoadSettings("ConcurrentBusBoarding", Settings,
                new ConcurrentBusBoardingSettings(this));
            CrashBreadcrumbs.Write("mod-onload after-settings");
            updateSystem.UpdateBefore<PublicTransportAttractivenessSystem, RoutesModifiedSystem>(
                SystemUpdatePhase.Modification5);
            // ponytail: no approach/front-position or passenger-spread system; native traffic owns movement.
            if (!RegisterAllAboardOrdering(updateSystem))
            {
                updateSystem.UpdateBefore<ConcurrentBoardingSystem, TransportCarAISystem>(
                    SystemUpdatePhase.GameSimulation);
                updateSystem.UpdateAfter<RouteHandoffSystem, TransportCarAISystem>(
                    SystemUpdatePhase.GameSimulation);
                updateSystem.UpdateAfter<PassengerDistributionSystem, TransportCarAISystem>(
                    SystemUpdatePhase.GameSimulation);
            }
            updateSystem.UpdateAfter<BoardingHoldSystem, CarNavigationSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAt<BoardingZoneToolSystem>(SystemUpdatePhase.ToolUpdate);
            updateSystem.UpdateAt<BoardingZoneRenderSystem>(SystemUpdatePhase.Rendering);
            updateSystem.UpdateAt<BoardingZoneEditorUISystem>(SystemUpdatePhase.UIUpdate);
            CrashBreadcrumbs.Write("mod-onload rear-zone-boarding-systems-registered");
        }

        private static bool RegisterAllAboardOrdering(UpdateSystem updateSystem)
        {
            Type replacement = Type.GetType(
                "AllAboard.System.Patched.PatchedTransportCarAISystem, AllAboard", false);
            if (replacement == null)
                return false;

            try
            {
                MethodInfo before = FindRelativeUpdateMethod(nameof(UpdateSystem.UpdateBefore));
                MethodInfo after = FindRelativeUpdateMethod(nameof(UpdateSystem.UpdateAfter));
                object[] phase = { SystemUpdatePhase.GameSimulation };
                before.MakeGenericMethod(typeof(ConcurrentBoardingSystem), replacement)
                    .Invoke(updateSystem, phase);
                after.MakeGenericMethod(typeof(RouteHandoffSystem), replacement)
                    .Invoke(updateSystem, phase);
                after.MakeGenericMethod(typeof(PassengerDistributionSystem), replacement)
                    .Invoke(updateSystem, phase);
                Log.Info("Ordered boarding systems around All Aboard's replacement car AI.");
                return true;
            }
            catch (Exception exception)
            {
                Log.Warn($"Could not register All Aboard compatibility ordering: {exception.Message}");
                return false;
            }
        }

        private static MethodInfo FindRelativeUpdateMethod(string name)
        {
            foreach (MethodInfo method in typeof(UpdateSystem).GetMethods(BindingFlags.Instance |
                         BindingFlags.Public))
            {
                if (method.Name == name && method.IsGenericMethodDefinition &&
                    method.GetGenericArguments().Length == 2)
                    return method;
            }
            throw new MissingMethodException(typeof(UpdateSystem).FullName, name);
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
