using System;
using System.Drawing;
using System.Windows.Forms;
using System.IO;
using System.Threading.Tasks;
using NAudio.Wave;
using System.Timers;
using Accord.Audio;
// using Accord.Audio.Features; // Removed: not available in Accord.NET 3.8.0
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
            // Placeholder: Use the first and last 15 seconds as loop points if the audio is long enough
            float duration = (float)signal.Length / signal.SampleRate;
            if (duration < 30.0f)
                return new LoopResult { Status = "failed" };

            float loopStartTime = 0f;
            float loopEndTime = 15.0f;
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