using Zep.Core.Application;

namespace Zep.Editor.Bridge.Theming
{
    internal static class ZepThemeProviderRegistration
    {
        internal static ZepThemeRegistry CreateConfigured()
        {
            var __sw_ctor = System.Diagnostics.Stopwatch.StartNew();
            var registry = new ZepThemeRegistry();
            __sw_ctor.Stop();
            if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/perf] new ZepThemeRegistry (first RefreshRegistry) ms={__sw_ctor.Elapsed.TotalMilliseconds:F2}");

            registry.RegisterProvider(new BuiltinThemeProvider());
            registry.RegisterProvider(new ProjectAssetThemeProvider());
            registry.RegisterProvider(new UserPrefsThemeProvider());

            var __sw_refresh = System.Diagnostics.Stopwatch.StartNew();
            registry.RefreshRegistry();
            __sw_refresh.Stop();
            if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/perf] ZepThemeRegistry.RefreshRegistry (second, with providers) ms={__sw_refresh.Elapsed.TotalMilliseconds:F2}");
            return registry;
        }

        internal static ZepThemeRegistry CreateForInitialPanelPaint()
        {
            return ZepThemeRegistry.CreateInitial(
                ZepThemePreferences.GetTheme());
        }

        internal static void CompleteProjectAssetDiscovery(
            ZepThemeRegistry registry)
        {
            if (registry == null)
            {
                return;
            }

            registry.RegisterProvider(new BuiltinThemeProvider());
            registry.RegisterProvider(new UserPrefsThemeProvider());
            registry.RegisterProvider(new ProjectAssetThemeProvider());
            registry.RefreshRegistry();
            registry.PublishRefreshedThemes();
        }
    }
}
