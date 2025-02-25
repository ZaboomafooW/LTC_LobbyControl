using HarmonyLib;
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
        LobbyControl.Log.LogInfo("Registering Custom Messages!");
        NamedMessages.RegisterMessages();
    }
}