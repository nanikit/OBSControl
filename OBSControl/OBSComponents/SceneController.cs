﻿using BS_Utils.Utilities;
using OBSControl.HarmonyPatches;
using OBSControl.Utilities;
using ObsStrawket;
using ObsStrawket.DataTypes.Predefineds;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
#nullable enable

namespace OBSControl.OBSComponents
{
    public enum SceneStage
    {
        Resting = 0,
        IntroStarted = 1,
        Game = 3,
        OutroStarted = 4,
        OutroFinished = 5,
        Aborted = 100
    }
    /// <summary>
    /// Monobehaviours (scripts) are added to GameObjects.
    /// For a full list of Messages a Monobehaviour can receive from the game, see https://docs.unity3d.com/ScriptReference/MonoBehaviour.html.
    /// </summary>
    [DisallowMultipleComponent]
    public class SceneController : OBSComponent
    {
        internal readonly HarmonyPatchInfo LevelDelayPatch = HarmonyManager.GetLevelDelayPatch();
        private readonly object _availableSceneLock = new object();
        public const string LevelStartingSourceName = "SceneController";

        private CancellationTokenSource _introSequenceCancelSource = new CancellationTokenSource();
        public CancellationTokenSource IntroSequenceCancelSource
        {
            get { return _introSequenceCancelSource; }
            set
            {
                if (_introSequenceCancelSource == value) return;
                CancellationTokenSource? oldSource = _introSequenceCancelSource;
                _introSequenceCancelSource = value;
                oldSource?.Cancel();
                oldSource?.Dispose();
            }
        }
        private CancellationTokenSource _outroSequenceCancelSource = new CancellationTokenSource();
        public CancellationTokenSource OutroSequenceCancelSource
        {
            get { return _outroSequenceCancelSource; }
            set
            {
                if (_outroSequenceCancelSource == value) return;
                CancellationTokenSource? oldSource = _outroSequenceCancelSource;
                _outroSequenceCancelSource = value;
                oldSource?.Cancel();
                oldSource?.Dispose();
            }
        }
        #region Exposed Events
        public event EventHandler<string?>? SceneChanged;
        public event EventHandler? SceneListUpdated;
        public event EventHandler<SceneStageChangedEventArgs>? SceneStageChanged;
        #endregion
        #region Options
        /// <summary>
        /// Wait for song start before switching to GameScene.
        /// </summary>
        public bool GameSceneOnSongStart
        {
            get => false;
        }

        public bool SceneSequenceEnabled => (Plugin.config?.RecordStartOption ?? RecordStartOption.None) == RecordStartOption.SceneSequence;

        public bool IntroSceneSequenceEnabled
        {
            get
            {
                if (!SceneSequenceEnabled || !Connected)
                    return false;
                return Plugin.config.RecordStartOption == RecordStartOption.SceneSequence;
            }
        }

        public bool OutroSceneSequenceEnabled
        {
            get
            {
                return IntroSceneSequenceEnabled;
            }
        }
        #endregion
        protected Func<SceneStage, CancellationToken, Task>[] RaiseSceneStageChanged(SceneStage sceneStage)
        {
            EventHandler<SceneStageChangedEventArgs>[] handlers = SceneStageChanged?.GetInvocationList().Select(d => (EventHandler<SceneStageChangedEventArgs>)d).ToArray()
                ?? Array.Empty<EventHandler<SceneStageChangedEventArgs>>();
            SceneStageChangedEventArgs args = new SceneStageChangedEventArgs(sceneStage);
            for (int i = 0; i < handlers.Length; i++)
            {
                try
                {
                    handlers[i].Invoke(this, args);
                }
                catch (Exception ex)
                {
                    Logger.log?.Error($"Error in {nameof(SceneStageChanged)} handlers '{handlers[i]?.Method.Name}': {ex.Message}");
                    Logger.log?.Debug(ex);
                }
            }
            return args.GetCallbacks() ?? Array.Empty<Func<SceneStage, CancellationToken, Task>>();
        }

        private AsyncEventListenerWithArg<string?, string?, string?> StartSceneSequenceSceneListener { get; } = new AsyncEventListenerWithArg<string?, string?, string?>((s, sceneName, expectedScene) =>
        {
            if (string.IsNullOrEmpty(expectedScene))
                return new EventListenerResult<string?>(null, true);
            if (sceneName == expectedScene)
                return new EventListenerResult<string?>(sceneName, true);
            else
                return new EventListenerResult<string?>(sceneName, false);
        }, string.Empty, 5000);

