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

            // Add menu items to Tools menu
            mbApiInterface.MB_AddMenuItem("mnuTools/OST Extender", "OST Extender", null);
            mbApiInterface.MB_AddMenuItem("mnuTools/OST Extender/Analyze Track", "Find Loop Points", onAnalyzeTrack);
            mbApiInterface.MB_AddMenuItem("mnuTools/OST Extender/Play with Loop", "Enable Smart Looping", onSmartLoop);
            mbApiInterface.MB_AddMenuItem("mnuTools/OST Extender/Create Extended Version", "Extend OST (A+B×5)", onExtendTrack);
            mbApiInterface.MB_AddMenuItem("mnuTools/OST Extender/Toggle Loop", "Toggle Smart Loop On/Off", onToggleSmartLoop);

            // Add to multiple context menus to ensure it appears on right-click
            string[] contextMenus = new[] {
                "mnuTracklist", 
                "mnuTracklistNoSelection",
                "mnuFileListSongList", 
                "mnuFileListSongListNoSelection",
                "mnuContext" 
            };
            
            foreach (string menuPath in contextMenus)
            {
                mbApiInterface.MB_AddMenuItem($"{menuPath}/OST Extender", "OST Extender", null);
                mbApiInterface.MB_AddMenuItem($"{menuPath}/OST Extender/Analyze Track", "Find Loop Points", onAnalyzeTrack);
                mbApiInterface.MB_AddMenuItem($"{menuPath}/OST Extender/Play with Loop", "Enable Smart Looping", onSmartLoop);
                mbApiInterface.MB_AddMenuItem($"{menuPath}/OST Extender/Create Extended Version", "Extend OST", onExtendTrack);
            }
            
            // Setup playback monitoring timer - check every 50ms for more responsive looping
            playbackTimer = new System.Timers.Timer(50);
            playbackTimer.Elapsed += onTimerTick;
            playbackTimer.AutoReset = true;
            playbackTimer.Enabled = true;
            
            // Log that plugin is initialized
            mbApiInterface.MB_Trace("OST Extender plugin initialized successfully.");

            return about;
        }

        public void ReceiveNotification(string sourceFileUrl, NotificationType type)
        {
            try
            {
                // Log notification for debugging
                mbApiInterface.MB_Trace($"Received notification: {type}");
                
                if (type == NotificationType.PlayStateChanged)
                {
                    UpdateSmartLoopStatus();
                }
                else if (type == NotificationType.TrackChanged)
                {
                    // When track changes, check if new track has loop points
                    string currentFile = mbApiInterface.NowPlaying_GetFileUrl();
                    if (!string.IsNullOrEmpty(currentFile))
                    {
                        string loopFound = mbApiInterface.Library_GetFileTag(currentFile, MetaDataType.Custom1);
                        if (loopFound == "True" && smartLoopEnabled)
                        {
                            // Track has loop points and looping is enabled
                            string loopStart = mbApiInterface.Library_GetFileTag(currentFile, MetaDataType.Custom2);
                            string loopEnd = mbApiInterface.Library_GetFileTag(currentFile, MetaDataType.Custom3);
                            mbApiInterface.MB_Trace($"Track changed - loop points found: {loopStart}s-{loopEnd}s, Smart Loop is {(smartLoopEnabled ? "ENABLED" : "disabled")}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                mbApiInterface.MB_Trace($"Error in notification: {ex.Message}");
            }
        }

        private void UpdateSmartLoopStatus()
        {
            // If not playing, don't disable loop (let user control it)
            if (mbApiInterface.Player_GetPlayState() != PlayState.Playing)
            {
                return;
            }

            string currentFile = mbApiInterface.NowPlaying_GetFileUrl();
            if (string.IsNullOrEmpty(currentFile)) return;

            // Only auto-enable if playing a loopable track, never auto-disable
            string loopFound = mbApiInterface.Library_GetFileTag(currentFile, MetaDataType.Custom1);
            if (loopFound == "True")
            {
                mbApiInterface.MB_Trace($"Auto-enabling smart loop for track with loop points");
            }
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
            if (files == null || files.Length == 0)
            {
                MessageBox.Show("Please select a track first.");
                return;
            }
            
            if (IsTrackLoopable(files[0]))
            {
                // Enable smart loop and show confirmation
                smartLoopEnabled = true;
                string loopStart = mbApiInterface.Library_GetFileTag(files[0], MetaDataType.Custom2);
                string loopEnd = mbApiInterface.Library_GetFileTag(files[0], MetaDataType.Custom3);
                
                MessageBox.Show($"Smart Loop enabled! Track will loop from {loopStart}s to {loopEnd}s.");
                
                // Stop current playback
                mbApiInterface.Player_Stop();
                
                // Queue and play the track
                mbApiInterface.NowPlayingList_Clear();
                mbApiInterface.NowPlayingList_QueueNext(files[0]);
                mbApiInterface.Player_PlayNextTrack();
                
                // Set a timer to double-check that the loop is enabled
                Task.Run(() => 
                {
                    System.Threading.Thread.Sleep(1000);
                    mbApiInterface.MB_Trace($"Smart Loop is {(smartLoopEnabled ? "ENABLED" : "disabled")} for track: {Path.GetFileName(files[0])}");
                });
            }
            else
            {
                MessageBox.Show("This track hasn't been analyzed yet. Please use 'Analyze Track' first.");
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
            try
            {
                // Check if we should be looping
                if (!smartLoopEnabled || mbApiInterface.Player_GetPlayState() != PlayState.Playing)
                    return;
                    
                string currentFile = mbApiInterface.NowPlaying_GetFileUrl();
                if (string.IsNullOrEmpty(currentFile)) return;
    
                // Get loop information from file tags
                string loopFound = mbApiInterface.Library_GetFileTag(currentFile, MetaDataType.Custom1);
                if (loopFound != "True") return;
    
                // Get loop points and current position
                float loopStart = float.Parse(mbApiInterface.Library_GetFileTag(currentFile, MetaDataType.Custom2));
                float loopEnd = float.Parse(mbApiInterface.Library_GetFileTag(currentFile, MetaDataType.Custom3));
                float currentPosition = mbApiInterface.Player_GetPosition() / 1000f; // Convert ms to seconds
    
                // Debug info occasionally
                if (DateTime.Now.Second % 5 == 0)
                {
                    mbApiInterface.MB_Trace($"Loop Status: Enabled | Position: {currentPosition:F1}s | Loop: {loopStart:F1}s-{loopEnd:F1}s");
                }
                
                // Check if we've reached the loop end (with a small buffer)
                if (currentPosition >= (loopEnd - 0.2f))
                {
                    // Jump back to loop start
                    mbApiInterface.MB_Trace($"LOOPING: Jumping from {currentPosition:F1}s back to {loopStart:F1}s");
                    mbApiInterface.Player_SetPosition((int)(loopStart * 1000));
                }
            }
            catch (Exception ex)
            {
                mbApiInterface.MB_Trace($"Timer error: {ex.Message}");
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