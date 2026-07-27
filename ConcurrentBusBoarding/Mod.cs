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
        private static FieldInfo s_AllAboardSettings;
        private static PropertyInfo s_AllAboardBusDwellMinutes;

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

            // One-time migration. Concurrent boarding is now opt-in, and a settings file written by
            // an earlier version has no SettingsVersion field, so it deserializes as 0 and lands
            // here. Anyone who had the feature on is switched off once and can turn it back on.
            if (Settings.SettingsVersion < ConcurrentBusBoardingSettings.CurrentSettingsVersion)
            {
                bool wasEnabled = Settings.EnableConcurrentBoarding;
                Settings.EnableConcurrentBoarding = false;
                Settings.SettingsVersion = ConcurrentBusBoardingSettings.CurrentSettingsVersion;
                Settings.ApplyAndSave();
                if (wasEnabled)
                {
                    Log.Info("Concurrent boarding is now opt-in and has been switched off. " +
                        "Re-enable it in Options if you want it.");
                }
            }
            CrashBreadcrumbs.Write("mod-onload after-settings");
#if CBB_OBSERVER_ONLY
            Log.Warn("Observer-only diagnostic build: no simulation systems are registered. " +
                "Boarding, holding, passenger distribution and transport attractiveness are all inactive.");
#else
            updateSystem.UpdateBefore<PublicTransportAttractivenessSystem, ResidentAISystem>(
                SystemUpdatePhase.GameSimulation);
            // ponytail: no approach/front-position or passenger-spread system; native traffic owns
            // movement and the native queue owns where cims wait. The spread was briefly registered
            // on the theory that concurrent buses were unreachable; measurement disproved that
            // (buses board and unload normally at zone distances), so its only remaining effect was
            // to displace waiting cims from their stop, which correlates with cims abandoning the
            // wait. Do not re-register it without evidence that waiting position is the problem.
            BoardingSystemRegistrationSystem.Configure(updateSystem);
            updateSystem.UpdateAt<BoardingSystemRegistrationSystem>(SystemUpdatePhase.Modification1);
            updateSystem.UpdateAfter<BoardingHoldSystem, CarNavigationSystem>(SystemUpdatePhase.GameSimulation);
#endif
            // Modification1, not GameSimulation: both perform structural changes, and doing that
            // inside the simulation phase makes the game's own UpdateGroupSystem fail to obtain an
            // EntityCommandBuffer. Registered in observer-only builds too, because repairing residue
            // left by earlier versions is independent of whether concurrent boarding is active.
            updateSystem.UpdateAt<BoardingRepairSystem>(SystemUpdatePhase.Modification1);
            updateSystem.UpdateAt<LineDiagnosticsSystem>(SystemUpdatePhase.Modification1);
            updateSystem.UpdateAt<BoardingZoneToolSystem>(SystemUpdatePhase.ToolUpdate);
            updateSystem.UpdateAt<BoardingZoneRenderSystem>(SystemUpdatePhase.Rendering);
            updateSystem.UpdateAt<BoardingZoneEditorUISystem>(SystemUpdatePhase.UIUpdate);
            CrashBreadcrumbs.Write("mod-onload rear-zone-boarding-systems-registered");
        }

        internal static void RegisterBoardingSystems(UpdateSystem updateSystem)
        {
            Type replacement = Type.GetType(
                "AllAboard.System.Patched.PatchedTransportCarAISystem, AllAboard", false);
            if (replacement == null)
            {
                updateSystem.UpdateBefore<ConcurrentBoardingSystem, TransportCarAISystem>(
                    SystemUpdatePhase.GameSimulation);
                updateSystem.UpdateAfter<RouteHandoffSystem, TransportCarAISystem>(
                    SystemUpdatePhase.GameSimulation);
                updateSystem.UpdateAfter<PassengerDistributionSystem, TransportCarAISystem>(
                    SystemUpdatePhase.GameSimulation);
                Log.Info("Ordered boarding systems around the native car AI.");
                return;
            }

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
                Type allAboard = replacement.Assembly.GetType("AllAboard.AllAboard");
                Type settings = replacement.Assembly.GetType("AllAboard.AllAboardSettings");
                s_AllAboardSettings = allAboard?.GetField("m_AllAboardSettings",
                    BindingFlags.Public | BindingFlags.Static);
                s_AllAboardBusDwellMinutes = settings?.GetProperty("BusMaxDwellDelaySlider",
                    BindingFlags.Public | BindingFlags.Instance);
                Log.Info("Ordered boarding systems around All Aboard's replacement car AI.");
                Log.Info($"Managed follower dwell limit: {GetManagedBoardingTimeoutFrames()} frames.");
            }
            catch (Exception exception)
            {
                Log.Warn($"Could not register All Aboard compatibility ordering: {exception.Message}");
                updateSystem.UpdateBefore<ConcurrentBoardingSystem, TransportCarAISystem>(
                    SystemUpdatePhase.GameSimulation);
                updateSystem.UpdateAfter<RouteHandoffSystem, TransportCarAISystem>(
                    SystemUpdatePhase.GameSimulation);
                updateSystem.UpdateAfter<PassengerDistributionSystem, TransportCarAISystem>(
                    SystemUpdatePhase.GameSimulation);
            }
        }

        internal static uint GetManagedBoardingTimeoutFrames()
        {
            try
            {
                object settings = s_AllAboardSettings?.GetValue(null);
                object minutes = settings == null ? null : s_AllAboardBusDwellMinutes?.GetValue(settings);
                return minutes is int value
                    ? BoardingPolicy.BoardingTimeoutFrames(value)
                    : BoardingPolicy.ManagedBoardingTimeoutFrames;
            }
            catch
            {
                return BoardingPolicy.ManagedBoardingTimeoutFrames;
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

    public partial class BoardingSystemRegistrationSystem : GameSystemBase
    {
        private static UpdateSystem s_UpdateSystem;

        internal static void Configure(UpdateSystem updateSystem)
        {
            s_UpdateSystem = updateSystem;
        }

        // Deferred to the first Modification1 update so every other mod's OnLoad has completed and
        // All Aboard's replacement car AI can be detected. Shipped in 1.4.1; leave the timing alone
        // unless there is direct evidence against it.
        protected override void OnUpdate()
        {
            if (s_UpdateSystem != null)
                Mod.RegisterBoardingSystems(s_UpdateSystem);
            Enabled = false;
        }
    }
}
