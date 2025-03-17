using System.Collections;
using UnityEngine;

namespace LobbyControl.Utils;

public static class HudUtils
{
    internal static IEnumerator ShowWarningAfterDelay(string title, string text, float delay, bool isWarning = false)
    {
        yield return new WaitForSeconds(delay);
        yield return new WaitUntil(() => HUDManager.Instance.CanTipDisplay(isWarning, false, null));
        HUDManager.Instance.DisplayTip(title, text, isWarning);
    }

    internal static IEnumerator ShowTipAfterDelay(string title, string text, float delay, string saveKey)
    {
        yield return new WaitForSeconds(delay);
        yield return new WaitUntil(() => HUDManager.Instance.CanTipDisplay(false, false, null));
        HUDManager.Instance.DisplayTip(title, text, false, true, saveKey);
    }
}