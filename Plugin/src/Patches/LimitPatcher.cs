using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using LobbyControl.Utils.IL;
using Unity.Collections;
using Unity.Netcode;

namespace LobbyControl.Patches;

[HarmonyPatch]
internal class LimitPatcher
{
    [HarmonyTranspiler]
    [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.SyncShipUnlockablesClientRpc))]
    private static IEnumerable<CodeInstruction> PacketSizePatch(IEnumerable<CodeInstruction> instructions)
    {
        if (!PluginConfig.SaveLimit.Enabled.Value)
            return instructions;

        var codes = instructions.ToList();
        // - FastBufferWriter bufferWriter = this.__beginSendClientRpc(1450473930U, clientRpcParams, RpcDelivery.Reliable);
        // + FastBufferWriter bufferWriter = LimitPatcher.BiggerBuffer(this.__beginSendClientRpc(1450473930U, clientRpcParams, RpcDelivery.Reliable));
        //   bool isNotNull = playerSuitIDs != null;
        var injector = new ILInjector(codes)
            .Find([
                ILMatcher.Ldarg(),
                ILMatcher.Ldc(),
                ILMatcher.Ldloc(),
                ILMatcher.Ldc(),
                ILMatcher.Call(typeof(NetworkBehaviour).GetMethod(nameof(NetworkBehaviour.__beginSendClientRpc),
                    BindingFlags.Instance | BindingFlags.NonPublic)),
                ILMatcher.Stloc(),
            ]);

        if (!injector.IsValid)
        {
            // print error
            LobbyControl.Log.LogWarning("SyncShipUnlockablesClientRpc patch failed!!");
            LobbyControl.Log.LogDebug(string.Join("\n", injector.ReleaseInstructions()));
            return codes;
        }

        return injector
            .GoToMatchEnd()
            .Back(1)
            .Insert([
                new CodeInstruction(OpCodes.Call,
                    typeof(LimitPatcher).GetMethod(nameof(BiggerBuffer), BindingFlags.Static | BindingFlags.NonPublic)),
            ])
            .ReleaseInstructions();
    }

    private static FastBufferWriter BiggerBuffer(FastBufferWriter bufferWriter)
    {
        bufferWriter.Dispose();
        return new FastBufferWriter(1024, Allocator.Temp, 1 << 29 /*500MiB*/);
    }

    [HarmonyTranspiler]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.SyncShipUnlockablesServerRpc))]
    private static IEnumerable<CodeInstruction> SyncUnlockablesPatch(IEnumerable<CodeInstruction> instructions)
    {
        if (!PluginConfig.SaveLimit.Enabled.Value)
            return instructions;

        var codes = instructions.ToList();
        var injector = new ILInjector(codes);

        // - if (i > 500) {
        // - {
        // -   ..
        // - }
        //   if (items[i].itemProperties.saveItemVariable)
        injector
            .Find([
                ILMatcher.Ldfld(typeof(GrabbableObject).GetField(nameof(GrabbableObject.itemProperties))),
                ILMatcher.Ldfld(typeof(Item).GetField(nameof(Item.saveItemVariable))),
            ])
            .ReverseFind([
                ILMatcher.Ldloc(),
                ILMatcher.Ldc(),
                ILMatcher.Opcode(OpCodes.Ble).CaptureOperandAs(out Label itemInBoundsLabel),
            ]);

        if (!injector.IsValid)
        {
            LobbyControl.Log.LogWarning("SyncShipUnlockablesServerRpc patch failed 1!!");
            LobbyControl.Log.LogDebug(string.Join("\n", injector.ReleaseInstructions()));
            return codes;
        }

        return injector
            .RemoveLastMatch()
            .FindLabel(itemInBoundsLabel)
            .RemoveLastMatch()
            .ReleaseInstructions();
    }

    [HarmonyTranspiler]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(GameNetworkManager), nameof(GameNetworkManager.SaveItemsInShip))]
    private static IEnumerable<CodeInstruction> SaveItemsInShipPatch(IEnumerable<CodeInstruction> instructions)
    {
        if (!PluginConfig.SaveLimit.Enabled.Value)
            return instructions;

        var codes = instructions.ToList();
        //   int num = 0;
        // - for (int i = 0; i < objectsByType.Length && i <= StartOfRound.Instance.maxShipItemCapacity; ++i)
        // + for (int i = 0; i < objectsByType.Length; ++i)
        //   {
        var injector = new ILInjector(codes)
            .Find([
                ILMatcher.Ldloc(),
                ILMatcher.Call(typeof(StartOfRound).GetProperty(nameof(StartOfRound.Instance))?.GetMethod),
                ILMatcher.Ldfld(typeof(StartOfRound).GetField(nameof(StartOfRound.maxShipItemCapacity))),
                ILMatcher.Opcode(OpCodes.Bgt),
            ]);

        if (!injector.IsValid)
        {
            LobbyControl.Log.LogWarning("SaveItemsInShip patch failed 1!!");
            LobbyControl.Log.LogDebug(string.Join("\n", injector.ReleaseInstructions()));
            return codes;
        }

        return injector
            .RemoveLastMatch()
            .ReleaseInstructions();
    }
}
