using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using LobbyControl.Dependency;
using Unity.Netcode;

namespace LobbyControl;

internal static class PluginConfig
{
    internal static void Init(BaseUnityPlugin plugin)
    {
        var config = plugin.Config;
        config.SaveOnConfigSet = false;
        //Initialize Configs
        //SaveLimit
        SaveLimit.Enabled = config.Bind("SaveLimit", "enabled", true
            , "remove the limit to the amount of items that can be saved");
        //SteamLobby
        SteamLobby.AutoLobby = config.Bind("SteamLobby", "auto_lobby", false
            , "automatically reopen the lobby as soon as you reach orbit");
        //LogSpam
        LogSpam.Enabled = config.Bind("LogSpam", "enabled", true
            , "prevent some annoying log spam");
        LogSpam.CalculatePolygonPath = config.Bind("LogSpam", "CalculatePolygonPath", true
            , "stop pathfinding for dead Enemies");
        //JoinQueue
        JoinQueue.Enabled = config.Bind("JoinQueue", "enabled", true
            , "handle joining players as a queue instead of at the same time");
        JoinQueue.MaxSize = config.Bind("JoinQueue", "max_size", 3
            , new ConfigDescription("max number of players in queue ( if queue is full extra connections will be refused )", new AcceptableValueRange<int>(-1, 10)));
        JoinQueue.ConnectionTimeout = config.Bind("JoinQueue", "connection_timeout_ms", 40000
            , new ConfigDescription("After how much time discard a hanging connection", new AcceptableValueRange<int>(10000, int.MaxValue)));
        JoinQueue.ConnectionDelay = config.Bind("JoinQueue", "connection_delay_ms", 2000
            , new ConfigDescription("Delay between each successful connection", new AcceptableValueRange<int>(100, int.MaxValue)));
        JoinQueue.TimeoutPopup = config.Bind("JoinQueue", "timeout_notification", true
            , "show a popup when a client fails to join before the timeout");
        JoinQueue.ConnectionPopup = config.Bind("JoinQueue", "connection_notification", false
            , "show a popup when a client tries to join");
        //Networking
        Networking.Enabled = config.Bind("Networking", "enabled", true
            , "handle extra actions requested by host");
        Networking.SyncRadarNames = config.Bind("Networking", "sync_radar_names", false
            , "allow host to reorder radar names to align clients\nWARNING: all clients need to have the mod installed or desyncs might will happen");
        Networking.ResetPlayerValues = config.Bind("Networking", "reset_player_values", true
            , "allow host to force clients to reset most fields of a playerObject ( fix for invisible players )");

        //update the networkmanager
        JoinQueue.ConnectionTimeout.SettingChanged +=
            (_, _) =>
            {
                var networkManager = NetworkManager.Singleton;
                if (networkManager is null)
                    return;

                networkManager.NetworkConfig.ClientConnectionBufferTimeout =
                    JoinQueue.ConnectionTimeout.Value / 1000 * 4;
            };

        if (LethalConfigProxy.Enabled)
        {
            //SaveLimit
            LethalConfigProxy.AddConfig(SaveLimit.Enabled);
            //SteamLobby
            LethalConfigProxy.AddConfig(SteamLobby.AutoLobby);
            //LogSpam
            LethalConfigProxy.AddConfig(LogSpam.Enabled);
            LethalConfigProxy.AddConfig(LogSpam.CalculatePolygonPath);
            //JoinQueue
            LethalConfigProxy.AddConfig(JoinQueue.Enabled);
            LethalConfigProxy.AddConfig(JoinQueue.MaxSize);
            LethalConfigProxy.AddConfig(JoinQueue.ConnectionTimeout);
            LethalConfigProxy.AddConfig(JoinQueue.ConnectionDelay);
            LethalConfigProxy.AddConfig(JoinQueue.TimeoutPopup);
            LethalConfigProxy.AddConfig(JoinQueue.ConnectionPopup);
            //Networking
            LethalConfigProxy.AddConfig(Networking.Enabled);
            LethalConfigProxy.AddConfig(Networking.SyncRadarNames);
            LethalConfigProxy.AddConfig(Networking.ResetPlayerValues);
        }

        //remove unused options
        var orphanedEntriesProp = config.GetType()
            .GetProperty("OrphanedEntries", BindingFlags.NonPublic | BindingFlags.Instance);

        var orphanedEntries = (Dictionary<ConfigDefinition, string>)orphanedEntriesProp!.GetValue(config, null);

        orphanedEntries.Clear(); // Clear orphaned entries (Unbinded/Abandoned entries)
        config.Save(); // Save the config file

        config.SaveOnConfigSet = true;
    }

    internal static class SteamLobby
    {
        internal static ConfigEntry<bool> AutoLobby;
    }

    internal static class SaveLimit
    {
        internal static ConfigEntry<bool> Enabled;
    }

    internal static class LogSpam
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> CalculatePolygonPath;
    }

    internal static class JoinQueue
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<int> MaxSize;
        internal static ConfigEntry<int> ConnectionTimeout;
        internal static ConfigEntry<int> ConnectionDelay;
        internal static ConfigEntry<bool> TimeoutPopup;
        internal static ConfigEntry<bool> ConnectionPopup;
    }

    internal static class Networking
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> SyncRadarNames;
        internal static ConfigEntry<bool> ResetPlayerValues;
    }
}