        protected static async Task ExecuteCallbacks(Func<SceneStage, CancellationToken, Task>[] callbacks,
            SceneStage sceneStage, CancellationToken cancellationToken)
        {
            if (callbacks == null || callbacks.Length == 0)
                return;
            try
            {
                await Task.WhenAll(callbacks.Select(c => c.Invoke(sceneStage, cancellationToken))).ConfigureAwait(false);
            }
            catch (AggregateException ex)
            {
                Logger.log?.Error($"Error executing SceneSequence callbacks: {ex.Message}");
                foreach (var exception in ex.InnerExceptions)
                {
                    Logger.log?.Debug(exception);
                }
            }
            catch (Exception ex)
            {
                Logger.log?.Error($"Error executing SceneSequence callbacks: {ex.Message}");
                Logger.log?.Debug(ex);
            }
        }
        private bool IntroSequenceRunning = false;
        public async Task<bool> StartIntroSceneSequence(CancellationToken cancellationToken)
        {
            try
            {
                SceneStage currentStage = SceneStage.Resting;
                Logger.log?.Debug($"In StartIntroSceneSequence.");
                OutroSequenceCancelSource.Cancel();
                IntroSequenceCancelSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cancellationToken = IntroSequenceCancelSource.Token;
                Func<SceneStage, CancellationToken, Task>[] callbacks;
                if (cancellationToken.IsCancellationRequested)
                {
                    RaiseSceneStageChanged(SceneStage.Aborted);
                    return false;
                }
                bool success = false;
                string? gameScene = Plugin.config.GameSceneName;
                if (gameScene == null || gameScene.Length == 0)
                    gameScene = null;
                string? startScene = Plugin.config.StartSceneName;
                if (startScene == null || startScene.Length == 0)
                    startScene = gameScene;
                ObsClientSocket? obs = Obs.Obs;
                if (obs == null)
                {
                    Logger.log?.Error($"Could not get OBS connection, aborting StartIntroSceneSequence.");
                    currentStage = SceneStage.Aborted;
                    callbacks = RaiseSceneStageChanged(currentStage);
                    if (callbacks.Length > 0)
                        await ExecuteCallbacks(callbacks, currentStage, cancellationToken);
                    return false;
                }
                TimeSpan startSceneDuration = TimeSpan.FromSeconds(Plugin.config.StartSceneDuration);
                try
                {
                    await UpdateScenes(true);
                    cancellationToken.ThrowIfCancellationRequested();
                    string[] availableScenes = GetAvailableScenes();
                    bool invalidScene = false;
                    if (string.IsNullOrEmpty(gameScene) || !availableScenes.Contains(gameScene))
                    {
                        Logger.log?.Warn($"GameScene '{gameScene}' is not a valid scene in OBS. A valid GameSceneName must be set to allow automatic scene switching.");
                        gameScene = null;
                    }
                    else if (string.IsNullOrEmpty(startScene) || !availableScenes.Contains(startScene))
                    {
                        Logger.log?.Warn($"StartScene '{startScene}' is not a valid scene in OBS, using GameScene {gameScene}.");
                        startScene = gameScene;
                    }
                    if (invalidScene || gameScene == null)
                    {
                        Logger.log?.Info($"Valid Scenes are: {string.Join(", ", availableScenes.Select(s => $"'{s}'"))}");
                        currentStage = SceneStage.Aborted;
                        callbacks = RaiseSceneStageChanged(currentStage);
                        if (callbacks.Length > 0)
                            await ExecuteCallbacks(callbacks, currentStage, cancellationToken);
                        return false;
                    }
                    Logger.log?.Info($"Beginning Intro Scene Sequence '{startScene}' => {startSceneDuration.TotalMilliseconds}ms => '{gameScene}'");
                    SceneChanged += StartSceneSequenceSceneListener.OnEvent;
                    StartSceneSequenceSceneListener.Reset(startScene, cancellationToken);
                    StartSceneSequenceSceneListener.StartListening();
                    if (startScene != null && CurrentScene != startScene)
                    {
                        Logger.log?.Info($"Setting starting scene to '{startScene}'.");
                        await obs.SetCurrentProgramSceneAsync(startScene, cancellation: cancellationToken);
                        await StartSceneSequenceSceneListener.Task;
                    }
                    else
                        StartSceneSequenceSceneListener.TrySetResult(startScene);
                    currentStage = SceneStage.IntroStarted;
                    callbacks = RaiseSceneStageChanged(currentStage);
                    if (callbacks.Length > 0)
                        await ExecuteCallbacks(callbacks, currentStage, cancellationToken);
                    if (startSceneDuration > TimeSpan.Zero)
                        await Task.Delay(startSceneDuration, cancellationToken);
                    StartSceneSequenceSceneListener.Reset(gameScene, cancellationToken);
                    StartSceneSequenceSceneListener.StartListening();
                    if (gameScene.Length > 0 && CurrentScene != gameScene)
                    {
                        Logger.log?.Info($"Setting game scene to '{gameScene}'.");
                        await obs.SetCurrentProgramSceneAsync(gameScene, cancellation: cancellationToken);
                        await StartSceneSequenceSceneListener.Task;
                    }
                    else
                        StartSceneSequenceSceneListener.TrySetResult(gameScene);
                    currentStage = SceneStage.Game;
                    callbacks = RaiseSceneStageChanged(currentStage);
                    if (callbacks.Length > 0)
                        await ExecuteCallbacks(callbacks, currentStage, cancellationToken);
                    success = true;
                }
                catch (OperationCanceledException)
                {
                    if (gameScene != null && gameScene.Length > 0)
                    {
                        if (CurrentScene == gameScene)
                        {
                            Logger.log?.Warn($"StartIntroSceneSequence canceled, switching to GameScene.");
                            await obs.SetCurrentProgramSceneAsync(gameScene);
                        }
                        else
                            Logger.log?.Warn($"StartIntroSceneSequence canceled and already on GameScene.");

                    }
                    else
                        Logger.log?.Warn($"StartIntroSceneSequence canceled.");
                    currentStage = SceneStage.Aborted;
                    callbacks = RaiseSceneStageChanged(currentStage);
                    if (callbacks.Length > 0)
                        await ExecuteCallbacks(callbacks, currentStage, cancellationToken);
                }
                catch (Exception ex)
                {
                    Logger.log?.Error($"Error in StartSceneSequence: {ex.Message}");
                    Logger.log?.Debug(ex);
                    currentStage = SceneStage.Aborted;
                    callbacks = RaiseSceneStageChanged(currentStage);
                    if (callbacks.Length > 0)
                        await ExecuteCallbacks(callbacks, currentStage, cancellationToken);
                }
                finally
                {
                    Logger.log?.Debug($"Exiting StartIntroSceneSequence {(success ? "successfully" : "after failure")}.");
                    StartSceneSequenceSceneListener.TrySetCanceled();
                    SceneChanged -= StartSceneSequenceSceneListener.OnEvent;
                }
                return success;
            }
            finally
            {
                IntroSequenceRunning = false;
            }
        }

