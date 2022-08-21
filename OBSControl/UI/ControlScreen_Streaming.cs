using BeatSaberMarkupLanguage.Attributes;
using OBSControl.OBSComponents;
using OBSControl.UI.Formatters;
using ObsStrawket.DataTypes;
using ObsStrawket.DataTypes.Predefineds;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
#nullable enable

namespace OBSControl.UI
{
    public partial class ControlScreen
    {
        protected GetStreamStatusResponse? CurrentStreamStatus;
        [UIValue(nameof(TimeFormatter))]
        public readonly TimeFormatter TimeFormatter = new TimeFormatter();
        #region Properties

        [UIValue(nameof(StreamingTextColor))]
        public string StreamingTextColor
        {
            get
            {
                return IsStreaming switch
                {
                    true => "green",
                    false => "red"
                };
            }
        }

        [UIValue(nameof(IsNotStreaming))]
        public bool IsNotStreaming
        {
            get => !_isStreaming;
            set => IsStreaming = !value;
        }

        private bool _isStreaming;

        [UIValue(nameof(IsStreaming))]
        public bool IsStreaming
        {
            get { return _isStreaming; }
            set
            {
                if (_isStreaming == value)
                    return;
                _isStreaming = value;
                NotifyPropertyChanged();
                NotifyPropertyChanged(nameof(IsNotStreaming));
                NotifyPropertyChanged(nameof(StreamingTextColor));
            }
        }

        private bool _streamButtonInteractable = true;
        [UIValue(nameof(StreamButtonInteractable))]
        public bool StreamButtonInteractable
        {
            get { return _streamButtonInteractable && IsConnected; }
            set
            {
                if (_streamButtonInteractable == value) return;
                _streamButtonInteractable = value;
#if DEBUG
                Logger.log?.Debug($"Stream Interactable Changed: {value}");
#endif
                NotifyPropertyChanged();
            }
        }

        [UIValue(nameof(StreamTime))]
        public int StreamTime => CurrentStreamStatus?.OutputDuration ?? 0;
        [UIValue(nameof(Bitrate))]
        public float Bitrate => (CurrentStreamStatus?.OutputBytes ?? 0f) * 8f * ((CurrentStreamStatus?.OutputDuration ?? 0) / 1000f);
        [UIValue(nameof(Strain))]
        public float Strain => StreamingDroppedFrames / Math.Max(1, StreamingOutputFrames);
        [UIValue(nameof(StreamingDroppedFrames))]
        public int StreamingDroppedFrames => CurrentStreamStatus?.OutputSkippedFrames ?? 0;
        [UIValue(nameof(StreamingOutputFrames))]
        public int StreamingOutputFrames => CurrentStreamStatus?.OutputTotalFrames ?? 0;


        #endregion

        #region Actions

        [UIAction(nameof(StartStreaming))]
        public async void StartStreaming()
        {
            StreamButtonInteractable = false;
            try
            {
                await StreamingController.StartStreaming();
                await Task.Delay(2000);
            }
            catch (Exception ex)
            {
                Logger.log?.Warn($"Error starting streaming: {ex.Message}");
                Logger.log?.Debug(ex);
            }
            StreamButtonInteractable = true;
        }

        [UIAction(nameof(StopStreaming))]
        public async void StopStreaming()
        {
            StreamButtonInteractable = false;
            try
            {
                await StreamingController.StopStreaming();
                await Task.Delay(2000);
            }
            catch (Exception ex)
            {
                Logger.log?.Warn($"Error stopping streaming: {ex.Message}");
                Logger.log?.Debug(ex);
            }
            StreamButtonInteractable = true;
        }
        #endregion

        #region Event Handlers
        private void OnStreamingStateChanged(object sender, StreamStateChanged ev)
        {
            HMMainThreadDispatcher.instance.Enqueue(() =>
            {
                var e = ev.OutputState;
                bool enabled = GetOutputStateIsSettled(e);
                if (enabled)
                    StartCoroutine(DelayedStreamInteractableEnable(e == OutputState.Stopped));
                else
                    StreamButtonInteractable = false;
                if (e == OutputState.Started)
                    IsStreaming = true;
                else if (e == OutputState.Stopped)
                    IsStreaming = false;
            });
        }

        private void OnStreamStatus(object sender, GetStreamStatusResponse e)
        {
            CurrentStreamStatus = e;
            NotifyPropertyChanged(nameof(StreamTime));
            NotifyPropertyChanged(nameof(Bitrate));
            NotifyPropertyChanged(nameof(Strain));
            NotifyPropertyChanged(nameof(StreamingDroppedFrames));
            NotifyPropertyChanged(nameof(StreamingOutputFrames));
        }
        #endregion

        private bool StreamButtonCoroutineRunning = false;
        private WaitForSeconds StreamInteractableDelay = new WaitForSeconds(2f);
        protected IEnumerator<WaitForSeconds> DelayedStreamInteractableEnable(bool stopped)
        {
            if (StreamInteractableDelay == null)
            {
                Logger.log?.Warn("StreamInteractableDelay was null.");
                StreamInteractableDelay = new WaitForSeconds(2f);
            }
            if (StreamButtonCoroutineRunning) yield break;
            StreamButtonCoroutineRunning = true;
            yield return StreamInteractableDelay;
            StreamButtonInteractable = true;
            StreamButtonCoroutineRunning = false;
            CurrentStreamStatus = null;
            NotifyPropertyChanged(nameof(Bitrate));
            NotifyPropertyChanged(nameof(Strain));
        }
    }
}
