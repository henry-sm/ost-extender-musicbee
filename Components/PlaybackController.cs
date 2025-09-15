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
        private DateTime lastPositionCheckTime = DateTime.MinValue; // For high-precision timing
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
                
                // Simple and robust approach: just get the current position directly
                int rawPositionMs = mbApiInterface.Player_GetPosition();
                float currentPosition = rawPositionMs / 1000.0f; // Convert ms to seconds
    
                // Debug info occasionally, but not too often to avoid log spam
                if (DateTime.Now.Second % 5 == 0 && DateTime.Now.Millisecond < 100)
                {
                    mbApiInterface.MB_Trace($"Loop Monitor | Position: {currentPosition:F3}s | Loop: {currentLoopStart:F3}s-{currentLoopEnd:F3}s");
                }
                
                // SIMPLIFIED LOOP LOGIC
                // Check if we're past the loop end point (or very close to it)
                if (currentPosition >= (currentLoopEnd - 0.05f)) // Within 50ms of end point
                {
                    // Don't trigger more than once per half-second to avoid rapid jumping
                    if ((DateTime.Now - lastLoopTime).TotalSeconds < 0.5)
                        return;
                        
                    // Standard, direct looping - jump back to loop start
                    int loopPositionMs = (int)(currentLoopStart * 1000);
                    mbApiInterface.MB_Trace($"LOOP EXECUTED: Jumping from {currentPosition:F3}s to {currentLoopStart:F3}s");
                    
                    // Execute the loop jump directly without delay
                    mbApiInterface.Player_SetPosition(loopPositionMs);
                    
                    // Set flag to avoid multiple loops in rapid succession
                    lastLoopTime = DateTime.Now;
                    loopPendingTransition = false;
                    
                    // Visual feedback
                    mbApiInterface.MB_SetBackgroundTaskMessage("♻️ Loop activated");
                    Task.Run(() => {
                        System.Threading.Thread.Sleep(800);
                        mbApiInterface.MB_SetBackgroundTaskMessage("");
                    });
                }
                
                // Enhanced safety check for when we miss the loop point completely
                if (currentPosition > currentLoopEnd + 0.1f) // More generous threshold (100ms)
                {
                    // Don't trigger more than once per half-second to avoid rapid jumping
                    if ((DateTime.Now - lastLoopTime).TotalSeconds < 0.5)
                        return;
                        
                    mbApiInterface.MB_Trace($"SAFETY TRIGGERED: Position {currentPosition:F3}s is past loop end {currentLoopEnd:F3}s - correcting");
                    
                    // Simple direct approach - just go back to the loop start
                    int safetyLoopMs = (int)(currentLoopStart * 1000);
                    
                    mbApiInterface.Player_SetPosition(safetyLoopMs);
                    lastLoopTime = DateTime.Now;
                    loopPendingTransition = false;
                    
                    // Visual feedback for safety loop
                    mbApiInterface.MB_SetBackgroundTaskMessage("⚠️ Loop recovered");
                    Task.Run(() => {
                        System.Threading.Thread.Sleep(800);
                        mbApiInterface.MB_SetBackgroundTaskMessage("");
                    });
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
        /// using improved sample-accurate processing for smoother transitions
        /// </summary>
        private void CreateCrossfadeTransition(string sourceFile, string outputFile, float loopStart, float loopEnd, float crossfadeDuration)
        {
            mbApiInterface.MB_Trace($"Creating enhanced crossfade transition from {loopEnd-crossfadeDuration:F3}s to {loopStart+crossfadeDuration:F3}s");
            
            using (var reader = new AudioFileReader(sourceFile))
            {
                // Look for stored sample-accurate positions if available
                int startSample = 0;
                int endSample = 0;
                int actualSampleRate = reader.WaveFormat.SampleRate;
                
                string startSampleStr = mbApiInterface.Library_GetFileTag(sourceFile, MetaDataType.Custom4);
                string endSampleStr = mbApiInterface.Library_GetFileTag(sourceFile, MetaDataType.Custom5);
                string sampleRateStr = mbApiInterface.Library_GetFileTag(sourceFile, MetaDataType.Custom6);
                
                // Use sample-accurate positions if available
                if (!string.IsNullOrEmpty(startSampleStr) && !string.IsNullOrEmpty(endSampleStr) && 
                    !string.IsNullOrEmpty(sampleRateStr))
                {
                    if (int.TryParse(startSampleStr, out startSample) &&
                        int.TryParse(endSampleStr, out endSample) &&
                        int.TryParse(sampleRateStr, out int storedSampleRate))
                    {
                        mbApiInterface.MB_Trace("Using sample-accurate positions for precise crossfade");
                        
                        // Adjust if stored sample rate doesn't match actual file
                        if (storedSampleRate != actualSampleRate)
                        {
                            float ratio = (float)actualSampleRate / storedSampleRate;
                            startSample = (int)(startSample * ratio);
                            endSample = (int)(endSample * ratio);
                        }
                    }
                    else
                    {
                        // Fall back to time-based positions if parsing fails
                        startSample = (int)(loopStart * actualSampleRate);
                        endSample = (int)(loopEnd * actualSampleRate);
                    }
                }
                else
                {
                    // Use time-based positions if no sample positions stored
                    startSample = (int)(loopStart * actualSampleRate);
                    endSample = (int)(loopEnd * actualSampleRate);
                }
                
                // Calculate crossfade parameters
                int crossfadeSamples = (int)(crossfadeDuration * actualSampleRate);
                int preCrossfadeEndSample = endSample - crossfadeSamples;
                int channels = reader.WaveFormat.Channels;
                
                // Create the crossfade output file with the same format
                using (var writer = new WaveFileWriter(outputFile, reader.WaveFormat))
                {
                    // Read the ending portion into memory for better processing
                    reader.Position = (long)preCrossfadeEndSample * reader.WaveFormat.BlockAlign;
                    int endPortionSamples = crossfadeSamples * 2; // Include crossfade region + extra
                    float[] endingPortion = new float[endPortionSamples * channels];
                    int samplesRead = reader.Read(endingPortion, 0, endingPortion.Length);
                    
                    // Read the beginning portion into memory
                    reader.Position = (long)startSample * reader.WaveFormat.BlockAlign;
                    int beginPortionSamples = crossfadeSamples * 2; // Include crossfade region + extra
                    float[] beginningPortion = new float[beginPortionSamples * channels];
                    int beginSamplesRead = reader.Read(beginningPortion, 0, beginningPortion.Length);
                    
                    // Calculate the optimal overlap alignment by finding the best matching point
                    int bestOffset = 0;
                    float bestCorrelation = float.MinValue;
                    
                    // Try different alignment offsets (±10% of crossfade length)
                    int maxOffset = crossfadeSamples / 10 * channels;
                    
                    for (int offset = -maxOffset; offset <= maxOffset; offset += channels)
                    {
                        float correlation = 0;
                        int correlationPoints = 0;
                        
                        // Compare a small portion of the audio (for performance)
                        int compareSize = Math.Min(crossfadeSamples * channels / 4, 2000);
                        
                        for (int i = 0; i < compareSize; i += channels)
                        {
                            int endIdx = crossfadeSamples * channels + offset + i;
                            int beginIdx = i;
                            
                            if (endIdx >= 0 && endIdx < samplesRead && beginIdx < beginSamplesRead)
                            {
                                for (int ch = 0; ch < channels; ch++)
                                {
                                    // Calculate correlation (product of samples)
                                    correlation += endingPortion[endIdx + ch] * beginningPortion[beginIdx + ch];
                                    correlationPoints++;
                                }
                            }
                        }
                        
                        if (correlationPoints > 0)
                        {
                            correlation /= correlationPoints;
                            if (correlation > bestCorrelation)
                            {
                                bestCorrelation = correlation;
                                bestOffset = offset;
                            }
                        }
                    }
                    
                    mbApiInterface.MB_Trace($"Best crossfade alignment offset: {bestOffset} samples (correlation: {bestCorrelation:F4})");
                    
                    // Write the first part of the ending portion (before crossfade)
                    for (int i = 0; i < crossfadeSamples * channels; i++)
                    {
                        writer.WriteSample(endingPortion[i]);
                    }
                    
                    // Create the crossfaded region with optimal alignment
                    for (int i = 0; i < crossfadeSamples * channels; i++)
                    {
                        // Calculate fade factors with smooth curve (cubic)
                        float progress = (float)i / (crossfadeSamples * channels);
                        float fadeOutFactor = 1.0f - (progress * progress * (3 - 2 * progress)); // Smooth cubic curve
                        float fadeInFactor = progress * progress * (3 - 2 * progress); // Smooth cubic curve
                        
                        // Apply crossfade with alignment offset
                        int endIdx = crossfadeSamples * channels + i;
                        int beginIdx = i + bestOffset;
                        
                        if (endIdx < samplesRead && beginIdx >= 0 && beginIdx < beginSamplesRead)
                        {
                            float blended = (endingPortion[endIdx] * fadeOutFactor) + 
                                           (beginningPortion[beginIdx] * fadeInFactor);
                            writer.WriteSample(blended);
                        }
                        else if (endIdx < samplesRead)
                        {
                            writer.WriteSample(endingPortion[endIdx] * fadeOutFactor);
                        }
                        else if (beginIdx >= 0 && beginIdx < beginSamplesRead)
                        {
                            writer.WriteSample(beginningPortion[beginIdx] * fadeInFactor);
                        }
                    }
                    
                    // Write the rest of the beginning portion after the crossfade
                    int remainingSamples = beginSamplesRead - (crossfadeSamples * channels + bestOffset);
                    if (remainingSamples > 0)
                    {
                        for (int i = 0; i < remainingSamples; i++)
                        {
                            int idx = crossfadeSamples * channels + bestOffset + i;
                            if (idx >= 0 && idx < beginSamplesRead)
                            {
                                writer.WriteSample(beginningPortion[idx]);
                            }
                        }
                    }
                    
                    // If we need more audio, read additional content from the file
                    if (writer.Position < writer.WaveFormat.AverageBytesPerSecond * 2)
                    {
                        // Continue from where we left off
                        byte[] buffer = new byte[reader.WaveFormat.AverageBytesPerSecond];
                        int bytesRead;
                        
                        while ((bytesRead = reader.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            writer.Write(buffer, 0, bytesRead);
                            
                            // Stop at a reasonable point
                            if (writer.Position >= writer.WaveFormat.AverageBytesPerSecond * 2)
                                break;
                        }
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