using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using GameNetcodeStuff;
using HarmonyLib;
using LobbyControl.API;
using LobbyControl.Networking;
using MonoMod.RuntimeDetour;
using Unity.Netcode;
using Object = UnityEngine.Object;

namespace LobbyControl.Patches;

[HarmonyPatch]
internal class JoinPatches
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
        var gameStartedField = AccessTools.Field(typeof(GameNetworkManager), nameof(GameNetworkManager.gameHasStarted));
        List<CodeInstruction> code = instructions.ToList();

        for (var index = 0; index < code.Count; index++)
        {
            var curr = code[index];
            if (curr.LoadsField(gameStartedField))
            {
                var next = code[index + 1];
                var prec = code[index - 1];
                if (next.Branches(out Label? dest))
                {
                    code[index - 1] = new CodeInstruction(OpCodes.Nop)
                    {
                        labels = prec.labels,
                        blocks = prec.blocks
                    };
                    code[index] = new CodeInstruction(OpCodes.Nop)
                    {
                        labels = curr.labels,
                        blocks = curr.blocks
                    };
                    code[index + 1] = new CodeInstruction(OpCodes.Br, dest)
                    {
                        labels = next.labels,
                        blocks = next.blocks
                    };
                    LobbyControl.Log.LogDebug("Patched ConnectionApproval!!");
                    break;
                }
            }
        }

        return code;
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

        if (Utils.TryGetRpcID(methodInfo, out var id))
        {
            var harmonyTarget = AccessTools.Method(typeof(StartOfRound), $"__rpc_handler_{id}");
            var harmonyFinalizer = AccessTools.Method(typeof(JoinPatches), nameof(SyncAlreadyHeldObjectsCheckpoint));
            LobbyControl._harmony.Patch(harmonyTarget, null, null, null, new HarmonyMethod(harmonyFinalizer), null);
        }
        else
        {
            LobbyControl.Log.LogFatal("Could not find RPC id for SyncAlreadyHeldObjectsServerRpc");
        }


        methodInfo =
            AccessTools.Method(typeof(PlayerControllerB), nameof(PlayerControllerB.SendNewPlayerValuesServerRpc));

        if (Utils.TryGetRpcID(methodInfo, out id))
        {
            var harmonyTarget = AccessTools.Method(typeof(PlayerControllerB), $"__rpc_handler_{id}");
            var harmonyFinalizer = AccessTools.Method(typeof(JoinPatches), nameof(SendNewPlayerValuesCheckpoint));
            LobbyControl._harmony.Patch(harmonyTarget, null, null, null, new HarmonyMethod(harmonyFinalizer), null);
        }
        else
        {
            LobbyControl.Log.LogFatal("Could not find RPC id for SendNewPlayerValuesServerRpc");
        }

        methodInfo =
            AccessTools.Method(typeof(HUDManager), nameof(HUDManager.SyncAllPlayerLevelsServerRpc),
                [typeof(int), typeof(int)]);

        if (Utils.TryGetRpcID(methodInfo, out id))
        {
            var harmonyTarget = AccessTools.Method(typeof(PlayerControllerB), $"__rpc_handler_{id}");
            var harmonyFinalizer = AccessTools.Method(typeof(JoinPatches), nameof(SyncAllPlayerLevelsCheckpoint));
            LobbyControl._harmony.Patch(harmonyTarget, null, null, null, new HarmonyMethod(harmonyFinalizer), null);
        }
        else
        {
            LobbyControl.Log.LogFatal("Could not find RPC id for SyncAllPlayerLevelsServerRpc");
        }

        //StartOfMatchLever

        var monoModTarget = AccessTools.Method(typeof(StartOfRound), nameof(StartOfRound.StartGame));

        if (monoModTarget != null)
            LobbyControl.Hooks.Add(new Hook(monoModTarget, CheckValidStart, new HookConfig { Priority = 999 }));
        else
            LobbyControl.Log.LogFatal("Cannot apply patch to StartGame");
    }


    [HarmonyPrefix]
    [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.OnClientConnect))]
    private static void OnClientConnect(StartOfRound __instance, ulong clientId)
    {
        if (!__instance.IsServer || !LobbyControl.PluginConfig.JoinQueue.Enabled.Value)
            return;

        if (ConnectionCheckpoint.ConnectingClientId != clientId)
            LobbyControl.Log.LogError(
                $"client {clientId} connected while {ConnectionCheckpoint.ConnectingClientId} was still being processed!");
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GameNetworkManager), nameof(GameNetworkManager.Singleton_OnClientDisconnectCallback))]
    private static void OnClientDisconnect(GameNetworkManager __instance, ulong clientId)
    {
        if (!__instance.isHostingGame)
            return;

        LobbyControl.Log.LogInfo($"{clientId} disconnected");

        if (ConnectionCheckpoint.ConnectingClientId != clientId)
            return;

        ConnectionCheckpoint.ConnectingClientId = null;
        _currentConnectingExpiration = 0;
    }

    private static void SyncAlreadyHeldObjectsCheckpoint(
        NetworkBehaviour target,
        __RpcParams rpcParams)
    {
        if (!target.IsServer)
            return;

        var clientId = rpcParams.Server.Receive.SenderClientId;

        _syncAlreadyHeldObjectsCheckpoint.Complete(clientId);
    }

    private static void SendNewPlayerValuesCheckpoint(
        NetworkBehaviour target, __RpcParams rpcParams)
    {
        if (!target.IsServer)
            return;

        var clientId = rpcParams.Server.Receive.SenderClientId;

        _sendNewPlayerValuesCheckpoint.Complete(clientId);
    }

    private static void SyncAllPlayerLevelsCheckpoint(
        NetworkBehaviour target, __RpcParams rpcParams)
    {
        if (!target.IsServer)
            return;

        var clientId = rpcParams.Server.Receive.SenderClientId;

        _syncAllPlayerLevelsCheckpoint.Complete(clientId);
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
            if (ConnectionCheckpoint.CurrentHasCompleted && ConnectionCheckpoint.ConnectingClientId != null)
            {
                var clientId = ConnectionCheckpoint.ConnectingClientId.Value;

                LobbyControl.Log.LogDebug($"{clientId} completed all the checkpoints");
                ConnectionCheckpoint.ConnectingClientId = null;
                _currentConnectingExpiration = (ulong)(Environment.TickCount +
                                                       LobbyControl.PluginConfig.JoinQueue.ConnectionDelay.Value);

                LobbyControl.Log.LogWarning($"{clientId} completed the connection");

                //notify other players to reset the object variables
                var playerIndex = StartOfRound.Instance.ClientPlayerList[clientId];
                var targets = NetworkManager.Singleton.ConnectedClientsIds.ToList();
                //skip the connecting client as he's guaranteed to have all the values correct
                targets.Remove(clientId);
                NamedMessages.ResetPlayerValuesClientRpc(playerIndex, targets.ToArray());
                //re-sort the radar map so all clients are aligned
                NamedMessages.ReorderRadarClientRpc();
            }

            //if we are still waiting for a connection to complete
            if (ConnectionCheckpoint.ConnectingClientId.HasValue)
            {
                var clientId = ConnectionCheckpoint.ConnectingClientId.Value;

                //wait till the timeout expires
                if ((ulong)Environment.TickCount < _currentConnectingExpiration)
                    return;

                //if there was an actual client
                if (clientId != 0L)
                {
                    LobbyControl.Log.LogWarning(
                        $"Connection to {clientId} expired, Disconnecting!");
                    LobbyControl.Log.LogDebug(
                        $"missing checkpoints for {clientId}: [{string.Join(",", ConnectionCheckpoint.CurrentMissingCheckpoints)}]");
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
                ConnectionCheckpoint.ConnectingClientId = null;
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

                ConnectionCheckpoint.ConnectingClientId = entry.request.ClientNetworkId;
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
        ConnectionCheckpoint.ConnectingClientId = null;
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
        return !ConnectionCheckpoint.ConnectingClientId.HasValue && ConnectionQueue.IsEmpty && !_testValue;
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
            var count = ConnectionQueue.Count + (ConnectionCheckpoint.ConnectingClientId.HasValue ? 1 : 0);

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
