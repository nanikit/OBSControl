using BS_Utils.Utilities;
using OBSControl.HarmonyPatches;
using OBSControl.UI;
using OBSControl.Wrappers;
using ObsStrawket;
using ObsStrawket.DataTypes;
using ObsStrawket.DataTypes.Predefineds;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.SceneManagement;
#nullable enable

namespace OBSControl.OBSComponents
{
    public partial class RecordingController
    {
        /// <summary>
        /// Event handler for <see cref="StartLevelPatch.LevelStarting"/>.
        /// Sets a level start delay if using <see cref="RecordStartOption.LevelStartDelay"/>.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void OnLevelStarting(object sender, LevelStartingEventArgs e)
        {
            RecordStartOption recordStartOption = RecordStartOption;
            Logger.log?.Debug($"RecordingController OnLevelStarting. StartOption is {recordStartOption}");
            switch (recordStartOption)
            {
                case RecordStartOption.None:
                    break;
                case RecordStartOption.SceneSequence:
                    break;
                case RecordStartOption.SongStart:
                    break;
                case RecordStartOption.LevelStartDelay:
                    e.SetResponse(LevelStartingSourceName, (int)(RecordingStartDelay * 1000));
                    break;
                case RecordStartOption.Immediate:
                    break;
                default:
                    break;
            }
        }

        private async void OnLevelStart(object sender, LevelStartEventArgs e)
        {
            RecordStartOption recordStartOption = RecordStartOption;
            switch (e.StartResponseType)
            {
                case LevelStartResponse.None:
                    break;
                case LevelStartResponse.Immediate:
                    break;
                case LevelStartResponse.Delayed:
                    break;
                case LevelStartResponse.Handled:
                    if (recordStartOption == RecordStartOption.SceneSequence)
                        return;
                    break;
                default:
                    break;
            }
            Logger.log?.Debug($"RecordingController OnLevelStart. RecordStartOption: {RecordStartOption}.");
            if (recordStartOption == RecordStartOption.LevelStartDelay || recordStartOption == RecordStartOption.Immediate)
            {
                await TryStartRecordingAsync(RecordActionSourceType.Auto, recordStartOption, true).ConfigureAwait(false);
            }
        }
        /// <summary>
        /// Event handler for <see cref="SceneController.SceneStageChanged"/>
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void OnSceneStageChanged(object sender, SceneStageChangedEventArgs e)
        {
#if DEBUG
            Logger.log?.Debug($"RecordingController: OnSceneStageChanged - {e.SceneStage}.");
#endif
            e.AddCallback(SceneSequenceCallback);
        }
        #region Game Event Handlers

