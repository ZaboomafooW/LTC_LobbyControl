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
}