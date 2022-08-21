using OBSControl.Utilities;
using ObsStrawket;
using ObsStrawket.DataTypes.Predefineds;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OBSControl.OBSComponents.Actions
{
    public class SceneChangeAction : ObsAction
    {
        public override ControlEventType EventType => ControlEventType.SceneChange;
        public string SceneName { get; }
        public int Timeout { get; set; } = 5000; // TODO: What if transition > 5 seconds?
        private AsyncEventListenerWithArg<string?, string, string> SceneListener { get; }

        public SceneChangeAction(ObsClientSocket obs, string sceneName)
            : base(obs)
        {
            if (string.IsNullOrEmpty(sceneName))
                throw new ArgumentNullException(nameof(sceneName));
            SceneName = sceneName;
            SceneListener = new AsyncEventListenerWithArg<string?, string, string>((s, sceneName, expectedScene) =>
             {
                 if (string.IsNullOrEmpty(expectedScene))
                     return new EventListenerResult<string?>(null, true);
                 if (sceneName == expectedScene)
                     return new EventListenerResult<string?>(sceneName, true);
                 else
                     return new EventListenerResult<string?>(sceneName, false);
             }, string.Empty, Timeout);
        }

        private void OnSceneChange(CurrentProgramSceneChanged ev)
        {
            SceneListener.OnEvent(obs, ev.SceneName);
        }

        protected override async Task ActionAsync(CancellationToken cancellationToken)
        {
            obs.CurrentProgramSceneChanged -= OnSceneChange;
            obs.CurrentProgramSceneChanged += OnSceneChange;
            SceneListener.Reset(SceneName, cancellationToken);
            SceneListener.StartListening();
            var currentScene = await obs.GetCurrentProgramSceneAsync(cancellation: cancellationToken).ConfigureAwait(false);
            if (currentScene?.RequestStatus.Result == true && currentScene.CurrentProgramSceneName == SceneName)
                return;
            await SceneListener.Task.ConfigureAwait(false);
        }

        protected override void Cleanup()
        {
            obs.CurrentProgramSceneChanged -= OnSceneChange;
            SceneListener.TrySetCanceled();
        }
    }
}