        private AsyncEventListenerWithArg<string?, string, string?> StopSceneSequenceSceneListener { get; }
            = new AsyncEventListenerWithArg<string?, string, string?>((s, sceneName, expectedScene) =>
        {
            if (string.IsNullOrEmpty(expectedScene))
                return new EventListenerResult<string?>(null, true);
            if (sceneName == expectedScene)
                return new EventListenerResult<string?>(sceneName, true);
            else
                return new EventListenerResult<string?>(sceneName, false);
        }, string.Empty, 5000);

        private bool OutroSequenceRunning = false;
        public async Task<bool> StartOutroSceneSequence(CancellationToken cancellationToken)
        {
            try
            {
                OutroSequenceRunning = true;
                OutroSequenceCancelSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cancellationToken = OutroSequenceCancelSource.Token;
                Logger.log?.Debug($"In StartOutroSceneSequence.");
                if (IntroSequenceRunning)
                {
                    Logger.log?.Warn($"Intro scene sequence is already running, cancelling outro.");
                    return false;
                }
                Func<SceneStage, CancellationToken, Task>[]? callbacks = null;
                SceneStage currentStage = SceneStage.Resting;
                if (cancellationToken.IsCancellationRequested)
                {
                    currentStage = SceneStage.Aborted;
                    callbacks = RaiseSceneStageChanged(currentStage);
                    if (callbacks.Length > 0)
                        await ExecuteCallbacks(callbacks, currentStage, cancellationToken);
                    return false;
                }
                bool success = false;
                string? endScene = Plugin.config.EndSceneName;
                string? gameScene = Plugin.config.GameSceneName;
                string? restingScene = Plugin.config.RestingSceneName;
                if (string.IsNullOrEmpty(endScene))
                    endScene = null;
                if (string.IsNullOrEmpty(gameScene))
                    gameScene = null;
                if (string.IsNullOrEmpty(restingScene))
                    restingScene = null;
                ObsClientSocket? obs = Obs.Obs;
                if (obs == null)
                {
                    Logger.log?.Error($"Could not get OBS connection, aborting StartOutroSceneSequence.");
                    currentStage = SceneStage.Aborted;
                    callbacks = RaiseSceneStageChanged(currentStage);
                    if (callbacks.Length > 0)
                        await ExecuteCallbacks(callbacks, currentStage, cancellationToken);
                    return false;
                }
                try
                {
                    await UpdateScenes(true);
                    string[] availableScenes = GetAvailableScenes();
                    bool invalidScene = false;
                    if (gameScene != null && !availableScenes.Contains(gameScene))
                    {
                        invalidScene = true;
                        Logger.log?.Warn($"GameSceneName '{gameScene}' is not a valid scene in OBS. A valid GameSceneName must be set to allow automatic scene switching.");
                        gameScene = null;
                    }
                    if (endScene == null || !availableScenes.Contains(endScene))
                    {
                        if (endScene != null)
                            Logger.log?.Warn($"EndSceneName '{endScene}' is not a valid scene in OBS, using GameScene {gameScene}.");
                        endScene = gameScene;
                    }
                    if (restingScene == null || !availableScenes.Contains(restingScene))
                    {
                        Logger.log?.Warn($"RestingSceneName '{restingScene}' is not a valid scene in OBS, using GameScene {gameScene}.");
                        restingScene = gameScene;
                    }
                    if (invalidScene || gameScene == null)
                    {
                        Logger.log?.Info($"Valid Scenes are: {string.Join(", ", availableScenes.Select(s => $"'{s}'"))}");
                        currentStage = SceneStage.Aborted;
                        callbacks = RaiseSceneStageChanged(currentStage);
                        if (callbacks.Length > 0)
                            await ExecuteCallbacks(callbacks, currentStage, cancellationToken);
                        return false;
                    }
                    TimeSpan endSceneDuration = TimeSpan.FromSeconds(Plugin.config.EndSceneDuration);
                    TimeSpan endSceneStartDelay = TimeSpan.FromSeconds(Plugin.config.EndSceneStartDelay);
                    Logger.log?.Info($"Beginning Outro Scene Sequence  {endSceneStartDelay.TotalMilliseconds}ms => '{endScene}' => {endSceneDuration.TotalMilliseconds}ms => '{restingScene ?? gameScene}'");
#pragma warning disable CS8622 // Nullability of reference types in type of parameter doesn't match the target delegate.
                    SceneChanged += StopSceneSequenceSceneListener.OnEvent;
#pragma warning restore CS8622 // Nullability of reference types in type of parameter doesn't match the target delegate.
                    StopSceneSequenceSceneListener.Reset(endScene);
                    StopSceneSequenceSceneListener.StartListening();
                    if (endScene != null && CurrentScene != endScene)
                    {
                        if (endSceneStartDelay > TimeSpan.Zero)
                        {
                            Logger.log?.Info($"Delaying end scene '{endScene}' by {endSceneStartDelay.TotalMilliseconds}ms.");
                            await Task.Delay(endSceneStartDelay);
                        }
                        Logger.log?.Info($"Setting end scene scene to '{endScene}'.");
                        await obs.SetCurrentProgramSceneAsync(endScene, cancellation: cancellationToken);
                        await StopSceneSequenceSceneListener.Task;
                    }
                    else
                        StopSceneSequenceSceneListener.TrySetResult(endScene);
                    currentStage = SceneStage.OutroStarted;
                    callbacks = RaiseSceneStageChanged(currentStage);
                    if (callbacks.Length > 0)
                        await ExecuteCallbacks(callbacks, currentStage, cancellationToken);
                    if (endSceneDuration > TimeSpan.Zero)
                    {
                        Logger.log?.Info($"Delaying resting scene '{restingScene}' by {endSceneStartDelay.TotalMilliseconds}ms.");
                        await Task.Delay(endSceneDuration);
                    }
                    currentStage = SceneStage.OutroFinished;
                    callbacks = RaiseSceneStageChanged(currentStage);
                    if (callbacks.Length > 0)
                        await ExecuteCallbacks(callbacks, currentStage, cancellationToken);
                    StopSceneSequenceSceneListener.Reset(restingScene);
                    StopSceneSequenceSceneListener.StartListening();
                    if (restingScene != null && CurrentScene != restingScene)
                    {
                        await obs.SetCurrentProgramSceneAsync(restingScene, cancellation: cancellationToken);
                        await StopSceneSequenceSceneListener.Task;
                    }
                    else
                        StopSceneSequenceSceneListener.TrySetResult(gameScene);
                    currentStage = SceneStage.Resting;
                    callbacks = RaiseSceneStageChanged(currentStage);
                    if (callbacks.Length > 0)
                        await ExecuteCallbacks(callbacks, currentStage, cancellationToken);
                    success = true;
                }
                catch (OperationCanceledException)
                {
                    if (gameScene != null && gameScene.Length > 0)
                    {
                        if (CurrentScene != gameScene)
                        {
                            if (IntroSceneSequenceEnabled)
                            {
                                Logger.log?.Warn($"StartOutroSceneSequence canceled while intro sequence is running. Current scene: {CurrentScene}");
                            }
                            else
                            {
                                Logger.log?.Warn($"StartOutroSceneSequence canceled, switching to GameScene.");
                                await obs.SetCurrentProgramSceneAsync(gameScene, cancellation: cancellationToken);
                            }
                        }
                        else
                            Logger.log?.Warn($"StartOutroSceneSequence canceled and already on GameScene.");

                    }
                    else
                        Logger.log?.Warn($"StartOutroSceneSequence canceled.");
                    currentStage = SceneStage.Aborted;
                    callbacks = RaiseSceneStageChanged(currentStage);
                    if (callbacks.Length > 0)
                        await ExecuteCallbacks(callbacks, currentStage, cancellationToken);
                }
                catch (Exception ex)
                {
                    Logger.log?.Error($"Error in StartOutroSceneSequence: {ex.Message}");
                    Logger.log?.Debug(ex);
                    currentStage = SceneStage.Aborted;
                    callbacks = RaiseSceneStageChanged(currentStage);
                    if (callbacks.Length > 0)
                        await ExecuteCallbacks(callbacks, currentStage, cancellationToken);
                }
                finally
                {
                    Logger.log?.Debug($"Exiting StartOutroSceneSequence {(success ? "successfully" : "after failure")}.");
                    StopSceneSequenceSceneListener.TrySetCanceled();
#pragma warning disable CS8622 // Nullability of reference types in type of parameter doesn't match the target delegate.
                    SceneChanged -= StopSceneSequenceSceneListener.OnEvent;
#pragma warning restore CS8622 // Nullability of reference types in type of parameter doesn't match the target delegate.
                }
                return success;
            }
            finally
            {
                OutroSequenceRunning = false;
            }
        }

