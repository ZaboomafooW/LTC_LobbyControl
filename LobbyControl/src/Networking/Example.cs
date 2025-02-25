using Unity.Collections;
using Unity.Netcode;

namespace LobbyControl.Networking;

internal static class Example
{
    private static readonly string BaseName = typeof(Example).FullName;
    private static readonly string ExampleServerRpcMessage = $"{BaseName}|ExampleServerRpc";
    private static readonly string ExampleClientRpcMessage = $"{BaseName}|ExampleClientRpc";

    internal static void RegisterMessages()
    {
        NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(ExampleServerRpcMessage,
            OnExampleServerRpc);
        NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(ExampleClientRpcMessage,
            OnExampleClientRpc);
    }

    public static void ExampleServerRpc()
    {
        var buffer = new FastBufferWriter(1024, Allocator.Temp);
        NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage(ExampleServerRpcMessage,
            NetworkManager.ServerClientId, buffer);
    }

    private static void OnExampleServerRpc(ulong senderId, FastBufferReader data)
    {
        if (!NetworkManager.Singleton.IsServer)
            return;
        ExampleClientRpc();
    }

    private static void ExampleClientRpc(ulong[] targets = null)
    {
        var buffer = new FastBufferWriter(1024, Allocator.Temp);
        if (targets == null)
            NetworkManager.Singleton.CustomMessagingManager.SendNamedMessageToAll(ExampleClientRpcMessage, buffer);
        else
            NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage(ExampleClientRpcMessage, targets,
                buffer);
    }

    private static void OnExampleClientRpc(ulong senderId, FastBufferReader data)
    {
    }
}