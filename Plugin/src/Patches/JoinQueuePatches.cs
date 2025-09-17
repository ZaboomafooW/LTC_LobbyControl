using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Threading.Tasks;
using System.Timers;
using GameNetcodeStuff;
using HarmonyLib;
using LobbyControl.API;
using LobbyControl.Networking;
using LobbyControl.Utils;
using LobbyControl.Utils.IL;
using MonoMod.RuntimeDetour;
using Steamworks;
using Unity.Netcode;
using Object = UnityEngine.Object;

namespace LobbyControl.Patches;

[HarmonyPatch]
internal class JoinQueuePatches
{
    internal static void Init()
    {
        var methodInfo =
            AccessTools.Method(typeof(StartOfRound), nameof(StartOfRound.SyncAlreadyHeldObjectsServerRpc));

        if (RPCUtils.TryGetRpcID(methodInfo, out var id))
        {
            var harmonyTarget = AccessTools.Method(typeof(StartOfRound), $"__rpc_handler_{id}");
            var harmonyFinalizer =
                AccessTools.Method(typeof(JoinQueuePatches), nameof(OnSyncAlreadyHeldObjectsServerRpc));
            LobbyControl.Harmony.Patch(harmonyTarget, null, null, null, new HarmonyMethod(harmonyFinalizer), null);
        }
        else
        {
            LobbyControl.Log.LogFatal("Could not find RPC id for SyncAlreadyHeldObjectsServerRpc");
        }


        methodInfo =
            AccessTools.Method(typeof(PlayerControllerB), nameof(PlayerControllerB.SendNewPlayerValuesServerRpc));

        if (RPCUtils.TryGetRpcID(methodInfo, out id))
        {
            var harmonyTarget = AccessTools.Method(typeof(PlayerControllerB), $"__rpc_handler_{id}");
            var harmonyFinalizer = AccessTools.Method(typeof(JoinQueuePatches), nameof(OnSendNewPlayerValuesServerRpc));
            LobbyControl.Harmony.Patch(harmonyTarget, null, null, null, new HarmonyMethod(harmonyFinalizer), null);
        }
        else
        {
            LobbyControl.Log.LogFatal("Could not find RPC id for SendNewPlayerValuesServerRpc");
        }

        methodInfo =
            AccessTools.Method(typeof(HUDManager), nameof(HUDManager.SyncAllPlayerLevelsServerRpc),
                [typeof(int), typeof(int)]);

        if (RPCUtils.TryGetRpcID(methodInfo, out id))
        {
            var harmonyTarget = AccessTools.Method(typeof(HUDManager), $"__rpc_handler_{id}");
            var harmonyFinalizer = AccessTools.Method(typeof(JoinQueuePatches), nameof(OnSyncAllPlayerLevelsServerRpc));
            LobbyControl.Harmony.Patch(harmonyTarget, null, null, null, new HarmonyMethod(harmonyFinalizer), null);
        }
        else
        {
            LobbyControl.Log.LogFatal("Could not find RPC id for SyncAllPlayerLevelsServerRpc");
        }

        //Connection completed callback

        ConnectionEvents.OnConnectionCompletedServer += OnConnectionCompletedServer;

        //StartOfMatchLever

        var monoModTarget = AccessTools.Method(typeof(StartOfRound), nameof(StartOfRound.StartGame));

        if (monoModTarget != null)
            LobbyControl.Hooks.Add(new Hook(monoModTarget, CheckValidStart, new HookConfig { Priority = 999 }));
        else
            LobbyControl.Log.LogFatal("Cannot apply patch to StartGame");
    }

    private static bool _checkpointsInitialized;
    private static ConnectionCheckpoint _syncAlreadyHeldObjectsCheckpoint;
    private static ConnectionCheckpoint _sendNewPlayerValuesCheckpoint;
    private static ConnectionCheckpoint _syncAllPlayerLevelsCheckpoint;

    private static readonly
        ConcurrentQueue<(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse
            response, ulong? steamId)> ConnectionQueue = new();

    private static int QueuedClients => ConnectionQueue.Count + (ConnectionEvents.ConnectingClientId.HasValue ? 1 : 0);

    private static readonly Timer ConnectionTimer = new()
    {
        AutoReset = false
    };
    
    internal static ConcurrentDictionary<ulong, bool> DroppedConnections = new();

