using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

// This namespace handles loading dependent DLLs. Leave it as is.
namespace YourNamespace
{
    public sealed class LibraryEntryPoint
    {
        // This is from the template and handles dependencies. We can ignore it.
        static LibraryEntryPoint() {}
    }
}

// This is the primary namespace for the plugin.
namespace MusicBeePlugin
{
    public partial class Plugin
    {
        // Required so the template's entry point can be called
        static private YourNamespace.LibraryEntryPoint entryPoint = new YourNamespace.LibraryEntryPoint();

        private MusicBeeApiInterface mbApiInterface;
        private PluginInfo about = new PluginInfo();
        
        // This dictionary stores the loop points for each track file path.
        private Dictionary<string, (double start, double end)> trackLoopPoints = new Dictionary<string, (double, double)>();
        
        // A timer to constantly check the player's position.
        private Timer playbackTimer = new Timer();

        public PluginInfo Initialise(IntPtr apiInterfacePtr)
        {
            // Read plugin info from the .csproj file
            Assembly thisAssem = typeof(Plugin).Assembly;
            about.Name = thisAssem.GetCustomAttribute<AssemblyTitleAttribute>().Title;
            about.Description = thisAssem.GetCustomAttribute<AssemblyDescriptionAttribute>().Description;
            about.Author = thisAssem.GetCustomAttribute<AssemblyCompanyAttribute>().Company;
            Version ver = thisAssem.GetName().Version;
            about.VersionMajor = (short)ver.Major;
            about.VersionMinor = (short)ver.Minor;
            about.Revision = (short)ver.Revision;
            
            mbApiInterface = new MusicBeeApiInterface();
            mbApiInterface.Initialise(apiInterfacePtr);
            about.PluginInfoVersion = PluginInfoVersion;
            about.Type = PluginType.General;
            about.TargetApplication = "";
            about.MinInterfaceVersion = MinInterfaceVersion;
            about.MinApiRevision = MinApiRevision;
            about.ReceiveNotifications = ReceiveNotificationFlags.PlayerEvents;
            about.ConfigurationPanelHeight = 0;

            // Add our "Activate Smart Loop" option to the right-click context menu.
            mbApiInterface.MB_AddMenuItem("CONTEXT/Activate Smart Loop", null, ActivateSmartLoop_Clicked);

            // Setup and start the timer.
            playbackTimer.Interval = 200; // Check 5 times per second
            playbackTimer.Tick += PlaybackTimer_Tick;
            playbackTimer.Start();

            return about;
        }

        private void ActivateSmartLoop_Clicked(object sender, EventArgs e)
        {
            // THIS IS THE CORRECTED CODE TO GET THE SELECTED FILE
            string[] selectedFiles = new string[0];
            mbApiInterface.NowPlayingList_QueryFilesEx("domain=SelectedFiles", out selectedFiles);

            if (selectedFiles.Length == 0) return; // No file was selected
            string selectedFile = selectedFiles[0]; // Get the first selected file

            mbApiInterface.MB_SetBackgroundTaskMessage("Smart Looper: Analyzing track...");
            string result = RunPythonScript(selectedFile);
            mbApiInterface.MB_SetBackgroundTaskMessage("");

            if (string.IsNullOrEmpty(result) || !result.Contains(":"))
            {
                MessageBox.Show($"Could not find loop points.\n\nDetails: {result}", "Smart Loop Analysis Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                string[] parts = result.Split(':');
                double start = double.Parse(parts[0], CultureInfo.InvariantCulture);
                double end = double.Parse(parts[1], CultureInfo.InvariantCulture);
                
                trackLoopPoints[selectedFile] = (start, end); 
                MessageBox.Show($"Smart Loop activated!\nLooping between {start:F2}s and {end:F2}s.", "Smart Loop Ready", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error parsing loop points.\n\nDetails: {ex.Message}", "Smart Loop Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private string RunPythonScript(string inputFile)
        {
            string pythonExePath = "python.exe"; // Assumes python is in your system's PATH
            string pluginDirectory = Path.GetDirectoryName(this.GetType().Assembly.Location);
            string scriptPath = Path.Combine(pluginDirectory, "looper.py");

            if (!File.Exists(scriptPath)) return "Error: looper.py not found in Plugins directory.";

            var startInfo = new ProcessStartInfo
            {
                FileName = pythonExePath,
                Arguments = $"\"{scriptPath}\" \"{inputFile}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            try
            {
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

        private void PlaybackTimer_Tick(object sender, EventArgs e)
        {
            if (mbApiInterface.Player_GetPlayState() != PlayState.Playing) return;

            string currentFile = mbApiInterface.NowPlaying_GetFileUrl();
            if (trackLoopPoints.ContainsKey(currentFile))
            {
                (double loopStart, double loopEnd) = trackLoopPoints[currentFile];
                double currentPositionSec = mbApiInterface.Player_GetPosition() / 1000.0;

                if (currentPositionSec >= loopEnd)
                {
                    mbApiInterface.Player_SetPosition((int)(loopStart * 1000));
                }
            }
        }

        public bool Configure(IntPtr panelHandle) => false;
        public void SaveSettings() {}
        public void Close(PluginCloseReason reason) { playbackTimer.Stop(); }
        public void Uninstall() {}
        public void ReceiveNotification(string sourceFileUrl, NotificationType type) {}
    }
}