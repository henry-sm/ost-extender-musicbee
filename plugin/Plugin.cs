using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using System.Threading;

// This namespace is from the template to handle dependencies.
namespace YourNamespace
{
    public sealed class LibraryEntryPoint { static LibraryEntryPoint() {} }
}

// This is the primary namespace for the plugin.
namespace MusicBeePlugin
{
    public partial class Plugin
    {
        static private YourNamespace.LibraryEntryPoint entryPoint = new YourNamespace.LibraryEntryPoint();
        private MusicBeeApiInterface mbApiInterface;
        private PluginInfo about = new PluginInfo();
        
        // **RESTORED ROBUST STATE MANAGEMENT**
        private Dictionary<string, (double start, double end)> trackLoopPoints = new Dictionary<string, (double, double)>();
        private HashSet<string> activeLoopingTracks = new HashSet<string>();
        private System.Windows.Forms.Timer playbackTimer = new System.Windows.Forms.Timer();
        public PluginInfo Initialise(IntPtr apiInterfacePtr)
        {
            try
            {
                mbApiInterface = new MusicBeeApiInterface();
                mbApiInterface.Initialise(apiInterfacePtr);

                Assembly thisAssem = typeof(Plugin).Assembly;
                about.Name = thisAssem.GetCustomAttribute<AssemblyTitleAttribute>()?.Title ?? "Smart Looper";
                about.Description = thisAssem.GetCustomAttribute<AssemblyDescriptionAttribute>()?.Description ?? "Finds and plays seamless audio loops.";
                about.Author = thisAssem.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company ?? "Unknown";
                Version ver = thisAssem.GetName().Version;
                about.VersionMajor = (short)ver.Major;
                about.VersionMinor = (short)ver.Minor;
                about.Revision = (short)ver.Revision;

                about.PluginInfoVersion = PluginInfoVersion;
                about.Type = PluginType.General;
                about.MinInterfaceVersion = MinInterfaceVersion;
                about.MinApiRevision = MinApiRevision;
                about.ReceiveNotifications = ReceiveNotificationFlags.PlayerEvents;
                about.ConfigurationPanelHeight = 0;

                // Register menu items for both library and now playing contexts
                mbApiInterface.MB_AddMenuItem("context.Library/Smart Loop/Activate", null, ActivateSmartLoop_Clicked);
                mbApiInterface.MB_AddMenuItem("context.Library/Smart Loop/Deactivate", null, DeactivateSmartLoop_Clicked);
                mbApiInterface.MB_AddMenuItem("context.Main/Smart Loop/Activate", null, ActivateSmartLoop_Clicked);
                mbApiInterface.MB_AddMenuItem("context.Main/Smart Loop/Deactivate", null, DeactivateSmartLoop_Clicked);

                playbackTimer.Interval = 10; // A responsive but safe interval
                playbackTimer.Tick += PlaybackTimer_Tick;
                playbackTimer.Start();

                return about;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Plugin initialization failed:\n{ex.Message}", "Plugin Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return about;
            }
        }

        // Add these variables to your Plugin class
        private string currentLoopTrack = "";
        private (double start, double end) currentLoopPoints = (0, 0);

        private void ActivateSmartLoop_Clicked(object sender, EventArgs e)
        {
            string[] selectedFiles = new string[0];
            mbApiInterface.Library_QueryFilesEx("domain=SelectedFiles", out selectedFiles);
            if (selectedFiles.Length == 0) return;
            string selectedFile = selectedFiles[0];
            
            // Always normalize the path
            string normalizedPath = Path.GetFullPath(selectedFile).ToLowerInvariant();

            if (!trackLoopPoints.ContainsKey(normalizedPath))
            {
                mbApiInterface.MB_SetBackgroundTaskMessage("Smart Looper: Analyzing track...");
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
                    
                    // Store using the normalized path
                    trackLoopPoints[normalizedPath] = (start, end);
                    
                    // Log for debugging
                    File.AppendAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "SmartLoop.log"), 
                        $"{DateTime.Now}: Stored loop for '{normalizedPath}' - Start: {start}, End: {end}\n");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error parsing loop points.\n\nDetails: {ex.Message}", "Plugin Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }
            
            activeLoopingTracks.Add(normalizedPath);
            var points = trackLoopPoints[normalizedPath];
            
            // Check if this is the currently playing track
            string nowPlayingFile = mbApiInterface.NowPlaying_GetFileUrl();
            string normalizedNowPlaying = Path.GetFullPath(nowPlayingFile).ToLowerInvariant();
            
