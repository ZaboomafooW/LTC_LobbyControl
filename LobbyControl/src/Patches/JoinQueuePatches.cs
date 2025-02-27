using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using GameNetcodeStuff;
using HarmonyLib;
using LobbyControl.API;
using LobbyControl.Networking;
using LobbyControl.Utils;
using LobbyControl.Utils.IL;
using MonoMod.RuntimeDetour;
using Unity.Netcode;
using Object = UnityEngine.Object;

namespace LobbyControl.Patches;

[HarmonyPatch]
internal class JoinQueuePatches
{
    private static bool _allowNewConnection;

    private static bool _checkpointsInitialized;
    private static ConnectionCheckpoint _syncAlreadyHeldObjectsCheckpoint;
    private static ConnectionCheckpoint _sendNewPlayerValuesCheckpoint;
    private static ConnectionCheckpoint _syncAllPlayerLevelsCheckpoint;

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

    //-----------------------ALLOW LATE JOINS----------------------------

    //Do not check for gameHasStarted.
    [HarmonyTranspiler]
    [HarmonyPatch(typeof(GameNetworkManager), nameof(GameNetworkManager.ConnectionApproval))]
    private static IEnumerable<CodeInstruction> FixConnectionApprovalPrefix(
        IEnumerable<CodeInstruction> instructions)
    {
        var codes = instructions.ToList();
        //   }
        // - else if (GameNetworkManager.Instance.gameHasStarted)
        // - {
        // -     response.Reason = "Game has already started!";
        // -     flag = false;
        // - }
        //   else if (GameNetworkManager.Instance.gameVersionNum.ToString() != strArray[0])
        var injector = new ILInjector(codes)
            .Find([
                ILMatcher.Call(typeof(GameNetworkManager).GetProperty(nameof(GameNetworkManager.Instance))?.GetMethod),
                ILMatcher.Ldfld(typeof(GameNetworkManager).GetField(nameof(GameNetworkManager.gameHasStarted),
                    BindingFlags.Instance | BindingFlags.Public)),
                ILMatcher.Opcode(OpCodes.Brfalse).CaptureLabelOperandAs(out var gameHasStartedLabel),
            ]);

        if (!injector.IsValid)
        {
            // print error
            LobbyControl.Log.LogWarning("ConnectionApproval patch failed!!");
            LobbyControl.Log.LogDebug(string.Join("\n", injector.ReleaseInstructions()));
            return codes;
        }

        return injector
            .RemoveLastMatch()
            .FindLabel(gameHasStartedLabel)
            .RemoveLastMatch()
            .ReleaseInstructions();
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

        if (!response.Approved)
            return null;

        //if we're already landing
        if (!_allowNewConnection)
        {
            LobbyControl.Log.LogDebug("connection refused ( ship was landed ).");
            response.Reason = "Ship has already landed!";
            response.Approved = false;
            return null;
        }

        //if lobby is closed
        if (!__instance.disableSteam &&
            (!__instance.currentLobby.HasValue || !LobbyPatcher.IsOpen(__instance.currentLobby.Value)))
        {
            LobbyControl.Log.LogDebug("connection refused ( lobby was closed ).");
            response.Reason = "Lobby has been closed!";
            response.Approved = false;
            return null;
        }

        //log late joins
        if (__instance.gameHasStarted)
        {
            LobbyControl.Log.LogDebug("Incoming late connection.");
        }

        if (!LobbyControl.PluginConfig.JoinQueue.Enabled.Value)
            return null;

        response.Pending = true;
        ConnectionQueue.Enqueue((request, response));
        LobbyControl.Log.LogWarning($"Connection request Enqueued! count:{ConnectionQueue.Count}");
        return null;
    }

    //--------------------JOIN QUEUE LOGIC----------------------------

    private static readonly
        ConcurrentQueue<(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse
            response)> ConnectionQueue = new();

    private static ulong _currentConnectingExpiration;

