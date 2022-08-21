using OBSControl.Utilities;
using ObsStrawket;
using ObsStrawket.DataTypes;
using ObsStrawket.DataTypes.Predefineds;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OBSControl.OBSComponents.Actions
{
    public class StopRecordAction : ObsAction
    {
        public override ControlEventType EventType => ControlEventType.StopRecord;

        public int Timeout { get; set; } = 5000; // TODO: What if transition > 5 seconds?
        private AsyncEventListenerWithArg<OutputState, OutputState, OutputState> RecordStateListener { get; }
        public StopRecordAction(ObsClientSocket obs)
            : base(obs)
        {


            RecordStateListener = new AsyncEventListenerWithArg<OutputState, OutputState, OutputState>((s, state, expectedState) =>
            {
                if (state == expectedState)
                    return new EventListenerResult<OutputState>(state, true);
                else
                    return new EventListenerResult<OutputState>(state, false);
            }, OutputState.Stopped, Timeout);
        }

        protected async override Task ActionAsync(CancellationToken cancellationToken)
        {
            try
            {
                obs.RecordStateChanged -= OnRecordingStateChanged;
                obs.RecordStateChanged += OnRecordingStateChanged;
                RecordStateListener.Reset(OutputState.Stopped, cancellationToken);
                RecordStateListener.StartListening();
                bool isRecording = (await obs.GetRecordingStatus(cancellationToken)) == OutputState.Started;
                if (!isRecording)
                    return;
                await obs.StopRecordAsync(cancellation: cancellationToken).ConfigureAwait(false);
                await RecordStateListener.Task.ConfigureAwait(false);
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Logger.log?.Debug($"Stop recording was canceled in 'StopRecordAction'.");
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
                RecordStateListener.TrySetCanceled();
            }
        }

        private void OnRecordingStateChanged(RecordStateChanged e)
        {
            RecordStateListener.OnEvent(this, e.OutputState);
        }

        protected override void Cleanup()
        {
            obs.RecordStateChanged -= OnRecordingStateChanged;
            RecordStateListener.TrySetCanceled();
        }
    }
}
