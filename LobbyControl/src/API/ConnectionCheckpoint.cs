using System;
using BepInEx;

// ReSharper disable MemberCanBePrivate.Global

namespace LobbyControl.API;

public partial class ConnectionCheckpoint : IDisposable
{
    public static partial ConnectionCheckpoint RegisterCheckpoint(BaseUnityPlugin source, string name);

    public static partial ConnectionCheckpoint RegisterCheckpoint(PluginInfo source, string name);

    public static partial ConnectionCheckpoint RegisterCheckpoint(BepInPlugin source, string name);

    public string Name { get; }
    public BepInPlugin Plugin { get; }

    public Int64 Mask { get; private set; }

    public partial bool IsDisposed { get; }

    public partial bool Set(ulong clientId);

    public partial bool Reset(ulong clientId);

    public partial void Dispose();
}