    internal static void Init()
    {
        var methodInfo =
            AccessTools.Method(typeof(StartOfRound), nameof(StartOfRound.SyncAlreadyHeldObjectsServerRpc));

        if (RPCUtils.TryGetRpcID(methodInfo, out var id))
        {
            var harmonyTarget = AccessTools.Method(typeof(StartOfRound), $"__rpc_handler_{id}");
            var harmonyFinalizer =
                AccessTools.Method(typeof(JoinQueuePatches), nameof(OnSyncAlreadyHeldObjectsServerRpc));
            LobbyControl._harmony.Patch(harmonyTarget, null, null, null, new HarmonyMethod(harmonyFinalizer), null);
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
            LobbyControl._harmony.Patch(harmonyTarget, null, null, null, new HarmonyMethod(harmonyFinalizer), null);
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
            LobbyControl._harmony.Patch(harmonyTarget, null, null, null, new HarmonyMethod(harmonyFinalizer), null);
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

    private static void OnConnectionCompletedServer(ulong clientId)
    {
        if (!LobbyControl.PluginConfig.JoinQueue.Enabled.Value)
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
        if (!__instance.IsServer || !LobbyControl.PluginConfig.JoinQueue.Enabled.Value)
            return;

        if (ConnectionEvents.ConnectingClientId != clientId)
            LobbyControl.Log.LogError(
                $"client {clientId} connected while {ConnectionEvents.ConnectingClientId} was still being processed!");
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

        if (LobbyControl.PluginConfig.JoinQueue.Enabled.Value)
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

        if (ConnectionEvents.ConnectingClientId != clientId)
            return;

        ConnectionEvents.ConnectingClientId = null;
        _currentConnectingExpiration = 0;
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
            if (ConnectionEvents.HasCompletedAllCheckpoints && ConnectionEvents.ConnectingClientId != null)
            {
                var clientId = ConnectionEvents.ConnectingClientId.Value;

                LobbyControl.Log.LogDebug($"{clientId} completed all the checkpoints");
                ConnectionEvents.ConnectingClientId = null;
                _currentConnectingExpiration = (ulong)(Environment.TickCount +
                                                       LobbyControl.PluginConfig.JoinQueue.ConnectionDelay.Value);

                LobbyControl.Log.LogWarning($"{clientId} completed the connection");

                ConnectionEvents.RaiseConnectionCompleteServerEvent(clientId);
            }

            //if we are still waiting for a connection to complete
            if (ConnectionEvents.ConnectingClientId.HasValue)
            {
                var clientId = ConnectionEvents.ConnectingClientId.Value;

                //wait till the timeout expires
                if ((ulong)Environment.TickCount < _currentConnectingExpiration)
                    return;

                //if there was an actual client
                if (clientId != 0L)
                {
                    LobbyControl.Log.LogWarning(
                        $"Connection to {clientId} expired, Disconnecting!");
                    LobbyControl.Log.LogWarning(
                        $"missing checkpoints for {clientId}: [{string.Join<ConnectionCheckpoint>(",", ConnectionEvents.MissingCheckpoints)}]");
                    try
                    {
                        NetworkManager.Singleton.DisconnectClient(clientId);
                    }
                    catch (Exception ex)
                    {
                        LobbyControl.Log.LogError(ex);
                    }
                }

                //allow the connection of the next client
                ConnectionEvents.ConnectingClientId = null;
                _currentConnectingExpiration = 0;
            }

            //if we can let new connections in
            if (_allowNewConnection)
            {
                //wait until the delay between connections
                if ((ulong)Environment.TickCount < _currentConnectingExpiration)
                    return;

                if (!ConnectionQueue.TryDequeue(out var entry))
                    return;

                LobbyControl.Log.LogWarning(
                    $"Connection request Resumed! remaining: {ConnectionQueue.Count}");

                entry.response.Pending = false;
                if (!entry.response.Approved)
                    return;

                //if the queue has been disabled approve all the connections w/o waiting
                if (!LobbyControl.PluginConfig.JoinQueue.Enabled.Value)
                    return;

                ConnectionEvents.ConnectingClientId = entry.request.ClientNetworkId;
                _currentConnectingExpiration = (ulong)(Environment.TickCount +
                                                       LobbyControl.PluginConfig.JoinQueue.ConnectionTimeout.Value);
                return;
            }

            if (ConnectionQueue.IsEmpty)
                return;

            foreach (var (_, approvalResponse) in ConnectionQueue)
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
        _currentConnectingExpiration = 0UL;
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

    //--------------HANDLE SHIP LEVER----------------

    // ReSharper disable once ConvertToConstant.Local
    // ReSharper disable once FieldCanBeMadeReadOnly.Local
    private static bool _testValue = false;

    private static bool CanStartGame()
    {
        return !ConnectionEvents.ConnectingClientId.HasValue && ConnectionQueue.IsEmpty && !_testValue;
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

            HUDManager.Instance.DisplayTip(
                "GAME START CANCELLED",
                $"{count} Players Connecting!!",
                true);

            HUDManager.Instance.AddTextMessageServerRpc(
                $"there are still {count} Players connecting!!\n");
            return;
        }

        _allowNewConnection = false;

        orig(@this);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(StartMatchLever), nameof(StartMatchLever.CancelStartGame))]
    private static void FixUnusableLever(StartMatchLever __instance)
    {
        __instance.triggerScript.interactable = true;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.SetShipReadyToLand))]
    [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.Start))]
    private static void OnReadyToLand()
    {
        _allowNewConnection = true;
    }
}