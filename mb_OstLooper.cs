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
        /// Analyzes a track to find optimal loop points using multiple detection methods
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
                            
                            // Step 2: Use multiple detection methods for more robust results
                            
                            // First try the automatic method
                            mbApiInterface.MB_Trace("Trying automatic loop detection...");
                            result = loopDetector.AnalyzeAudioData(audioData, sampleRate, channels, audioFile.TotalTime.TotalSeconds);
                            
                            // If automatic method yields low confidence or fails, try visual guided method
                            if (result.Status == "failed" || result.Confidence < 0.6f)
                            {
                                mbApiInterface.MB_Trace("Automatic detection did not yield high confidence results, trying visual guided detection...");
                                mbApiInterface.MB_SetBackgroundTaskMessage($"Trying visual pattern detection on {Path.GetFileName(filePath)}");
                                
                                // Try visual guided detection (The Seamless Extender approach)
                                LoopResult visualResult = VisualGuidedLoopDetection(audioData, sampleRate, channels, audioFile.TotalTime.TotalSeconds);
                                
                                // Use the visual result if it has higher confidence
                                if (visualResult.Status == "success" && (result.Status == "failed" || visualResult.Confidence > result.Confidence))
                                {
                                    mbApiInterface.MB_Trace("Visual guided detection found better results, using those instead");
                                    result = visualResult;
                                }
                            }
                        }
                        
                        // Step 3: Save the results
                        mbApiInterface.MB_Trace($"Analysis result: {result.Status}, Method: {result.DetectionMethod}");
                        
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
                                    string confidenceText = result.Confidence >= 0.7f ? 
                                        $"Confidence: {result.Confidence:P0} (High)" : 
                                        $"Confidence: {result.Confidence:P0} (Moderate)";
                                    
                                    // Add detection method info
                                    string methodText = "Detection Method: ";
                                    switch (result.DetectionMethod)
                                    {
                                        case DetectionMethod.Automatic:
                                            methodText += "Automatic Pattern Analysis";
                                            break;
                                        case DetectionMethod.Manual:
                                            methodText += "Visual Guided Detection";
                                            break;
                                        case DetectionMethod.Fallback:
                                            methodText += "Fallback (Best Guess)";
                                            break;
                                    }
                                        
                                    string message = $"Analysis complete! Loop points detected:\n\n" +
                                                    $"Loop Start: {formattedStart} ({startPercent}% of track)\n" +
                                                    $"Loop End: {formattedEnd} ({endPercent}% of track)\n" +
                                                    $"Loop Length: {formattedLength}\n\n" +
                                                    $"{loopVisual}\n\n" +
                                                    $"{confidenceText}\n" +
                                                    $"{methodText}\n\n" +
                                                    $"Use \"Play with Loop\" to test these loop points.";
                                    
                                    MessageBox.Show(mainForm, message, "OST Extender", 
                                        MessageBoxButtons.OK, 
                                        result.DetectionMethod != DetectionMethod.Fallback ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                                        
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
        
        /// <summary>
        /// Implementation of "The Seamless Extender" human-guided approach for loop detection
        /// This focuses on finding repeating waveform patterns with visual similarity
        /// </summary>
        private LoopResult VisualGuidedLoopDetection(float[] audioData, int sampleRate, int channels, double duration)
        {
            mbApiInterface.MB_Trace("Starting visual guided loop detection...");
            
            try
            {
                // Step 1: Convert stereo to mono for easier analysis
                float[] monoData = featureExtractor.ConvertToMono(audioData, channels);
                
                // Step 2: Create a "visual fingerprint" of the audio by segmenting and analyzing
                int fingerprintWindowSize = sampleRate / 10; // 100ms segments
                int stepSize = fingerprintWindowSize / 2;     // 50% overlap
                int numSegments = (monoData.Length - fingerprintWindowSize) / stepSize + 1;
                
                mbApiInterface.MB_Trace($"Creating {numSegments} audio fingerprint segments...");
                
                // Features for each segment: RMS energy, zero-crossing rate, spectral balance
                float[] segmentEnergy = new float[numSegments];
                float[] segmentZeroCrossings = new float[numSegments];
                float[] segmentSpectralBalance = new float[numSegments]; // Ratio of high vs low frequency energy
                
                // Extract features for each segment
                for (int i = 0; i < numSegments; i++)
                {
                    int startIdx = i * stepSize;
                    float sumSquared = 0;
                    int zeroCrossings = 0;
                    float highFreqEnergy = 0;
                    float lowFreqEnergy = 0;
                    
                    // Previous sample for zero-crossing detection
                    float prevSample = monoData[startIdx];
                    
                    // Process the segment
                    for (int j = 0; j < fingerprintWindowSize; j++)
                    {
                        int idx = startIdx + j;
                        if (idx >= monoData.Length) break;
                        
                        float sample = monoData[idx];
                        
                        // Calculate energy
                        sumSquared += sample * sample;
                        
                        // Count zero-crossings
                        if ((prevSample >= 0 && sample < 0) || (prevSample < 0 && sample >= 0))
                            zeroCrossings++;
                            
                        prevSample = sample;
                        
                        // Simple frequency analysis - alternate samples for high/low split
                        if (j % 2 == 0)
                            lowFreqEnergy += Math.Abs(sample);
                        else
                            highFreqEnergy += Math.Abs(sample);
                    }
                    
                    // Store the features
                    segmentEnergy[i] = (float)Math.Sqrt(sumSquared / fingerprintWindowSize);
                    segmentZeroCrossings[i] = (float)zeroCrossings / fingerprintWindowSize;
                    
                    // Calculate spectral balance (avoid division by zero)
                    if (lowFreqEnergy > 0.0001f)
                        segmentSpectralBalance[i] = highFreqEnergy / lowFreqEnergy;
                    else
                        segmentSpectralBalance[i] = 0;
                        
                    // Update progress
                    if (i % 100 == 0)
                    {
                        int progress = i * 100 / numSegments;
                        mbApiInterface.MB_SetBackgroundTaskMessage($"Building audio fingerprints: {progress}%");
                    }
                }
                
                // Normalize features for better comparison
                featureExtractor.NormalizeArray(segmentEnergy);
                featureExtractor.NormalizeArray(segmentZeroCrossings);
                featureExtractor.NormalizeArray(segmentSpectralBalance);
                
                // Step 3: Build a similarity matrix to find potential loop points
                mbApiInterface.MB_Trace("Building similarity matrix for visual pattern matching...");
                
                // We'll build a smaller, more focused matrix for efficiency
                int maxSegmentsToAnalyze = Math.Min(numSegments, 2000); // Limit for performance
                float[,] similarityMatrix = new float[maxSegmentsToAnalyze, maxSegmentsToAnalyze];
                
                // Feature weights (energy patterns are most visually apparent)
                float energyWeight = 0.5f;
                float zcWeight = 0.3f;
                float spectralWeight = 0.2f;
                
                // Calculate similarity between segments
                for (int i = 0; i < maxSegmentsToAnalyze; i++)
                {
                    // Use batch processing for better performance
                    for (int j = i; j < maxSegmentsToAnalyze; j += 10)
                    {
                        int endJ = Math.Min(j + 10, maxSegmentsToAnalyze);
                        for (int k = j; k < endJ; k++)
                        {
                            // Calculate weighted feature similarity
                            float energySim = 1.0f - Math.Abs(segmentEnergy[i] - segmentEnergy[k]);
                            float zcSim = 1.0f - Math.Abs(segmentZeroCrossings[i] - segmentZeroCrossings[k]);
                            float spectralSim = 1.0f - Math.Abs(segmentSpectralBalance[i] - segmentSpectralBalance[k]);
                            
                            // Combine with weights (higher is better for similarity)
                            float similarity = (energyWeight * energySim) + 
                                             (zcWeight * zcSim) + 
                                             (spectralWeight * spectralSim);
                            
                            similarityMatrix[i, k] = similarityMatrix[k, i] = similarity;
                        }
                    }
                    
                    // Update progress
                    if (i % 100 == 0)
                    {
                        int progress = i * 100 / maxSegmentsToAnalyze;
                        mbApiInterface.MB_SetBackgroundTaskMessage($"Building similarity matrix: {progress}%");
                    }
                }
                
                // Step 4: Search for the best loop candidates using a pattern-based approach
                mbApiInterface.MB_Trace("Searching for visual patterns indicating good loop points...");
                mbApiInterface.MB_SetBackgroundTaskMessage("Searching for optimal loop points...");
                
                // Parameters for loop detection
                int minSegmentDistance = sampleRate * 8 / fingerprintWindowSize; // Min 8 seconds between points
                int maxSegmentDistance = Math.Min(maxSegmentsToAnalyze / 2, 
                                               sampleRate * 120 / fingerprintWindowSize); // Max 2 minutes or half the song
                
                // Collect potential loop candidates
                List<LoopCandidate> candidates = new List<LoopCandidate>();
                
                // Looking for distinct diagonal patterns in the matrix
                for (int startSegment = 0; startSegment < maxSegmentsToAnalyze / 3; startSegment += 5)
                {
                    // Look for patterns farther ahead in the track (good loops are typically far apart)
                    for (int distance = minSegmentDistance; distance < maxSegmentDistance; distance += 5)
                    {
                        int endSegment = startSegment + distance;
                        if (endSegment >= maxSegmentsToAnalyze) break;
                        
                        // Check if these points have high similarity
                        float pointSimilarity = similarityMatrix[startSegment, endSegment];
                        
                        // Check surrounding context for a matching pattern (more robust detection)
                        float patternSimilarity = 0;
                        int contextSize = 10; // Check surrounding ±10 segments
                        int contextCount = 0;
                        
                        for (int i = -contextSize; i <= contextSize; i++)
                        {
                            for (int j = -contextSize; j <= contextSize; j++)
                            {
                                int si = startSegment + i;
                                int ei = endSegment + j;
                                
                                if (si >= 0 && ei >= 0 && si < maxSegmentsToAnalyze && ei < maxSegmentsToAnalyze)
                                {
                                    patternSimilarity += similarityMatrix[si, ei];
                                    contextCount++;
                                }
                            }
                        }
                        
                        // Calculate average context similarity
                        if (contextCount > 0)
                            patternSimilarity /= contextCount;
                            
                        // Calculate final score (focused more on the exact point)
                        float finalSimilarity = (pointSimilarity * 0.7f) + (patternSimilarity * 0.3f);
                        
                        // If similarity is high enough, add as a candidate
                        if (finalSimilarity > 0.7f) // High similarity threshold
                        {
                            candidates.Add(new LoopCandidate 
                            {
                                StartFrame = startSegment, 
                                EndFrame = endSegment,
                                Score = finalSimilarity
                            });
                            
                            mbApiInterface.MB_Trace($"Found potential loop: {startSegment * stepSize / (float)sampleRate:F2}s → " +
                                                 $"{endSegment * stepSize / (float)sampleRate:F2}s (similarity: {finalSimilarity:F3})");
                        }
                    }
                }
                
                // Step 5: Evaluate the candidates to find the best musical loop
                mbApiInterface.MB_Trace($"Found {candidates.Count} potential loop candidates");
                
                // If no good candidates found, return fallback
                if (candidates.Count == 0)
                {
                    mbApiInterface.MB_Trace("No strong visual patterns found, using fallback");
                    return new LoopResult { 
                        Status = "fallback", 
                        LoopStart = (float)(duration * 0.3), 
                        LoopEnd = (float)(duration * 0.9),
                        Confidence = 0.4f,
                        DetectionMethod = DetectionMethod.Fallback
                    };
                }
                
                // Sort candidates by score (highest similarity first)
                candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
                
                // Get top candidates
                var topCandidates = candidates.Take(10).ToList();
                
                // Select the best candidate based on musical criteria and visual patterns
                LoopCandidate bestCandidate = topCandidates.First(); // Start with the highest similarity
                float bestScore = bestCandidate.Score;
                
                // Apply additional criteria similar to how a human editor would choose
                foreach (var candidate in topCandidates)
                {
                    // Calculate loop length in seconds
                    float loopLength = (candidate.EndFrame - candidate.StartFrame) * stepSize / (float)sampleRate;
                    
                    // Prefer loops that:
                    // 1. Start early in the track (common for OST intros)
                    float startPositionFactor = 1.0f - (candidate.StartFrame / (float)maxSegmentsToAnalyze);
                    
                    // 2. Are long enough to be enjoyable (8+ seconds is better)
                    float lengthFactor = Math.Min(loopLength / 16.0f, 1.0f); // Normalize up to 16s
                    
                    // 3. Have clear repeating patterns (already captured in the similarity score)
                    
                    // Combine factors for final score
                    float adjustedScore = candidate.Score * (0.7f + 0.2f * startPositionFactor + 0.1f * lengthFactor);
                    
                    // If this candidate is better, update best candidate
                    if (adjustedScore > bestScore)
                    {
                        bestCandidate = candidate;
                        bestScore = adjustedScore;
                    }
                }
                
                // Convert the selected candidate to actual time points
                float loopStart = bestCandidate.StartFrame * stepSize / (float)sampleRate;
                float loopEnd = bestCandidate.EndFrame * stepSize / (float)sampleRate;
                
                // Step 6: Refine the loop points for smooth transitions
                float[] refinedPoints = loopDetector.RefineLoopPoints(monoData, sampleRate, loopStart, loopEnd);
                loopStart = refinedPoints[0];
                loopEnd = refinedPoints[1];
                
                int loopStartSample = refinedPoints.Length > 2 ? (int)refinedPoints[2] : (int)(loopStart * sampleRate);
                int loopEndSample = refinedPoints.Length > 3 ? (int)refinedPoints[3] : (int)(loopEnd * sampleRate);
                
                mbApiInterface.MB_Trace($"Visual guided detection complete: {loopStart:F3}s to {loopEnd:F3}s (confidence: {bestCandidate.Score:F3})");
                
                return new LoopResult {
                    Status = "success",
                    LoopStart = loopStart,
                    LoopEnd = loopEnd,
                    LoopStartSample = loopStartSample,
                    LoopEndSample = loopEndSample,
                    Confidence = bestCandidate.Score,
                    DetectionMethod = DetectionMethod.Manual // The visual method is considered manual guidance
                };
            }
            catch (Exception ex)
            {
                mbApiInterface.MB_Trace($"Visual analysis error: {ex.Message}");
                return new LoopResult { 
                    Status = "fallback", 
                    LoopStart = (float)(duration * 0.3), 
                    LoopEnd = (float)(duration * 0.9),
                    Confidence = 0.4f,
                    DetectionMethod = DetectionMethod.Fallback
                };
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