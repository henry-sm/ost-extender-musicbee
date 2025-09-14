using System;
using System.Drawing;
using System.Windows.Forms;
using System.IO;
using System.Threading.Tasks;
using NAudio.Wave;
using System.Timers;
using Accord.Audio;
using Accord.Audio.Features;
using Accord.Math;

namespace MusicBeePlugin
{
    public partial class Plugin
    {
        private MusicBeeApiInterface mbApiInterface;
        private PluginInfo about = new PluginInfo();
        private System.Timers.Timer playbackTimer;

        public PluginInfo Initialise(IntPtr apiInterfacePtr)
        {
            mbApiInterface = new MusicBeeApiInterface();
            mbApiInterface.Initialise(apiInterfacePtr);
            about.PluginInfoVersion = PluginInfoVersion;
            about.Name = "OST Looper (Native)";
            about.Description = "Finds seamless loops using a self-contained C# analysis engine.";
            about.Author = "Your Name";
            about.TargetApplication = "";
            about.Type = PluginType.General;
            about.VersionMajor = 2; // Version 2.0
            about.VersionMinor = 0;
            about.ReceiveNotifications = (ReceiveNotificationFlags.PlayerEvents);
            about.ConfigurationPanelHeight = 0;

            mbApiInterface.MB_AddMenuItem("CONTEXT/TRACK:Find Seamless Loop (Native)", "OST Looper: Analyze Track", onAnalyzeTrack);
            
            playbackTimer = new System.Timers.Timer(100);
            playbackTimer.Elapsed += onTimerTick;
            playbackTimer.AutoReset = true;
            playbackTimer.Enabled = true;

            return about;
        }

        private void onAnalyzeTrack(object sender, EventArgs e)
        {
            string[] files = null;
            mbApiInterface.Library_QueryFilesEx("domain=SelectedFiles", out files);
            if (files == null || files.Length == 0) return;
            Task.Run(() => AnalyseTrack(files[0]));
        }

        private void AnalyseTrack(string filePath)
        {
            try
            {
                mbApiInterface.MB_SetBackgroundTaskMessage($"Analyzing (Native): {Path.GetFileName(filePath)}");

                // Decode the audio file using NAudio
                Signal signal = Signal.FromFile(filePath);

                // Find the loop points using our new C# method
                var result = FindLoopPoints(signal);

                if (result.Status == "success")
                {
                    mbApiInterface.Library_SetFileTag(filePath, MetaDataType.Custom1, "True");
                    mbApiInterface.Library_SetFileTag(filePath, MetaDataType.Custom2, result.LoopStart.ToString());
                    mbApiInterface.Library_SetFileTag(filePath, MetaDataType.Custom3, result.LoopEnd.ToString());
                    MessageBox.Show($"Analysis complete! Loop found: {result.LoopStart:F2}s to {result.LoopEnd:F2}s");
                }
                else
                {
                    mbApiInterface.Library_SetFileTag(filePath, MetaDataType.Custom1, "False");
                    MessageBox.Show("Analysis failed: Could not find a suitable loop.");
                }

                mbApiInterface.Library_CommitTagsToFile(filePath);
                mbApiInterface.MB_SetBackgroundTaskMessage("");
            }
            catch (Exception ex)
            {
                mbApiInterface.MB_SetBackgroundTaskMessage("");
                MessageBox.Show($"Analysis failed: {ex.Message}");
            }
        }

        public class LoopResult
        {
            public string Status { get; set; }
            public float LoopStart { get; set; }
            public float LoopEnd { get; set; }
        }

        private LoopResult FindLoopPoints(Signal signal)
        {
            // 1. Create a feature extractor for harmony (similar to chroma features)
            var extractor = new ChromaFeature(windowSize: 4096, hopSize: 2048);
            var features = extractor.ProcessSignal(signal).ToJagged();

            // 2. Create the self-similarity matrix
            var matrix = new DistanceMatrix(features, (v1, v2) => Distance.Cosine(v1, v2));

            // Invert distance to similarity (0 distance = 1 similarity)
            for (int i = 0; i < matrix.Count; i++)
            for (int j = 0; j < matrix.Count; j++)
                matrix[i, j] = 1.0 - matrix[i, j];

            // 3. Find the brightest point (most similar, non-adjacent sections)
            double maxSimilarity = 0;
            int bestFrame1 = -1, bestFrame2 = -1;
            int minLoopFrames = (int)(15.0 * signal.SampleRate / 2048); // 15s min loop

            for (int i = 0; i < matrix.Count; i++)
            {
                for (int j = i + minLoopFrames; j < matrix.Count; j++)
                {
                    if (matrix[i, j] > maxSimilarity)
                    {
                        maxSimilarity = matrix[i, j];
                        bestFrame1 = i;
                        bestFrame2 = j;
                    }
                }
            }

            if (bestFrame1 == -1) return new LoopResult { Status = "failed" };

            // 4. Convert frame indices to time
            float loopStartTime = (float)bestFrame1 * 2048 / signal.SampleRate;
            float loopEndTime = (float)bestFrame2 * 2048 / signal.SampleRate;

            return new LoopResult { Status = "success", LoopStart = loopStartTime, LoopEnd = loopEndTime };
        }

        private void onTimerTick(object sender, ElapsedEventArgs e)
        {
            if (mbApiInterface.Player_GetPlayState() != PlayState.Playing) return;
            string currentFile = mbApiInterface.NowPlaying_GetFileUrl();
            if (string.IsNullOrEmpty(currentFile)) return;

            string loopFound = mbApiInterface.Library_GetFileTag(currentFile, MetaDataType.Custom1);

            if (loopFound == "True")
            {
                float loopStart = float.Parse(mbApiInterface.Library_GetFileTag(currentFile, MetaDataType.Custom2));
                float loopEnd = float.Parse(mbApiInterface.Library_GetFileTag(currentFile, MetaDataType.Custom3));
                float currentPosition = mbApiInterface.Player_GetPosition() / 1000f;

                if (currentPosition >= (loopEnd - 0.1f))
                {
                    mbApiInterface.Player_SetPosition((int)(loopStart * 1000));
                }
            }
        }
        
        public bool Configure(IntPtr panelHandle) => false;
        public void SaveSettings() { }
        public void Close(PluginCloseReason reason) { }
        public void Uninstall() { }
        public void ReceiveNotification(string sourceFileUrl, NotificationType type) { }
    }
}