            if (normalizedNowPlaying == normalizedPath)
            {
                // This is the track that's currently playing, so activate loop immediately
                currentLoopTrack = normalizedPath;
                currentLoopPoints = points;
                
                // Log for debugging
                File.AppendAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "SmartLoop.log"), 
                    $"{DateTime.Now}: ACTIVATED LOOP for current track: {normalizedPath}\n");
            }
            
            MessageBox.Show($"Smart Loop activated!\nLooping between {points.start:F2}s and {points.end:F2}s.", "Loop Ready", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void DeactivateSmartLoop_Clicked(object sender, EventArgs e)
        {
            string[] selectedFiles = new string[0];
            mbApiInterface.Library_QueryFilesEx("domain=SelectedFiles", out selectedFiles);
            if (selectedFiles.Length == 0) return;
            
            string selectedFile = selectedFiles[0];
            string normalizedPath = Path.GetFullPath(selectedFile).ToLowerInvariant();

            if (activeLoopingTracks.Contains(normalizedPath))
            {
                activeLoopingTracks.Remove(normalizedPath);
                
                // If this was the current loop track, clear it
                if (currentLoopTrack == normalizedPath)
                {
                    currentLoopTrack = "";
                }
                
                File.AppendAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "SmartLoop.log"), 
                    $"{DateTime.Now}: DEACTIVATED LOOP for track: {normalizedPath}\n");
                    
                MessageBox.Show("Smart Loop deactivated for this track.", "Loop Off", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        
        

        private void PlaybackTimer_Tick(object sender, EventArgs e)
        {
            // Skip cooldown ticks
            if (skipNextTicks > 0)
            {
                skipNextTicks--;
                return;
            }

            // Check if playing
            if (mbApiInterface.Player_GetPlayState() != PlayState.Playing) return;

            string currentFile = mbApiInterface.NowPlaying_GetFileUrl();
            if (string.IsNullOrEmpty(currentFile)) return;
            
            // Try to find loop points for this track
            bool hasLoop = false;
            (double loopStart, double loopEnd) = (0, 0);
            
            // Try exact match first
            if (trackLoopPoints.ContainsKey(currentFile))
            {
                hasLoop = true;
                loopStart = trackLoopPoints[currentFile].start;
                loopEnd = trackLoopPoints[currentFile].end;
            }
            else
            {
                // Try normalized path
                try
                {
                    string normalizedPath = Path.GetFullPath(currentFile).ToLowerInvariant();
                    if (trackLoopPoints.ContainsKey(normalizedPath))
                    {
                        hasLoop = true;
                        loopStart = trackLoopPoints[normalizedPath].start;
                        loopEnd = trackLoopPoints[normalizedPath].end;
                    }
                }
                catch { /* ignore normalization errors */ }
            }

            if (!hasLoop) return;

            // Get current position and check if we need to loop
            double position = mbApiInterface.Player_GetPosition() / 1000.0;
            double duration = mbApiInterface.NowPlaying_GetDuration() / 1000.0;
            
            // *** PREDICTIVE APPROACH: Look ahead to compensate for position lag ***
            // Calculate playback rate (how fast position is changing)
            double elapsedTime = (DateTime.Now - lastCheckTime).TotalSeconds;
            double positionDelta = position - lastPosition;
            
            // Store current values for next calculation
            lastPosition = position;
            lastCheckTime = DateTime.Now;
            
            // Only calculate rate if we have meaningful values
            double playbackRate = 1.0; // Default to 1x speed
            if (elapsedTime > 0 && Math.Abs(positionDelta) > 0.001)
            {
                playbackRate = positionDelta / elapsedTime;
                
                // Sanity check - playback rate should be close to 1.0
                if (playbackRate > 0.5 && playbackRate < 1.5)
                {
                    // Use an exponential moving average to smooth the rate
                    smoothedPlaybackRate = (smoothedPlaybackRate * 0.7) + (playbackRate * 0.3);
                }
            }
            
            // Predict where playback will be in the next 100ms
            double predictedPosition = position + (smoothedPlaybackRate * 0.1);
            
            // Log occasionally for debugging
            if (DateTime.Now.Second % 5 == 0 && DateTime.Now.Millisecond < 200)
            {
                File.AppendAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "SmartLoop.log"), 
                    $"{DateTime.Now}: Position={position:F2}s, Predicted={predictedPosition:F2}s, End={loopEnd:F2}s, Rate={smoothedPlaybackRate:F3}x\n");
            }
            
            // Check if current OR predicted position is past the loop point
            // This catches both the case where we're already past it AND the case where we'll be past it soon
            if (position >= loopEnd - 0.2 || predictedPosition >= loopEnd)
            {
                // Log the loop attempt
                File.AppendAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "SmartLoop.log"), 
                    $"{DateTime.Now}: ATTEMPTING LOOP from {position:F2}s (predicted {predictedPosition:F2}s) to {loopStart:F2}s\n");
                
                try
                {
                    // Two-step approach: pause, then reset position
                    mbApiInterface.Player_PlayPause(); // Pause
                    Thread.Sleep(20);
                    mbApiInterface.Player_SetPosition((int)(loopStart * 1000));
                    Thread.Sleep(20);
                    mbApiInterface.Player_PlayPause(); // Resume
                    
                    // Reset our predictive variables after a jump
                    lastPosition = loopStart;
                    lastCheckTime = DateTime.Now;
                    
                    File.AppendAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "SmartLoop.log"), 
                        $"{DateTime.Now}: Used PAUSE-REPOSITION-PLAY method\n");
                }
                catch (Exception ex)
                {
                    File.AppendAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "SmartLoop.log"), 
                        $"{DateTime.Now}: ERROR: {ex.Message}\n");
                    
                    // If the two-step approach fails, try the full reload method
                    try
                    {
                        // Get current track in playlist
                        int currentIndex = mbApiInterface.NowPlayingList_GetCurrentIndex();
                        
                        mbApiInterface.Player_Stop();
                        Thread.Sleep(50);
                        mbApiInterface.NowPlayingList_PlayNow(currentIndex.ToString());
                        Thread.Sleep(100);
                        mbApiInterface.Player_SetPosition((int)(loopStart * 1000));
                        mbApiInterface.Player_PlayPause();
                        
                        File.AppendAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "SmartLoop.log"), 
                            $"{DateTime.Now}: Used TRACK RELOAD METHOD as fallback\n");
                    }
                    catch (Exception ex2)
                    {
                        File.AppendAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "SmartLoop.log"), 
                            $"{DateTime.Now}: FALLBACK ERROR: {ex2.Message}\n");
                    }
                }
                
                // Skip several ticks to avoid multiple attempts
                skipNextTicks = 20;
            }
        }