        #region OBS Properties
        private string? _currentScene;

        public string? CurrentScene
        {
            get { return _currentScene; }
            protected set
            {
                if (_currentScene == value) return;
                _currentScene = value;
                SceneChanged?.Invoke(this, value);
            }
        }

        protected readonly List<string> AvailableScenes = new List<string>();
        public string[] GetAvailableScenes()
        {
            string[] scenes;
            lock (_availableSceneLock)
            {
                scenes = AvailableScenes.ToArray();
            }
            return scenes;
        }
        #endregion

        #region OBS Actions

        public async Task SetScene(string sceneName)
        {
            ObsClientSocket obs = Obs.Obs ?? throw new InvalidOperationException("ObsClientSocket is unavailable.");

            var SceneListener = new AsyncEventListener<bool, CurrentProgramSceneChanged>((s, name) =>
              {
                  bool result = sceneName == name.SceneName;
                  return new EventListenerResult<bool>(result, true);
              }, 5000, AllTasksCancelSource.Token);

            void OnEvent(CurrentProgramSceneChanged ev) => SceneListener!.OnEvent(this, ev);

            try
            {
                obs.CurrentProgramSceneChanged += OnEvent;
                await obs.SetCurrentProgramSceneAsync(sceneName).ConfigureAwait(false);
                string? current = await UpdateCurrentScene(AllTasksCancelSource.Token).ConfigureAwait(false);
                if (current == sceneName)
                    return;
                await SceneListener.Task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.log?.Error($"Error setting scene to '{sceneName}': {ex.Message}");
                Logger.log?.Debug(ex);
            }
            finally
            {
                obs.CurrentProgramSceneChanged -= OnEvent;
            }
        }

