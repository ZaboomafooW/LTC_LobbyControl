using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using GameNetcodeStuff;
using HarmonyLib;

namespace LobbyControl.Patches;

internal static class RadarPatches
{
    [HarmonyTranspiler]
    [HarmonyPatch(typeof(PlayerControllerB), nameof(PlayerControllerB.SendNewPlayerValuesClientRpc))]
    private static IEnumerable<CodeInstruction> FixRadarNames(IEnumerable<CodeInstruction> instructions)
    {
        if (!LobbyControl.PluginConfig.SteamLobby.RadarFix.Value)
            return instructions;

        var codes = instructions.ToList();

        var fieldInfo = typeof(TransformAndName).GetField(nameof(TransformAndName.name));
        var methodInfo = typeof(RadarPatches).GetMethod(nameof(SetNewName),
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Static);

        for (var i = 0; i < codes.Count; i++)
        {
            var curr = codes[i];

            if (curr.StoresField(fieldInfo))
            {
                for (var index = i - 6; index < i; index++)
                {
                    var iterator = codes[index];
                    if (!iterator.IsLdloc())
                        codes[index] = new CodeInstruction(OpCodes.Nop)
                        {
                            blocks = iterator.blocks,
                            labels = iterator.labels
                        };
                }

                codes[i] = new CodeInstruction(OpCodes.Call, methodInfo)
                {
                    blocks = curr.blocks,
                    labels = curr.labels
                };
                LobbyControl.Log.LogDebug("SendNewPlayerValuesClientRpc patched!");
            }
        }

        return codes;
    }


    private static void SetNewName(int index, string name)
    {
        var startOfRound = StartOfRound.Instance;
        var playerObject = startOfRound.allPlayerObjects[index];
        startOfRound.mapScreen.ChangeNameOfTargetTransform(playerObject.transform, name);
    }
}
