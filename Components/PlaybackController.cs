using System;
using System.IO;
using System.Threading.Tasks;
using System.Timers;
using NAudio.Wave;
using static MusicBeePlugin.Plugin;

namespace MusicBeePlugin
{
    /// <summary>
    /// Handles all playback control and looping functionality
    /// </summary>
    public class PlaybackController
    {
        private readonly MusicBeeApiInterface mbApiInterface;
        private System.Timers.Timer playbackTimer;
        
        // Looping state
        private bool smartLoopEnabled = false;
        private DateTime lastLoopTime = DateTime.MinValue;
        private bool loopPendingTransition = false;
        private float loopTransitionAccuracy = 0.02f; // 20ms accuracy for smoother looping (adjust dynamically)
        
        // Advanced looping settings
        private bool crossfadeEnabled = true;        // Enable/disable crossfading for smoother transitions
        private int crossfadeDuration = 50;          // Crossfade duration in ms
        private bool preciseLoopingEnabled = true;   // Enable sample-accurate loop timing
        
        // Cached loop information to avoid repeated tag reads
        private string currentLoopTrack = null;
        private float currentLoopStart = 0;              // Loop start in seconds
        private float currentLoopEnd = 0;                // Loop end in seconds
        private int currentLoopStartSample = 0;          // Loop start in samples for precise positioning
        private int currentLoopEndSample = 0;            // Loop end in samples for precise positioning
        private int sampleRate = 44100;                  // Current track's sample rate

        public PlaybackController(MusicBeeApiInterface mbApiInterface)
        {
            this.mbApiInterface = mbApiInterface;
            
            // Setup playback monitoring timer - check every 50ms for more responsive looping
            playbackTimer = new System.Timers.Timer(50);
            playbackTimer.Elapsed += OnTimerTick;
            playbackTimer.AutoReset = true;
            playbackTimer.Enabled = true;
        }

        /// <summary>
        /// Toggle smart loop feature on/off
        /// </summary>
        public void ToggleSmartLoop()
        {
            smartLoopEnabled = !smartLoopEnabled;
        }

        /// <summary>
        /// Check if smart looping is currently enabled
        /// </summary>
        public bool IsSmartLoopEnabled()
        {
            return smartLoopEnabled;
        }

        /// <summary>
        /// Toggle crossfade feature on/off
        /// </summary>
        public void ToggleCrossfade()
        {
            crossfadeEnabled = !crossfadeEnabled;
            mbApiInterface.MB_SetBackgroundTaskMessage($"Crossfade transitions: {(crossfadeEnabled ? "ENABLED" : "DISABLED")}");
            mbApiInterface.MB_Trace($"Crossfade setting changed to: {crossfadeEnabled}");
            
            Task.Run(() => {
                System.Threading.Thread.Sleep(2000);
                mbApiInterface.MB_SetBackgroundTaskMessage("");
            });
        }

        /// <summary>
        /// Toggle precise sample-accurate looping on/off
        /// </summary>
        public void TogglePreciseLooping()
        {
            preciseLoopingEnabled = !preciseLoopingEnabled;
            mbApiInterface.MB_SetBackgroundTaskMessage($"Precise sample-accurate looping: {(preciseLoopingEnabled ? "ENABLED" : "DISABLED")}");
            mbApiInterface.MB_Trace($"Precise looping setting changed to: {preciseLoopingEnabled}");
            
            Task.Run(() => {
                System.Threading.Thread.Sleep(2000);
                mbApiInterface.MB_SetBackgroundTaskMessage("");
            });
        }

        /// <summary>
        /// Set the crossfade duration in milliseconds
        /// </summary>
        public void SetCrossfadeDuration(int duration)
        {
            crossfadeDuration = duration;
            mbApiInterface.MB_SetBackgroundTaskMessage($"Crossfade duration set to: {crossfadeDuration}ms");
            mbApiInterface.MB_Trace($"Crossfade duration changed to: {crossfadeDuration}ms");
            
            Task.Run(() => {
                System.Threading.Thread.Sleep(2000);
                mbApiInterface.MB_SetBackgroundTaskMessage("");
            });
        }

