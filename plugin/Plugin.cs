using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

// This namespace is from your template to handle dependencies.
namespace YourNamespace
{
    public sealed class LibraryEntryPoint { static LibraryEntryPoint() {} }
}

// This is the primary namespace for the plugin.
namespace MusicBeePlugin
{
    public partial class Plugin
    {
        private static YourNamespace.LibraryEntryPoint entryPoint = new YourNamespace.LibraryEntryPoint();
        private MusicBeeApiInterface mbApiInterface;
        private PluginInfo about = new PluginInfo();
        
        // **RESTORED ROBUST STATE MANAGEMENT**
        private Dictionary<string, (double start, double end)> trackLoopPoints = new Dictionary<string, (double, double)>();
        private HashSet<string> activeLoopingTracks = new HashSet<string>();
        private Timer playbackTimer = new Timer();

        public PluginInfo Initialise(IntPtr apiInterfacePtr)
        {
            mbApiInterface = new MusicBeeApiInterface();
            mbApiInterface.Initialise(apiInterfacePtr);

            // Set plugin info manually to ensure it's consistent
            about.Name = "OST Extender";
            about.Description = "Analyzes audio tracks to create optimized loop points for game soundtracks";
            about.Author = "Siddhant";
            about.VersionMajor = 1;
            about.VersionMinor = 0;
            about.Revision = 0;
            
            about.PluginInfoVersion = PluginInfoVersion;
            about.Type = PluginType.General;
            about.MinInterfaceVersion = MinInterfaceVersion;
            about.MinApiRevision = MinApiRevision;
            about.ReceiveNotifications = ReceiveNotificationFlags.PlayerEvents;
            about.ConfigurationPanelHeight = 0;

            // **Using the correct format for the context menu ensures reliability**
            mbApiInterface.MB_AddMenuItem("context.Main/OST Extender/Activate Smart Loop", null, ActivateSmartLoop_Clicked);
            mbApiInterface.MB_AddMenuItem("context.Main/OST Extender/Deactivate Smart Loop", null, DeactivateSmartLoop_Clicked);

            playbackTimer.Interval = 75; // A responsive but non-interfering interval
            playbackTimer.Tick += PlaybackTimer_Tick;
            playbackTimer.Start();
            return about;
        }

