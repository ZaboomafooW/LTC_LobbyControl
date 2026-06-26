using HarmonyLib;
using LobbyControl.API;
using LobbyControl.Networking;
using Unity.Netcode;

namespace LobbyControl.Patches;

[HarmonyPatch]
internal class NetworkManagerPatch
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(NetworkManager), nameof(NetworkManager.Initialize))]
    private static void AfterInitialize()
    {
        LobbyControl.Log.LogInfo("Registering Named Messages!");
        NamedMessages.RegisterNamedMessages();
        ConnectionEvents.RegisterNamedMessages();
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameNetworkManager), "SetInstanceValuesBackToDefault")]
    public static void SetInstanceValuesBackToDefault()
    {
        if (NetworkManager.Singleton == null || NetworkManager.Singleton.CustomMessagingManager == null)
            return;
        
        LobbyControl.Log.LogInfo("Unregistering Named Messages!");
        NamedMessages.UnregisterNamedMessages();
        ConnectionEvents.UnregisterNamedMessages();
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(NetworkManager), nameof(NetworkManager.SetSingleton))]
    public static void SetClientTimeout(NetworkManager __instance)
    {
        __instance.NetworkConfig.ClientConnectionBufferTimeout =
            PluginConfig.JoinQueue.ConnectionTimeout.Value / 1000 * 4;
    }
}