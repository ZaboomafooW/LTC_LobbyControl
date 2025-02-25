using System;
using System.Collections.Generic;
using BepInEx;
using UnityEngine.Pool;

namespace LobbyControl.API;

public struct ConnectionCheckpoint : IDisposable
{
    public static ConnectionCheckpoint RegisterCheckpoint(BaseUnityPlugin source, string name)
    {
        return RegisterCheckpoint(source.Info, name);
    }

    public static ConnectionCheckpoint RegisterCheckpoint(PluginInfo source, string name)
    {
        return RegisterCheckpoint(source.Metadata, name);
    }

    public static ConnectionCheckpoint RegisterCheckpoint(BepInPlugin source, string name)
    {
        var dictionaryKey = (source, name);

        if (RegisteredCheckpoints.TryGetValue(dictionaryKey, out var checkpoint) && !checkpoint.IsDisposed)
            return checkpoint;

        checkpoint = new ConnectionCheckpoint(source, name);

        RegisteredCheckpoints[dictionaryKey] = checkpoint;
        return checkpoint;
    }

    public string Name { get; }
    public BepInPlugin Plugin { get; }

    public Int64 Mask { get; private set; }

    public bool IsDisposed => Mask == 0L;

    public bool Complete(ulong clientId)
    {
        if (IsDisposed)
            throw new InvalidOperationException($"This {nameof(ConnectionCheckpoint)} has already been disposed");

        if (_connectingClientId is null && !LobbyControl.PluginConfig.JoinQueue.Enabled.Value)
            return true;

        if (_connectingClientId != clientId)
        {
            LobbyControl.Log.LogWarning(
                $"CompleteCheckpoint ('{Name}' from '{Plugin.Name}') was called for client '{clientId}' but '{_connectingClientId}' was expected");
            return false;
        }

        _currentCheckpoints |= Mask;
        LobbyControl.Log.LogDebug($"client '{clientId}' completed checkpoint '{Name}' from '{Plugin.Name}'");

        return true;
    }

    public bool Reset(ulong clientId)
    {
        if (IsDisposed)
            throw new InvalidOperationException($"This {nameof(ConnectionCheckpoint)} has already been disposed");

        if (_connectingClientId is null && !LobbyControl.PluginConfig.JoinQueue.Enabled.Value)
            return true;

        if (_connectingClientId != clientId)
        {
            LobbyControl.Log.LogWarning(
                $"ResetCheckpoint ('{Name}' from '{Plugin.Name}') was called for client '{clientId}' but '{_connectingClientId}' was expected");
            return false;
        }

        LobbyControl.Log.LogDebug($"'{Plugin.Name}' reset checkpoint '{Name}' for client '{clientId}'");
        _currentCheckpoints &= ~Mask;

        return true;
    }

    public void Dispose()
    {
        if (IsDisposed)
            throw new InvalidOperationException($"This {nameof(ConnectionCheckpoint)} has already been disposed");

        _checkpointMask &= ~Mask;
        _currentCheckpoints &= ~Mask;

        Mask = 0;
    }

    public override string ToString()
    {
        return $"{{{Name} from {Plugin.Name}}}";
    }

    // internal stuff

    private ConnectionCheckpoint(BepInPlugin source, string name)
    {
        Name = name;
        Plugin = source;
        if (!GetNewMask(out var mask))
            throw new IndexOutOfRangeException("Too many checkpoints");

        LobbyControl.Log.LogDebug(
            $"Checkpoint '{name}' from '{source.Name}' created with mask '{Convert.ToString(mask, 2)}'");
        Mask = mask;
        _checkpointMask |= Mask;
    }

    private static bool GetNewMask(out Int64 mask)
    {
        uint index = 0;
        mask = 0b1;

        LobbyControl.Log.LogDebug(
            $"{Convert.ToString(_checkpointMask, 2)} & {Convert.ToString(mask, 2)} = {Convert.ToString(_checkpointMask & mask, 2)}");
        while ((_checkpointMask & mask) != 0)
        {
            index++;

            if (index >= 64)
                return false;

            mask <<= 1;
            LobbyControl.Log.LogDebug(
                $"{Convert.ToString(_checkpointMask, 2)} & {Convert.ToString(mask, 2)} = {Convert.ToString(_checkpointMask & mask, 2)}b");
        }

        return true;
    }

    private static Dictionary<(BepInPlugin plugin, string name), ConnectionCheckpoint> RegisteredCheckpoints = [];
    private static Int64 _checkpointMask;

    private static Int64 _currentCheckpoints;
    private static ulong? _connectingClientId;

    internal static ulong? ConnectingClientId
    {
        get => _connectingClientId;
        set
        {
            _currentCheckpoints = 0;
            _connectingClientId = value;
        }
    }

    internal static bool CurrentHasCompleted => _checkpointMask == _currentCheckpoints;
    internal static Int64 CurrentMissingMask => ~_currentCheckpoints & _checkpointMask;

    public static ConnectionCheckpoint[] CurrentMissingCheckpoints
    {
        get
        {
            var missing = CurrentMissingMask;
            using (ListPool<ConnectionCheckpoint>.Get(out var list))
            {
                foreach (var checkpoint in RegisteredCheckpoints.Values)
                {
                    if (checkpoint.IsDisposed)
                        continue;

                    if ((checkpoint.Mask & missing) != 0)
                        list.Add(checkpoint);
                }

                return list.ToArray();
            }
        }
    }
}
