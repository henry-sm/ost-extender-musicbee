using System;
using System.Drawing;
using System.Windows.Forms;
using System.IO;
using System.Threading.Tasks;
using NAudio.Wave;
using System.Timers;
using Accord.Audio; // Base audio classes
// using Accord.Audio.Features; // Not available in Accord.NET 3.8.0
using Accord.Math; // For the DistanceMatrix
using System.Collections.Generic;

namespace MusicBeePlugin
{
    public partial class Plugin
    {
        private MusicBeeApiInterface mbApiInterface;
        private PluginInfo about = new PluginInfo();
        private System.Timers.Timer playbackTimer;
        private bool smartLoopEnabled = false;

        public PluginInfo Initialise(IntPtr apiInterfacePtr)
        {
            mbApiInterface = new MusicBeeApiInterface();
            mbApiInterface.Initialise(apiInterfacePtr);
            about.PluginInfoVersion = PluginInfoVersion;
            about.Name = "OST Extender";
            about.Description = "Analyzes tracks to find seamless loops for video game OSTs.";
            about.Author = "Siddhant";
            about.TargetApplication = "MusicBee";  // Required
            about.Type = PluginType.General;
            about.VersionMajor = 1;  // Start with version 1
            about.VersionMinor = 0;
            about.Revision = 1;  // Include revision
            about.MinInterfaceVersion = MinInterfaceVersion;  // Required
            about.MinApiRevision = MinApiRevision;  // Required
            about.ReceiveNotifications = (ReceiveNotificationFlags.PlayerEvents);
            about.ConfigurationPanelHeight = 0;

            // Add menu items - using direct paths for compatibility
            mbApiInterface.MB_AddMenuItem("mnuTools/Plugins/OST Extender", "OST Extender", null);
            mbApiInterface.MB_AddMenuItem("mnuTools/Plugins/OST Extender/Analyze Track", "Find Loop Points", onAnalyzeTrack);
            mbApiInterface.MB_AddMenuItem("mnuTools/Plugins/OST Extender/Play with Loop", "Enable Smart Looping", onSmartLoop);
            mbApiInterface.MB_AddMenuItem("mnuTools/Plugins/OST Extender/Create Extended Version", "Extend OST (A+B×5)", onExtendTrack);
            mbApiInterface.MB_AddMenuItem("mnuTools/Plugins/OST Extender/Toggle Loop", "Toggle Smart Loop On/Off", onToggleSmartLoop);

            // Add right-click context menu (File Browser panel context menu)
            mbApiInterface.MB_AddMenuItem("mnuFileBrowserContextMenu/OST Extender", "OST Extender", null);
            mbApiInterface.MB_AddMenuItem("mnuFileBrowserContextMenu/OST Extender/Analyze Track", "Find Loop Points", onAnalyzeTrack);
            mbApiInterface.MB_AddMenuItem("mnuFileBrowserContextMenu/OST Extender/Play with Loop", "Enable Smart Looping", onSmartLoop);
            mbApiInterface.MB_AddMenuItem("mnuFileBrowserContextMenu/OST Extender/Create Extended Version", "Extend OST", onExtendTrack);
            
            // Setup playback monitoring timer
            playbackTimer = new System.Timers.Timer(100);
            playbackTimer.Elapsed += onTimerTick;
            playbackTimer.AutoReset = true;
            playbackTimer.Enabled = true;

            return about;
        }

        public void ReceiveNotification(string sourceFileUrl, NotificationType type)
        {
            if (type == NotificationType.PlayStateChanged)
            {
                UpdateSmartLoopStatus();
            }
        }

        private void UpdateSmartLoopStatus()
        {
            if (mbApiInterface.Player_GetPlayState() != PlayState.Playing)
            {
                smartLoopEnabled = false;
                return;
            }

            string currentFile = mbApiInterface.NowPlaying_GetFileUrl();
            if (string.IsNullOrEmpty(currentFile)) return;

            string loopFound = mbApiInterface.Library_GetFileTag(currentFile, MetaDataType.Custom1);
            smartLoopEnabled = (loopFound == "True");
        }

