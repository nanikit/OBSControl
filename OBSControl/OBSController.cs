using OBSControl.OBSComponents;
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
namespace OBSControl
{
    /// <summary>
    /// Monobehaviours (scripts) are added to GameObjects.
    /// For a full list of Messages a Monobehaviour can receive from the game, see https://docs.unity3d.com/ScriptReference/MonoBehaviour.html.
    /// </summary>
	public class OBSController
        : MonoBehaviour
    {
        #region Exposed Events
        public event EventHandler<bool>? ConnectionStateChanged;
        public event EventHandler<RecordStateChanged>? RecordingStateChanged;
        public event EventHandler<StreamStateChanged>? StreamingStateChanged;
        public event EventHandler<GetStreamStatusResponse>? StreamStatus;
        public event EventHandler<OBSComponentChangedEventArgs>? OBSComponentChanged;
        #endregion

        public static int ConnectTimeout = 10000;
        private const string Error_OBSWebSocketNotRunning = "No connection could be made because the target machine actively refused it.";

        public static OBSController? instance { get; private set; } = null;
        private ObsClientSocket? _obs;
        public ObsClientSocket? Obs
        {
            get { return _obs; }
            protected set
            {
                if (_obs == value)
                    return;
                Logger.log?.Info($"obs.set");
                ObsClientSocket? previous = _obs;
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
                    Logger.log?.Error($"Error setting ObsClientSocket: {ex.Message}");
                    Logger.log?.Debug(ex);
                }
            }
        }

        public readonly Dictionary<Type, OBSComponent> OBSComponents = new Dictionary<Type, OBSComponent>();

        public ObsClientSocket? GetConnectedObs()
        {
            ObsClientSocket? obs = Obs;
            if (obs != null && obs.IsConnected)
                return obs;
            return null;
        }
        private DateTime LastHeartbeatTime = DateTime.MinValue;
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

        public string? RecordingFolder;

        public bool IsConnected => Obs?.IsConnected ?? false;

        private PluginConfig Config => Plugin.config;
        public event EventHandler<ObsClientSocket>? ObsCreated;
        public event EventHandler<ObsClientSocket>? DestroyingObs;
        private readonly WaitForSeconds HeartbeatCheckInterval = new WaitForSeconds(10);
        private TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(30);

        public T? GetOBSComponent<T>() where T : OBSComponent, new()
        {
            if (OBSComponents.TryGetValue(typeof(T), out OBSComponent component))
                return component as T;
            return null;
        }


        public async Task<T?> AddOBSComponentAsync<T>() where T : OBSComponent, new()
        {
            //if (OBSComponents.ContainsKey(typeof(T)))
            //    throw new InvalidOperationException($"OBSController already has an instance of type '{typeof(T).Name}'.");
            GameObject go = new GameObject($"OBSControl_{typeof(T).Name}");
            go.SetActive(false);
            DontDestroyOnLoad(go);
            T comp = go.AddComponent<T>();
            try
            {
                await comp.InitializeAsync(this);
                OBSComponents.Add(typeof(T), comp);
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception ex)
            {
                Logger.log?.Error($"Error initializing {typeof(T).Name}: {ex.Message}.");
                Logger.log?.Debug(ex);
                Destroy(go);
            }
#pragma warning restore CA1031 // Do not catch general exception types
            RaiseOBSComponentChanged(comp, null);
            return null;
        }
        #region OBS Properties

        #endregion

        #region Setup/Teardown

        private void CreateObsInstance()
        {
            Logger.log?.Debug("CreateObsInstance()");
            var newObs = new ObsClientSocket(Logger.log == null ? null : new MicrosoftToIPALogger(Logger.log));
            SetEvents(newObs);
            Obs = newObs;
            Logger.log?.Debug("CreateObsInstance finished");
        }

        private bool wasConnected = false;

