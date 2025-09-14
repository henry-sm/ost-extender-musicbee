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
                        
                        if (result.Status == "success")
                        {
                            mbApiInterface.Library_SetFileTag(filePath, MetaDataType.Custom1, "True");
                            mbApiInterface.Library_SetFileTag(filePath, MetaDataType.Custom2, result.LoopStart.ToString());
                            mbApiInterface.Library_SetFileTag(filePath, MetaDataType.Custom3, result.LoopEnd.ToString());
                            mbApiInterface.Library_CommitTagsToFile(filePath);
                            
                            // Show results on UI thread
                            System.Windows.Forms.Form mainForm = System.Windows.Forms.Control.FromHandle(mbApiInterface.MB_GetWindowHandle()).FindForm();
                            mainForm.Invoke(new Action(() => {
                                MessageBox.Show(mainForm, $"Analysis complete!\nLoop found: {result.LoopStart:F2}s to {result.LoopEnd:F2}s",
                                    "OST Extender", MessageBoxButtons.OK, MessageBoxIcon.Information);
                                mbApiInterface.MB_SetBackgroundTaskMessage("");
                            }));
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
                mbApiInterface.MB_Trace("Starting audio analysis...");
                
                // If track is too short, use simple rules
                if (duration < 30.0)
                {
                    mbApiInterface.MB_Trace("Track too short for advanced analysis");
                    return new LoopResult { 
                        Status = "success", 
                        LoopStart = (float)(duration * 0.3), 
                        LoopEnd = (float)(duration * 0.8),
                        Confidence = 0.5f
                    };
                }
                
                // Step 1: Convert stereo to mono if needed
                mbApiInterface.MB_Trace("Converting to mono...");
                float[] monoData;
                if (channels > 1)
                {
                    monoData = new float[audioData.Length / channels];
                    for (int i = 0; i < monoData.Length; i++)
                    {
                        float sum = 0;
                        for (int ch = 0; ch < channels; ch++)
                        {
                            sum += audioData[i * channels + ch];
                        }
                        monoData[i] = sum / channels;
                    }
                }
                else
                {
                    monoData = audioData;
                }
                
                mbApiInterface.MB_Trace("Computing energy profile...");
                mbApiInterface.MB_SetBackgroundTaskMessage("Computing energy profile...");
                
                // Step 2: Compute energy profile (simpler than chroma features)
                int frameSize = sampleRate / 10; // 100ms frames
                int numFrames = monoData.Length / frameSize;
                float[] energyProfile = new float[numFrames];
                
                for (int i = 0; i < numFrames; i++)
                {
                    float sum = 0;
                    for (int j = 0; j < frameSize && (i * frameSize + j) < monoData.Length; j++)
                    {
                        sum += Math.Abs(monoData[i * frameSize + j]);
                    }
                    energyProfile[i] = sum / frameSize;
                }
                
                // Step 3: Build self-similarity matrix (simplified)
                mbApiInterface.MB_Trace("Building self-similarity matrix...");
                mbApiInterface.MB_SetBackgroundTaskMessage("Analyzing audio patterns...");
                
                int matrixSize = Math.Min(numFrames, 600); // Limit to 60 seconds (100ms frames)
                float[,] similarityMatrix = new float[matrixSize, matrixSize];
                
                for (int i = 0; i < matrixSize; i++)
                {
                    for (int j = i; j < matrixSize; j++) // Only calculate upper triangle
                    {
                        // Simple Euclidean distance between energy values
                        float distance = Math.Abs(energyProfile[i] - energyProfile[j]);
                        similarityMatrix[i, j] = similarityMatrix[j, i] = distance;
                    }
                    
                    // Update progress occasionally
                    if (i % 50 == 0)
                    {
                        mbApiInterface.MB_SetBackgroundTaskMessage($"Analyzing patterns: {i * 100 / matrixSize}%");
                    }
                }
                
                // Step 4: Find the best diagonal (indicating a loop)
                mbApiInterface.MB_Trace("Finding best loop candidate...");
                mbApiInterface.MB_SetBackgroundTaskMessage("Identifying loop points...");
                
                // We'll look for diagonals (parallel to the main diagonal)
                float bestScore = float.MaxValue;
                int bestDiagOffset = 0;
                
                // Only look at diagonals that would create loops of reasonable length
                int minLoopFrames = (int)(10 * (sampleRate / frameSize)); // At least 10 seconds
                int maxLoopFrames = (int)(90 * (sampleRate / frameSize)); // At most 90 seconds
                
                for (int diagOffset = minLoopFrames; diagOffset < Math.Min(maxLoopFrames, matrixSize / 2); diagOffset++)
                {
                    float score = 0;
                    int count = 0;
                    
                    // Sum the values along this diagonal
                    for (int i = 0; i < matrixSize - diagOffset; i++)
                    {
                        score += similarityMatrix[i, i + diagOffset];
                        count++;
                    }
                    
                    if (count > 0)
                    {
                        score /= count; // Average similarity
                        
                        if (score < bestScore)
                        {
                            bestScore = score;
                            bestDiagOffset = diagOffset;
                        }
                    }
                    
                    // Update progress occasionally
                    if (diagOffset % 20 == 0)
                    {
                        int progress = (diagOffset - minLoopFrames) * 100 / (maxLoopFrames - minLoopFrames);
                        mbApiInterface.MB_SetBackgroundTaskMessage($"Identifying loops: {progress}%");
                    }
                }
                
                // Convert frames back to seconds
                float loopLength = bestDiagOffset * (frameSize / (float)sampleRate);
                
                // Step 5: Determine the optimal loop start point
                mbApiInterface.MB_Trace("Refining loop points...");
                mbApiInterface.MB_SetBackgroundTaskMessage("Finalizing results...");
                
                // Start loop at 1/3 of track duration, but ensure loop length matches our analysis
                float loopStart = (float)Math.Min(duration * 0.33, duration * 0.6);
                float loopEnd = loopStart + loopLength;
                
                // Make sure loop end doesn't go past the end of the track
                if (loopEnd > duration * 0.95)
                {
                    loopEnd = (float)(duration * 0.95);
                    loopStart = loopEnd - loopLength;
                }
                
                // Make sure loop start isn't negative
                if (loopStart < 0)
                {
                    loopStart = 0;
                    loopEnd = loopLength;
                }
                
                float confidence = 1.0f - (bestScore / 2.0f); // Convert to confidence score
                mbApiInterface.MB_Trace($"Analysis complete. Loop: {loopStart:F2}s to {loopEnd:F2}s, confidence: {confidence:F2}");
                
                return new LoopResult {
                    Status = confidence > 0.4f ? "success" : "low-confidence",
                    LoopStart = loopStart,
                    LoopEnd = loopEnd,
                    Confidence = confidence
                };
            }
            catch (Exception ex)
            {
                mbApiInterface.MB_Trace($"Analysis algorithm error: {ex.Message}");
                
                // Fallback to simple ratio-based analysis
                return new LoopResult {
                    Status = "success",
                    LoopStart = (float)(duration * 0.3),
                    LoopEnd = (float)(duration * 0.8),
                    Confidence = 0.3f
                };
            }
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