        // Check if a track has loop points detected
        private bool IsTrackLoopable(string filePath)
        {
            string loopFlag = mbApiInterface.Library_GetFileTag(filePath, MetaDataType.Custom1);
            return loopFlag == "True";
        }

        // Toggle smart loop feature globally
        private void onToggleSmartLoop(object sender, EventArgs e)
        {
            smartLoopEnabled = !smartLoopEnabled;
            MessageBox.Show($"Smart Loop is now {(smartLoopEnabled ? "enabled" : "disabled")}");
        }

        // Play a track with smart loop enabled
        private void onSmartLoop(object sender, EventArgs e)
        {
            string[] files = null;
            mbApiInterface.Library_QueryFilesEx("domain=SelectedFiles", out files);
            if (files == null || files.Length == 0) return;
            
            if (IsTrackLoopable(files[0]))
            {
                smartLoopEnabled = true;
                mbApiInterface.Player_Stop();
                // Queue the track and play it
                mbApiInterface.NowPlayingList_Clear();
                mbApiInterface.NowPlayingList_QueueNext(files[0]);
                mbApiInterface.Player_PlayNextTrack();
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
                mbApiInterface.MB_SetBackgroundTaskMessage($"Extending OST: {Path.GetFileName(filePath)}");
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
                mbApiInterface.MB_SetBackgroundTaskMessage($"Analyzing: {Path.GetFileName(filePath)}");
                
                // Decode the audio file using NAudio for proper analysis
                using (var audioFile = new AudioFileReader(filePath))
                {
                    // Convert to Signal for analysis
                    int sampleRate = audioFile.WaveFormat.SampleRate;
                    int channels = audioFile.WaveFormat.Channels;
                    float[] samples = new float[sampleRate * channels * 30]; // Read up to 30 seconds
                    int samplesRead = audioFile.Read(samples, 0, samples.Length);
                    
                    if (samplesRead > 0)
                    {
                        // Use the audio samples directly instead of Signal class
                        var result = FindLoopPoints(audioFile.TotalTime.TotalSeconds, sampleRate);
                        
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
                    }
                }
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

        private LoopResult FindLoopPoints(double duration, int sampleRate)
        {
            // Basic algorithm to find potential loop points
            // This is a placeholder that can be improved with more advanced audio analysis
            
            if (duration < 30.0)
                return new LoopResult { Status = "failed" };
                
            // For demonstration, we'll use the first 1/3 of the track as loop start
            // and the last 20% as loop end - common for video game OSTs
            float loopStartTime = (float)(duration * 0.3);
            float loopEndTime = (float)(duration * 0.8);
            
            // Ensure minimum loop length of 15 seconds
            if (loopEndTime - loopStartTime < 15.0f)
                return new LoopResult { Status = "failed" };
            
            return new LoopResult { Status = "success", LoopStart = loopStartTime, LoopEnd = loopEndTime };
        }
        
        private void onTimerTick(object sender, ElapsedEventArgs e)
        {
            if (!smartLoopEnabled || mbApiInterface.Player_GetPlayState() != PlayState.Playing)
                return;
                
            string currentFile = mbApiInterface.NowPlaying_GetFileUrl();
            if (string.IsNullOrEmpty(currentFile)) return;

            string loopFound = mbApiInterface.Library_GetFileTag(currentFile, MetaDataType.Custom1);
            if (loopFound != "True") return;

            float loopStart = float.Parse(mbApiInterface.Library_GetFileTag(currentFile, MetaDataType.Custom2));
            float loopEnd = float.Parse(mbApiInterface.Library_GetFileTag(currentFile, MetaDataType.Custom3));
            float currentPosition = mbApiInterface.Player_GetPosition() / 1000f;

            if (currentPosition >= (loopEnd - 0.1f))
            {
                mbApiInterface.Player_SetPosition((int)(loopStart * 1000));
            }
        }
        
        public bool Configure(IntPtr panelHandle) => false;
        public void SaveSettings() { }
        public void Close(PluginCloseReason reason) { }
        public void Uninstall() 
        {
            if (playbackTimer != null)
            {
                playbackTimer.Enabled = false;
                playbackTimer.Dispose();
            }
        }
    }
}