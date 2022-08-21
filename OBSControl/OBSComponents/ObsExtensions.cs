using ObsStrawket;
using ObsStrawket.DataTypes;
using System.Threading;
using System.Threading.Tasks;

namespace OBSControl.OBSComponents
{
    public static class ObsExtensions
    {
        public static async Task<OutputState> GetRecordingStatus(this ObsClientSocket obs, CancellationToken cancellationToken)
        {
            var status = await obs.GetRecordStatusAsync(cancellationToken).ConfigureAwait(false);
            return status.OutputActive ? OutputState.Started : OutputState.Stopped;
        }
    }
}
