using System;

namespace LobbyControl.API;

public static partial class ConnectionEvents
{
    public static partial ulong? ConnectingClientId { get; internal set; }
    public static ulong? ConnectingSteamId { get; internal set; }
    public static partial ConnectionCheckpoint[] MissingCheckpoints { get; }

    //---------------------EVENTS------------------------
    public static event Action<ulong> OnConnectionCompletedServer;
    public static event Action<ulong> OnClientConnectionCompletedClient;
    public static event Action<ulong, ConnectionCheckpoint> OnConnectionCheckpointServer;
}
