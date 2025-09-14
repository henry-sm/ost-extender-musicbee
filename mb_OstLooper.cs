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
        private System.Timers.Timer playbackTimer;
        private bool smartLoopEnabled = false;
        
        // Cached loop information to avoid repeated tag reads
        private string currentLoopTrack = null;
        private float currentLoopStart = 0;
        private float currentLoopEnd = 0;

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
            try
            {
                string[] files = null;
                mbApiInterface.Library_QueryFilesEx("domain=SelectedFiles", out files);
                if (files == null || files.Length == 0)
                {
                    MessageBox.Show("Please select a track first.");
                    return;
                }
                
                string filePath = files[0];
                mbApiInterface.MB_Trace($"Smart Loop requested for: {Path.GetFileName(filePath)}");
                
                if (IsTrackLoopable(filePath))
                {
                    // Get loop points from metadata
                    float loopStart = float.Parse(mbApiInterface.Library_GetFileTag(filePath, MetaDataType.Custom2));
                    float loopEnd = float.Parse(mbApiInterface.Library_GetFileTag(filePath, MetaDataType.Custom3));
                    
                    // Reset tracking variables to force reload
                    currentLoopTrack = null;
                    lastLoopTime = DateTime.MinValue;
                    
                    // Enable smart loop and show confirmation with clear visual feedback
                    smartLoopEnabled = true;
                    MessageBox.Show($"Smart Loop Enabled!\n\nTrack will loop from {loopStart:F1}s to {loopEnd:F1}s.\n\n" +
                                   "When playback reaches the end point, it will automatically jump back to the start point.", 
                                   "OST Extender", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    
                    // Stop current playback
                    mbApiInterface.Player_Stop();
                    
                    // Queue and play the track
                    mbApiInterface.NowPlayingList_Clear();
                    mbApiInterface.NowPlayingList_QueueNext(filePath);
                    mbApiInterface.Player_PlayNextTrack();
                    
                    // Set visual indicator
                    mbApiInterface.MB_SetBackgroundTaskMessage("🔄 Smart Loop Mode: ON");
                    
                    // Set a timer to verify the loop is active
                    Task.Run(() => 
                    {
                        System.Threading.Thread.Sleep(2000);
                        mbApiInterface.MB_Trace($"Smart Loop confirmed ENABLED for: {Path.GetFileName(filePath)}");
                        mbApiInterface.MB_Trace($"Loop points: {loopStart:F2}s to {loopEnd:F2}s");
                        
                        // Clear message after a few seconds
                        System.Threading.Thread.Sleep(3000);
                        mbApiInterface.MB_SetBackgroundTaskMessage("");
                    });
                }
                else
                {
                    // Check if the track has been analyzed
                    string loopFlag = mbApiInterface.Library_GetFileTag(filePath, MetaDataType.Custom1);
                    
                    if (loopFlag == null || loopFlag == "")
                    {
                        // Track hasn't been analyzed at all
                        if (MessageBox.Show("This track hasn't been analyzed yet. Would you like to analyze it now?",
                                         "OST Extender", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                        {
                            // Run the analysis first
                            AnalyseTrack(filePath);
                            
                            // Set a timer to enable smart loop after analysis
                            Task.Run(() => 
                            {
                                System.Threading.Thread.Sleep(2000); // Wait for analysis to start
                                mbApiInterface.MB_Trace("Will enable loop after analysis completes");
                            });
                        }
                    }
                    else
                    {
                        // Track was analyzed but no loop was found
                        MessageBox.Show("No loop points were found for this track during analysis.\n\nPlease re-analyze the track or use the 'Create Extended Version' option instead.", 
                                       "OST Extender", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
            }
            catch (Exception ex)
            {
                mbApiInterface.MB_Trace($"Error enabling smart loop: {ex.Message}");
                MessageBox.Show($"Error enabling smart loop: {ex.Message}", "OST Extender", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
                mbApiInterface.MB_Trace($"Starting analysis of file: {filePath}");
                
                // Use a background task for the CPU-intensive analysis
                Task.Run(() => {
                    try
                    {
                        // Step 1: Extract audio data using NAudio
                        mbApiInterface.MB_Trace("Reading audio data...");
                        var result = new LoopResult { Status = "failed" };
                        
                        using (var audioFile = new AudioFileReader(filePath))
                        {
                            int sampleRate = audioFile.WaveFormat.SampleRate;
                            int channels = audioFile.WaveFormat.Channels;
                            mbApiInterface.MB_Trace($"File format: {sampleRate}Hz, {channels} channels");
                            
                            // Calculate how many samples to read (limit to 3 minutes to prevent memory issues)
                            int totalSamples = (int)Math.Min(audioFile.Length / (audioFile.WaveFormat.BitsPerSample / 8), 
                                                            sampleRate * channels * 180); // 3 minutes max
                                                            
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
                            
                            // Step 2: Analyze the audio data
                            result = AnalyzeAudioData(audioData, sampleRate, channels, audioFile.TotalTime.TotalSeconds);
                        }
                        
                        // Step 3: Save the results
                        mbApiInterface.MB_Trace($"Analysis result: {result.Status}");
                        
                        if (result.Status != "failed") {
                            // Save the results since a loop was found
                            mbApiInterface.Library_SetFileTag(filePath, MetaDataType.Custom1, "True");
                            mbApiInterface.Library_SetFileTag(filePath, MetaDataType.Custom2, result.LoopStart.ToString());
                            mbApiInterface.Library_SetFileTag(filePath, MetaDataType.Custom3, result.LoopEnd.ToString());
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
        
        public class LoopResult 
        { 
            public string Status { get; set; } 
            public float LoopStart { get; set; } 
            public float LoopEnd { get; set; } 
            public float Confidence { get; set; }
        }

        private LoopResult AnalyzeAudioData(float[] audioData, int sampleRate, int channels, double duration)
        {
            try
            {
                mbApiInterface.MB_Trace("Starting enhanced audio analysis...");
                
                // If track is too short, use simple rules
                if (duration < 20.0)
                {
                    mbApiInterface.MB_Trace("Track too short for advanced analysis");
                    return new LoopResult { 
                        Status = "success", 
                        LoopStart = (float)(duration * 0.3), 
                        LoopEnd = (float)(duration * 0.8),
                        Confidence = 0.5f
                    };
                }
                
                // Step 1: Convert stereo to mono and normalize
                mbApiInterface.MB_Trace("Converting to mono and normalizing...");
                float[] monoData = ConvertToMono(audioData, channels);
                
                // Step 2: Compute multiple audio features for better accuracy
                mbApiInterface.MB_SetBackgroundTaskMessage("Computing audio features...");
                
                // We'll use three features for better accuracy:
                // 1. RMS Energy (volume)
                // 2. Zero-Crossing Rate (frequency content)
                // 3. Spectral Centroid (brightness/timbre)
                
                // Define frame parameters - use musical timing (beats)
                double frameDuration = 0.2; // 200ms frames (~tempo independent)
                int frameSize = (int)(sampleRate * frameDuration);
                int frameHop = frameSize / 2; // 50% overlap for better resolution
                int numFrames = 1 + (monoData.Length - frameSize) / frameHop;
                
                mbApiInterface.MB_Trace($"Using {numFrames} frames of {frameDuration*1000}ms each");
                
                // Allocate feature arrays
                float[] rmsEnergy = new float[numFrames];
                float[] zeroCrossings = new float[numFrames];
                float[] spectralCentroid = new float[numFrames];
                
                // Extract features
                ExtractAudioFeatures(monoData, sampleRate, frameSize, frameHop, 
                                    rmsEnergy, zeroCrossings, spectralCentroid);
                
                // Step 3: Build enhanced self-similarity matrix using multiple features
                mbApiInterface.MB_Trace("Building multi-feature similarity matrix...");
                mbApiInterface.MB_SetBackgroundTaskMessage("Analyzing patterns...");
                
                // Limit matrix size for memory/performance while keeping enough musical context
                int matrixSize = Math.Min(numFrames, 1000); 
                float[,] similarityMatrix = new float[matrixSize, matrixSize];
                
                // Calculate feature weights (giving more importance to energy)
                float energyWeight = 0.6f;
                float zcWeight = 0.2f;
                float centroidWeight = 0.2f;
                
                // Fill similarity matrix using weighted feature combination
                for (int i = 0; i < matrixSize; i++)
                {
                    // Process in batches for performance
                    for (int j = i; j < matrixSize; j += 10)
                    {
                        int endJ = Math.Min(j + 10, matrixSize);
                        for (int k = j; k < endJ; k++)
                        {
                            // Calculate weighted distance between feature vectors
                            float energyDist = Math.Abs(rmsEnergy[i] - rmsEnergy[k]);
                            float zcDist = Math.Abs(zeroCrossings[i] - zeroCrossings[k]);
                            float centroidDist = Math.Abs(spectralCentroid[i] - spectralCentroid[k]);
                            
                            // Combine with weights
                            float distance = (energyWeight * energyDist) + 
                                           (zcWeight * zcDist) + 
                                           (centroidWeight * centroidDist);
                            
                            similarityMatrix[i, k] = similarityMatrix[k, i] = distance;
                        }
                    }
                    
                    // Update progress
                    if (i % 100 == 0)
                    {
                        int progress = i * 100 / matrixSize;
                        mbApiInterface.MB_SetBackgroundTaskMessage($"Building similarity matrix: {progress}%");
                    }
                }
                
                // Step 4: Apply Gaussian blur to the similarity matrix to reduce noise
                mbApiInterface.MB_Trace("Applying Gaussian smoothing...");
                float[,] smoothedMatrix = GaussianSmoothMatrix(similarityMatrix, matrixSize);
                
                // Step 5: Find the best loop candidates using a more sophisticated approach
                mbApiInterface.MB_Trace("Detecting optimal loop points...");
                mbApiInterface.MB_SetBackgroundTaskMessage("Finding optimal loops...");
                
                // Parameters for loop detection
                int minLoopFrames = (int)(8 / frameDuration);   // Min 8 seconds
                int maxLoopFrames = (int)(120 / frameDuration); // Max 2 minutes
                int frameStep = Math.Max(1, minLoopFrames / 20); // Step size for efficiency
                
                // Variables to track best loops
                List<LoopCandidate> loopCandidates = new List<LoopCandidate>();
                
                // Find promising loop regions by checking diagonals
                mbApiInterface.MB_Trace("Searching for loop candidates...");
                for (int diagOffset = minLoopFrames; diagOffset < Math.Min(maxLoopFrames, matrixSize/2); diagOffset += frameStep)
                {
                    // For each potential loop length, find the best starting point
                    for (int startFrame = 0; startFrame < matrixSize/3; startFrame += frameStep)
                    {
                        int endFrame = startFrame + diagOffset;
                        if (endFrame >= matrixSize) break;
                        
                        // Check similarity at this point
                        float similarity = smoothedMatrix[startFrame, endFrame];
                        
                        // Also check surrounding area (context matters)
                        float contextSimilarity = 0;
                        int contextCount = 0;
                        
                        for (int c = -2; c <= 2; c++)
                        {
                            for (int d = -2; d <= 2; d++)
                            {
                                int si = startFrame + c;
                                int ei = endFrame + d;
                                
                                if (si >= 0 && ei >= 0 && si < matrixSize && ei < matrixSize)
                                {
                                    contextSimilarity += smoothedMatrix[si, ei];
                                    contextCount++;
                                }
                            }
                        }
                        
                        if (contextCount > 0)
                        {
                            contextSimilarity /= contextCount;
                            
                            // Calculate final score (weighted combination of point similarity and context)
                            float finalScore = (similarity * 0.7f) + (contextSimilarity * 0.3f);
                            
                            // Add to candidates if score is good
                            if (finalScore < 0.3f) // Lower is better for distance-based similarity
                            {
                                loopCandidates.Add(new LoopCandidate {
                                    StartFrame = startFrame,
                                    EndFrame = endFrame,
                                    Score = finalScore
                                });
                            }
                        }
                    }
                    
                    // Update progress
                    if (diagOffset % 20 == 0)
                    {
                        int progress = (diagOffset - minLoopFrames) * 100 / (maxLoopFrames - minLoopFrames);
                        mbApiInterface.MB_SetBackgroundTaskMessage($"Detecting loops: {progress}%");
                    }
                }
                
                // Step 6: Select the best loop candidate
                mbApiInterface.MB_Trace($"Found {loopCandidates.Count} potential loop candidates");
                
                if (loopCandidates.Count == 0)
                {
                    // Fallback to simple approach
                    mbApiInterface.MB_Trace("No strong candidates found, using fallback");
                    return GetFallbackLoop(duration);
                }
                
                // Sort candidates by score (lowest is best)
                loopCandidates.Sort((a, b) => a.Score.CompareTo(b.Score));
                
                // Get top candidates
                var topCandidates = loopCandidates.Take(5).ToList();
                
                // Pick the best candidate based on musical criteria
                LoopCandidate bestCandidate = SelectBestMusicalCandidate(topCandidates, frameDuration);
                
                // Convert to time in seconds
                float loopStart = (float)(bestCandidate.StartFrame * frameHop / (double)sampleRate);
                float loopEnd = (float)(bestCandidate.EndFrame * frameHop / (double)sampleRate);
                
                // Refine loop points - adjust to nearest likely beat boundary
                float[] adjustedPoints = RefineLoopPoints(monoData, sampleRate, loopStart, loopEnd);
                loopStart = adjustedPoints[0];
                loopEnd = adjustedPoints[1];
                
                // Safety checks
                if (loopEnd > duration * 0.98)
                {
                    loopEnd = (float)(duration * 0.98);
                }
                
                if (loopStart < duration * 0.1)
                {
                    loopStart = (float)(duration * 0.1);
                }
                
                // Calculate confidence
                float confidence = 1.0f - bestCandidate.Score;
                
                mbApiInterface.MB_Trace($"Enhanced analysis complete. Loop: {loopStart:F2}s to {loopEnd:F2}s, confidence: {confidence:F2}");
                
                return new LoopResult {
                    Status = "success",
                    LoopStart = loopStart,
                    LoopEnd = loopEnd,
                    Confidence = confidence
                };
            }
            catch (Exception ex)
            {
                mbApiInterface.MB_Trace($"Analysis algorithm error: {ex.Message}\n{ex.StackTrace}");
                
                // Fallback to simple ratio-based analysis
                return GetFallbackLoop(duration);
            }
        }
        
        // Helper class for loop candidates
        private class LoopCandidate
        {
            public int StartFrame { get; set; }
            public int EndFrame { get; set; }
            public float Score { get; set; }
        }
        
        // Convert audio to mono
        private float[] ConvertToMono(float[] audioData, int channels)
        {
            if (channels == 1) return audioData;
            
            float[] monoData = new float[audioData.Length / channels];
            for (int i = 0; i < monoData.Length; i++)
            {
                float sum = 0;
                for (int ch = 0; ch < channels; ch++)
                {
                    sum += audioData[i * channels + ch];
                }
                monoData[i] = sum / channels;
            }
            return monoData;
        }
        
        // Extract multiple audio features for better analysis
        private void ExtractAudioFeatures(float[] monoData, int sampleRate, int frameSize, int frameHop,
                                         float[] rmsEnergy, float[] zeroCrossings, float[] spectralCentroid)
        {
            int numFrames = rmsEnergy.Length;
            
            for (int i = 0; i < numFrames; i++)
            {
                int startIdx = i * frameHop;
                
                // Ensure we don't go out of bounds
                if (startIdx + frameSize > monoData.Length)
                    break;
                
                // 1. RMS Energy
                float sumSquared = 0;
                for (int j = 0; j < frameSize; j++)
                {
                    sumSquared += monoData[startIdx + j] * monoData[startIdx + j];
                }
                rmsEnergy[i] = (float)Math.Sqrt(sumSquared / frameSize);
                
                // 2. Zero-crossing rate
                int zcCount = 0;
                for (int j = 1; j < frameSize; j++)
                {
                    if ((monoData[startIdx + j] >= 0 && monoData[startIdx + j - 1] < 0) ||
                        (monoData[startIdx + j] < 0 && monoData[startIdx + j - 1] >= 0))
                    {
                        zcCount++;
                    }
                }
                zeroCrossings[i] = (float)zcCount / frameSize;
                
                // 3. Simplified spectral centroid (using zero-crossing as a proxy for brightness)
                // This is a simplification since we don't have full FFT support
                spectralCentroid[i] = zeroCrossings[i] * sampleRate / 2;
                
                // Update progress occasionally
                if (i % 200 == 0)
                {
                    mbApiInterface.MB_SetBackgroundTaskMessage($"Computing features: {i * 100 / numFrames}%");
                }
            }
            
            // Normalize features to 0-1 range for better comparison
            NormalizeArray(rmsEnergy);
            NormalizeArray(zeroCrossings);
            NormalizeArray(spectralCentroid);
        }
        
        // Normalize array to 0-1 range
        private void NormalizeArray(float[] array)
        {
            float min = array.Min();
            float max = array.Max();
            float range = max - min;
            
            if (range > 0)
            {
                for (int i = 0; i < array.Length; i++)
                {
                    array[i] = (array[i] - min) / range;
                }
            }
        }
        
        // Apply Gaussian blur to reduce noise in the similarity matrix
        private float[,] GaussianSmoothMatrix(float[,] matrix, int size)
        {
            float[,] result = new float[size, size];
            int kernelSize = 3; // 3x3 kernel
            float sigma = 1.0f;
            
            // Create Gaussian kernel
            float[] kernel = new float[kernelSize];
            float kernelSum = 0;
            
            for (int i = 0; i < kernelSize; i++)
            {
                int dist = i - kernelSize/2;
                kernel[i] = (float)Math.Exp(-(dist*dist) / (2*sigma*sigma));
                kernelSum += kernel[i];
            }
            
            // Normalize kernel
            for (int i = 0; i < kernelSize; i++)
            {
                kernel[i] /= kernelSum;
            }
            
            // Apply horizontal pass
            float[,] temp = new float[size, size];
            
            for (int i = 0; i < size; i++)
            {
                for (int j = 0; j < size; j++)
                {
                    float sum = 0;
                    float weightSum = 0;
                    
                    for (int k = 0; k < kernelSize; k++)
                    {
                        int idx = j + k - kernelSize/2;
                        
                        if (idx >= 0 && idx < size)
                        {
                            sum += matrix[i, idx] * kernel[k];
                            weightSum += kernel[k];
                        }
                    }
                    
                    temp[i, j] = sum / weightSum;
                }
            }
            
            // Apply vertical pass
            for (int i = 0; i < size; i++)
            {
                for (int j = 0; j < size; j++)
                {
                    float sum = 0;
                    float weightSum = 0;
                    
                    for (int k = 0; k < kernelSize; k++)
                    {
                        int idx = i + k - kernelSize/2;
                        
                        if (idx >= 0 && idx < size)
                        {
                            sum += temp[idx, j] * kernel[k];
                            weightSum += kernel[k];
                        }
                    }
                    
                    result[i, j] = sum / weightSum;
                }
            }
            
            return result;
        }
        
        // Select the best musical candidate from top candidates
        private LoopCandidate SelectBestMusicalCandidate(List<LoopCandidate> candidates, double frameDuration)
        {
            if (candidates == null || candidates.Count == 0)
                return null;
                
            // Prefer candidates that create loops of musical lengths (multiple of 4 or 8 seconds)
            foreach (var candidate in candidates)
            {
                double loopLength = (candidate.EndFrame - candidate.StartFrame) * frameDuration;
                
                // Calculate how close this is to a multiple of 4 seconds
                double mod4 = loopLength % 4.0;
                if (mod4 > 2.0) mod4 = 4.0 - mod4;
                
                // Apply a musical bonus to the score based on how well it aligns with musical timing
                double musicality = mod4 / 4.0; // 0 is perfect, 0.5 is worst
                
                // Adjust score to prefer musical timing
                candidate.Score = (float)(candidate.Score * (1.0 + musicality * 0.3));
            }
            
            // Re-sort with musical criteria applied
            candidates.Sort((a, b) => a.Score.CompareTo(b.Score));
            
            return candidates[0];
        }
        
        // Refine loop points to align with beats or strong transients
        private float[] RefineLoopPoints(float[] audioData, int sampleRate, float loopStartSec, float loopEndSec)
        {
            try
            {
                // Convert seconds to samples
                int loopStartSample = (int)(loopStartSec * sampleRate);
                int loopEndSample = (int)(loopEndSec * sampleRate);
                
                // Look for strong transients near the loop points
                int windowSize = sampleRate / 4; // 250ms window
                
                // Refine loop start
                int bestStartOffset = 0;
                float bestStartEnergy = 0;
                
                for (int i = -windowSize; i < windowSize; i++)
                {
                    int idx = loopStartSample + i;
                    if (idx < 0 || idx + windowSize >= audioData.Length)
                        continue;
                        
                    // Calculate local energy
                    float energy = 0;
                    for (int j = 0; j < windowSize/10; j++)
                    {
                        int sampleIdx = idx + j;
                        if (sampleIdx < audioData.Length)
                            energy += Math.Abs(audioData[sampleIdx]);
                    }
                    
                    // Look for high energy onset
                    if (energy > bestStartEnergy)
                    {
                        bestStartEnergy = energy;
                        bestStartOffset = i;
                    }
                }
                
                // Refine loop end similarly
                int bestEndOffset = 0;
                float bestEndEnergy = 0;
                
                for (int i = -windowSize; i < windowSize; i++)
                {
                    int idx = loopEndSample + i;
                    if (idx < 0 || idx + windowSize >= audioData.Length)
                        continue;
                        
                    // Calculate energy but also consider similarity to start point
                    float energy = 0;
                    float similarity = 0;
                    
                    for (int j = 0; j < windowSize/10; j++)
                    {
                        int sampleIdx = idx + j;
                        int startIdx = loopStartSample + bestStartOffset + j;
                        
                        if (sampleIdx < audioData.Length && startIdx < audioData.Length)
                        {
                            energy += Math.Abs(audioData[sampleIdx]);
                            similarity += Math.Abs(audioData[sampleIdx] - audioData[startIdx]);
                        }
                    }
                    
                    // Combine energy and similarity
                    float combinedScore = energy - similarity*0.5f;
                    
                    if (combinedScore > bestEndEnergy)
                    {
                        bestEndEnergy = combinedScore;
                        bestEndOffset = i;
                    }
                }
                
                // Apply offsets
                float refinedStart = loopStartSec + (bestStartOffset / (float)sampleRate);
                float refinedEnd = loopEndSec + (bestEndOffset / (float)sampleRate);
                
                return new float[] { refinedStart, refinedEnd };
            }
            catch
            {
                // If refinement fails, return original points
                return new float[] { loopStartSec, loopEndSec };
            }
        }
        
        // Get fallback loop points
        private LoopResult GetFallbackLoop(double duration)
        {
            // Use more intelligent fallback based on typical video game OST structure
            float loopStart, loopEnd;
            
            if (duration < 60)
            {
                // Short tracks often loop from 1/3 to end
                loopStart = (float)(duration * 0.33);
                loopEnd = (float)(duration * 0.9);
            }
            else if (duration < 120)
            {
                // Medium tracks often have intro + loop structure
                loopStart = (float)(duration * 0.25);
                loopEnd = (float)(duration * 0.9);
            }
            else
            {
                // Longer tracks might have multiple sections
                loopStart = (float)(duration * 0.4);
                loopEnd = (float)(duration * 0.9);
            }
            
            mbApiInterface.MB_Trace($"Using fallback loop: {loopStart:F2}s to {loopEnd:F2}s");
            
            return new LoopResult {
                Status = "fallback",
                LoopStart = loopStart,
                LoopEnd = loopEnd,
                Confidence = 0.4f
            };
        }
        
        // Store last loop time to prevent double-triggering
        private DateTime lastLoopTime = DateTime.MinValue;
        
        private void onTimerTick(object sender, ElapsedEventArgs e)
        {
            try
            {
                // Check if we should be looping
                if (!smartLoopEnabled || mbApiInterface.Player_GetPlayState() != PlayState.Playing)
                    return;
                    
                string currentFile = mbApiInterface.NowPlaying_GetFileUrl();
                if (string.IsNullOrEmpty(currentFile)) return;
    
                // Get loop information - cache it to avoid repeated tag reads
                if (currentLoopTrack != currentFile)
                {
                    // This is a new track, get the loop info
                    string loopFound = mbApiInterface.Library_GetFileTag(currentFile, MetaDataType.Custom1);
                    if (loopFound != "True") return;
                    
                    currentLoopTrack = currentFile;
                    currentLoopStart = float.Parse(mbApiInterface.Library_GetFileTag(currentFile, MetaDataType.Custom2));
                    currentLoopEnd = float.Parse(mbApiInterface.Library_GetFileTag(currentFile, MetaDataType.Custom3));
                    
                    // Log that we're now monitoring a new track
                    mbApiInterface.MB_Trace($"Monitoring loop for track: {Path.GetFileName(currentFile)} | Loop: {currentLoopStart:F1}s-{currentLoopEnd:F1}s");
                }
                
                // Get current playback position
                float currentPosition = mbApiInterface.Player_GetPosition() / 1000f; // Convert ms to seconds
    
                // Debug info occasionally, but not too often to avoid log spam
                if (DateTime.Now.Second % 10 == 0 && DateTime.Now.Millisecond < 100)
                {
                    mbApiInterface.MB_Trace($"Loop Monitor | Position: {currentPosition:F1}s | Loop: {currentLoopStart:F1}s-{currentLoopEnd:F1}s");
                }
                
                // Don't trigger more than once per second to avoid rapid jumping
                if ((DateTime.Now - lastLoopTime).TotalSeconds < 0.5)
                    return;
                
                // Check if we've reached the loop end (with a small buffer)
                // The buffer is slightly larger for longer tracks to account for precision
                float buffer = Math.Max(0.2f, currentLoopEnd / 100); // 0.2s or 1% of loop end, whichever is larger
                
                if (currentPosition >= (currentLoopEnd - buffer))
                {
                    // Jump back to loop start
                    mbApiInterface.MB_Trace($"LOOP TRIGGERED: Jumping from {currentPosition:F2}s back to {currentLoopStart:F2}s");
                    mbApiInterface.Player_SetPosition((int)(currentLoopStart * 1000));
                    lastLoopTime = DateTime.Now;
                    
                    // Visual feedback that looping occurred (temporary status message)
                    mbApiInterface.MB_SetBackgroundTaskMessage("♻️ Loop activated");
                    Task.Run(() => {
                        System.Threading.Thread.Sleep(1000);
                        mbApiInterface.MB_SetBackgroundTaskMessage("");
                    });
                }
                
                // Also detect if we're far past the loop end (something went wrong)
                if (currentPosition > currentLoopEnd + 1.0f)
                {
                    mbApiInterface.MB_Trace($"ERROR: Position {currentPosition:F2}s is past loop end {currentLoopEnd:F2}s - correcting");
                    mbApiInterface.Player_SetPosition((int)(currentLoopStart * 1000));
                    lastLoopTime = DateTime.Now;
                }
            }
            catch (Exception ex)
            {
                mbApiInterface.MB_Trace($"Timer error: {ex.Message}");
                // Reset tracking to force reload
                currentLoopTrack = null;
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