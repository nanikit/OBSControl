using BS_Utils.Utilities;

using OBSControl.HarmonyPatches;
using OBSControl.UI;
using OBSControl.Utilities;
using OBSControl.Wrappers;
using ObsStrawket;
using ObsStrawket.DataTypes;
using ObsStrawket.DataTypes.Predefineds;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using UnityEngine;
#nullable enable
namespace OBSControl.OBSComponents
{
    [DisallowMultipleComponent]
    public partial class RecordingController : OBSComponent
    {
        //private ObsClientSocket? _obs => OBSController.instance?.GetConnectedObs();
        internal readonly HarmonyPatchInfo ReadyToStartPatch = HarmonyManager.GetReadyToStartPatch();
        public const string LevelStartingSourceName = "RecordingController";
        private const string DefaultFileFormat = "%CCYY-%MM-%DD %hh-%mm-%ss";
        public const string DefaultDateTimeFormat = "yyyyMMddHHmmss";
        #region Encapsulated Fields
        private SceneController? _sceneController;
        private RecordingData? _lastLevelData;
        private RecordStopOption _recordStopOption;
        private RecordStartOption _recordStartOption;

        #endregion
        public bool recordingCurrentLevel;
        private bool validLevelData;
        private string? RenameStringOverride;
        protected bool WasInGame;
        private readonly Channel<RecordStateChanged> _recordStateChanged = Channel.CreateUnbounded<RecordStateChanged>();

        private CancellationTokenSource _recordStopCancellationSource = new CancellationTokenSource();
        public CancellationTokenSource RecordStopCancellationSource
        {
            get { return _recordStopCancellationSource; }
            set
            {
                if (_recordStopCancellationSource == value) return;
                CancellationTokenSource? oldSource = _recordStopCancellationSource;
                _recordStopCancellationSource = value;
                oldSource?.Cancel();
                oldSource?.Dispose();
            }
        }

        #region Properties

        private string? CurrentFileFormat { get; set; }

        /// <summary>
        /// Data about the last level played. If not null when a level is finished, <see cref="RecordingData.MultipleLastLevels"/> will be set to true.
        /// Should be set to null after it's used to rename a recording.
        /// </summary>
        protected RecordingData? LastLevelData
        {
            get => validLevelData ? _lastLevelData : null;
            set
            {
                validLevelData = _lastLevelData == null;
                _lastLevelData = value;
            }
        }
        public OutputState RecordingState { get; private set; }
        public DateTime RecordStartTime { get; private set; }
        #endregion

        #region Options
        public bool AutoStopOnManual => Plugin.config?.AutoStopOnManual ?? true;
        /// <summary>
        /// True if delayed stop is enabled, does not affect SceneSequence recordings.
        /// </summary>
        public bool DelayedStopEnabled => RecordingStopDelay > 0;
        public bool DelayedStartEnabled => RecordingStartDelay > 0;

        public float RecordingStartDelay => Plugin.config?.LevelStartDelay ?? 0;

        public float RecordingStopDelay => Plugin.config?.RecordingStopDelay ?? 0;
        /// <summary>
        /// If not recording with SceneSequence, start recording when the song is started.
        /// </summary>
        public bool RecordOnSongStart => false;

        public RecordStartOption RecordStartOption
        {
            get
            {
                if (!(ControlScreenCoordinator.Instance?.ControlScreen?.EnableAutoRecord ?? true))
                {
                    return RecordStartOption.None;
                }
                return Plugin.config?.RecordStartOption ?? RecordStartOption.None;
            }
            set => _recordStartOption = value;
        }

        public RecordStopOption RecordStopOption
        {
            get
            {
                return AutoStop ? _recordStopOption : RecordStopOption.None;
            }
            set
            {
                if (_recordStopOption == value) return;
                Logger.log?.Debug($"RecordingController: RecordStopOption changed to: '{value}'.");
                _recordStopOption = value;
            }
        }
        /// <summary>
        /// When should record stop be triggered, if at all.
        /// </summary>
        public bool AutoStop
        {
            get
            {
                return (RecordStartSource, AutoStopOnManual) switch
                {
                    (RecordActionSourceType.Manual, true) => true,
                    (RecordActionSourceType.Manual, false) => false,
                    (RecordActionSourceType.ManualOBS, true) => true,
                    (RecordActionSourceType.ManualOBS, false) => false,
                    (RecordActionSourceType.Auto, _) => true,
                    _ => false

                };
            }
        }

        /// <summary>
        /// Directory OBS should record to.
        /// </summary>
        public string? RecordingFolder { get; protected set; }
        #endregion

