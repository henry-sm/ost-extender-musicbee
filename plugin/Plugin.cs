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

            // This correctly reads "OST Extender" from your .csproj file
            Assembly thisAssem = typeof(Plugin).Assembly;
            about.Name = thisAssem.GetCustomAttribute<AssemblyTitleAttribute>()?.Title;
            about.Description = thisAssem.GetCustomAttribute<AssemblyDescriptionAttribute>()?.Description;
            about.Author = thisAssem.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company;
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

            // **Using the correct format for the context menu ensures reliability**
            mbApiInterface.MB_AddMenuItem("context.Main/OST Extender/Activate Smart Loop", null, ActivateSmartLoop_Clicked);
            mbApiInterface.MB_AddMenuItem("context.Main/OST Extender/Deactivate Smart Loop", null, DeactivateSmartLoop_Clicked);

            playbackTimer.Interval = 75; // A responsive but non-interfering interval
            playbackTimer.Tick += PlaybackTimer_Tick;
            playbackTimer.Start();
            return about;
        }

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
            MessageBox.Show($"Loop activated!\nLooping between {points.start:F2}s and {points.end:F2}s.", "OST Extender", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                MessageBox.Show("Loop deactivated for this track.", "OST Extender", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void PlaybackTimer_Tick(object sender, EventArgs e)
        {
            if (mbApiInterface.Player_GetPlayState() != PlayState.Playing) return;

            string currentFile = mbApiInterface.NowPlaying_GetFileUrl();

            // If this is a looping track
            if (!string.IsNullOrEmpty(currentFile) && activeLoopingTracks.Contains(currentFile))
            {
                // Safely get the loop points for this track
                if (trackLoopPoints.TryGetValue(currentFile, out var points))
                {
                    // Define a look-ahead buffer to compensate for latency
                    double buffer = (playbackTimer.Interval / 1000.0) * 2.0;
                    
                    if (points.end > buffer)
                    {
                        double currentPositionSec = mbApiInterface.Player_GetPosition() / 1000.0;
                        
                        // If we are past the end OR within the buffer zone, jump.
                        if (currentPositionSec >= points.end - buffer)
                        {
                            // Try pause-jump-play technique
                            PlayState originalState = mbApiInterface.Player_GetPlayState();
                            mbApiInterface.Player_PlayPause(); // Pause
                            System.Threading.Thread.Sleep(50); // Wait a bit
                            mbApiInterface.Player_SetPosition((int)(points.start * 1000)); // Set position
                            System.Threading.Thread.Sleep(50); // Wait a bit
                            
                            // If it was playing before, resume
                            if (originalState == PlayState.Playing)
                            {
                                mbApiInterface.Player_PlayPause();
                            }
                            
                            // Log to desktop for debugging
                            File.AppendAllText(
                                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "SmartLoop.log"), 
                                $"{DateTime.Now}: Loop jumped from {currentPositionSec:F2}s to {points.start:F2}s\n"
                            );
                        }
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
        public void ReceiveNotification(string sourceFileUrl, NotificationType type) {}
    }
}