using BepInEx;
using BepInEx.Bootstrap;

namespace LobbyControl.Dependency;

public static class LethalLevelLoaderDependency
{
    private static bool? _enabled;
    private static PluginInfo _pluginInfo;

    public static bool Enabled
    {
        get
        {
            _enabled ??= Chainloader.PluginInfos.TryGetValue("imabatby.lethallevelloader", out _pluginInfo);
            return _enabled.Value;
        }
    }
}