        /// <summary>
        /// Triggered after song ends, but before transition out of game scene.
        /// </summary>
        /// <param name="levelScenesTransitionSetupDataSO"></param>
        /// <param name="levelCompletionResults"></param>
        private async void OnLevelFinished(object sender, LevelFinishedEventArgs e)
        {
            ScenesTransitionSetupDataSO levelScenesTransitionSetupDataSO = e.ScenesTransitionSetupDataSO;
            LevelCompletionResults? levelCompletionResults = null;
            if (e is LevelFinishedWithResultsEventArgs resultArgs)
            {
                levelCompletionResults = resultArgs.CompletionResults;
            }
            Logger.log?.Debug($"RecordingController OnLevelFinished: {SceneManager.GetActiveScene().name}. RecordStopOption: {RecordStopOption}.");
            bool multipleLevelData = LastLevelData?.LevelResults != null || (LastLevelData?.MultipleLastLevels ?? false) == true;
            try
            {
                PlayerLevelStatsData? stats = null;
                IBeatmapLevel? levelInfo = GameStatus.LevelInfo;
                IDifficultyBeatmap? difficultyBeatmap = GameStatus.DifficultyBeatmap ?? LastLevelData?.LevelData?.DifficultyBeatmap;
                PlayerDataModel? playerData = OBSController.instance?.PlayerData;
                if (difficultyBeatmap != null)
                {
                    if (playerData != null && levelInfo != null)
                    {
                        stats = playerData.playerData.GetPlayerLevelStatsData(
                            levelInfo.levelID, difficultyBeatmap.difficulty, difficultyBeatmap.parentDifficultyBeatmapSet.beatmapCharacteristic);
                    }
                    LevelCompletionResultsWrapper? levelResults = null;
                    if (levelCompletionResults != null)
                        levelResults = new LevelCompletionResultsWrapper(levelCompletionResults, stats?.playCount ?? 0, GameStatus.GetMaxModifiedScore(levelCompletionResults.energy));
                    RecordingData? recordingData = LastLevelData;
                    if (recordingData == null)
                    {
                        recordingData = new RecordingData(new BeatmapLevelWrapper(difficultyBeatmap), levelResults, stats)
                        {
                            MultipleLastLevels = multipleLevelData
                        };
                        LastLevelData = recordingData;
                    }
                    else
                    {
                        if (recordingData.LevelData == null)
                        {
                            recordingData.LevelData = new BeatmapLevelWrapper(difficultyBeatmap);
                        }
                        else if (recordingData.LevelData.DifficultyBeatmap != difficultyBeatmap)
                        {
                            Logger.log?.Debug($"Existing beatmap data doesn't match level completion beatmap data: '{recordingData.LevelData.SongName}' != '{difficultyBeatmap.level.songName}'");
                            recordingData.LevelData = new BeatmapLevelWrapper(difficultyBeatmap);
                        }
                        recordingData.LevelResults = levelResults;
                        recordingData.PlayerLevelStats = stats;
                        recordingData.MultipleLastLevels = multipleLevelData;
                    }
                }
                else
                    Logger.log?.Warn($"Beatmap data unavailable, unable to generate data for recording file rename.");

            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception ex)
            {
                Logger.log?.Error($"Error generating new file name: {ex}");
                Logger.log?.Debug(ex);
            }
#pragma warning restore CA1031 // Do not catch general exception types
            if (RecordStopOption == RecordStopOption.SongEnd)
            {
                try
                {
                    await TryStopOrRestartRecording().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Logger.log?.Debug($"Auto stop recording was canceled in 'OnLevelFinished'.");
                }
                catch (Exception ex)
                {
                    Logger.log?.Error($"Exception auto stop recording in 'OnLevelFinished': {ex.Message}");
                    Logger.log?.Debug(ex);
                }
            }
        }

        private async void OnGameSceneActive()
        {
            WasInGame = true;
            Logger.log?.Debug($"RecordingController OnGameSceneActive. RecordStartOption: {RecordStartOption}.");
            StartCoroutine(GameStatusSetup());
            if (RecordStartOption == RecordStartOption.SongStart)
            {
                await TryStartRecordingAsync(RecordActionSourceType.Auto, RecordStartOption.SongStart, true).ConfigureAwait(false);
            }
            // TODO: Add fallback to start recording for other options that should've had recording running by now.
        }