        protected SceneController? SceneController
        {
            get => _sceneController;
            set
            {
                Logger.log?.Debug($"Setting SceneController{(value == null ? " to <NULL>" : "")}.");
                if (value == _sceneController) return;
                if (_sceneController != null)
                {
                    _sceneController.SceneStageChanged -= OnSceneStageChanged;
                }
                _sceneController = value;
                if (_sceneController != null)
                {
#if DEBUG
                    Logger.log?.Debug($"RecordingController: Connected to SceneController.");
#endif
                    _sceneController.SceneStageChanged -= OnSceneStageChanged;
                    _sceneController.SceneStageChanged += OnSceneStageChanged;
                }
            }
        }
        public OutputState OutputState { get; protected set; }
        /// <summary>
        /// Time of the last recording state update (UTC) from the OBS OnRecordingStateChanged event.
        /// </summary>
        public DateTime LastRecordingStateUpdate { get; protected set; }
        public bool WaitingToStop { get; private set; }
        public Task? StopRecordingTask { get; private set; }
        public Task? StartRecordingTask { get; private set; }
        private SemaphoreSlim _semaphore = new(1);

        /// <summary>
        /// Source that started current/last recording.
        /// </summary>
        public RecordActionSourceType RecordStartSource { get; protected set; }
        private string ToDateTimeFileFormat(DateTime dateTime)
        {
            return dateTime.ToString(DefaultDateTimeFormat);
        }

        private AsyncEventListener<RecordStateChanged, RecordStateChanged> RecordingStartedListener = new((s, e) =>
        {
            if (e.OutputState == OutputState.Started)
                return new EventListenerResult<RecordStateChanged>(e, true);
            return new EventListenerResult<RecordStateChanged>(e, false);
        }, 5000);

        private AsyncEventListener<RecordStateChanged, RecordStateChanged> RecordingStoppedListener = new((s, e) =>
        {
            if (e.OutputState == OutputState.Stopped)
                return new EventListenerResult<RecordStateChanged>(e, true);
            return new EventListenerResult<RecordStateChanged>(e, false);
        }, 5000);

        public async Task TryStartRecordingAsync(RecordActionSourceType startType, RecordStartOption recordStartOption, bool forceStopPrevious = false, string? fileFormat = null)
        {
            Logger.log?.Debug($"TryStartRecordingAsync");

            ObsClientSocket? obs = Obs.GetConnectedObs();
            if (obs == null)
            {
                Logger.log?.Error($"Unable to start recording, obs instance not found.");
                return;
            }

            if (OutputState == OutputState.Started || OutputState == OutputState.Starting)
            {
                if (forceStopPrevious)
                {
                    //await TryStopRecordingAsync(CancellationToken.None).ConfigureAwait(false);
                    await SplitFile(obs).ConfigureAwait(false);
                    RecordStartSource = startType;
                    RecordStopOption = recordStartOption switch
                    {
                        RecordStartOption.SceneSequence => RecordStopOption.SceneSequence,
                        _ => Plugin.config?.RecordStopOption ?? RecordStopOption.ResultsView
                    };
                    Logger.log?.Info($"Recording started.");
                    return;
                }
                else
                {
                    Logger.log?.Warn($"Cannot start recording, already {OutputState}.");
                    return;
                }
            }
            try
            {
                var response = (await obs.GetRecordDirectoryAsync().ConfigureAwait(false))!;
                RecordingFolder = response!.RecordDirectory;
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Logger.log?.Error($"Error getting recording folder from OBS: {ex.Message}");
                Logger.log?.Debug(ex);
                return;
            }

            try
            {
                Logger.log?.Debug($"Start recording from {RecordStartSource}. Planned Stop type: {RecordStopOption}");
                RecordStartSource = startType;
                // RecordStartOption = recordStartOption;

                var interval = LastRecordingStateUpdate - DateTime.Now + new TimeSpan(0, 0, 0, 0, 500);
                if (interval.TotalMilliseconds > 0)
                {
                    Logger.log?.Debug($"Delay start by {interval} due to obs-websocket overload.");
                    await Task.Delay(interval).ConfigureAwait(false);
                }

                await obs.StartRecordAsync().ConfigureAwait(false);

                RecordStopOption = recordStartOption switch
                {
                    RecordStartOption.None => RecordStopOption.None,
                    RecordStartOption.SceneSequence => RecordStopOption.SceneSequence,
                    _ => Plugin.config?.RecordStopOption ?? RecordStopOption.ResultsView
                };
                Logger.log?.Info($"Recording started.");
            }
            catch (Exception ex)
            {
                OutputState state = OutputState;
                if (!(state == OutputState.Starting || OutputState == OutputState.Started))
                {
                    RecordStartSource = RecordActionSourceType.None;
                    // RecordStartOption = RecordStartOption.None;
                }
                Logger.log?.Error($"Error starting recording in OBS: {ex.Message}");
                Logger.log?.Debug(ex);
            }
        }

