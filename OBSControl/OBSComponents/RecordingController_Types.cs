using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OBSControl.Wrappers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine.Networking;
#nullable enable

namespace OBSControl.OBSComponents
{
    public enum RecordActionSourceType
    {
        /// <summary>
        /// No information on recording action.
        /// </summary>
        None = 0,
        /// <summary>
        /// Recording started by OBS.
        /// </summary>
        ManualOBS = 1,
        /// <summary>
        /// Recording started manually from OBSControl.
        /// </summary>
        Manual = 2,
        /// <summary>
        /// Recording start/stop automatically.
        /// </summary>
        Auto = 3
    }

    public enum RecordActionType
    {
        /// <summary>
        /// No information on recording action.
        /// </summary>
        None = 0,
        /// <summary>
        /// Recording should be stopped only manually.
        /// </summary>
        NoAction = 1,
        /// <summary>
        /// Recording should be start/stopped immediately.
        /// </summary>
        Immediate = 2,
        /// <summary>
        /// Recording should be start/stopped after a delay.
        /// </summary>
        Delayed = 3,
        /// <summary>
        /// Recording should be stopped automatically (by SceneSequence callback).
        /// </summary>
        Auto = 4
    }

    public enum RecordStartOption
    {
        /// <summary>
        /// Recording will not be auto started
        /// </summary>
        None = 0,
        /// <summary>
        /// Recording starts when triggered by SceneSequence.
        /// </summary>
        SceneSequence = 2,
        /// <summary>
        /// Recording will be started in GameCore at the start of the song.
        /// </summary>
        SongStart = 3,
        /// <summary>
        /// Level start will begin after recording starts and a delay.
        /// </summary>
        LevelStartDelay = 4,
        /// <summary>
        /// Recording will be started immediately when LevelStarting is triggered.
        /// </summary>
        Immediate = 5
    }

    public enum RecordStopOption
    {
        /// <summary>
        /// Recording will not be auto stopped
        /// </summary>
        None = 0,
        /// <summary>
        /// Recording stopped when triggered by SceneSequence.
        /// </summary>
        SceneSequence = 2,
        /// <summary>
        /// Recording will be stopped based on when the song ends (paired with stop delay).
        /// </summary>
        SongEnd = 3,
        /// <summary>
        /// Recording will be stopped based on when the results view is presented (paired with stop delay).
        /// </summary>
        ResultsView = 4
    }




    public partial class RecordingController
    {
        protected class RecordingData
        {
            public bool MultipleLastLevels;
            public BeatmapLevelWrapper LevelData;
            public PlayerLevelStatsData? PlayerLevelStats;
            public LevelCompletionResultsWrapper? LevelResults;

            public string? BeatSaverKey { get; set; }

            public RecordingData(BeatmapLevelWrapper levelData, PlayerLevelStatsData? playerLevelStats = null)
            {
                LevelData = levelData;
                PlayerLevelStats = playerLevelStats;
            }
            public RecordingData(BeatmapLevelWrapper levelData, LevelCompletionResultsWrapper? levelResults, PlayerLevelStatsData? playerLevelStats)
            {
                LevelResults = levelResults;
                LevelData = levelData;
                PlayerLevelStats = playerLevelStats;
            }

            public async Task<string?> GetFileName(string? fileFormat, string? invalidSubstitute, string? spaceReplacement)
            {
                if (fileFormat?.Contains("?k") == true)
                {
                    await PrepareBeatSaverInformation().ConfigureAwait(false);
                }
                return GetFilenameString(fileFormat, invalidSubstitute, spaceReplacement);
            }

            private string? GetFilenameString(string? fileFormat, string? invalidSubstitute, string? spaceReplacement)
            {
                // TODO: Handle MultipleLastLevels?
                if (LevelData == null)
                    return null;
                return Utilities.FileRenaming.GetFilenameString(fileFormat,
                        LevelData,
                        LevelResults,
                        invalidSubstitute,
                        spaceReplacement,
                        BeatSaverKey);
            }

            private async Task PrepareBeatSaverInformation()
            {
                if (BeatSaverKey != null)
                {
                    return;
                }

                var match = Regex.Match(LevelData.LevelID, @"custom_level_([0-9a-f]{40})", RegexOptions.IgnoreCase);
                Logger.log?.Info($"match: {match.Success} {LevelData.LevelID}");
                if (!match.Success)
                {
                    return;
                }

                var hash = match.Groups[1];
                var request = UnityWebRequest.Get($"https://beatsaver.com/api/maps/hash/{hash}");
                var operation = request.SendWebRequest();
                var source = new TaskCompletionSource<string>();
                operation.completed += (_) => source.TrySetResult(request.downloadHandler.text);
                string json = await source.Task.ConfigureAwait(false);
                Logger.log?.Info($"request error: {request.isNetworkError} {request.isHttpError}");
                if (request.isNetworkError || request.isHttpError)
                {
                    return;
                }

                string? id = JObject.Parse(json)["id"]?.Value<string>();
                BeatSaverKey = id;
                Logger.log?.Info($"BeatSaverKey: {id}");
            }

        }

        public struct RecordingSettings
        {
            public static RecordingSettings None => new RecordingSettings();

            public string? PreviousOutputDirectory;
            public bool OutputDirectorySet;

            public string? PreviousFileFormat;
            public bool FileFormatSet;
        }
    }
}