        public async Task<string?> UpdateCurrentScene(CancellationToken cancellationToken)
        {
            ObsClientSocket? obs = Obs.GetConnectedObs();
            if (obs == null)
            {
                Logger.log?.Warn("Unable to update current scene. OBS not connected.");
                return null;
            }
            try
            {
                string currentScene = (await obs.GetCurrentProgramSceneAsync(cancellation: cancellationToken).ConfigureAwait(false))!.CurrentProgramSceneName;
                CurrentScene = currentScene;
                return currentScene;
            }
            catch (Exception ex)
            {
                Logger.log?.Error($"Error getting current scene: {ex.Message}");
                Logger.log?.Debug(ex);
                return null;
            }
        }

        /// <summary>
        /// Updates the scene list and, optionally, the current scene
        /// </summary>
        /// <param name="forceCurrentUpdate">If true, the current scene will be forced to update if it isn't present in 'GetSceneList'</param>
        /// <returns></returns>
        public async Task UpdateScenes(bool forceCurrentUpdate = true)
        {
            ObsClientSocket? obs = Obs.GetConnectedObs();
            if (obs == null)
            {
                Logger.log?.Warn("Unable to update current scene. OBS not connected.");
                return;
            }
            string[] availableScenes = null!;
            try
            {
                var sceneData = await obs.GetSceneListAsync().ConfigureAwait(false);
                availableScenes = sceneData!.Scenes.Select(s => s.Name).ToArray();
                Logger.log?.Info($"OBS scene list updated: {string.Join(", ", availableScenes)}");
                try
                {
                    string? current = sceneData.CurrentProgramSceneName;
                    if (current != null && current.Length > 0)
                        CurrentScene = current;
                    else if (forceCurrentUpdate)
                    {
                        current = await UpdateCurrentScene(AllTasksCancelSource.Token).ConfigureAwait(false);
                        if (current != null && current.Length > 0)
                            CurrentScene = current;
                    }
                }
                catch (Exception ex)
                {
                    Logger.log?.Error($"Error getting current scene: {ex.Message}");
                    Logger.log?.Debug(ex);
                }
                UpdateSceneList(availableScenes);
            }
            catch (Exception ex)
            {
                if (availableScenes == null)
                    availableScenes = Array.Empty<string>();
                Logger.log?.Error($"Error getting scene list: {ex.Message}");
                Logger.log?.Debug(ex);
            }
        }

