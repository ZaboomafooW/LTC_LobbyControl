using System;
using Steamworks;

namespace LobbyControl.API;

public static partial class ConnectionEvents
{
    public static partial ulong? ConnectingClientId { get; }
    public static partial SteamId? ConnectingSteamId { get; }
    public static partial ConnectionCheckpoint[] MissingCheckpoints { get; }

    //---------------------EVENTS------------------------
    public static event Action<ulong> OnConnectionCompletedServer;
    public static event Action<ulong> OnClientConnectionCompletedClient;
    public static event Action<ulong, ConnectionCheckpoint> OnConnectionCheckpointServer;
}
