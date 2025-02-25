using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace LobbyControl.Networking;

internal static class NamedMessages
{
    private static readonly string BaseName = typeof(NamedMessages).FullName;
    private static readonly string ReorderRadarClientRpcMessage = $"{BaseName}|ReorderRadarClientRpc";
    private static readonly string ResetPlayerValuesClientRpcMessage = $"{BaseName}|ResetPlayerValuesClientRpc";

    internal static void RegisterMessages()
    {
        NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(ReorderRadarClientRpcMessage,
            OnReorderRadarClientRpc);
        NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(ResetPlayerValuesClientRpcMessage,
            OnResetPlayerValuesClientRpc);
    }

    internal static void ReorderRadarClientRpc(ulong[] targets = null)
    {
        if (!NetworkManager.Singleton.IsServer)
            return;

        if (!LobbyControl.PluginConfig.Networking.Enabled.Value ||
            !LobbyControl.PluginConfig.Networking.SyncRadarNames.Value)
            return;

        var buffer = new FastBufferWriter(0, Allocator.Temp);

        if (targets == null)
            NetworkManager.Singleton.CustomMessagingManager.SendNamedMessageToAll(ReorderRadarClientRpcMessage, buffer);
        else
            NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage(ReorderRadarClientRpcMessage, targets,
                buffer);
    }

    private static void OnReorderRadarClientRpc(ulong senderId, FastBufferReader data)
    {
        if (senderId != NetworkManager.ServerClientId)
            return;

        if (!LobbyControl.PluginConfig.Networking.Enabled.Value ||
            !LobbyControl.PluginConfig.Networking.SyncRadarNames.Value)
            return;

        if (!StartOfRound.Instance || !StartOfRound.Instance.localPlayerController || !StartOfRound.Instance.mapScreen)
        {
            LobbyControl.Log.LogError($"Received {nameof(ReorderRadarClientRpc)} while not connected to a lobby!");
            return;
        }

        StartOfRound.Instance.mapScreen.SyncOrderOfRadarBoostersInList();
    }

    internal static void ResetPlayerValuesClientRpc(int playerIndex, ulong[] targets = null)
    {
        if (!NetworkManager.Singleton.IsServer)
            return;

        if (!LobbyControl.PluginConfig.Networking.Enabled.Value ||
            !LobbyControl.PluginConfig.Networking.ResetPlayerValues.Value)
            return;

        var buffer = new FastBufferWriter(sizeof(int), Allocator.Temp);
        buffer.WriteValue(playerIndex);

        if (targets == null)
            NetworkManager.Singleton.CustomMessagingManager.SendNamedMessageToAll(ReorderRadarClientRpcMessage, buffer);
        else
            NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage(ReorderRadarClientRpcMessage, targets,
                buffer);
    }

    private static void OnResetPlayerValuesClientRpc(ulong senderId, FastBufferReader data)
    {
        if (senderId != NetworkManager.ServerClientId)
            return;

        if (!LobbyControl.PluginConfig.Networking.Enabled.Value ||
            !LobbyControl.PluginConfig.Networking.ResetPlayerValues.Value)
            return;

        if (!GameNetworkManager.Instance || !GameNetworkManager.Instance.localPlayerController)
        {
            LobbyControl.Log.LogError($"Received {nameof(ResetPlayerValuesClientRpc)} while not connected to a lobby!");
            return;
        }

        data.ReadValue(out int playerIndex);

        var startOfRound = StartOfRound.Instance;
        var playerScript = startOfRound.allPlayerScripts[playerIndex];
        var playerObject = startOfRound.allPlayerObjects[playerIndex];

        //do not update our own data
        if (playerScript == GameNetworkManager.Instance.localPlayerController)
            return;

        playerScript.ResetPlayerBloodObjects(playerScript.isPlayerDead);

        playerScript.isClimbingLadder = false;
        playerScript.clampLooking = false;
        playerScript.inVehicleAnimation = false;
        playerScript.disableMoveInput = false;
        playerScript.ResetZAndXRotation();
        playerScript.thisController.enabled = true;
        playerScript.health = 100;
        playerScript.hasBeenCriticallyInjured = false;
        playerScript.disableLookInput = false;
        playerScript.disableInteract = false;
        Debug.Log("Reviving players B");

        playerScript.isPlayerDead = false;

        playerScript.overrideGameOverSpectatePivot = null;
        startOfRound.SetPlayerObjectExtrapolate(enable: false);
        playerScript.setPositionOfDeadPlayer = false;
        playerScript.DisablePlayerModel(playerObject, enable: true, disableLocalArms: true);

        playerScript.helmetLight.enabled = false;

        playerScript.Crouch(crouch: false);

        playerScript.criticallyInjured = false;

        if (playerScript.playerBodyAnimator != null)
        {
            playerScript.playerBodyAnimator.SetBool("Limp", value: false);
        }

        playerScript.bleedingHeavily = false;
        playerScript.activatingItem = false;

        playerScript.twoHanded = false;

        playerScript.inShockingMinigame = false;
        playerScript.inSpecialInteractAnimation = false;
        playerScript.freeRotationInInteractAnimation = false;
        playerScript.disableSyncInAnimation = false;
        playerScript.inAnimationWithEnemy = null;

        playerScript.holdingWalkieTalkie = false;
        playerScript.speakingToWalkieTalkie = false;

        playerScript.isSinking = false;
        playerScript.isUnderwater = false;
        playerScript.sinkingValue = 0f;
        playerScript.statusEffectAudio.Stop();

        playerScript.DisableJetpackControlsLocally();

        playerScript.health = 100;

        playerScript.mapRadarDotAnimator.SetBool("dead", value: false);
        playerScript.externalForceAutoFade = Vector3.zero;

        playerScript.voiceMuffledByEnemy = false;
        SoundManager.Instance.playerVoicePitchTargets[playerIndex] = 1f;
        SoundManager.Instance.SetPlayerPitch(1f, playerIndex);

        if (playerScript.currentVoiceChatIngameSettings == null)
        {
            startOfRound.RefreshPlayerVoicePlaybackObjects();
        }

        if (playerScript.currentVoiceChatIngameSettings != null)
        {
            if (playerScript.currentVoiceChatIngameSettings.voiceAudio == null)
            {
                playerScript.currentVoiceChatIngameSettings.InitializeComponents();
            }

            if (playerScript.currentVoiceChatIngameSettings.voiceAudio == null)
            {
                return;
            }

            playerScript.currentVoiceChatIngameSettings.voiceAudio.GetComponent<OccludeAudio>().overridingLowPass =
                false;
        }
    }
}
