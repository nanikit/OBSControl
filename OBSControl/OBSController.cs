﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using OBSWebsocketDotNet;
using OBSWebsocketDotNet.Types;
using System.Threading.Tasks;
using System.Net.Sockets;
using System.Threading;
using OBSControl.OBSComponents;

#nullable enable
namespace OBSControl
{
    /// <summary>
    /// Monobehaviours (scripts) are added to GameObjects.
    /// For a full list of Messages a Monobehaviour can receive from the game, see https://docs.unity3d.com/ScriptReference/MonoBehaviour.html.
    /// </summary>
	public class OBSController
        : MonoBehaviour
    {
        private const string Error_OBSWebSocketNotRunning = "No connection could be made because the target machine actively refused it.";
        private OBSWebsocket? _obs;
        public OBSWebsocket? Obs
        {
            get { return _obs; }
            protected set
            {
                if (_obs == value)
                    return;
                Logger.log?.Info($"obs.set");
                OBSWebsocket? previous = _obs;
                if (previous != null)
                {
                    DestroyObsInstance(previous);
                }
                _obs = value;
                try
                {
                    if (_obs != null)
                    {
                        ObsCreated?.Invoke(this, _obs);
                    }
                }
                catch (Exception ex)
                {
                    Logger.log?.Error($"Error setting OBSWebsocket: {ex.Message}");
                    Logger.log?.Debug(ex);
                }
            }
        }

        public OBSWebsocket? GetConnectedObs()
        {
            OBSWebsocket? obs = Obs;
            if (obs != null && obs.IsConnected)
                return obs;
            return null;
        }
        private DateTime LastHeartbeat = DateTime.MinValue;
        private bool HeartbeatTimerActive = false;

        private PlayerDataModel? _playerData;
        public PlayerDataModel? PlayerData
        {
            get
            {
                if (_playerData == null)
                {
                    _playerData = Resources.FindObjectsOfTypeAll<PlayerDataModel>().FirstOrDefault();
                    if (_playerData != null)
                    {
                        Logger.log?.Debug("Found PlayerData");
                    }
                    else
                        Logger.log?.Warn($"Unable to find PlayerData");
                }
#if DEBUG
                else
                    Logger.log?.Debug("PlayerData already exists, don't need to find it");
#endif
                return _playerData;
            }
        }

        private bool OnConnectTriggered = false;
        public string? RecordingFolder;

        public static OBSController instance { get; private set; } = null!;
        public bool IsConnected => Obs?.IsConnected ?? false;

        private PluginConfig Config => Plugin.config;
        public event EventHandler<OBSWebsocket>? ObsCreated;
        public event EventHandler<OBSWebsocket>? DestroyingObs;
        private readonly WaitForSeconds HeartbeatCheckInterval = new WaitForSeconds(10);
        private TimeSpan HeartbeatTimeout = new TimeSpan(0, 0, 30);
        private IEnumerator<WaitForSeconds> HeartbeatCoroutine()
        {
            while (wasConnected)
            {
                yield return HeartbeatCheckInterval;
                if ((DateTime.UtcNow - LastHeartbeat) > HeartbeatTimeout)
                {
                    Logger.log?.Error($"Lost connection to OBS, did not receive heartbeat.");
                    Obs?.Disconnect();
                    HeartbeatTimerActive = false;
                    break;
                }
#if DEBUG
                //else
                //    Logger.log?.Debug($"Last heartbeat {(DateTime.UtcNow - LastHeartbeat).TotalSeconds} sec ago.");
#endif
            }

        }

        public T? GetOBSComponent<T>() where T : OBSComponent, new()
        {
            T? comp = gameObject.GetComponent<T>();
            return comp;
        }

        public async Task<T> AddOBSComponentAsync<T>() where T : OBSComponent, new()
        {
            //if (OBSComponents.ContainsKey(typeof(T)))
            //    throw new InvalidOperationException($"OBSController already has an instance of type '{typeof(T).Name}'.");
            T comp = new GameObject().AddComponent<T>();
            try
            {
                await comp.InitializeAsync(this);
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception ex)
            {
                Logger.log?.Error($"Error initializing {typeof(T).Name}: {ex.Message}.");
                Logger.log?.Debug(ex);
                Destroy(comp);
            }
#pragma warning restore CA1031 // Do not catch general exception types
            return comp;
        }
        #region OBS Properties

        #endregion

        #region Setup/Teardown

        private void CreateObsInstance()
        {
            Logger.log?.Debug("CreateObsInstance()");
            var newObs = new OBSWebsocket();
            SetEvents(newObs);
            Obs = newObs;
            Logger.log?.Debug("CreateObsInstance finished");
        }

        private bool wasConnected = false;

        private void DestroyObsInstance(OBSWebsocket? target)
        {
            if (target == null)
                return; 
            try
            {
                Logger.log?.Debug("Disconnecting from obs instance.");
                try
                {
                    DestroyingObs?.Invoke(this, target);
                }
                catch (Exception ex)
                {
                    Logger.log?.Error($"Error in 'DestroyingObs' event: {ex.Message}");
                    Logger.log?.Debug(ex);
                }
                if (target.IsConnected)
                {
                    //target.Api.SetFilenameFormatting(DefaultFileFormat);
                    target.Disconnect();
                }
                RemoveEvents(target);
            }
            catch (Exception ex)
            {
                Logger.log?.Error($"Error destroying OBS: {ex.Message}");
                Logger.log?.Debug(ex);
            }
        }

        public string? lastTryConnectMessage;
        public enum TryConnectResponse
        {
            Connected, Retry, NoRetry
        }
        public async Task<TryConnectResponse> TryConnect(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string message;

            if (Obs != null && !Obs.IsConnected)
            {
                try
                {
                    await Obs.Connect(Config.ServerAddress, Config.ServerPassword).ConfigureAwait(false);
                    message = $"Finished attempting to connect to {Config.ServerAddress}";
                    if (message != lastTryConnectMessage)
                    {
                        Logger.log?.Info(message);
                        lastTryConnectMessage = message;
                    }
                }
                catch (AuthFailureException)
                {
                    message = $"Authentication failed connecting to server {Config.ServerAddress}.";
                    if (message != lastTryConnectMessage)
                    {
                        Logger.log?.Info(message);
                        lastTryConnectMessage = message;
                    }
                    return TryConnectResponse.NoRetry;
                }
                catch (SocketErrorResponseException ex)
                {
                    bool logException = true;
                    IPA.Logging.Logger.Level logLevel = IPA.Logging.Logger.Level.Info;
                    TryConnectResponse response = TryConnectResponse.Retry;
                    switch (ex.SocketErrorCode)
                    {
                        case SocketError.ConnectionRefused:
                            message = $"Failed to connect to server {Config.ServerAddress}, OBSWebsocket not running?";
                            logLevel = IPA.Logging.Logger.Level.Warning;
                            logException = false;
                            response = TryConnectResponse.Retry;
                            break;
                        default:
                            message = $"Failed to connect to server {Config.ServerAddress}: {ex.Message}.";
                            break;
                    }
                    Logger.log?.Log(logLevel, message);
                    if (logException && message != lastTryConnectMessage)
                    {
                        Logger.log?.Debug(ex);
                        lastTryConnectMessage = message;
                    }
                    return response;
                }
                catch (ErrorResponseException ex)
                {
                    message = $"Failed to connect to server {Config.ServerAddress}: {ex.Message}.";
                    Logger.log?.Info(message);
                    if (message != lastTryConnectMessage)
                    {
                        Logger.log?.Debug(ex);
                        lastTryConnectMessage = message;
                    }
                    return TryConnectResponse.Retry;
                }
                catch (Exception ex)
                {
                    message = $"Failed to connect to server {Config.ServerAddress}: {ex.Message}.";
                    if (message != lastTryConnectMessage)
                    {
                        Logger.log?.Info(message);
                        Logger.log?.Debug(ex);
                        lastTryConnectMessage = message;
                    }
                    return TryConnectResponse.Retry;
                }
                if (Obs.IsConnected)
                    Logger.log?.Info($"Connected to OBS @ {Config.ServerAddress}");
            }
            else
                Logger.log?.Info("TryConnect: OBS is already connected.");
            if (Obs == null)
                return TryConnectResponse.Retry;
            else
                return Obs.IsConnected ? TryConnectResponse.Connected : TryConnectResponse.Retry;
        }

        private async Task RepeatTryConnect(CancellationToken cancellationToken)
        {
            OBSWebsocket? obs = Obs;
            if (obs == null)
            {
                Logger.log?.Error($"Obs instance is null in RepeatTryConnect()");
                return;
            }
            try
            {
                if (string.IsNullOrEmpty(Plugin.config.ServerAddress))
                {
                    Logger.log?.Error("The ServerAddress in the config is null or empty. Unable to connect to OBS.");
                    return;
                }
                int connectAttempts = 0;
                Logger.log?.Info($"Attempting to connect to {Config.ServerAddress}");
                TryConnectResponse connectResponse = TryConnectResponse.Retry;
                while (connectResponse == TryConnectResponse.Retry && connectAttempts < 5)
                {
                    if (connectAttempts > 0)
                        await Task.Delay(5000, cancellationToken).ConfigureAwait(false);
                    connectAttempts++;
                    connectResponse = await TryConnect(cancellationToken).ConfigureAwait(false);
                }
                if (obs.IsConnected)
                {
                    Logger.log?.Info($"OBS {(await obs.GetVersion().ConfigureAwait(false)).OBSStudioVersion} is connected.");
                    Logger.log?.Info($"OnConnectTriggered: {OnConnectTriggered}");
                }
                else
                    Logger.log?.Warn($"OBS is not connected, automatic connect aborted after {connectAttempts} {(connectAttempts == 1 ? "attempt" : "attempts")}.");
            }
            catch (OperationCanceledException)
            {
                Logger.log?.Warn($"OBS connection aborted.");
            }
            catch (Exception ex)
            {
                Logger.log?.Error($"Error in RepeatTryConnect: {ex.Message}");
                Logger.log?.Debug(ex);
            }
        }

        protected void SetEvents(OBSWebsocket obs)
        {
            if (obs == null)
                return;
            RemoveEvents(obs);
            obs.Connected += OnConnect;
            obs.Disconnected += OnDisconnect;

            obs.RecordingStateChanged += OnRecordingStateChanged;
            obs.StreamingStateChanged += OnStreamingStateChanged;
            obs.StreamStatus += OnStreamStatus;
            obs.Heartbeat += OnHeartbeat;
            obs.SceneChanged += OnSceneChanged;
        }

        protected void RemoveEvents(OBSWebsocket obs)
        {
            if (obs == null)
                return;
            obs.Connected -= OnConnect;
            obs.Disconnected -= OnDisconnect;

            obs.Heartbeat -= OnHeartbeat;
            obs.RecordingStateChanged -= OnRecordingStateChanged;
            obs.StreamingStateChanged -= OnStreamingStateChanged;
            obs.StreamStatus -= OnStreamStatus;
            obs.SceneChanged -= OnSceneChanged;
        }
        #endregion
        #region Events
        public event EventHandler<bool>? ConnectionStateChanged;
        public event EventHandler<Heartbeat>? Heartbeat;
        public event EventHandler<OutputState>? RecordingStateChanged;
        public event EventHandler<OutputState>? StreamingStateChanged;
        public event EventHandler<StreamStatus>? StreamStatus;
        public event EventHandler<string>? SceneChanged;
        #endregion



        #region Event Handlers
        private async void OnConnect(object sender, EventArgs e)
        {
            OBSWebsocket? obs = _obs;
            if (obs == null)
            {
                Logger.log?.Error($"OnConnect was triggered, but OBSController._obs is null (this shouldn't happen). OBSControl may be broken :'(");
                return;
            }
            OnConnectTriggered = true;
            wasConnected = true;
            Logger.log?.Info($"OnConnect: Connected to OBS.");
            try
            {

                await obs.SetHeartbeat(true);
                if (!HeartbeatTimerActive)
                {
                    Logger.log?.Debug($"Enabling HeartBeat check.");
                    LastHeartbeat = DateTime.UtcNow;
                    HeartbeatTimerActive = true;
                    StartCoroutine(HeartbeatCoroutine());
                }
            }
            catch (Exception ex)
            {
                Logger.log?.Error($"Error in OBSController.OnConnect: {ex.Message}");
                Logger.log?.Debug(ex);
            }
            try
            {
                ConnectionStateChanged?.Invoke(this, true);
            }
            catch (Exception ex)
            {
                Logger.log?.Error($"Error in 'ConnectionStateChanged' event: {ex.Message}");
                Logger.log?.Debug(ex);
            }
        }
        protected void OnDisconnect(object sender, EventArgs _)
        {
            StopCoroutine(HeartbeatCoroutine());
            if (!wasConnected)
                return;
            Logger.log?.Warn("Disconnected from OBS.");
            wasConnected = false;
            try
            {
                ConnectionStateChanged?.Invoke(this, false);
            }
            catch (Exception ex)
            {
                Logger.log?.Error($"Error in 'ConnectionStateChanged' event: {ex.Message}");
                Logger.log?.Debug(ex);
            }
        }

        protected void OnHeartbeat(OBSWebsocket sender, Heartbeat heartbeat)
        {
#if DEBUG
            // Logger.log?.Debug("Heartbeat Received");
#endif
            try
            {
                LastHeartbeat = DateTime.UtcNow;
                Heartbeat?.Invoke(this, heartbeat);
            }
            catch (Exception ex)
            {
                Logger.log?.Error($"Error in 'Heartbeat' event: {ex.Message}");
                Logger.log?.Debug(ex);
            }
        }

        protected void OnRecordingStateChanged(OBSWebsocket sender, OutputState outputState)
        {
            try
            {
                RecordingStateChanged?.Invoke(this, outputState);
            }
            catch (Exception ex)
            {
                Logger.log?.Error($"Error in 'RecordingStateChanged' event: {ex.Message}");
                Logger.log?.Debug(ex);
            }
        }

        private void OnStreamingStateChanged(OBSWebsocket sender, OutputState outputState)
        {
            try
            {
                StreamingStateChanged?.Invoke(this, outputState);
            }
            catch (Exception ex)
            {
                Logger.log?.Error($"Error in 'StreamingStateChanged' event: {ex.Message}");
                Logger.log?.Debug(ex);
            }
        }

        private void OnStreamStatus(OBSWebsocket sender, StreamStatus status)
        {
#if DEBUG
            Logger.log?.Info($"Stream Time: {status.TotalStreamTime.ToString()} sec");
            Logger.log?.Info($"Bitrate: {(status.KbitsPerSec / 1024f).ToString("N2")} Mbps");
            Logger.log?.Info($"FPS: {status.FPS.ToString()} FPS");
            Logger.log?.Info($"Strain: {(status.Strain * 100).ToString()} %");
            Logger.log?.Info($"DroppedFrames: {status.DroppedFrames.ToString()} frames");
            Logger.log?.Info($"TotalFrames: {status.TotalFrames.ToString()} frames");
#endif
            try
            {
                StreamStatus?.Invoke(this, status);
            }
            catch (Exception ex)
            {
                Logger.log?.Error($"Error in 'StreamStatus' event: {ex.Message}");
                Logger.log?.Debug(ex);
            }
        }

        private void OnSceneChanged(OBSWebsocket sender, string newSceneName)
        {

        }

        #endregion

        #region Monobehaviour Messages
        /// <summary>
        /// Only ever called once, mainly used to initialize variables.
        /// </summary>
        private void Awake()
        {
            Logger.log?.Debug("OBSController Awake()");
            if (instance != null)
            {
                GameObject.DestroyImmediate(this);
                return;
            }
            GameObject.DontDestroyOnLoad(this);
            instance = this;
            CreateObsInstance();
        }

        /// <summary>
        /// Only ever called once on the first frame the script is Enabled. Start is called after every other script's Awake() and before Update().
        /// </summary>
        private async void Start()
        {
            Logger.log?.Debug("OBSController Start()");
            await RepeatTryConnect(CancellationToken.None);
            await AddOBSComponentAsync<SceneController>();
            await AddOBSComponentAsync<RecordingController>();
            await AddOBSComponentAsync<StreamingController>();
        }

        /// <summary>
        /// Called when the script becomes enabled and active
        /// </summary>
        private void OnEnable()
        {
            OBSComponents.RecordingController.instance?.gameObject.SetActive(true);
        }

        /// <summary>
        /// Called when the script becomes disabled or when it is being destroyed.
        /// </summary>
        private void OnDisable()
        {
            OBSComponents.RecordingController.instance?.gameObject.SetActive(false);
        }

        /// <summary>
        /// Called when the script is being destroyed.
        /// </summary>
        private void OnDestroy()
        {
            instance = null;
            if (OBSComponents.RecordingController.instance != null)
            {
                Destroy(OBSComponents.RecordingController.instance);
            }
            DestroyObsInstance(Obs);
        }
        #endregion
    }
}
