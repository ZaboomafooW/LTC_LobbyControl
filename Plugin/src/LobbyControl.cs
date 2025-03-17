using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using LobbyControl.Dependency;
using LobbyControl.Patches;
using LobbyControl.PopUp;
using LobbyControl.TerminalCommands;
using MonoMod.RuntimeDetour;
using Unity.Netcode;
using PluginInfo = BepInEx.PluginInfo;

namespace LobbyControl;

[BepInPlugin(GUID, NAME, VERSION)]
//soft-deps
[BepInDependency("FlipMods.ReservedItemSlotCore", Flags: BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("imabatby.lethallevelloader", Flags: BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("BMX.LobbyCompatibility", Flags: BepInDependency.DependencyFlags.SoftDependency)]
//incompatibilities
[BepInDependency("com.github.tinyhoot.ShipLobby", Flags: BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("twig.latecompany", Flags: BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("McBowie.VeryLateCompany", Flags: BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("com.potatoepet.AdvancedCompany", Flags: BepInDependency.DependencyFlags.SoftDependency)]
internal class LobbyControl : BaseUnityPlugin
{
    public const string GUID = MyPluginInfo.PLUGIN_GUID;
    public const string NAME = MyPluginInfo.PLUGIN_NAME;
    public const string VERSION = MyPluginInfo.PLUGIN_VERSION;

    public static LobbyControl Instance;

    internal static ManualLogSource Log;

    internal static readonly Harmony Harmony = new Harmony(GUID);

    public static bool CanModifyLobby = true;

    public static bool CanSave = true;
    public static bool AutoSaveEnabled = true;

    // ReSharper disable once CollectionNeverQueried.Global
    public static readonly List<Hook> Hooks = [];


    private static readonly string[] IncompatibleGUIDs =
    [
        "com.github.tinyhoot.ShipLobby",
        "twig.latecompany",
        "McBowie.VeryLateCompany",
        "com.potatoepet.AdvancedCompany"
    ];

    // ReSharper disable once CollectionNeverQueried.Global
    internal static readonly List<PluginInfo> FoundIncompatibilities = [];

    private void Awake()
    {
        Log = Logger;
        Instance = this;
        try
        {
            PluginInfo[] incompatibleMods = Chainloader.PluginInfos.Values
                .Where(p => IncompatibleGUIDs.Contains(p.Metadata.GUID)).ToArray();
            if (incompatibleMods.Length > 0)
            {
                StringBuilder sb = new StringBuilder("LOBBY CONTROL was DISABLED!\nIncompatible:");
                FoundIncompatibilities.AddRange(incompatibleMods);
                foreach (var mod in incompatibleMods)
                {
                    Log.LogWarning($"{mod.Metadata.Name} is incompatible!");
                    sb.Append("\n").Append(mod.Metadata.Name);
                }

                Log.LogError($"{incompatibleMods.Length} incompatible mods found! Disabling!");
                PopUpPatch.PopUps.Add(("LC_Incompatibility", sb.ToString()));
                Harmony.PatchAll(typeof(PopUpPatch));
            }
            else
            {
                if (LobbyCompatibilityChecker.Enabled)
                    LobbyCompatibilityChecker.Init(GUID, Version.Parse(VERSION), 1, 2);
                Log.LogInfo("Initializing Configs");

                PluginConfig.Init(this);

                CommandManager.Initialize();

                LobbyCommand.Init();

                Log.LogInfo("Patching Methods");

                Harmony.PatchAll(typeof(PopUpPatch));

                Harmony.PatchAll(typeof(JoinQueuePatches));
                JoinQueuePatches.Init();
                Harmony.PatchAll(typeof(LateJoinPatches));
                Harmony.PatchAll(typeof(LimitPatcher));
                Harmony.PatchAll(typeof(LobbyPatcher));
                Harmony.PatchAll(typeof(LogSpamPatches));
                Harmony.PatchAll(typeof(NetworkManagerPatch));
                Harmony.PatchAll(typeof(SavePatches));
                Harmony.PatchAll(typeof(TerminalPatch));

                Log.LogInfo(NAME + " v" + VERSION + " Loaded!");
            }
        }
        catch (Exception ex)
        {
            Log.LogError("Exception while initializing: \n" + ex);
        }
    }

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
            LogSpam.AudioSpatializer = config.Bind("LogSpam", "audio_spatializer", true
                , "disable audio spatialization as there is not spatialization plugin");
            //JoinQueue
            JoinQueue.Enabled = config.Bind("JoinQueue", "enabled", true
                , "handle joining players as a queue instead of at the same time");
            JoinQueue.ConnectionTimeout = config.Bind("JoinQueue", "connection_timeout_ms", 20000
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
                LethalConfigProxy.AddConfig(LogSpam.AudioSpatializer);
                //JoinQueue
                LethalConfigProxy.AddConfig(JoinQueue.Enabled);
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
            internal static ConfigEntry<bool> AudioSpatializer;
        }

        internal static class JoinQueue
        {
            internal static ConfigEntry<bool> Enabled;
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
}