    private static void ForcefullyDisconnectClient(ulong clientId)
    {
        if (clientId == NetworkManager.Singleton.LocalClientId)
            return;
        
        DroppedConnections[clientId] = true;
        
        try
        {
            //Forcefully close the connection
            NetworkManager.Singleton.NetworkConfig.NetworkTransport.DisconnectRemoteClient(clientId);
        }
        catch (Exception ex)
        {
            LobbyControl.Log.LogError(ex);
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GameNetworkManager), nameof(GameNetworkManager.Awake))]
    private static void OnStartup(GameNetworkManager __instance)
    {
        if (_checkpointsInitialized)
            return;

        _checkpointsInitialized = true;

        _syncAlreadyHeldObjectsCheckpoint =
            ConnectionCheckpoint.RegisterCheckpoint(LobbyControl.Instance, "SyncAlreadyHeldObjects");

        _syncAllPlayerLevelsCheckpoint =
            ConnectionCheckpoint.RegisterCheckpoint(LobbyControl.Instance, "SyncAllPlayerLevels");

        if (__instance.disableSteam)
            return;

        _sendNewPlayerValuesCheckpoint =
            ConnectionCheckpoint.RegisterCheckpoint(LobbyControl.Instance, "SendNewPlayerValues");
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(GameNetworkManager), nameof(GameNetworkManager.ConnectionApproval))]
    [HarmonyPriority(10)]
    private static Exception ThrottleApprovals(
        GameNetworkManager __instance,
        NetworkManager.ConnectionApprovalRequest request,
        NetworkManager.ConnectionApprovalResponse response,
        Exception __exception)
    {
        if (__exception != null)
            return __exception;

        if (!PluginConfig.JoinQueue.Enabled.Value)
            return null;

        var maxQueueSize = PluginConfig.JoinQueue.MaxSize.Value;
        if (maxQueueSize > 0 && QueuedClients >= maxQueueSize)
        {
            LobbyControl.Log.LogWarning($"Connection refused, Queue full! count:{QueuedClients}");
            if (PluginConfig.JoinQueue.ConnectionPopup.Value)
            {
                HUDManager.Instance.StartCoroutine(HudUtils.ShowMessageAfterDelay("Connection refused",
                    $"Client {request.ClientNetworkId} requested a connection but queue was full!"));
            }

            var message = new StringBuilder();

            if (maxQueueSize == 1)
            {
                message.Append("Another player is connecting\n");
            }
            else
            {
                message.Append("Join Queue is Full !\n");
                message.AppendFormat("Queued connections: {0}\n", QueuedClients);
            }

            message.Append("Please Wait a bit before retrying");

            response.Approved = false;
            response.Reason = message.ToString();
        }

        if (!response.Approved)
            return null;

        response.Pending = true;

        var array = Encoding.ASCII.GetString(request.Payload).Split(",");

        ulong? friendId = null;

        if (!__instance.disableSteam && array.Length > 1 && ulong.TryParse(array[1], out var steamID))
        {
            friendId = steamID;
            Task.Run(() => SteamFriends.RequestUserInformation(steamID));

            if (PluginConfig.JoinQueue.ConnectionPopup.Value)
            {
                HUDManager.Instance.StartCoroutine(HudUtils.ShowMessageAfterDelay("Connection request",
                    $"Player {new Friend(steamID).Name}({request.ClientNetworkId}) requested a connection"));
            }
        }
        else
        {
            if (PluginConfig.JoinQueue.ConnectionPopup.Value)
            {
                HUDManager.Instance.StartCoroutine(HudUtils.ShowMessageAfterDelay("Connection request",
                    $"Client {request.ClientNetworkId} requested a connection"));
            }
        }

        ConnectionQueue.Enqueue((request, response, friendId));
        LobbyControl.Log.LogWarning($"Connection request Enqueued! count:{QueuedClients}");
        return null;
    }


    private static void OnConnectionCompletedServer(ulong clientId)
    {
        if (!PluginConfig.JoinQueue.Enabled.Value)
            return;

        //notify other players to reset the object variables
        var playerIndex = StartOfRound.Instance.ClientPlayerList[clientId];
        var targets = NetworkManager.Singleton.ConnectedClientsIds.ToList();
        //skip the connecting client as he's guaranteed to have all the values correct
        targets.Remove(clientId);
        NamedMessages.ResetPlayerValuesClientRpc(playerIndex, targets.ToArray());
        //re-sort the radar map so all clients are aligned
        NamedMessages.ReorderRadarClientRpc();
    }


    [HarmonyPrefix]
    [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.OnClientConnect))]
    private static void OnClientConnect(StartOfRound __instance, ulong clientId)
    {
        if (!__instance.IsServer)
            return;

        if (DroppedConnections.ContainsKey(clientId))
        {
            LobbyControl.Log.LogWarning(
                $"Dropped client {clientId} reconnected again, Terminating the connection!");
            ForcefullyDisconnectClient(clientId);
            return;
        }
        
        if (!PluginConfig.JoinQueue.Enabled.Value)
            return;

        if (ConnectionEvents.ConnectingClientId != clientId)
            LobbyControl.Log.LogError(
                $"client {clientId} connected while {ConnectionEvents.ConnectingClientId} was still being processed!");
        else
        {
            //reset timeout to be a bit more lenient
            ConnectionTimer.Stop();
            ConnectionTimer.Interval = PluginConfig.JoinQueue.ConnectionTimeout.Value;
            ConnectionTimer.Start();
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.OnPlayerConnectedClientRpc))]
    private static void OnClientConnect2(StartOfRound __instance, ulong clientId)
    {
        //run only if we're actually executing the Rpc code
        var networkManager = __instance.NetworkManager;
        if (networkManager == null || !networkManager.IsListening)
            return;
        if (__instance.__rpc_exec_stage != NetworkBehaviour.__RpcExecStage.Client ||
            !networkManager.IsClient && !networkManager.IsHost)
            return;

        if (!__instance.IsServer)
            return;

        if (PluginConfig.JoinQueue.Enabled.Value)
            return;

        //fallback event to when vanilla adds the playerIndex to StartOfRound.Instance.ClientPlayerList
        ConnectionEvents.RaiseConnectionCompleteServerEvent(clientId);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GameNetworkManager), nameof(GameNetworkManager.Singleton_OnClientDisconnectCallback))]
    private static void OnClientDisconnect(GameNetworkManager __instance, ulong clientId)
    {
        if (!__instance.isHostingGame)
            return;

        LobbyControl.Log.LogInfo($"{clientId} disconnected");

        if (ConnectionEvents.ConnectingClientId == clientId)
        {
            ConnectionEvents.ConnectingClientId = null;
            ConnectionEvents.ConnectingSteamId = null;
            ConnectionTimer.Stop();
        }
        else
        {
            //if we disconnected while in queue mark the response as Approved to skip it
            foreach (var (_, response, _) in ConnectionQueue.Where(e => e.request.ClientNetworkId == clientId))
            {
                response.Approved = false;
            }
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GameNetworkManager), nameof(GameNetworkManager.Singleton_OnClientConnectedCallback))]
    private static void OnClientConnect(GameNetworkManager __instance, ulong clientId)
    {
        if (!__instance.isHostingGame)
            return;

        LobbyControl.Log.LogInfo($"{clientId} connected");
    }

    private static void OnSyncAlreadyHeldObjectsServerRpc(
        NetworkBehaviour target,
        __RpcParams rpcParams)
    {
        if (!target.IsServer)
            return;

        if (ConnectionEvents.ConnectingClientId is null)
            return;

        var clientId = rpcParams.Server.Receive.SenderClientId;

        _syncAlreadyHeldObjectsCheckpoint.Set(clientId);
    }

    private static void OnSendNewPlayerValuesServerRpc(
        NetworkBehaviour target, __RpcParams rpcParams)
    {
        if (!target.IsServer)
            return;

        if (ConnectionEvents.ConnectingClientId is null)
            return;

        var clientId = rpcParams.Server.Receive.SenderClientId;

        _sendNewPlayerValuesCheckpoint.Set(clientId);
    }

    private static void OnSyncAllPlayerLevelsServerRpc(
        NetworkBehaviour target, __RpcParams rpcParams)
    {
        if (!target.IsServer)
            return;

        if (ConnectionEvents.ConnectingClientId is null)
            return;

        var clientId = rpcParams.Server.Receive.SenderClientId;

        _syncAllPlayerLevelsCheckpoint.Set(clientId);
    }


    [HarmonyPostfix]
    [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.LateUpdate))]
    private static void ProcessConnectionQueue(StartOfRound __instance)
    {
        if (!__instance.IsServer)
            return;

        try
        {
            //check if current connection reached all checkpoints!
            if (ConnectionEvents.HasCompletedAllCheckpoints && ConnectionEvents.ConnectingClientId.HasValue)
            {
                var clientId = ConnectionEvents.ConnectingClientId.Value;

                LobbyControl.Log.LogDebug($"{clientId} completed all the checkpoints");
                ConnectionEvents.ConnectingClientId = null;
                ConnectionEvents.ConnectingSteamId = null;

                ConnectionTimer.Stop();
                ConnectionTimer.Interval = PluginConfig.JoinQueue.ConnectionDelay.Value;
                ConnectionTimer.Start();

                LobbyControl.Log.LogWarning($"{clientId} completed the connection");

                ConnectionEvents.RaiseConnectionCompleteServerEvent(clientId);
            }

            //if we are still waiting for a connection to complete
            if (ConnectionEvents.ConnectingClientId.HasValue)
            {
                var clientId = ConnectionEvents.ConnectingClientId.Value;
                var steamId = ConnectionEvents.ConnectingSteamId;

                //wait till the timeout expires
                if (ConnectionTimer.Enabled)
                    return;

                //if there was an actual client
                if (clientId != 0L)
                {
                    var missing = ConnectionEvents.MissingCheckpoints;

                    LobbyControl.Log.LogWarning(
                        $"missing checkpoints for {clientId}: [{string.Join<ConnectionCheckpoint>(",", missing)}]");
                    

                    if (PluginConfig.JoinQueue.TimeoutPopup.Value)
                        if (steamId.HasValue)
                            HUDManager.Instance.StartCoroutine(HudUtils.ShowMessageAfterDelay("Connection Timeout",
                                $"Player {new Friend(steamId.Value).Name}({clientId})\nCheckpoints before the timeout"));
                        else
                            HUDManager.Instance.StartCoroutine(HudUtils.ShowMessageAfterDelay("Connection Timeout",
                                $"Client {clientId} missed {missing.Length}\nCheckpoints before the timeout"));

                    HUDManager.Instance.StartCoroutine(HudUtils.ShowTipAfterDelay("Connection Timeout",
                        "If clients frequently fail to connect maybe consider increasing \"connection_timeout_ms\" in LobbyControl config",
                        5, "LCTip_LCTimeout"));
                    
                    LobbyControl.Log.LogError(
                        $"Connection to {clientId} expired, Disconnecting!");
                    
                    ForcefullyDisconnectClient(clientId);
                }

                //allow the connection of the next client
                ConnectionEvents.ConnectingClientId = null;
                ConnectionEvents.ConnectingSteamId = null;
            }

            //if we can let new connections in
            if (LateJoinPatches._allowNewConnection)
            {
                //wait until the delay between connections
                if (ConnectionEvents.ConnectingClientId.HasValue || ConnectionTimer.Enabled)
                    return;

                if (!ConnectionQueue.TryDequeue(out var entry))
                    return;

                LobbyControl.Log.LogWarning(
                    $"Connection request Resumed! remaining: {ConnectionQueue.Count}");

                entry.response.Pending = false;
                if (!entry.response.Approved)
                    return;

                //if the queue has been disabled approve all the connections w/o waiting
                if (!PluginConfig.JoinQueue.Enabled.Value)
                    return;

                ConnectionEvents.ConnectingClientId = entry.request.ClientNetworkId;
                ConnectionEvents.ConnectingSteamId = entry.steamId;
                ConnectionTimer.Interval = PluginConfig.JoinQueue.ConnectionTimeout.Value;
                ConnectionTimer.Start();

                if (PluginConfig.JoinQueue.ConnectionPopup.Value)
                    if (entry.steamId.HasValue)
                        HUDManager.Instance.StartCoroutine(HudUtils.ShowMessageAfterDelay("Connection resumed",
                            $"Player {new Friend(entry.steamId.Value).Name}({entry.request.ClientNetworkId}) is now connecting"));

                return;
            }

            if (!ConnectionQueue.IsEmpty)
                return;

            foreach (var (_, approvalResponse, _) in ConnectionQueue)
            {
                approvalResponse.Approved = false;
                approvalResponse.Reason = "ship has landed!";
                approvalResponse.Pending = false;
            }

            ConnectionQueue.Clear();
        }
        catch (Exception ex)
        {
            LobbyControl.Log.LogError(ex);
        }
    }


    [HarmonyFinalizer]
    [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.OnLocalDisconnect))]
    private static void FlushConnectionQueue()
    {
        ConnectionEvents.ConnectingClientId = null;
        ConnectionEvents.ConnectingSteamId = null;
        ConnectionTimer.Stop();

        if (ConnectionQueue.Count > 0)
        {
            LobbyControl.Log.LogWarning(
                $"Disconnecting with {ConnectionQueue.Count} pending connection, Flushing!");
        }

        while (ConnectionQueue.TryDequeue(out var entry))
        {
            entry.response.Reason = "Host has disconnected!";
            entry.response.Approved = false;
            entry.response.Pending = false;
        }
    }

    //-----------HOST TIMEOUT DETECTION--------------

    private static readonly Stopwatch ConnectionStopwatch = new();

    [HarmonyPrefix]
    [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.Awake))]
    private static void OnHostStartLoad()
    {
        if (!NetworkManager.Singleton.IsServer)
            return;

        if (!PluginConfig.JoinQueue.Enabled.Value)
            return;

        ConnectionStopwatch.Restart();
    }

    [HarmonyTranspiler]
    [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.StartSpatialVoiceChat), MethodType.Enumerator)]
    private static IEnumerable<CodeInstruction> PatchStartSpatialVoiceChat(IEnumerable<CodeInstruction> instructions,
        ILGenerator ilGenerator)
    {
        var codes = instructions.ToList();

        var injector = new ILInjector(codes, ilGenerator);

        injector.Find([
            ILMatcher.Callvirt(typeof(HUDManager).GetMethod(nameof(HUDManager.SyncAllPlayerLevelsServerRpc),
                [typeof(int), typeof(int)]))
        ]);

        if (!injector.IsValid)
        {
            LobbyControl.Log.LogFatal(
                "Failed to find HUDManager.SyncPlayerLevelServerRpc in StartOfRound.StartSpatialVoiceChat!");
            return codes;
        }

        injector
            .GoToMatchEnd()
            .Insert(new CodeInstruction(OpCodes.Call,
                typeof(JoinQueuePatches).GetMethod(nameof(OnHostLoaded),
                    BindingFlags.Static | BindingFlags.NonPublic)));

        return injector.ReleaseInstructions();
    }

    private static void OnHostLoaded()
    {
        if (!NetworkManager.Singleton.IsServer)
            return;

        if (!PluginConfig.JoinQueue.Enabled.Value)
            return;

        ConnectionStopwatch.Stop();

        var elapsed = ConnectionStopwatch.ElapsedMilliseconds;
        var currentTimeout = PluginConfig.JoinQueue.ConnectionTimeout.Value;

        LobbyControl.Log.LogDebug($"Lobby took {elapsed}ms to load");

        if (currentTimeout >= elapsed)
            return;

        LobbyControl.Log.LogWarning(
            $"Lobby took {elapsed}ms to load but the configured connectionTimeout is only {currentTimeout}ms !");

        HUDManager.Instance.StartCoroutine(HudUtils.ShowMessageAfterDelay("Low Connection Timeout!",
            $"Lobby took {elapsed}ms to load but the configured connectionTimeout is only {currentTimeout}ms", 5));
    }


    //--------------HANDLE SHIP LEVER----------------

    private static bool CanStartGame()
    {
        return !ConnectionEvents.ConnectingClientId.HasValue && ConnectionQueue.IsEmpty;
    }

    private static void CheckValidStart(Action<StartOfRound> orig, StartOfRound @this)
    {
        if (!@this.IsServer || !@this.inShipPhase)
        {
            orig(@this);
            return;
        }

        if (!CanStartGame())
        {
            var count = ConnectionQueue.Count + (ConnectionEvents.ConnectingClientId.HasValue ? 1 : 0);

            var leverScript = Object.FindAnyObjectByType<StartMatchLever>();

            leverScript.CancelStartGame();
            leverScript.CancelStartGameClientRpc();

            HUDManager.Instance.StartCoroutine(
                HudUtils.ShowMessageAfterDelay(
                    "GAME START CANCELLED",
                    $"{count} Players Connecting!!",
                    0,
                    true
                ));

            HUDManager.Instance.AddTextMessageServerRpc(
                $"there are still {count} Players connecting!!\n");
            return;
        }

        LateJoinPatches._allowNewConnection = false;

        orig(@this);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(StartMatchLever), nameof(StartMatchLever.CancelStartGame))]
    private static void FixUnusableLever(StartMatchLever __instance)
    {
        __instance.triggerScript.interactable = true;
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.SetShipReadyToLand))]
    [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.Start))]
    private static void OnReadyToLand()
    {
        LateJoinPatches._allowNewConnection = true;
    }
}
