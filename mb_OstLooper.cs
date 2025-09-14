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
using System.Linq; // For LINQ extension methods like Take()

namespace MusicBeePlugin
{
    public partial class Plugin
    {
        private MusicBeeApiInterface mbApiInterface;
        private PluginInfo about = new PluginInfo();
        
        // Component instances
        private ConfigManager configManager;
        private PlaybackController playbackController;
        private AudioFeatureExtractor featureExtractor;
        private LoopDetector loopDetector;
        private UserInterface userInterface;

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
            about.Revision = 2;  // Include revision
            about.MinInterfaceVersion = MinInterfaceVersion;  // Required
            about.MinApiRevision = MinApiRevision;  // Required
            about.ReceiveNotifications = (ReceiveNotificationFlags.PlayerEvents);
            about.ConfigurationPanelHeight = 0;

            // Initialize components
            InitializeComponents();
            
            // Log that plugin is initialized
            mbApiInterface.MB_Trace("OST Extender plugin initialized successfully.");

            return about;
        }

        /// <summary>
        /// Initialize all plugin components
        /// </summary>
        private void InitializeComponents()
        {
            // Create component instances
            configManager = new ConfigManager(mbApiInterface);
            playbackController = new PlaybackController(mbApiInterface);
            featureExtractor = new AudioFeatureExtractor(mbApiInterface);
            loopDetector = new LoopDetector(mbApiInterface);
            userInterface = new UserInterface(mbApiInterface, playbackController, configManager);
            
            // Initialize the UI
            userInterface.InitializeMenuItems();
            
            // Subscribe to UI events
            userInterface.AnalyzeTrackRequested += AnalyseTrack;
            userInterface.ExtendTrackRequested += playbackController.ExtendTrack;
            userInterface.ReanalyzeAllRequested += ReanalyzeAllTracks;
            
            // Apply settings from configuration
            ApplySettings();
        }
        
        /// <summary>
        /// Apply saved settings to components
        /// </summary>
        private void ApplySettings()
        {
            var settings = configManager.GetSettings();
            
            // Apply smart loop setting if enabled in config
            if (settings.SmartLoopEnabled)
            {
                playbackController.ToggleSmartLoop();
            }
        }