        /// <summary>
        /// Triggered after transition out of game scene.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="_"></param>
        public async void OnLevelDidFinish()
        {
            if (!WasInGame) return;
            WasInGame = false;
            Logger.log?.Debug($"RecordingController OnLevelDidFinish: {SceneManager.GetActiveScene().name}. RecordStopOption: {RecordStopOption}.");
            try
            {
                if (RecordStopOption == RecordStopOption.ResultsView)
                {
                    await TryStopOrRestartRecording().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                Logger.log?.Debug($"Auto stop recording was canceled in 'OnLevelFinished'.");
            }
            catch (Exception ex)
            {
                Logger.log?.Error($"Exception auto stop recording in 'OnLevelDidFinish': {ex.Message}");
                Logger.log?.Debug(ex);
            }
        }

        private async Task TryStopOrRestartRecording()
        {
            TimeSpan stopDelay = TimeSpan.FromSeconds(WasInGame ? 0 : (Plugin.config?.RecordingStopDelay ?? 0));
            if (stopDelay > TimeSpan.Zero)
            {
                await Task.Delay(stopDelay, RecordStopCancellationSource.Token).ConfigureAwait(false);
            }

            if (ControlScreenCoordinator.Instance?.ControlScreen?.EnableAutoRecordLobby == true)
            {
                //await TryStartRecordingAsync(RecordActionSourceType.Auto, RecordStartOption.None, true).ConfigureAwait(false);
                if (GetObs() is ObsClientSocket obs)
                {
                    void SetLobbyFileName(RecordStateChanged changed)
                    {
                        if (changed.OutputState == OutputState.Started)
                        {
                            RenameStringOverride = $"Lobby {DateTime.Now:yyMMdd HHmmss}";
                            obs.RecordStateChanged -= SetLobbyFileName;
                        }
                    }
                    obs.RecordStateChanged += SetLobbyFileName;
                    await SplitFile(obs).ConfigureAwait(false);
                }
            }
            else
            {
                await TryStopRecordingAsync(RecordStopCancellationSource.Token).ConfigureAwait(false);
            }
        }

        private static async Task SplitFile(ObsClientSocket obs)
        {
            await obs.TriggerHotkeyByNameAsync("OBSBasic.SplitFile").ConfigureAwait(false);
        }

        private ObsClientSocket? GetObs()
        {
            var obs = Obs.GetConnectedObs();
            if (obs == null)
            {
                Logger.log?.Error($"obs instance not found.");
                return null;
            }
            return obs;
        }

        #endregion


        #region OBS Event Handlers

        private async void OnObsRecordingStateChanged(object sender, RecordStateChanged changed)
        {
            try
            {
                var type = changed.OutputState;
                Logger.log?.Info($"Recording State Changed: {type}");
                OutputState = type;
                LastRecordingStateUpdate = DateTime.Now;
                switch (type)
                {
                    case OutputState.Starting:
                        recordingCurrentLevel = true;
                        break;
                    case OutputState.Started:
                        RecordStartTime = DateTime.UtcNow;
                        recordingCurrentLevel = true;
                        //if (RecordStartSource == RecordActionSourceType.None)
                        //{
                        //    RecordStartSource = RecordActionSourceType.ManualOBS;
                        //    RecordStopOption recordStopOption = Plugin.config?.RecordStopOption ?? RecordStopOption.None;
                        //    RecordStopOption = recordStopOption == RecordStopOption.SceneSequence ? RecordStopOption.ResultsView : recordStopOption;
                        //}
                        RecordStopCancellationSource = new CancellationTokenSource();
                        break;
                    case OutputState.Stopping:
                        recordingCurrentLevel = false;
                        break;
                    case OutputState.Stopped:
                        recordingCurrentLevel = false;
                        RecordStartTime = DateTime.MaxValue;
                        RecordingData? lastLevelData = LastLevelData;
                        string? renameOverride = RenameStringOverride;
                        RenameStringOverride = null;
                        LastLevelData = null;
                        //RecordStartSource = RecordActionSourceType.None;
                        // RecordStartOption = RecordStartOption.None;
                        string? renameString = renameOverride;
                        if (lastLevelData != null)
                        {
                            renameString ??= await lastLevelData.GetFileName(Plugin.config.RecordingFileFormat, Plugin.config.InvalidCharacterSubstitute, Plugin.config.ReplaceSpacesWith).ConfigureAwait(false);
                        }
                        if (renameString != null)
                        {
                            string newName = $"{renameString}{Path.GetExtension(changed.OutputPath)}";
                            for (int i = 0; i < 5; i++)
                            {
                                try
                                {
                                    string formatted = Path.Combine(Path.GetDirectoryName(changed.OutputPath), newName);
                                    File.Move(changed.OutputPath, formatted);
                                    break;
                                }
                                catch (IOException ex) when (ex.Message.Contains("Sharing violation"))
                                {
                                    if (i == 4)
                                    {
                                        Logger.log?.Warn($"Renaming {changed.OutputPath} to {newName} failed. Don't try more: {ex}");
                                        break;
                                    }
                                    else
                                    {
                                        Logger.log?.Info($"Renaming {changed.OutputPath} to {newName} failed. Retry after a moment...");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Logger.log?.Warn($"Renaming {changed.OutputPath} to {newName} failed: {ex}");
                                }
                                await Task.Delay(2000).ConfigureAwait(false);
                            }
                        }
                        else
                        {
                            Logger.log?.Info("No data to rename the recording file.");
                            CurrentFileFormat = null;
                        }
                        break;
                    default:
                        Logger.log?.Debug($"Unknown type: {type}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.log?.Error(ex);
            }
        }

        #endregion
    }
}