        // Add these fields to store original metadata
        private Dictionary<string, (float originalStart, float originalEnd)> originalTrackTimes = new Dictionary<string, (float, float)>();
        private void ActivateSmartLoop_Clicked(object sender, EventArgs e)
        {
            string[] selectedFiles = new string[0];
            mbApiInterface.Library_QueryFilesEx("domain=SelectedFiles", out selectedFiles);
            if (selectedFiles.Length == 0) return;
            string selectedFile = selectedFiles[0];

            if (!trackLoopPoints.ContainsKey(selectedFile))
            {
                mbApiInterface.MB_SetBackgroundTaskMessage("OST Extender: Analyzing track...");
                string result = RunPythonScript(selectedFile);
                mbApiInterface.MB_SetBackgroundTaskMessage("");
                if (string.IsNullOrEmpty(result) || !result.Contains(":"))
                {
                    MessageBox.Show($"Could not find loop points.\n\nDetails: {result}", "Analysis Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                try
                {
                    string[] parts = result.Split(':');
                    double start = double.Parse(parts[0], CultureInfo.InvariantCulture);
                    double end = double.Parse(parts[1], CultureInfo.InvariantCulture);
                    trackLoopPoints[selectedFile] = (start, end);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error parsing loop points.\n\nDetails: {ex.Message}", "Plugin Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }
            
            activeLoopingTracks.Add(selectedFile);
            var points = trackLoopPoints[selectedFile];
            
            string originalStart = "0";
            string originalEnd = "0";
            originalStart = mbApiInterface.Library_GetFileProperty(selectedFile, FilePropertyType.Duration);
            originalEnd = originalStart; // End time is the same as duration
            originalTrackTimes[selectedFile] = (
                float.TryParse(originalStart, NumberStyles.Any, CultureInfo.InvariantCulture, out float os) ? os : 0,
                float.TryParse(originalEnd, NumberStyles.Any, CultureInfo.InvariantCulture, out float oe) ? oe : 0

            );
            

            // Use Virtual tags to store loop points
            mbApiInterface.Library_SetFileTag(selectedFile, MetaDataType.Virtual1, points.start.ToString(CultureInfo.InvariantCulture));
            mbApiInterface.Library_SetFileTag(selectedFile, MetaDataType.Virtual2, points.end.ToString(CultureInfo.InvariantCulture));
            // Also set MusicBee's internal repeat mode to enable looping
            mbApiInterface.Player_SetRepeat(RepeatMode.All);

            // Apply changes to the currently playing track if it's this one
            string nowPlayingFile = mbApiInterface.NowPlaying_GetFileUrl();
            if (nowPlayingFile == selectedFile)
            {
                bool wasPlaying = mbApiInterface.Player_GetPlayState() == PlayState.Playing;
                if (wasPlaying) mbApiInterface.Player_PlayPause(); // Pause
                mbApiInterface.Player_SetPosition(0); // Reset to beginning
                if (wasPlaying) mbApiInterface.Player_PlayPause(); // Resume if it was playing
            }
            
            // Commit changes to library
            mbApiInterface.Library_CommitTagsToFile(selectedFile);
            
            MessageBox.Show($"Loop activated!\nSet playback boundaries from {points.start:F2}s to {points.end:F2}s.", 
                "OST Extender", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void DeactivateSmartLoop_Clicked(object sender, EventArgs e)
        {
            string[] selectedFiles = new string[0];
            mbApiInterface.Library_QueryFilesEx("domain=SelectedFiles", out selectedFiles);
            if (selectedFiles.Length == 0) return;
            string selectedFile = selectedFiles[0];
            
            if (activeLoopingTracks.Contains(selectedFile))
            {
                activeLoopingTracks.Remove(selectedFile);
                
                // Restore original start/end times if we have them
                if (originalTrackTimes.TryGetValue(selectedFile, out var originalTimes))
                {
                    mbApiInterface.Library_SetFileTag(selectedFile, MetaDataType.Virtual1, "");
                    mbApiInterface.Library_SetFileTag(selectedFile, MetaDataType.Virtual2, "");
                    mbApiInterface.Player_SetRepeat(RepeatMode.None);
                        
                    // Apply changes to the currently playing track if it's this one
                    string nowPlayingFile = mbApiInterface.NowPlaying_GetFileUrl();
                    if (nowPlayingFile == selectedFile)
                    {
                        bool wasPlaying = mbApiInterface.Player_GetPlayState() == PlayState.Playing;
                        if (wasPlaying) mbApiInterface.Player_PlayPause(); // Pause
                        mbApiInterface.Player_SetPosition(0); // Reset to beginning
                        if (wasPlaying) mbApiInterface.Player_PlayPause(); // Resume if it was playing
                    }
                    
                    // Commit changes to library
                    mbApiInterface.Library_CommitTagsToFile(selectedFile);
                    
                    // Remove from our original times tracking
                    originalTrackTimes.Remove(selectedFile);
                }
                
                MessageBox.Show("Loop deactivated for this track. Original playback boundaries restored.", 
                    "OST Extender", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        // Active monitoring of playback position to handle custom loop points
        private void PlaybackTimer_Tick(object sender, EventArgs e)
        {
            // Only check if we're playing
            if (mbApiInterface.Player_GetPlayState() != PlayState.Playing) return;
            
            string currentFile = mbApiInterface.NowPlaying_GetFileUrl();
            if (!string.IsNullOrEmpty(currentFile) && activeLoopingTracks.Contains(currentFile))
            {
                if (trackLoopPoints.TryGetValue(currentFile, out var points))
                {
                    // Get current position in seconds
                    double currentPosition = mbApiInterface.Player_GetPosition() / 1000.0;
                    
                    // Check if we've reached the end point
                    if (currentPosition >= points.end - 0.1) // Small buffer to ensure we catch it
                    {
                        // Jump back to the loop start
                        mbApiInterface.Player_SetPosition((int)(points.start * 1000));
                        
                        // Log for debugging if needed
                        // mbApiInterface.MB_Trace($"Loop jumped: {currentPosition:F2}s → {points.start:F2}s");
                    }
                }
            }
        }


        
        private string RunPythonScript(string inputFile)
        {
            string pythonExePath = "python.exe";
            string pluginDirectory = Path.GetDirectoryName(this.GetType().Assembly.Location);
            string scriptPath = Path.Combine(pluginDirectory, "looper.py");
            if (!File.Exists(scriptPath)) return "Error: looper.py not found in Plugins directory.";
            var startInfo = new ProcessStartInfo {
                FileName = pythonExePath, Arguments = $"\"{scriptPath}\" \"{inputFile}\"",
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            try {
                using (var process = Process.Start(startInfo))
                {
                    string result = process.StandardOutput.ReadToEnd();
                    string err = process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    return process.ExitCode == 0 ? result.Trim() : err;
                }
            }
            catch (Exception ex)
            {
                return $"Failed to launch Python. Is it in your system PATH?\n\nDetails: {ex.Message}";
            }
        }

        public bool Configure(IntPtr panelHandle) => false;
        public void SaveSettings() {}
        public void Close(PluginCloseReason reason) { playbackTimer.Stop(); }
        public void Uninstall() {}
        public void ReceiveNotification(string sourceFileUrl, NotificationType type)
        {
            // Handle track changes
            if (type == NotificationType.TrackChanged)
            {
                string currentFile = mbApiInterface.NowPlaying_GetFileUrl();
                if (!string.IsNullOrEmpty(currentFile) && activeLoopingTracks.Contains(currentFile))
                {
                    // Ensure repeat is set to One for consistent looping
                    mbApiInterface.Player_SetRepeat(RepeatMode.One);
                }
            }
            // Handle playstate changes
            else if (type == NotificationType.PlayStateChanged)
            {
                if (mbApiInterface.Player_GetPlayState() != PlayState.Playing) return;
                
                string currentFile = mbApiInterface.NowPlaying_GetFileUrl();
                if (!string.IsNullOrEmpty(currentFile) && activeLoopingTracks.Contains(currentFile))
                {
                    // Update loop point handling whenever playback changes
                    if (trackLoopPoints.TryGetValue(currentFile, out var points))
                    {
                        // Make sure our custom loop points are set
                        mbApiInterface.Library_SetFileTag(currentFile, MetaDataType.Virtual1, 
                            points.start.ToString(CultureInfo.InvariantCulture));
                        mbApiInterface.Library_SetFileTag(currentFile, MetaDataType.Virtual2, 
                            points.end.ToString(CultureInfo.InvariantCulture));
                        mbApiInterface.Library_CommitTagsToFile(currentFile);
                    }
                }
            }
        }
    }
}