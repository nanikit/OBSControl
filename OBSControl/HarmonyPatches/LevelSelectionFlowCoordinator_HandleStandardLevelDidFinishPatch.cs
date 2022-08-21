using HarmonyLib;
using OBSControl.Utilities;
using System;
#nullable enable
namespace OBSControl.HarmonyPatches
{

    [HarmonyPatch(typeof(SinglePlayerLevelSelectionFlowCoordinator), "HandleStandardLevelDidFinish",
        new Type[] {
        typeof(LevelCompletionResults),
        typeof(IDifficultyBeatmap),
        typeof(GameplayModifiers),
        typeof(bool)
        })]
    internal class HandleStandardLevelDidFinishPatch
    {
        public static event EventHandler? LevelDidFinish;
        static void Postfix(SinglePlayerLevelSelectionFlowCoordinator __instance)
        {
            Logger.log?.Trace($"{nameof(HandleStandardLevelDidFinishPatch)}.Postfix: entered.");
            // Does not trigger in Multiplayer
            SinglePlayerLevelSelectionFlowCoordinator flowCoordinator = __instance;
            LevelDidFinish.RaiseEventSafe(flowCoordinator, nameof(LevelDidFinish));
            Logger.log?.Trace($"{nameof(HandleStandardLevelDidFinishPatch)}.Postfix: exit.");
        }
    }
}