        #endregion

        protected void UpdateSceneList(IEnumerable<string> scenes)
        {
            lock (_availableSceneLock)
            {
                AvailableScenes.Clear();
                AvailableScenes.AddRange(scenes);
            }
            HMMainThreadDispatcher.instance.Enqueue(() =>
            {
                try
                {
                    Plugin.config.UpdateSceneOptions(scenes);
                }
                catch (Exception ex)
                {
                    Logger.log?.Error($"Error setting scene list for config: {ex.Message}");
                    Logger.log?.Debug(ex);
                }
            });
            SceneListUpdated?.Invoke(this, null);
        }

        #region Setup/Teardown
        public override async Task InitializeAsync(OBSController obs)
        {
            await base.InitializeAsync(obs);
            await UpdateScenes().ConfigureAwait(false);
        }

        protected override async Task OnConnectAsync(CancellationToken cancellationToken)
        {
            await base.OnConnectAsync(cancellationToken);
            await UpdateScenes().ConfigureAwait(false);

        }

        protected override void OnDisconnect()
        {
            base.OnDisconnect();
            CurrentScene = null;
        }

        private void OnLevelDidFinish(object sender, LevelFinishedEventArgs e)
        {
            if (OutroSceneSequenceEnabled)
            {
                _ = StartOutroSceneSequence(AllTasksCancelSource.Token);
            }
        }