        public async Task<string[]> GetAvailableScenes()
        {
            ObsClientSocket? obs = Obs.GetConnectedObs();
            if (obs == null)
            {
                Logger.log?.Error($"Unable to get scenes, obs instance is null.");
                return Array.Empty<string>();
            }
            try
            {
                return (await obs.GetSceneListAsync().ConfigureAwait(false))!.Scenes.Select(s => s.Name).ToArray();
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception ex)
            {
                Logger.log?.Error($"Error validating scenes: {ex.Message}");
                Logger.log?.Debug(ex);
                return Array.Empty<string>();
            }
#pragma warning restore CA1031 // Do not catch general exception types
        }

        public bool ValidateScenes(IEnumerable<string> availableScenes, params string[] scenes)
        {
            if (availableScenes == null || scenes == null || scenes.Length == 0)
                return false;
            bool valid = true;
            foreach (var scene in availableScenes)
            {
                if (string.IsNullOrEmpty(scene))
                {
                    valid = false;
                    continue;
                }
                else if (!availableScenes.Contains(scene))
                {
                    valid = false;
                    Logger.log?.Warn($"Scene '{scene}' is not available.");
                    continue;
                }

            }
            return valid;
        }

        public async Task<bool> ValidateScenesAsync(params string[] scenes)
        {
            try
            {
                string[] availableScenes = await GetAvailableScenes().ConfigureAwait(false);
                Logger.log?.Debug($"Available scenes: {string.Join(", ", availableScenes)}");
                return scenes.All(s => availableScenes.Contains(s));
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception ex)
            {
                Logger.log?.Error($"Error validating scenes: {ex.Message}");
                Logger.log?.Debug(ex);
                return false;
            }
#pragma warning restore CA1031 // Do not catch general exception types
        }


        public async Task TryStopRecordingAsync(CancellationToken cancellationToken)
        {
            ObsClientSocket? obs = Obs.GetConnectedObs();
            if (obs == null)
            {
                Logger.log?.Error($"Unable to stop recording, ObsClientSocket is unavailable.");
                return;
            }
            try
            {
                using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, RecordStopCancellationSource.Token);
                //RenameStringOverride = renameTo;
                Logger.log?.Info($"Attempting to stop recording.");
                StopRecordingTask = ForceStopPrevious(obs, cts.Token);
                await StopRecordingTask.ConfigureAwait(false);
                recordingCurrentLevel = false;
            }
            catch (OperationCanceledException)
            {
                Logger.log?.Debug($"Stop recording was canceled in 'TryStopRecordingAsync'.");
            }
            catch (FailureResponseException ex)
            {
                Logger.log?.Error($"Error trying to stop recording: {ex.Message}");
                if (ex.Message != "recording not active")
                    Logger.log?.Debug(ex);
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception ex)
            {
                Logger.log?.Error($"Unexpected exception trying to stop recording: {ex.Message}");
                Logger.log?.Debug(ex);
            }
#pragma warning restore CA1031 // Do not catch general exception types
            finally
            {
                StopRecordingTask = null;
            }
        }

        public IEnumerator<WaitUntil> GameStatusSetup()
        {
            // TODO: Limit wait by tries/current scene so it doesn't go forever.
            WaitUntil waitForData = new WaitUntil(() =>
            {
                if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "MenuCore")
                    return false;
                return BS_Utils.Plugin.LevelData.IsSet && GameStatus.GpModSO != null;
            });
            yield return waitForData;
            Task setupTask = GameStatus.SetupAsync();
            WaitUntil waitForSetup = new WaitUntil(() =>
            {
                return setupTask.IsCompleted;
            });

