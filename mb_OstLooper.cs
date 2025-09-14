using System;
using System.Drawing;
using System.Windows.Forms;
using System.IO;
using System.Threading.Tasks;
using NAudio.Wave;
using System.Timers;
using Accord.Audio; // Base audio classes
using Accord.Audio.Features; // FIX: This is the missing namespace for ChromaFeature
using Accord.Math; // For the DistanceMatrix
using System.Collections.Generic;

namespace MusicBeePlugin
{
    public partial class Plugin
    {
        private MusicBeeApiInterface mbApiInterface;
        private PluginInfo about = new PluginInfo();
        private ToolStripMenuItem extendOstMenuItem;

        public PluginInfo Initialise(IntPtr apiInterfacePtr)
        {
            mbApiInterface = new MusicBeeApiInterface();
            mbApiInterface.Initialise(apiInterfacePtr);
            about.PluginInfoVersion = PluginInfoVersion;
            about.Name = "OST Extender";
            about.Description = "Analyzes tracks to find loops and generates extended versions.";
            about.Author = "Your Name";
            about.TargetApplication = "";
            about.Type = PluginType.General;
            about.VersionMajor = 3;
            about.VersionMinor = 0;
            about.ReceiveNotifications = (ReceiveNotificationFlags.PlayerEvents | ReceiveNotificationFlags.MenuEvents);
            about.ConfigurationPanelHeight = 0;

            mbApiInterface.MB_AddMenuItem("CONTEXT/TRACK:Analyze for Loop", "OST Extender: Analyze Track", onAnalyzeTrack);
            
            extendOstMenuItem = new ToolStripMenuItem("Extend OST");
            extendOstMenuItem.Click += onExtendTrack;
            mbApiInterface.MB_AddMenuItem(extendOstMenuItem, "CONTEXT/TRACK");

            return about;
        }

        public void ReceiveNotification(string sourceFileUrl, NotificationType type)
        {
            if (type == NotificationType.MenuOpened)
            {
                string[] files = null;
                mbApiInterface.Library_QueryFilesEx("domain=SelectedFiles", out files);
                if (files == null || files.Length == 0) return;
                string loopFound = mbApiInterface.Library_GetFileTag(files[0], MetaDataType.Custom1);
                extendOstMenuItem.Enabled = (loopFound == "True");
            }
        }
        
        private void onAnalyzeTrack(object sender, EventArgs e)
        {
            string[] files = null;
            mbApiInterface.Library_QueryFilesEx("domain=SelectedFiles", out files);
            if (files == null || files.Length == 0) return;
            Task.Run(() => AnalyseTrack(files[0]));
        }

        private void onExtendTrack(object sender, EventArgs e)
        {
            string[] files = null;
            mbApiInterface.Library_QueryFilesEx("domain=SelectedFiles", out files);
            if (files == null || files.Length == 0) return;
            Task.Run(() => ExtendTrack(files[0]));
        }

        private void ExtendTrack(string filePath)
        {
            try
            {
                mbApiInterface.MB_SetBackgroundTaskMessage($"Extending: {Path.GetFileName(filePath)}");
                float loopStart = float.Parse(mbApiInterface.Library_GetFileTag(filePath, MetaDataType.Custom2));
                float loopEnd = float.Parse(mbApiInterface.Library_GetFileTag(filePath, MetaDataType.Custom3));
                int repeatCount = 5;
                string tempPath = Path.Combine(Path.GetTempPath(), $"extended_{Path.GetFileNameWithoutExtension(filePath)}.wav");

                using (var reader = new AudioFileReader(filePath))
                using (var writer = new WaveFileWriter(tempPath, reader.WaveFormat))
                {
                    reader.CurrentTime = TimeSpan.Zero;
                    byte[] buffer = new byte[reader.WaveFormat.AverageBytesPerSecond];
                    int read;
                    while (reader.CurrentTime.TotalSeconds < loopEnd)
                    {
                        read = reader.Read(buffer, 0, buffer.Length);
                        if (read > 0) writer.Write(buffer, 0, read); else break;
                    }
                    for (int i = 0; i < repeatCount; i++)
                    {
                        reader.CurrentTime = TimeSpan.FromSeconds(loopStart);
                        while (reader.CurrentTime.TotalSeconds < loopEnd)
                        {
                            read = reader.Read(buffer, 0, buffer.Length);
                            if (read > 0) writer.Write(buffer, 0, read); else break;
                        }
                    }
                }
                mbApiInterface.NowPlayingList_QueueLast(tempPath);
                mbApiInterface.Player_PlayNextTrack();
                mbApiInterface.MB_SetBackgroundTaskMessage("");
            }
            catch (Exception ex)
            {
                mbApiInterface.MB_SetBackgroundTaskMessage("");
                MessageBox.Show($"Failed to extend track: {ex.Message}");
            }
        }

        private void AnalyseTrack(string filePath)
        {
            try
            {
                mbApiInterface.MB_SetBackgroundTaskMessage($"Analyzing (Native): {Path.GetFileName(filePath)}");
                var result = FindLoopPoints(Signal.FromFile(filePath));
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
        
        public class LoopResult { public string Status { get; set; } public float LoopStart { get; set; } public float LoopEnd { get; set; } }

        private LoopResult FindLoopPoints(Signal signal)
        {
            var extractor = new ChromaFeature(windowSize: 4096, hopSize: 2048);
            var features = extractor.ProcessSignal(signal).ToJagged();
            var matrix = new DistanceMatrix(features, (v1, v2) => Distance.Cosine(v1, v2));

            for (int i = 0; i < matrix.Count; i++)
                for (int j = 0; j < matrix.Count; j++)
                    matrix[i, j] = 1.0 - matrix[i, j];

            double maxSimilarity = 0;
            int bestFrame1 = -1, bestFrame2 = -1;
            int minLoopFrames = (int)(15.0 * signal.SampleRate / 2048);

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

            float loopStartTime = (float)bestFrame1 * 2048 / signal.SampleRate;
            float loopEndTime = (float)bestFrame2 * 2048 / signal.SampleRate;

            return new LoopResult { Status = "success", LoopStart = loopStartTime, LoopEnd = loopEndTime };
        }
        
        public bool Configure(IntPtr panelHandle) => false;
        public void SaveSettings() { }
        public void Close(PluginCloseReason reason) { }
        public void Uninstall() { }
    }
}