        protected override void SetEvents(OBSController obs)
        {
            base.SetEvents(obs);
            BSEvents.LevelFinished += OnLevelDidFinish;
            StartLevelPatch.LevelStarting += OnLevelStarting;
        }

        protected override void RemoveEvents(OBSController obs)
        {
            base.RemoveEvents(obs);
            BSEvents.LevelFinished -= OnLevelDidFinish;
            StartLevelPatch.LevelStarting -= OnLevelStarting;
        }
        protected override void SetEvents(ObsClientSocket obs)
        {
            RemoveEvents(obs);
            obs.SceneListChanged += OnObsSceneListChanged;
            obs.CurrentProgramSceneChanged += OnObsSceneChanged;
        }

        protected override void RemoveEvents(ObsClientSocket obs)
        {
            obs.SceneListChanged -= OnObsSceneListChanged;
            obs.CurrentProgramSceneChanged -= OnObsSceneChanged;
        }
        #endregion

        private void OnLevelStart(object sender, LevelStartEventArgs e)
        {
            if (!IntroSceneSequenceEnabled)
                return;
            StartIntroSceneSequence(AllTasksCancelSource?.Token ?? CancellationToken.None).ContinueWith(result =>
            {
                LevelStartEventArgs levelStartInfo = e;
                e.StartLevel(levelStartInfo.Coordinator, levelStartInfo.BeforeSceneSwitchCallback, levelStartInfo.Practice);
                e.ResetPlayButton();
            });
            StartLevelPatch.LevelStart -= OnLevelStart;
        }

        private void OnLevelStarting(object sender, LevelStartingEventArgs e)
        {
            if (IntroSceneSequenceEnabled)
            {
                Logger.log?.Debug($"SceneController OnLevelStarting: Intro sequence enabled.");
                e.SetHandledResponse(LevelStartingSourceName);
                StartLevelPatch.LevelStart -= OnLevelStart;
                StartLevelPatch.LevelStart += OnLevelStart;
            }
            else
                Logger.log?.Debug($"SceneController OnLevelStarting.");
        }

        #region OBS Websocket Event Handlers
        private async void OnObsSceneListChanged(SceneListChanged e)
        {
            try
            {
                await UpdateScenes().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.log?.Error($"Error in 'OnObsSceneListChanged' handler: {ex.Message}");
                Logger.log?.Debug(ex);
            }
        }
        private void OnObsSceneChanged(CurrentProgramSceneChanged e)
        {
            Logger.log?.Info($"Scene changed to '{e.SceneName}'");
            CurrentScene = e.SceneName;
        }
        #endregion

        #region Monobehaviour Messages
        /// <summary>
        /// Called when the script becomes enabled and active
        /// </summary>
        protected override void OnEnable()
        {
            base.OnEnable();
            SetEvents(Obs);
            if (!LevelDelayPatch.IsApplied)
                LevelDelayPatch.ApplyPatch();
        }

        /// <summary>
        /// Called when the script becomes disabled or when it is being destroyed.
        /// </summary>
        protected override void OnDisable()
        {
            base.OnDisable();
            RemoveEvents(Obs);
            if (LevelDelayPatch?.IsApplied ?? false)
                LevelDelayPatch.RemovePatch();
        }

        #endregion
    }



    public class SceneStageChangedEventArgs : EventArgs
    {
        public readonly SceneStage SceneStage;
        public Func<SceneStage, CancellationToken, Task>[] GetCallbacks() => callbacks?.ToArray()
            ?? Array.Empty<Func<SceneStage, CancellationToken, Task>>();
        protected List<Func<SceneStage, CancellationToken, Task>>? callbacks;
        public void AddCallback(Func<SceneStage, CancellationToken, Task> callback)
        {
            if (callback != null)
            {
                if (callbacks == null)
                    callbacks = new List<Func<SceneStage, CancellationToken, Task>>(1);
                callbacks.Add(callback);
            }
        }
        public SceneStageChangedEventArgs(SceneStage sceneStage)
        {
            SceneStage = sceneStage;
        }
    }
}