        public void ReceiveNotification(string sourceFileUrl, NotificationType type)
        {
            try
            {
                // Log notification for debugging
                mbApiInterface.MB_Trace($"Received notification: {type}");
                
                if (type == NotificationType.PlayStateChanged)
                {
                    playbackController.UpdateSmartLoopStatus();
                }
                else if (type == NotificationType.TrackChanged)
                {
                    // When track changes, check if new track has loop points
                    string currentFile = mbApiInterface.NowPlaying_GetFileUrl();
                    if (!string.IsNullOrEmpty(currentFile))
                    {
                        string loopFound = mbApiInterface.Library_GetFileTag(currentFile, MetaDataType.Custom1);
                        bool smartLoopEnabled = playbackController.IsSmartLoopEnabled();
                        
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

        /// <summary>
        /// Re-analyze all tracks in the library that have been previously analyzed
        /// </summary>
        private void ReanalyzeAllTracks()
        {
            Task.Run(() => {
                try
                {
                    // Find all tracks with the OST Extender metadata
                    string[] files = null;
                    mbApiInterface.Library_QueryFilesEx("domain=Library field=Custom1 comparison=Equal value=\"True\"", out files);
                    
                    if (files == null || files.Length == 0)
                    {
                        MessageBox.Show("No previously analyzed tracks were found in the library.", 
                                       "OST Extender", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    
                    mbApiInterface.MB_Trace($"Re-analyzing {files.Length} tracks with existing loop points");
                    mbApiInterface.MB_SetBackgroundTaskMessage($"Re-analyzing {files.Length} OST tracks...");
                    
                    int processed = 0;
                    foreach (string file in files)
                    {
                        processed++;
                        mbApiInterface.MB_SetBackgroundTaskMessage($"Analyzing track {processed}/{files.Length}: {Path.GetFileName(file)}");
                        AnalyseTrack(file);
                        
                        // Small delay to allow UI to update and prevent excessive CPU usage
                        System.Threading.Thread.Sleep(500);
                    }
                    
                    mbApiInterface.MB_SetBackgroundTaskMessage($"Re-analysis complete! Processed {processed} tracks.");
                    
                    System.Threading.Thread.Sleep(3000);
                    mbApiInterface.MB_SetBackgroundTaskMessage("");
                }
                catch (Exception ex)
                {
                    mbApiInterface.MB_Trace($"Error during batch re-analysis: {ex.Message}");
                    mbApiInterface.MB_SetBackgroundTaskMessage("");
                    MessageBox.Show($"An error occurred during re-analysis: {ex.Message}", 
                                   "OST Extender", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            });
        }

        /// <summary>
        /// Analyzes a track to find optimal loop points
        /// </summary>
        private void AnalyseTrack(string filePath)
        {
            try
            {
                mbApiInterface.MB_SetBackgroundTaskMessage($"Analyzing: {Path.GetFileName(filePath)}");
                mbApiInterface.MB_Trace($"Starting analysis of file: {filePath}");
                
                // Use a background task for the CPU-intensive analysis
                Task.Run(() => {
                    try
                    {
                        // Step 1: Extract audio data using NAudio
                        mbApiInterface.MB_Trace("Reading audio data...");
                        LoopResult result = new LoopResult { Status = "failed" };
                        int sampleRate = 0; // Declare outside the using block so it's available later
                        
                        using (var audioFile = new AudioFileReader(filePath))
                        {
                            sampleRate = audioFile.WaveFormat.SampleRate;
                            int channels = audioFile.WaveFormat.Channels;
                            mbApiInterface.MB_Trace($"File format: {sampleRate}Hz, {channels} channels");
                            
                            // Calculate how many samples to read (limit to 3 minutes to prevent memory issues)
                            int maxDurationSeconds = configManager.GetSettings().MaxAnalysisDuration;
                            int totalSamples = (int)Math.Min(audioFile.Length / (audioFile.WaveFormat.BitsPerSample / 8), 
                                                            sampleRate * channels * maxDurationSeconds);
                                                            
                            mbApiInterface.MB_Trace($"Reading {totalSamples} samples");
                            mbApiInterface.MB_SetBackgroundTaskMessage($"Reading audio data from {Path.GetFileName(filePath)}");
                            
                            // Read audio data in chunks to prevent memory issues
                            float[] audioData = new float[totalSamples];
                            int offset = 0;
                            int chunkSize = sampleRate * channels; // 1 second chunks
                            float[] buffer = new float[chunkSize];
                            
                            while (offset < totalSamples)
                            {
                                int toRead = Math.Min(chunkSize, totalSamples - offset);
                                int read = audioFile.Read(buffer, 0, toRead);
                                if (read <= 0) break;
                                
                                Array.Copy(buffer, 0, audioData, offset, read);
                                offset += read;
                                
                                // Update progress every 5 seconds
                                if (offset % (5 * chunkSize) == 0)
                                {
                                    double progress = (double)offset / totalSamples * 100;
                                    mbApiInterface.MB_SetBackgroundTaskMessage($"Reading audio: {progress:F0}% complete");
                                }
                            }
                            
                            mbApiInterface.MB_Trace($"Successfully read {offset} samples");
                            mbApiInterface.MB_SetBackgroundTaskMessage($"Analyzing audio patterns in {Path.GetFileName(filePath)}");
                            
                            // Step 2: Analyze the audio data using the loop detector
                            result = loopDetector.AnalyzeAudioData(audioData, sampleRate, channels, audioFile.TotalTime.TotalSeconds);
                        }
                        
                        // Step 3: Save the results
                        mbApiInterface.MB_Trace($"Analysis result: {result.Status}");
                        
                        if (result.Status != "failed") {
                            // Save the results since a loop was found
                            mbApiInterface.Library_SetFileTag(filePath, MetaDataType.Custom1, "True");
                            mbApiInterface.Library_SetFileTag(filePath, MetaDataType.Custom2, result.LoopStart.ToString());
                            mbApiInterface.Library_SetFileTag(filePath, MetaDataType.Custom3, result.LoopEnd.ToString());
                            
                            // Store sample-accurate positions for more precise looping
                            if (result.LoopStartSample > 0 && result.LoopEndSample > 0)
                            {
                                mbApiInterface.Library_SetFileTag(filePath, MetaDataType.Custom4, result.LoopStartSample.ToString());
                                mbApiInterface.Library_SetFileTag(filePath, MetaDataType.Custom5, result.LoopEndSample.ToString());
                                mbApiInterface.Library_SetFileTag(filePath, MetaDataType.Custom6, sampleRate.ToString());
                                mbApiInterface.MB_Trace($"Stored sample-accurate loop points: {result.LoopStartSample}-{result.LoopEndSample} @ {sampleRate}Hz");
                            }
                            mbApiInterface.Library_CommitTagsToFile(filePath);
                            
                            // Format time values for display
                            TimeSpan startTime = TimeSpan.FromSeconds(result.LoopStart);
                            TimeSpan endTime = TimeSpan.FromSeconds(result.LoopEnd);
                            TimeSpan loopLength = TimeSpan.FromSeconds(result.LoopEnd - result.LoopStart);
                            string formattedStart = string.Format("{0:D2}:{1:D2}.{2:D1}", (int)startTime.TotalMinutes, startTime.Seconds, startTime.Milliseconds / 100);
                            string formattedEnd = string.Format("{0:D2}:{1:D2}.{2:D1}", (int)endTime.TotalMinutes, endTime.Seconds, endTime.Milliseconds / 100);
                            string formattedLength = string.Format("{0:D2}:{1:D2}.{2:D1}", (int)loopLength.TotalMinutes, loopLength.Seconds, loopLength.Milliseconds / 100);
                            
                            // Calculate percentage of track for visual reference
                            using (var reader = new AudioFileReader(filePath))
                            {
                                double totalDuration = reader.TotalTime.TotalSeconds;
                                int startPercent = (int)(result.LoopStart * 100 / totalDuration);
                                int endPercent = (int)(result.LoopEnd * 100 / totalDuration);
                                
                                // Create a visual representation of the loop
                                string loopVisual = "[" + new string('-', startPercent/5) + "|" + 
                                                   new string('=', (endPercent - startPercent)/5) + "|" + 
                                                   new string('-', (100 - endPercent)/5) + "]";
                                
                                // Show results on UI thread with detailed information
                                System.Windows.Forms.Form mainForm = System.Windows.Forms.Control.FromHandle(mbApiInterface.MB_GetWindowHandle()).FindForm();
                                mainForm.Invoke(new Action(() => {
                                    string confidenceText = result.Status == "success" ? 
                                        $"Confidence: {result.Confidence:P0} (High)" : 
                                        $"Confidence: {result.Confidence:P0} (Moderate)";
                                        
                                    string message = $"Analysis complete! Loop points detected:\n\n" +
                                                    $"Loop Start: {formattedStart} ({startPercent}% of track)\n" +
                                                    $"Loop End: {formattedEnd} ({endPercent}% of track)\n" +
                                                    $"Loop Length: {formattedLength}\n\n" +
                                                    $"{loopVisual}\n\n" +
                                                    $"{confidenceText}\n\n" +
                                                    $"Use \"Play with Loop\" to test these loop points.";
                                    
                                    MessageBox.Show(mainForm, message, "OST Extender", 
                                        MessageBoxButtons.OK, 
                                        result.Status == "success" ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                                        
                                    mbApiInterface.MB_SetBackgroundTaskMessage("");
                                }));
                            }
                        }
                        else
                        {
                            mbApiInterface.Library_SetFileTag(filePath, MetaDataType.Custom1, "False");
                            mbApiInterface.Library_CommitTagsToFile(filePath);
                            
                            // Show results on UI thread
                            System.Windows.Forms.Form mainForm = System.Windows.Forms.Control.FromHandle(mbApiInterface.MB_GetWindowHandle()).FindForm();
                            mainForm.Invoke(new Action(() => {
                                MessageBox.Show(mainForm, $"Analysis couldn't find reliable loop points.\nUsing fallback points instead.",
                                    "OST Extender", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                                mbApiInterface.MB_SetBackgroundTaskMessage("");
                            }));
                            
                            // Set fallback points anyway so the user can still use smart loop
                            using (var reader = new AudioFileReader(filePath))
                            {
                                float duration = (float)reader.TotalTime.TotalSeconds;
                                mbApiInterface.Library_SetFileTag(filePath, MetaDataType.Custom1, "True");
                                mbApiInterface.Library_SetFileTag(filePath, MetaDataType.Custom2, (duration * 0.3f).ToString());
                                mbApiInterface.Library_SetFileTag(filePath, MetaDataType.Custom3, (duration * 0.8f).ToString());
                            }
                            mbApiInterface.Library_CommitTagsToFile(filePath);
                        }
                    }
                    catch (Exception ex)
                    {
                        mbApiInterface.MB_Trace($"Analysis error: {ex.Message}\n{ex.StackTrace}");
                        System.Windows.Forms.Form mainForm = System.Windows.Forms.Control.FromHandle(mbApiInterface.MB_GetWindowHandle()).FindForm();
                        mainForm.Invoke(new Action(() => {
                            MessageBox.Show(mainForm, $"Analysis error: {ex.Message}", 
                                "OST Extender", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            mbApiInterface.MB_SetBackgroundTaskMessage("");
                        }));
                    }
                });
            }
            catch (Exception ex)
            {
                mbApiInterface.MB_SetBackgroundTaskMessage("");
                mbApiInterface.MB_Trace($"Setup error: {ex.Message}");
                MessageBox.Show($"Failed to start analysis: {ex.Message}");
            }
        }
        
        public bool Configure(IntPtr panelHandle) => false;
        
        public void SaveSettings()
        {
            // Save plugin settings
            configManager.SaveSettings();
        }
        
        public void Close(PluginCloseReason reason)
        {
            // Clean up resources
            playbackController.Cleanup();
        }

        public void Uninstall()
        {
            // Clean up any resources when the plugin is uninstalled
            playbackController.Cleanup();
        }
    }
}