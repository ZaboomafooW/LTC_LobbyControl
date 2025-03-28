using System;
using HarmonyLib;
using Unity.Netcode;

namespace LobbyControl.Patches;

[HarmonyPatch]
internal class LogSpamPatches
{
    [HarmonyPatch(typeof(EnemyAI))]
    internal class EnemyAIPatch
    {
        [HarmonyPrefix]
        [HarmonyPatch(nameof(EnemyAI.SetDestinationToPosition))]
        private static bool StopIfDead1(EnemyAI __instance)
        {
            if (!LobbyControl.PluginConfig.LogSpam.Enabled.Value ||
                !LobbyControl.PluginConfig.LogSpam.CalculatePolygonPath.Value)
                return true;

            return !__instance.isEnemyDead;
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(EnemyAI.DoAIInterval))]
        private static void StopIfDead2(EnemyAI __instance)
        {
            if (!LobbyControl.PluginConfig.LogSpam.Enabled.Value ||
                !LobbyControl.PluginConfig.LogSpam.CalculatePolygonPath.Value)
                return;
            if (!__instance.isEnemyDead)
                return;

            __instance.moveTowardsDestination = false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(EnemyAI.PathIsIntersectedByLineOfSight))]
        private static bool StopIfDead3(EnemyAI __instance)
        {
            if (!LobbyControl.PluginConfig.LogSpam.Enabled.Value ||
                !LobbyControl.PluginConfig.LogSpam.CalculatePolygonPath.Value)
                return true;

            return !__instance.isEnemyDead;
        }
    }

    [HarmonyPatch]
    internal class AudioSpatializerPatch
    {
        [HarmonyFinalizer]
        [HarmonyPatch(typeof(NetworkSceneManager), nameof(NetworkSceneManager.OnSceneLoaded))]
        [HarmonyPatch(typeof(RoundManager), nameof(RoundManager.SetPowerOffAtStart))]
        private static void DisableSpatializers()
        {
            var startOfRound = StartOfRound.Instance;
            if (startOfRound == null)
                return;

            try
            {
                startOfRound.DisableSpatializationOnAllAudio();
            }
            catch (Exception ex)
            {
                LobbyControl.Log.LogError($"Exception disabling spatializers: {ex}");
            }
        }
    }
}