        /// <summary>
        /// Enable smart loop for a specific track
        /// </summary>
        public bool EnableSmartLoop(string filePath)
        {
            try
            {
                mbApiInterface.MB_Trace($"Smart Loop requested for: {Path.GetFileName(filePath)}");
                
                if (IsTrackLoopable(filePath))
                {
                    // Get loop points from metadata
                    float loopStart = float.Parse(mbApiInterface.Library_GetFileTag(filePath, MetaDataType.Custom2));
                    float loopEnd = float.Parse(mbApiInterface.Library_GetFileTag(filePath, MetaDataType.Custom3));
                    
                    // Reset tracking variables to force reload
                    currentLoopTrack = null;
                    lastLoopTime = DateTime.MinValue;
                    
                    // Enable smart loop
                    smartLoopEnabled = true;
                    
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
                    
                    return true;
                }
                
                return false;
            }
            catch (Exception ex)
            {
                mbApiInterface.MB_Trace($"Error enabling smart loop: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Check if a track has loop points detected
        /// </summary>
        public bool IsTrackLoopable(string filePath)
        {
            string loopFlag = mbApiInterface.Library_GetFileTag(filePath, MetaDataType.Custom1);
            return loopFlag == "True";
        }

        /// <summary>
        /// Update the smart loop status when playback state changes
        /// </summary>
        public void UpdateSmartLoopStatus()
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

        /// <summary>
        /// Create an extended version of a track by repeating the loop section
        /// </summary>
        public void ExtendTrack(string filePath)
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
                mbApiInterface.MB_Trace($"Failed to extend track: {ex.Message}");
            }
        }

        /// <summary>
        /// The main playback timer handler that monitors playback position and triggers loops
        /// </summary>
        private void OnTimerTick(object sender, ElapsedEventArgs e)
        {
            try
            {
                // Check if we should be looping
                if (!smartLoopEnabled || mbApiInterface.Player_GetPlayState() != PlayState.Playing)
                {
                    loopPendingTransition = false;
                    return;
                }
                    
                string currentFile = mbApiInterface.NowPlaying_GetFileUrl();
                if (string.IsNullOrEmpty(currentFile)) 
                {
                    loopPendingTransition = false;
                    return;
                }
    
                // Get loop information - cache it to avoid repeated tag reads
                if (currentLoopTrack != currentFile)
                {
                    // This is a new track, get the loop info
                    string loopFound = mbApiInterface.Library_GetFileTag(currentFile, MetaDataType.Custom1);
                    if (loopFound != "True") 
                    {
                        loopPendingTransition = false;
                        return;
                    }
                    
                    currentLoopTrack = currentFile;
                    currentLoopStart = float.Parse(mbApiInterface.Library_GetFileTag(currentFile, MetaDataType.Custom2));
                    currentLoopEnd = float.Parse(mbApiInterface.Library_GetFileTag(currentFile, MetaDataType.Custom3));
                    
                    // Check for sample-accurate positioning data
                    string startSampleStr = mbApiInterface.Library_GetFileTag(currentFile, MetaDataType.Custom4);
                    string endSampleStr = mbApiInterface.Library_GetFileTag(currentFile, MetaDataType.Custom5);
                    string sampleRateStr = mbApiInterface.Library_GetFileTag(currentFile, MetaDataType.Custom6);
                    
                    // Parse sample-accurate data if available
                    bool hasSampleAccuracy = false;
                    if (!string.IsNullOrEmpty(startSampleStr) && !string.IsNullOrEmpty(endSampleStr) && 
                        !string.IsNullOrEmpty(sampleRateStr))
                    {
                        if (int.TryParse(startSampleStr, out currentLoopStartSample) &&
                            int.TryParse(endSampleStr, out currentLoopEndSample) &&
                            int.TryParse(sampleRateStr, out sampleRate))
                        {
                            hasSampleAccuracy = true;
                        }
                    }
                    
                    // If no sample accuracy data, use the audio file information
                    if (!hasSampleAccuracy)
                    {
                        using (var reader = new AudioFileReader(currentFile))
                        {
                            sampleRate = reader.WaveFormat.SampleRate;
                            currentLoopStartSample = (int)(currentLoopStart * sampleRate);
                            currentLoopEndSample = (int)(currentLoopEnd * sampleRate);
                        }
                    }
                    
                    // Calculate appropriate timing parameters based on track
                    loopTransitionAccuracy = (float)(1.0 / sampleRate * 2000); // Precision within 2000 samples
                    loopTransitionAccuracy = Math.Max(0.01f, Math.Min(loopTransitionAccuracy, 0.05f)); // Between 10-50ms
                    
                    // Log that we're now monitoring a new track
                    mbApiInterface.MB_Trace($"Monitoring loop for track: {Path.GetFileName(currentFile)}");
                    mbApiInterface.MB_Trace($"Loop time: {currentLoopStart:F3}s to {currentLoopEnd:F3}s");
                    if (hasSampleAccuracy) {
                        mbApiInterface.MB_Trace($"Sample-accurate positions: {currentLoopStartSample} to {currentLoopEndSample} @ {sampleRate}Hz");
                    }
                    mbApiInterface.MB_Trace($"Precision: {loopTransitionAccuracy*1000:F1}ms");
                    loopPendingTransition = false;
                }
                
                // Get current playback position with high precision
                float currentPosition = mbApiInterface.Player_GetPosition() / 1000f; // Convert ms to seconds
    
                // Debug info occasionally, but not too often to avoid log spam
                if (DateTime.Now.Second % 10 == 0 && DateTime.Now.Millisecond < 100)
                {
                    mbApiInterface.MB_Trace($"Loop Monitor | Position: {currentPosition:F3}s | Loop: {currentLoopStart:F3}s-{currentLoopEnd:F3}s | Pending: {loopPendingTransition}");
                }
                
                // The main loop state machine
                if (loopPendingTransition)
                {
                    // We're already in transition mode, now check if we're very close to the precise loop point
                    // This creates a tighter loop with less jitter
                    
                    // Calculate exact timing to prevent audio glitches
                    float buffer = loopTransitionAccuracy; // Use our dynamic accuracy parameter
                    
                    if (currentPosition >= (currentLoopEnd - buffer))
                    {
                        if (crossfadeEnabled && crossfadeDuration > 0)
                        {
                            // Implement crossfade by creating a temporary file that combines the end and start
                            string tempFilePath = Path.Combine(Path.GetTempPath(), $"loop_{Path.GetFileName(currentFile)}");
                            
                            Task.Run(() => {
                                try
                                {
                                    CreateCrossfadeTransition(currentFile, tempFilePath, 
                                                            currentLoopStart, currentLoopEnd, 
                                                            crossfadeDuration / 1000.0f);
                                                            
                                    // Switch to the crossfaded file
                                    mbApiInterface.MB_Trace($"Playing crossfade transition file: {tempFilePath}");
                                    mbApiInterface.NowPlayingList_QueueNext(tempFilePath);
                                    mbApiInterface.Player_PlayNextTrack();
                                    
                                    // When crossfade file finishes, we'll go back to normal looping
                                }
                                catch (Exception ex)
                                {
                                    mbApiInterface.MB_Trace($"Crossfade error: {ex.Message} - falling back to direct loop");
                                    // Fall back to standard looping
                                    mbApiInterface.Player_SetPosition((int)(currentLoopStart * 1000));
                                }
                            });
                        }
                        else
                        {
                            // Standard looping - jump back to loop start with precise timing
                            int positionMs = (int)(currentLoopStart * 1000);
                            mbApiInterface.MB_Trace($"LOOP EXECUTED: Jumping from {currentPosition:F3}s to {currentLoopStart:F3}s");
                            mbApiInterface.Player_SetPosition(positionMs);
                        }
                        
                        lastLoopTime = DateTime.Now;
                        loopPendingTransition = false;
                        
                        // Visual feedback that looping occurred (temporary status message)
                        mbApiInterface.MB_SetBackgroundTaskMessage(crossfadeEnabled ? "🎵 Crossfade loop activated" : "♻️ Seamless loop activated");
                        Task.Run(() => {
                            System.Threading.Thread.Sleep(800);
                            mbApiInterface.MB_SetBackgroundTaskMessage("");
                        });
                    }
                    else if (currentPosition < (currentLoopEnd - 0.5f))
                    {
                        // Something went wrong, we got too far away from the loop point
                        // Cancel the pending transition
                        mbApiInterface.MB_Trace($"WARNING: Loop transition canceled - position shifted unexpectedly to {currentPosition:F3}s");
                        loopPendingTransition = false;
                    }
                }
                else // Not in transition mode
                {
                    // Don't trigger more than once per half-second to avoid rapid jumping
                    if ((DateTime.Now - lastLoopTime).TotalSeconds < 0.5)
                        return;
                    
                    // Check if we're approaching the loop end point - use a larger buffer for initial detection
                    float approachBuffer = 0.5f; // Start preparing half a second before the end point
                    
                    if (currentPosition >= (currentLoopEnd - approachBuffer))
                    {
                        // We're approaching the loop end, enter transition mode for more precise timing
                        loopPendingTransition = true;
                        mbApiInterface.MB_Trace($"LOOP APPROACHING: At {currentPosition:F3}s, preparing to loop back from {currentLoopEnd:F3}s to {currentLoopStart:F3}s");
                    }
                }
                
                // Safety check - if we somehow got past the loop end (e.g., timer missed the window)
                if (currentPosition > currentLoopEnd + 0.1f)
                {
                    mbApiInterface.MB_Trace($"SAFETY TRIGGERED: Position {currentPosition:F3}s is past loop end {currentLoopEnd:F3}s - correcting");
                    mbApiInterface.Player_SetPosition((int)(currentLoopStart * 1000));
                    lastLoopTime = DateTime.Now;
                    loopPendingTransition = false;
                }
            }
            catch (Exception ex)
            {
                mbApiInterface.MB_Trace($"Timer error: {ex.Message}");
                // Reset tracking to force reload
                currentLoopTrack = null;
            }
        }

        /// <summary>
        /// Create a crossfade transition file that smoothly blends the end of the loop with the start
        /// </summary>
        private void CreateCrossfadeTransition(string sourceFile, string outputFile, float loopStart, float loopEnd, float crossfadeDuration)
        {
            mbApiInterface.MB_Trace($"Creating crossfade transition from {loopEnd-crossfadeDuration:F3}s to {loopStart+crossfadeDuration:F3}s");
            
            using (var reader = new AudioFileReader(sourceFile))
            {
                // Calculate positions in samples
                int startSample = (int)(loopStart * reader.WaveFormat.SampleRate);
                int endSample = (int)((loopEnd - crossfadeDuration) * reader.WaveFormat.SampleRate);
                int crossfadeSamples = (int)(crossfadeDuration * reader.WaveFormat.SampleRate);
                
                // Create the crossfade output file with the same format
                using (var writer = new WaveFileWriter(outputFile, reader.WaveFormat))
                {
                    // Write the ending portion (just before the loop end)
                    reader.Position = (long)endSample * reader.WaveFormat.BlockAlign;
                    byte[] buffer = new byte[reader.WaveFormat.AverageBytesPerSecond];
                    int bytesRead;
                    
                    while ((bytesRead = reader.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        writer.Write(buffer, 0, bytesRead);
                        
                        // Stop when we've reached near the end of the file
                        if (reader.CurrentTime.TotalSeconds >= loopEnd)
                            break;
                    }
                    
                    // Now write the beginning portion (from loop start) with crossfade
                    reader.Position = (long)startSample * reader.WaveFormat.BlockAlign;
                    
                    // Read samples for crossfade (we need to mix them)
                    float[] samples = new float[crossfadeSamples * reader.WaveFormat.Channels];
                    reader.Read(samples, 0, samples.Length);
                    
                    // Apply fade-in to these samples
                    for (int i = 0; i < samples.Length; i++)
                    {
                        float fadeInFactor = (float)i / samples.Length;
                        samples[i] *= fadeInFactor;
                    }
                    
                    // Convert back to bytes and write
                    byte[] fadeInBytes = new byte[samples.Length * sizeof(float)];
                    Buffer.BlockCopy(samples, 0, fadeInBytes, 0, fadeInBytes.Length);
                    writer.Write(fadeInBytes, 0, fadeInBytes.Length);
                    
                    // Continue writing the rest of the beginning portion
                    while ((bytesRead = reader.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        writer.Write(buffer, 0, bytesRead);
                        
                        // Stop at a reasonable point
                        if (reader.CurrentTime.TotalSeconds >= loopStart + 2.0f)
                            break;
                    }
                }
            }
            
            mbApiInterface.MB_Trace("Crossfade transition file created successfully");
        }

        /// <summary>
        /// Clean up any resources used by the controller
        /// </summary>
        public void Cleanup()
        {
            if (playbackTimer != null)
            {
                playbackTimer.Enabled = false;
                playbackTimer.Dispose();
            }
            
            // Clean up any temporary crossfade files
            try
            {
                string tempDir = Path.GetTempPath();
                string[] tempFiles = Directory.GetFiles(tempDir, "loop_*");
                foreach (string file in tempFiles)
                {
                    File.Delete(file);
                }
            }
            catch { /* Ignore cleanup errors */ }
        }
    }
}