// Add these fields to your class
private DateTime lastCheckTime = DateTime.Now;
private double lastPosition = 0;
private double smoothedPlaybackRate = 1.0;


        // Add this field to your class
        private int skipNextTicks = 0;

        public void ReceiveNotification(string sourceFileUrl, NotificationType type)
        {
            // This function handles notifications from MusicBee, including playback state changes
            switch (type)
            {
                case NotificationType.PlayStateChanged:
                    // When play state changes, check if we need to jump
                    PlayState state = mbApiInterface.Player_GetPlayState();
                    if (state == PlayState.Playing && !string.IsNullOrEmpty(currentLoopTrack))
                    {
                        double currentPositionSec = mbApiInterface.Player_GetPosition() / 1000.0;
                        if (currentPositionSec >= currentLoopPoints.end)
                        {
                            File.AppendAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "SmartLoop.log"), 
                                $"{DateTime.Now}: NOTIFICATION JUMP from {currentPositionSec:F2}s to {currentLoopPoints.start:F2}s\n");
                            mbApiInterface.Player_SetPosition((int)(currentLoopPoints.start * 1000));
                        }
                    }
                    break;
                    
                case NotificationType.PlayingTracksChanged:
                case NotificationType.TrackChanged:
                    // Track changed, check if the new track has loop points
                    string nowPlayingFile = mbApiInterface.NowPlaying_GetFileUrl();
                    try
                    {
                        string normalizedNowPlaying = Path.GetFullPath(nowPlayingFile).ToLowerInvariant();
                        
                        if (activeLoopingTracks.Contains(normalizedNowPlaying) && 
                            trackLoopPoints.ContainsKey(normalizedNowPlaying))
                        {
                            currentLoopTrack = normalizedNowPlaying;
                            currentLoopPoints = trackLoopPoints[normalizedNowPlaying];
                            
                            File.AppendAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "SmartLoop.log"), 
                                $"{DateTime.Now}: NOTIFICATION - Loop activated for: {normalizedNowPlaying}\n");
                        }
                        else
                        {
                            currentLoopTrack = "";
                        }
                    }
                    catch
                    {
                        // Path normalization failed
                        currentLoopTrack = "";
                    }
                    break;
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
                using (var process = Process.Start(startInfo)) {
                    string result = process.StandardOutput.ReadToEnd();
                    string err = process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    return process.ExitCode == 0 ? result.Trim() : err;
                }
            } catch (Exception ex) {
                return $"Failed to launch Python. Is it in your system PATH?\n\nDetails: {ex.Message}";
            }
        }

        public bool Configure(IntPtr panelHandle) => false;
        public void SaveSettings() {}
        public void Close(PluginCloseReason reason) { playbackTimer.Stop(); }
        public void Uninstall() {}
    }
}