            if (LastLevelData == null)
            {
                if (GameStatus.DifficultyBeatmap != null)
                {
                    RecordingData recordingData = new RecordingData(new BeatmapLevelWrapper(GameStatus.DifficultyBeatmap));
                    LastLevelData = recordingData;
                }
            }
        }

        public async Task<Output[]> GetOutputsAsync(CancellationToken cancellationToken = default)
        {
            ObsClientSocket? obs = Obs.Obs;
            if (obs == null || !obs.IsConnected)
            {
                Logger.log?.Error($"Unable to get output list, ObsClientSocket is not connected.");
                return Array.Empty<Output>();
            }
            try
            {
                var outputList = (await obs.GetOutputListAsync(cancellation: cancellationToken).ConfigureAwait(false)).Outputs;
                if (outputList.Count == 0)
                    Logger.log?.Warn("No Outputs listed");
                return outputList?.ToArray() ?? Array.Empty<Output>();
            }
            catch (Exception ex)
            {
                Logger.log?.Error($"Error getting list of outputs: {ex.Message}");
                Logger.log?.Debug(ex);
                return Array.Empty<Output>();
            }
        }


        #region Setup/Teardown


        private void OnOBSComponentChanged(object sender, OBSComponentChangedEventArgs e)
        {
            if (e.AddedComponent is SceneController addedSceneController)
            {
                SceneController = addedSceneController;
            }
            if (e.RemovedComponent is SceneController removedSceneController
                && removedSceneController == SceneController)
            {
                SceneController = null;
            }
        }

        public override async Task InitializeAsync(OBSController obs)
        {
            await base.InitializeAsync(obs).ConfigureAwait(false);
            SceneController = obs.GetOBSComponent<SceneController>();
        }

        protected override void SetEvents(OBSController obs)
        {
            if (obs == null) return;
            base.SetEvents(obs);

            //obs.RecordingStateChanged += OnObsRecordingStateChanged;
            obs.OBSComponentChanged += OnOBSComponentChanged;
            obs.Obs!.RecordStateChanged += QueueRecordingEvent;
            StartLevelPatch.LevelStarting += OnLevelStarting;
            StartLevelPatch.LevelStart += OnLevelStart;
            BSEvents.menuSceneActive += OnLevelDidFinish;
            //HandleStandardLevelDidFinishPatch.LevelDidFinish += OnLevelDidFinish;
            BSEvents.gameSceneActive += OnGameSceneActive;
            BSEvents.LevelFinished += OnLevelFinished;
        }

        private void QueueRecordingEvent(RecordStateChanged e)
        {
            //_ = _recordStateChanged.Writer.WriteAsync(e);
            OnObsRecordingStateChanged(this, e);
        }

        protected override void RemoveEvents(OBSController obs)
        {
            if (obs == null) return;
            base.RemoveEvents(obs);
            //obs.RecordingStateChanged -= OnObsRecordingStateChanged;
            obs.OBSComponentChanged -= OnOBSComponentChanged;
            obs.Obs!.RecordStateChanged -= QueueRecordingEvent;
            StartLevelPatch.LevelStarting -= OnLevelStarting;
            StartLevelPatch.LevelStart -= OnLevelStart;
            BSEvents.menuSceneActive += OnLevelDidFinish;
            //HandleStandardLevelDidFinishPatch.LevelDidFinish -= OnLevelDidFinish;
            BSEvents.gameSceneActive -= OnGameSceneActive;
            BSEvents.LevelFinished -= OnLevelFinished;
        }

        private async Task SceneSequenceCallback(SceneStage sceneStage, CancellationToken cancellationToken)
        {
            Logger.log?.Debug($"RecordingController: SceneStage - {sceneStage}. RecordStartOption: {RecordStartOption}.");
            if (sceneStage == SceneStage.IntroStarted && RecordStartOption == RecordStartOption.SceneSequence)
            {
                await TryStartRecordingAsync(RecordActionSourceType.Auto, RecordStartOption.SceneSequence, true);
            }
            else if (sceneStage == SceneStage.OutroFinished)
            {
                using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, RecordStopCancellationSource.Token);
                Logger.log?.Debug($"RecordingController: RecordStopOption: {RecordStopOption}.");
                if (RecordStopOption == RecordStopOption.SceneSequence)
                    await TryStopRecordingAsync(cts.Token);
            }
        }

        private async Task ForceStopPrevious(ObsClientSocket obs, CancellationToken? cancellationToken = null)
        {
            try
            {
                RecordingStoppedListener.Reset();
                Obs.RecordingStateChanged += RecordingStoppedListener.OnEvent;
                RecordingStoppedListener.StartListening();

                Logger.log?.Info($"Stopping current recording to start a new one.");
                await obs.StopRecordAsync(cancellation: cancellationToken ?? CancellationToken.None).ConfigureAwait(false);
                if (OutputState != OutputState.Stopped)
                {
                    await RecordingStoppedListener.Task.ConfigureAwait(false);
                }
                Logger.log?.Debug($"Recording stopped.");
            }
            catch (Exception ex)
            {
                Logger.log?.Warn($"Cannot stop recording: {ex.Message}");
            }
            finally
            {
                Obs.RecordingStateChanged -= RecordingStoppedListener.OnEvent;
                RecordingStoppedListener.TrySetCanceled();
            }
        }

        protected override void SetEvents(ObsClientSocket obs)
        {
        }


        protected override void RemoveEvents(ObsClientSocket obs)
        {
        }
        #endregion

        #region Monobehaviour Messages
        /// <summary>
        /// Called when the script becomes enabled and active
        /// </summary>
        protected override void OnEnable()
        {
            base.OnEnable();
            ReadyToStartPatch.ApplyPatch();
            SetEvents(Obs);
        }

        /// <summary>
        /// Called when the script becomes disabled or when it is being destroyed.
        /// </summary>
        protected override void OnDisable()
        {
            base.OnDisable();
            ReadyToStartPatch.RemovePatch();
            RemoveEvents(Obs);
        }
        #endregion
    }

}