        private void DestroyObsInstance(ObsClientSocket? target)
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
                    _ = target.CloseAsync();
                }
                RemoveEvents(target);
            }
            catch (Exception ex)
            {
                Logger.log?.Error($"Error destroying OBS: {ex.Message}");
                Logger.log?.Debug(ex);
            }
        }

        public enum TryConnectResponse
        {
            Connected, Retry, NoRetry
        }

        public async Task<TryConnectResponse> TryConnect(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string message;
            string? serverAddress = Config.ServerAddress;
            if (serverAddress == null || serverAddress.Length == 0)
            {
                Logger.log?.Error($"ServerAddress cannot be null or empty.");
                return TryConnectResponse.NoRetry;
            }
            if (Obs != null && !Obs.IsConnected)
            {
                try
                {
                    string? address = Config.ServerAddress;
                    if (address == null || address.Length == 0)
                    {
                        Logger.log?.Warn($"Unable to connect to OBS, a server address was not specified.");
                        return TryConnectResponse.NoRetry;
                    }
                    Logger.log?.Debug("Try connecting...");
                    await Obs.ConnectAsync(new Uri(address), Config.ServerPassword, cancellation: cancellationToken);
                    OnConnect();

                    message = $"Finished attempting to connect to {Config.ServerAddress}.";
                    Logger.log?.Info(message);
                    if (Obs.IsConnected)
                    {
                        var version = (await Obs.GetVersionAsync())!.ObsVersion;
                        Logger.log?.Info($"OBS {version} is connected.");
                    }
                    Logger.log?.Debug("try exit");
                }
                catch (AuthenticationFailureException)
                {
                    message = $"Authentication failed connecting to server {Config.ServerAddress}.";
                    Logger.log?.Info(message);
                    return TryConnectResponse.NoRetry;
                }
                catch (FailureResponseException ex)
                {
                    message = $"Failed to connect to server {Config.ServerAddress}: {ex.Message}.";
                    Logger.log?.Info(message);
                    Logger.log?.Debug(ex);
                    return TryConnectResponse.Retry;
                }
                catch (ObsWebSocketException ex)
                {
                    bool logException = true;
                    IPA.Logging.Logger.Level logLevel = IPA.Logging.Logger.Level.Info;
                    TryConnectResponse response = TryConnectResponse.Retry;
                    switch (ex)
                    {
                        //case SocketError.ConnectionRefused:
                        //    message = $"Failed to connect to server {Config.ServerAddress}, ObsClientSocket not running?";
                        //    logLevel = IPA.Logging.Logger.Level.Warning;
                        //    logException = false;
                        //    response = TryConnectResponse.Retry;
                        //    break;
                        default:
                            message = $"Failed to connect to server {Config.ServerAddress}: {ex.Message}.";
                            break;
                    }
                    Logger.log?.Log(logLevel, message);
                    if (logException)
                    {
                        Logger.log?.Debug(ex);
                    }
                    return response;
                }
                catch (Exception ex)
                {
                    message = $"Failed to connect to server {Config.ServerAddress}: {ex.Message}.";
                    Logger.log?.Info(message);
                    Logger.log?.Debug(ex);
                    return TryConnectResponse.Retry;
                }
                if (Obs.IsConnected)
                {
                    Logger.log?.Info($"Connected to OBS @ {Config.ServerAddress}");
                }
            }
            else if (Obs == null)
            {
                Logger.log?.Warn($"Unable to connection, Obs is null.");
                return TryConnectResponse.NoRetry;
            }
            else
                Logger.log?.Info("TryConnect: OBS is already connected.");
            Logger.log?.Debug("before TryConnect return");
            return Obs.IsConnected ? TryConnectResponse.Connected : TryConnectResponse.Retry;
        }

        private async Task RepeatTryConnect(CancellationToken cancellationToken)
        {
            ObsClientSocket? obs = Obs;
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
                    OnConnect();
                }
                else
                {
                    Logger.log?.Warn($"OBS is not connected, automatic connect aborted after {connectAttempts} {(connectAttempts == 1 ? "attempt" : "attempts")}.");
                }
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

        protected void SetEvents(ObsClientSocket obs)
        {
            if (obs == null)
                return;
#if DEBUG
            Logger.log?.Debug($"OBSController.SetEvents");
#endif
            RemoveEvents(obs);
            obs.Disconnected += OnDisconnect;

            obs.RecordStateChanged += OnRecordingStateChanged;
            obs.StreamStateChanged += OnStreamingStateChanged;
        }

        protected void RemoveEvents(ObsClientSocket obs)
        {
            if (obs == null)
                return;
#if DEBUG
            Logger.log?.Debug($"OBSController.RemoveEvents");
#endif
            obs.Disconnected -= OnDisconnect;

            obs.RecordStateChanged -= OnRecordingStateChanged;
            obs.StreamStateChanged -= OnStreamingStateChanged;
        }
        #endregion



        #region Event Handlers
        private async void OnConnect()
        {
            ObsClientSocket? obs = _obs;
            if (obs == null)
            {
                Logger.log?.Error($"OnConnect was triggered, but OBSController._obs is null (this shouldn't happen). OBSControl may be broken :'(");
                return;
            }
            wasConnected = true;
            Logger.log?.Debug($"OnConnect: Connected to OBS.");
            try
            {
                ConnectionStateChanged.RaiseEventSafe(this, true, nameof(ConnectionStateChanged));
            }
            catch (Exception ex)
            {
                Logger.log?.Error($"Error in 'ConnectionStateChanged' event: {ex.Message}");
                Logger.log?.Debug(ex);
            }
            _ = Task.Run(async () =>
            {
                while (obs.IsConnected)
                {
                    var status = await obs.GetStreamStatusAsync().ConfigureAwait(false);
                    OnStreamStatus(status);
                    await Task.Delay(2000);
                }
            });
        }
        protected void OnDisconnect(object o)
        {
            if (!wasConnected)
                return;
            Logger.log?.Warn($"Disconnected from OBS: {o}");
            wasConnected = false;
            try
            {
                ConnectionStateChanged.RaiseEventSafe(this, false, nameof(ConnectionStateChanged));
            }
            catch (Exception ex)
            {
                Logger.log?.Error($"Error in 'ConnectionStateChanged' event: {ex.Message}");
                Logger.log?.Debug(ex);
            }
        }

        protected void OnRecordingStateChanged(RecordStateChanged e)
        {
            try
            {
                RecordingStateChanged?.RaiseEventSafe(this, e, nameof(RecordingStateChanged));
            }
            catch (Exception ex)
            {
                Logger.log?.Error($"Error in 'RecordingStateChanged' event: {ex.Message}");
                Logger.log?.Debug(ex);
            }
        }

        private void OnStreamingStateChanged(StreamStateChanged e)
        {
            try
            {
                StreamingStateChanged?.Invoke(this, e);
            }
            catch (Exception ex)
            {
                Logger.log?.Error($"Error in 'StreamingStateChanged' event: {ex.Message}");
                Logger.log?.Debug(ex);
            }
        }

        private void OnStreamStatus(GetStreamStatusResponse status)
        {
#if DEBUG
            Logger.log?.Info($"Stream Time: {status.OutputDuration / 1000} sec");
            Logger.log?.Info($"Bitrate: {status.OutputBytes * 8 / 1024f:N2} Mbps");
            Logger.log?.Info($"FPS: {status.OutputTotalFrames / status.OutputDuration * 1000} FPS");
            Logger.log?.Info($"Strain: {status.OutputCongestion * 100} %");
            Logger.log?.Info($"SkippedFrames: {status.OutputSkippedFrames} frames");
            Logger.log?.Info($"TotalFrames: {status.OutputTotalFrames} frames");
#endif
            try
            {
                StreamStatus?.RaiseEventSafe(this, status, nameof(StreamStatus));
            }
            catch (Exception ex)
            {
                Logger.log?.Error($"Error in 'StreamStatus' event: {ex.Message}");
                Logger.log?.Debug(ex);
            }
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
            await AddOBSComponentAsync<SceneController>();
            await AddOBSComponentAsync<AudioDevicesController>();
            await AddOBSComponentAsync<RecordingController>();
            await AddOBSComponentAsync<StreamingController>();
            await RepeatTryConnect(CancellationToken.None);
        }

        /// <summary>
        /// Called when the script becomes enabled and active
        /// </summary>
        private void OnEnable()
        {
            foreach (OBSComponent component in OBSComponents.Values.ToArray())
            {
                component.gameObject.SetActive(true);
            }
        }

        /// <summary>
        /// Called when the script becomes disabled or when it is being destroyed.
        /// </summary>
        private void OnDisable()
        {
            foreach (OBSComponent component in OBSComponents.Values.ToArray())
            {
                if (component != null)
                    component.gameObject.SetActive(false);
            }
        }

        /// <summary>
        /// Called when the script is being destroyed.
        /// </summary>
        private void OnDestroy()
        {
            instance = null;
            foreach (OBSComponent component in OBSComponents.Values.ToArray())
            {
                RaiseOBSComponentChanged(null, component);
                Destroy(component);
            }
            DestroyObsInstance(Obs);
        }
        #endregion

        protected void RaiseOBSComponentChanged(OBSComponent? added, OBSComponent? removed)
        {
            EventHandler<OBSComponentChangedEventArgs>[]? handlers =
                OBSComponentChanged?.GetInvocationList().Select(d => (EventHandler<OBSComponentChangedEventArgs>)d).ToArray();
            if (handlers != null && handlers.Length > 0)
            {
                OBSComponentChangedEventArgs args = new OBSComponentChangedEventArgs(added, removed);
                for (int i = 0; i < handlers.Length; i++)
                {
                    try
                    {
                        handlers[i].Invoke(this, args);
                    }
                    catch (Exception ex)
                    {
                        Logger.log?.Error($"Error in OBSComponentChanged handler: {ex.Message}");
                        Logger.log?.Debug(ex);
                    }
                }
            }
        }
    }


    public class OBSComponentChangedEventArgs : EventArgs
    {
        public readonly OBSComponent? RemovedComponent;
        public readonly OBSComponent? AddedComponent;
        public OBSComponentChangedEventArgs(OBSComponent? added, OBSComponent? removed)
        {
            AddedComponent = added;
            RemovedComponent = removed;
        }
    }
}
