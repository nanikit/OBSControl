using ObsStrawket;
using System;

namespace OBSControl.OBSComponents.Actions
{
    public abstract class ObsAction : ControlAction
    {
        protected readonly ObsClientSocket obs;
        protected ObsAction(ObsClientSocket obs)
        {
            this.obs = obs ?? throw new ArgumentNullException(nameof(obs));
        }
    }
}
