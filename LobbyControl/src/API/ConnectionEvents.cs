using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;

namespace LobbyControl.API;

public static class ConnectionEvents
{
    //---------------------EVENTS------------------------

    public static event Action<ulong> OnConnectionCompletedServer;
    public static event Action<ulong> OnClientConnectionCompletedClient;
    public static event Action<ulong, ConnectionCheckpoint> OnConnectionCheckpointServer;

    //--------------------INTERNAL STUFF------------------

    internal static void RaiseConnectionCheckpointServerEvent(ulong clientId, ConnectionCheckpoint checkpoint)
    {
        try
        {
            OnConnectionCheckpointServer?.Invoke(clientId, checkpoint);
        }
        catch (Exception ex)
        {
            LobbyControl.Log.LogError($"Exception while processing ConnectionCheckpointServerEvent: {ex}");
        }
    }

    internal static void RaiseConnectionCompleteServerEvent(ulong clientId)
    {
        try
        {
            RaiseConnectionCompleteEventClientRpc(clientId);
            OnConnectionCompletedServer?.Invoke(clientId);
        }
        catch (Exception ex)
        {
            LobbyControl.Log.LogError($"Exception while processing ConnectionCompleteServerEvent: {ex}");
        }
    }

    private static readonly string BaseName = typeof(ConnectionEvents).FullName;

    private static readonly string RaiseConnectionCompleteEventClientRpcMessage =
        $"{BaseName}|RaiseConnectionCompleteEvent";

    internal static void RegisterNamedMessages()
    {
        NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(
            RaiseConnectionCompleteEventClientRpcMessage,
            OnRaiseConnectionCompleteEventClientRpc);
    }


    internal static void RaiseConnectionCompleteEventClientRpc(ulong clientId, IReadOnlyList<ulong> targets = null)
    {
        if (!NetworkManager.Singleton.IsServer)
            return;

        var buffer = new FastBufferWriter(sizeof(ulong), Allocator.Temp);
        buffer.WriteValue(clientId);

        if (targets == null)
            NetworkManager.Singleton.CustomMessagingManager.SendNamedMessageToAll(
                RaiseConnectionCompleteEventClientRpcMessage, buffer);
        else
            NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage(
                RaiseConnectionCompleteEventClientRpcMessage, targets,
                buffer);
    }

    private static void OnRaiseConnectionCompleteEventClientRpc(ulong senderId, FastBufferReader data)
    {
        if (senderId != NetworkManager.ServerClientId)
            return;

        if (!GameNetworkManager.Instance || !GameNetworkManager.Instance.localPlayerController)
        {
            LobbyControl.Log.LogError(
                $"Received {nameof(RaiseConnectionCompleteEventClientRpc)} while not connected to a lobby!");
            return;
        }

        data.ReadValue(out ulong clientId);

        try
        {
            OnClientConnectionCompletedClient?.Invoke(clientId);
        }
        catch (Exception ex)
        {
            LobbyControl.Log.LogError($"Exception while processing ConnectionCompleteClientEvent: {ex}");
        }
    }
}