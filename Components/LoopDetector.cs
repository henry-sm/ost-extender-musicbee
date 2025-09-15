using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.Wave;
using System.IO;
using static MusicBeePlugin.Plugin;

namespace MusicBeePlugin
{
    /// <summary>
    /// The method used for loop detection
    /// </summary>
    public enum DetectionMethod
    {
        Automatic,
        Manual,
        Fallback
    }
    
    /// <summary>
    /// Container for loop detection results
    /// </summary>
    public class LoopResult 
    { 
        public string Status { get; set; } 
        public float LoopStart { get; set; }           // Loop start in seconds
        public float LoopEnd { get; set; }             // Loop end in seconds
        public int LoopStartSample { get; set; }       // Loop start in samples (for sample-accurate looping)
        public int LoopEndSample { get; set; }         // Loop end in samples
        public float Confidence { get; set; }
        public DetectionMethod DetectionMethod { get; set; } // Method used for detection: Automatic, Manual, or Fallback
    }

    /// <summary>
    /// Helper class for loop candidates
    /// </summary>
    public class LoopCandidate
    {
        public int StartFrame { get; set; }
        public int EndFrame { get; set; }
        public float Score { get; set; }
    }

    /// <summary>
    /// Handles detection of optimal loop points in audio
    /// </summary>
    public class LoopDetector
    {
        private readonly MusicBeeApiInterface mbApiInterface;
        private readonly AudioFeatureExtractor featureExtractor;

        public LoopDetector(MusicBeeApiInterface mbApiInterface)
        {
            this.mbApiInterface = mbApiInterface;
            this.featureExtractor = new AudioFeatureExtractor(mbApiInterface);
        }

        /// <summary>
        /// Main loop detection algorithm that analyzes audio data to find optimal loop points
        /// </summary>
        public LoopResult AnalyzeAudioData(float[] audioData, int sampleRate, int channels, double duration)
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
                float[] monoData = featureExtractor.ConvertToMono(audioData, channels);
                
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
                featureExtractor.ExtractAudioFeatures(monoData, sampleRate, frameSize, frameHop, 
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
                float[,] smoothedMatrix = featureExtractor.GaussianSmoothMatrix(similarityMatrix, matrixSize);
                
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
                
                // Calculate confidence
                float confidence = 1.0f - bestCandidate.Score;
                
                // Get refined points with sample accuracy
                float[] refinedPoints = RefineLoopPoints(monoData, sampleRate, loopStart, loopEnd);
                loopStart = refinedPoints[0];
                loopEnd = refinedPoints[1];
                
                // Get sample positions if available (newer method returns 4 values)
                int loopStartSample = refinedPoints.Length > 2 ? (int)refinedPoints[2] : (int)(loopStart * sampleRate);
                int loopEndSample = refinedPoints.Length > 3 ? (int)refinedPoints[3] : (int)(loopEnd * sampleRate);
                
                mbApiInterface.MB_Trace($"Enhanced analysis complete. Loop: {loopStart:F3}s to {loopEnd:F3}s, confidence: {confidence:F2}");
                mbApiInterface.MB_Trace($"Sample positions: {loopStartSample} to {loopEndSample}");
                
                return new LoopResult {
                    Status = "success",
                    LoopStart = loopStart,
                    LoopEnd = loopEnd,
                    LoopStartSample = loopStartSample,
                    LoopEndSample = loopEndSample,
                    Confidence = confidence,
                    DetectionMethod = DetectionMethod.Automatic
                };
            }
            catch (Exception ex)
            {
                mbApiInterface.MB_Trace($"Analysis algorithm error: {ex.Message}\n{ex.StackTrace}");
                
                // Fallback to simple ratio-based analysis
                return GetFallbackLoop(duration);
            }
        }

        /// <summary>
        /// Select the best musical candidate from top candidates
        /// </summary>
        private LoopCandidate SelectBestMusicalCandidate(List<LoopCandidate> candidates, double frameDuration)
        {
            if (candidates == null || candidates.Count == 0)
                return null;
            
            mbApiInterface.MB_Trace("Analyzing candidates for musical characteristics");
            
            // Common time signatures in video game music (in seconds, assuming 120 BPM as reference)
            double[] musicalLengths = new double[] { 
                2.0,   // 1 bar @ 120BPM
                4.0,   // 2 bars @ 120BPM
                8.0,   // 4 bars (very common)
                16.0,  // 8 bars (common phrase length)
                32.0   // 16 bars (common section length)
            };
                
            // Prefer candidates that create loops of musical lengths
            foreach (var candidate in candidates)
            {
                double loopLength = (candidate.EndFrame - candidate.StartFrame) * frameDuration;
                
                // Calculate base musicality score - how well it fits common musical patterns
                double bestMusicality = 1.0; // Worst score to start
                
                // Check alignment with common musical phrase lengths (allowing for tempo variations)
                foreach (double musicalLength in musicalLengths)
                {
                    // Calculate how many musical units would fit in this loop
                    double units = loopLength / musicalLength;
                    
                    // Round to nearest integer (i.e., nearest whole number of phrases)
                    double nearestWholeUnit = Math.Round(units);
                    
                    // Calculate deviation as percentage (0% = perfect match with a musical length)
                    double deviation = Math.Abs(units - nearestWholeUnit) / nearestWholeUnit;
                    
                    // Weight longer patterns higher (more accurate for loop detection)
                    double weightedDeviation = deviation / Math.Log10(1 + musicalLength);
                    
                    // Update best musicality if this pattern is better
                    if (weightedDeviation < bestMusicality)
                    {
                        bestMusicality = weightedDeviation;
                        mbApiInterface.MB_Trace($"Candidate loop: {loopLength:F1}s fits ~{nearestWholeUnit} units of {musicalLength:F1}s (deviation: {deviation:P1})");
                    }
                }
                
                // Apply musicality bonus (lower scores are better)
                // Scale so that perfect matches get big bonus, poor matches get small penalty
                double musicalityFactor = bestMusicality < 0.1 ? 0.5 : (bestMusicality < 0.2 ? 0.7 : 1.2);
                
                // Apply bonus for loops that start early in the track (common in OSTs)
                // Video game music often has an intro section then loops from a point early in the track
                double startPosFactor = candidate.StartFrame < 300 ? 0.8 : 1.0;
                
                // Apply final adjustments to score
                float originalScore = candidate.Score;
                candidate.Score = (float)(originalScore * musicalityFactor * startPosFactor);
                
                mbApiInterface.MB_Trace($"Candidate adjusted from {originalScore:F3} to {candidate.Score:F3} (musicality: {musicalityFactor:F2}, position: {startPosFactor:F1})");
            }
            
            // Re-sort with musical criteria applied
            candidates.Sort((a, b) => a.Score.CompareTo(b.Score));
            
            LoopCandidate best = candidates[0];
            double bestLoopLength = (best.EndFrame - best.StartFrame) * frameDuration;
            mbApiInterface.MB_Trace($"Selected best musical loop candidate: {bestLoopLength:F1}s with score {best.Score:F3}");
            
            return best;
        }

        /// <summary>
        /// Refine loop points to align with beats or strong transients for sample-accurate looping
        /// </summary>
        public float[] RefineLoopPoints(float[] audioData, int sampleRate, float loopStartSec, float loopEndSec)
        {
            try
            {
                mbApiInterface.MB_Trace($"Refining loop points from {loopStartSec:F3}s to {loopEndSec:F3}s");
                
                // Convert seconds to samples
                int loopStartSample = (int)(loopStartSec * sampleRate);
                int loopEndSample = (int)(loopEndSec * sampleRate);
                
                // Calculate initial loop length in samples
                int initialLoopLength = loopEndSample - loopStartSample;
                
                // Use wider windows for analysis to find better transitions
                int windowSize = sampleRate / 2; // 500ms window for more context
                
                // Prepare for zero-crossing analysis with improved detection
                bool[] zeroMatrix = new bool[windowSize * 2];
                float[] derivativeMatrix = new float[windowSize * 2]; // Track rate of change for better transitions
                float zeroThreshold = 0.005f; // Reduced threshold for more zero-crossing candidates
                
                // Calculate local RMS energy around loop points to identify good transition points
                float[] startEnergies = new float[windowSize * 2];
                float[] endEnergies = new float[windowSize * 2];
                
                // Fill energy arrays and derivatives
                for (int i = 0; i < windowSize * 2; i++)
                {
                    int startIdx = loopStartSample - windowSize + i;
                    int endIdx = loopEndSample - windowSize + i;
                    
                    if (startIdx >= 0 && startIdx + 100 < audioData.Length)
                    {
                        // Calculate RMS energy in a small window
                        float sum = 0;
                        for (int j = 0; j < 100; j++)
                        {
                            sum += audioData[startIdx + j] * audioData[startIdx + j];
                        }
                        startEnergies[i] = (float)Math.Sqrt(sum / 100);
                        
                        // Track zero crossings with improved detection
                        zeroMatrix[i] = Math.Abs(audioData[startIdx]) < zeroThreshold;
                        
                        // Calculate derivative (rate of change) for smoother transitions
                        if (startIdx > 0 && startIdx < audioData.Length - 1)
                        {
                            derivativeMatrix[i] = Math.Abs(audioData[startIdx + 1] - audioData[startIdx - 1]) / 2f;
                        }
                    }
                    
                    if (endIdx >= 0 && endIdx + 100 < audioData.Length)
                    {
                        float sum = 0;
                        for (int j = 0; j < 100; j++)
                        {
                            sum += audioData[endIdx + j] * audioData[endIdx + j];
                        }
                        endEnergies[i] = (float)Math.Sqrt(sum / 100);
                    }
                }
                
                // 1. First try: Find zero-crossing points with minimal audio discontinuity
                int bestStartOffset = 0;
                int bestEndOffset = 0;
                float bestSimilarity = float.MaxValue;
                
                // Use a more comprehensive scoring approach
                for (int startOffset = -windowSize; startOffset < windowSize; startOffset += 1)
                {
                    // Use more flexible zero-crossing detection
                    int startIdx = windowSize + startOffset;
                    if (startIdx < 0 || startIdx >= zeroMatrix.Length)
                        continue;
                    
                    // Prioritize zero-crossings but don't exclude other points completely
                    float zeroScore = zeroMatrix[startIdx] ? 0.0f : 0.5f;
                    
                    // Also prefer points with low rate of change (flatter waveform)
                    float derivScore = startIdx < derivativeMatrix.Length ? derivativeMatrix[startIdx] : 1.0f;
                    
                    for (int endOffset = -windowSize; endOffset < windowSize; endOffset += 1)
                    {
                        // More flexible loop length adjustment (up to 500ms change)
                        int loopLengthChange = endOffset - startOffset;
                        if (Math.Abs(loopLengthChange) > sampleRate / 2) 
                            continue;
                            
                        int endIdx = windowSize + endOffset;
                        if (endIdx < 0 || endIdx >= endEnergies.Length)
                            continue;
                            
                        // Multi-factor similarity calculation
                        // 1. Energy level similarity
                        float energySimilarity = Math.Abs(startEnergies[startIdx] - endEnergies[endIdx]);
                        
                        // 2. Check surrounding samples for context similarity (smoother transition)
                        float contextSimilarity = 0;
                        int validContextPoints = 0;
                        
                        // Check surrounding audio context
                        for (int contextOffset = -10; contextOffset <= 10; contextOffset++)
                        {
                            int startContextIdx = loopStartSample + startOffset + contextOffset;
                            int endContextIdx = loopEndSample + endOffset + contextOffset;
                            
                            if (startContextIdx >= 0 && startContextIdx < audioData.Length && 
                                endContextIdx >= 0 && endContextIdx < audioData.Length)
                            {
                                contextSimilarity += Math.Abs(audioData[startContextIdx] - audioData[endContextIdx]);
                                validContextPoints++;
                            }
                        }
                        
                        if (validContextPoints > 0)
                            contextSimilarity /= validContextPoints;
                        
                        // Combine scores (weighted for best perceptual quality)
                        float combinedScore = 
                            (energySimilarity * 0.3f) +  // Energy level matching
                            (contextSimilarity * 0.4f) + // Context matching
                            (zeroScore * 0.2f) +         // Zero crossing bonus
                            (derivScore * 0.1f);         // Rate of change bonus
                        
                        // Check if this is better than current best
                        if (combinedScore < bestSimilarity)
                        {
                            bestSimilarity = combinedScore;
                            bestStartOffset = startOffset;
                            bestEndOffset = endOffset;
                        }
                    }
                }
                
                // 2. If zero-crossing approach didn't find good points or has low confidence, try enhanced phase alignment
                if (bestSimilarity > 0.08f) // Lower threshold to try phase alignment more often
                {
                    mbApiInterface.MB_Trace("Zero-crossing approach insufficient, trying enhanced phase alignment");
                    
                    // Use a larger window for more accurate phase matching
                    int sampleWindow = 2048; // Increased from 1024 for better accuracy
                    float phaseBestSimilarity = float.MaxValue;
                    int phaseBestStartOffset = 0;
                    int phaseBestEndOffset = 0;
                    
                    // Use finer step size for more precise matching
                    int stepSize = 8; // Reduced from 16 for more granular search
                    
                    for (int startOffset = -windowSize; startOffset < windowSize; startOffset += stepSize)
                    {
                        int startIdx = loopStartSample + startOffset;
                        if (startIdx < 0 || startIdx + sampleWindow >= audioData.Length)
                            continue;
                            
                        for (int endOffset = -windowSize; endOffset < windowSize; endOffset += stepSize)
                        {
                            // Allow slightly more loop length variation
                            int loopLengthChange = endOffset - startOffset;
                            if (Math.Abs(loopLengthChange) > sampleRate / 2) // Increased to 500ms max adjustment
                                continue;
                                
                            int endIdx = loopEndSample + endOffset;
                            if (endIdx < 0 || endIdx + sampleWindow >= audioData.Length)
                                continue;
                                
                            // Calculate improved similarity with phase awareness
                            float sum = 0;
                            float dotProduct = 0;
                            float startMagnitude = 0;
                            float endMagnitude = 0;
                            
                            // Analyze sample differences and phase correlation
                            for (int i = 0; i < sampleWindow; i++)
                            {
                                float startSample = audioData[startIdx + i];
                                float endSample = audioData[endIdx + i];
                                
                                // Absolute difference (waveform shape)
                                sum += Math.Abs(endSample - startSample);
                                
                                // Phase correlation (dot product normalized by magnitudes)
                                dotProduct += startSample * endSample;
                                startMagnitude += startSample * startSample;
                                endMagnitude += endSample * endSample;
                            }
                            
                            // Calculate waveform similarity
                            float waveformSimilarity = sum / sampleWindow;
                            
                            // Calculate phase correlation coefficient (-1 to 1, higher is better)
                            float phaseCorrelation = 0;
                            if (startMagnitude > 0 && endMagnitude > 0)
                            {
                                phaseCorrelation = dotProduct / (float)(Math.Sqrt(startMagnitude) * Math.Sqrt(endMagnitude));
                                // Convert to 0-1 range, where lower is better (to match other similarity measures)
                                phaseCorrelation = (1 - phaseCorrelation) / 2;
                            }
                            
                            // Weighted combination of metrics
                            float combinedSimilarity = (waveformSimilarity * 0.6f) + (phaseCorrelation * 0.4f);
                            
                            // Check if this is better than current best
                            if (combinedSimilarity < phaseBestSimilarity)
                            {
                                phaseBestSimilarity = combinedSimilarity;
                                phaseBestStartOffset = startOffset;
                                phaseBestEndOffset = endOffset;
                            }
                        }
                    }
                    
                    // If phase alignment found a better match, use it instead
                    if (phaseBestSimilarity < bestSimilarity * 1.2f) // Allow phase method even if slightly worse
                    {
                        bestSimilarity = phaseBestSimilarity;
                        bestStartOffset = phaseBestStartOffset;
                        bestEndOffset = phaseBestEndOffset;
                        mbApiInterface.MB_Trace($"Using phase alignment result with similarity {bestSimilarity:F5}");
                    }
                }
                
                // Calculate precise sample positions
                int refinedStartSample = loopStartSample + bestStartOffset;
                int refinedEndSample = loopEndSample + bestEndOffset;
                
                // Final fine-tuning step: Scan for exact zero-crossing within a very small window
                // This provides sample-accurate precision for the smoothest possible transitions
                int microWindow = 16; // Look within +/- 16 samples
                int bestStartSample = refinedStartSample;
                int bestEndSample = refinedEndSample;
                float bestZCScore = float.MaxValue;
                
                for (int startMicroOffset = -microWindow; startMicroOffset <= microWindow; startMicroOffset++)
                {
                    int startSample = refinedStartSample + startMicroOffset;
                    if (startSample <= 0 || startSample >= audioData.Length - 1)
                        continue;
                    
                    // Check if this is a zero crossing (sign change)
                    if (audioData[startSample] * audioData[startSample - 1] >= 0) // Not a zero crossing
                        continue;
                    
                    for (int endMicroOffset = -microWindow; endMicroOffset <= microWindow; endMicroOffset++)
                    {
                        int endSample = refinedEndSample + endMicroOffset;
                        if (endSample <= 0 || endSample >= audioData.Length - 1)
                            continue;
                        
                        // Check if this is also a zero crossing
                        if (audioData[endSample] * audioData[endSample - 1] >= 0) // Not a zero crossing
                            continue;
                        
                        // Calculate how well the waveform shapes match around these points
                        float zcScore = 0;
                        for (int i = -4; i <= 4; i++)
                        {
                            int s1 = startSample + i;
                            int s2 = endSample + i;
                            
                            if (s1 >= 0 && s1 < audioData.Length && s2 >= 0 && s2 < audioData.Length)
                            {
                                zcScore += Math.Abs(audioData[s1] - audioData[s2]);
                            }
                        }
                        
                        if (zcScore < bestZCScore)
                        {
                            bestZCScore = zcScore;
                            bestStartSample = startSample;
                            bestEndSample = endSample;
                        }
                    }
                }
                
                // Use the fine-tuned sample positions if we found good zero crossings
                if (bestZCScore < float.MaxValue)
                {
                    refinedStartSample = bestStartSample;
                    refinedEndSample = bestEndSample;
                    mbApiInterface.MB_Trace($"Fine-tuned to exact zero crossings with score: {bestZCScore:F6}");
                }
                
                // Convert to seconds for display and compatibility with old code
                float refinedStart = refinedStartSample / (float)sampleRate;
                float refinedEnd = refinedEndSample / (float)sampleRate;
                
                // Ensure we don't go out of bounds
                if (refinedStart < 0.1f) {
                    refinedStart = 0.1f;
                    refinedStartSample = (int)(refinedStart * sampleRate);
                }
                
                if (refinedEnd < refinedStart + 4.0f) {
                    refinedEnd = refinedStart + 4.0f;  // Ensure at least 4s of audio
                    refinedEndSample = (int)(refinedEnd * sampleRate);
                }
                
                // Log the results
                mbApiInterface.MB_Trace($"Refined loop points: {refinedStart:F3}s to {refinedEnd:F3}s");
                mbApiInterface.MB_Trace($"Sample-accurate positions: {refinedStartSample} to {refinedEndSample} @ {sampleRate}Hz");
                mbApiInterface.MB_Trace($"Similarity score: {bestSimilarity:F5}");
                
                // Return time-based positions for backward compatibility
                return new float[] { refinedStart, refinedEnd, refinedStartSample, refinedEndSample };
            }
            catch (Exception ex)
            {
                mbApiInterface.MB_Trace($"Loop point refinement error: {ex.Message}");
                
                // If refinement fails, return original points
                return new float[] { loopStartSec, loopEndSec };
            }
        }

        /// <summary>
        /// Get fallback loop points when no good candidates are found
        /// </summary>
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
                LoopStartSample = (int)(loopStart * 44100), // Assuming default sample rate
                LoopEndSample = (int)(loopEnd * 44100),
                Confidence = 0.4f,
                DetectionMethod = DetectionMethod.Fallback
            };
        }
        
        /// <summary>
        /// Analyze audio using visual pattern matching approach inspired by "The Seamless Extender" technique
        /// This method focuses on identifying repeating waveform patterns visually to find optimal loop points
        /// </summary>
        public LoopResult AnalyzeVisualPatterns(string filePath, int sampleRate)
        {
            try
            {
                mbApiInterface.MB_Trace("Starting visual pattern analysis...");
                
                // Read the audio file and extract the waveform data for visual analysis
                float[] audioData;
                double duration;
                int channels;
                
                using (var audioFile = new AudioFileReader(filePath))
                {
                    duration = audioFile.TotalTime.TotalSeconds;
                    channels = audioFile.WaveFormat.Channels;
                    
                    // Calculate number of samples to read
                    int totalSamples = (int)(audioFile.Length / (audioFile.WaveFormat.BitsPerSample / 8));
                    audioData = new float[totalSamples];
                    
                    // Read audio data
                    audioFile.Read(audioData, 0, totalSamples);
                    
                    // Override sampleRate with actual file's sample rate if needed
                    sampleRate = audioFile.WaveFormat.SampleRate;
                }
                
                // Convert to mono for easier analysis
                float[] monoData = featureExtractor.ConvertToMono(audioData, channels);
                
                mbApiInterface.MB_Trace("Analyzing waveform patterns for repeating sections...");
                
                // Apply "The Seamless Extender" approach:
                // 1. Look for repeating patterns in the waveform
                // 2. Focus especially on sections that are far apart (intro repeating later)
                // 3. Make sure the cuts occur at similar amplitude points
                
                // Generate "fingerprints" of audio segments to compare
                var patternMatches = FindRepeatingPatterns(monoData, sampleRate);
                
                if (patternMatches.Count == 0)
                {
                    mbApiInterface.MB_Trace("No strong repeating patterns found. Using fallback approach.");
                    var fallback = GetFallbackLoop(duration);
                    // Fallback detection method is already set in GetFallbackLoop
                    return fallback;
                }
                
                // Select the best match based on pattern similarity and musical context
                var bestMatch = SelectBestVisualMatch(patternMatches, duration);
                
                // Convert match indices to time in seconds
                float loopStart = bestMatch.StartIndex / (float)sampleRate;
                float loopEnd = bestMatch.EndIndex / (float)sampleRate;
                
                // Refine points for precise cutting
                float[] refinedPoints = RefineLoopPoints(monoData, sampleRate, loopStart, loopEnd);
                loopStart = refinedPoints[0];
                loopEnd = refinedPoints[1];
                
                int loopStartSample = refinedPoints.Length > 2 ? (int)refinedPoints[2] : (int)(loopStart * sampleRate);
                int loopEndSample = refinedPoints.Length > 3 ? (int)refinedPoints[3] : (int)(loopEnd * sampleRate);
                
                mbApiInterface.MB_Trace($"Visual analysis complete. Loop: {loopStart:F3}s to {loopEnd:F3}s");
                mbApiInterface.MB_Trace($"Sample positions: {loopStartSample} to {loopEndSample}");
                
                return new LoopResult {
                    Status = "success",
                    LoopStart = loopStart,
                    LoopEnd = loopEnd,
                    LoopStartSample = loopStartSample,
                    LoopEndSample = loopEndSample,
                    Confidence = bestMatch.Similarity,
                    DetectionMethod = DetectionMethod.Manual // The visual method is considered a manual approach
                };
            }
            catch (Exception ex)
            {
                mbApiInterface.MB_Trace($"Visual analysis error: {ex.Message}");
                return GetFallbackLoop(0);
            }
        }
        
        /// <summary>
        /// Represents a match between repeating audio patterns
        /// </summary>
        private class PatternMatch
        {
            public int StartIndex { get; set; }    // Start sample of first section
            public int EndIndex { get; set; }      // Start sample of second section
            public int Length { get; set; }        // Length of matching pattern in samples
            public float Similarity { get; set; }  // How similar the patterns are (0-1)
            public float TimeDifference { get; set; } // Time between repeating sections
        }
        
        /// <summary>
        /// Find repeating patterns in audio data using a sliding window approach
        /// This mimics how a human might visually identify similar waveform sections
        /// </summary>
        private List<PatternMatch> FindRepeatingPatterns(float[] audioData, int sampleRate)
        {
            List<PatternMatch> matches = new List<PatternMatch>();
            
            // Parameters for pattern detection
            int minPatternLengthSeconds = 3;  // Minimum section length to consider (in seconds)
            int minPatternLength = minPatternLengthSeconds * sampleRate;
            int maxPatternLength = 15 * sampleRate; // Maximum pattern length to check
            float similarityThreshold = 0.80f;      // Threshold for considering patterns similar
            
            // Calculate the number of samples to skip between checks for performance
            // (checking every single sample would be too slow)
            int skipStep = sampleRate / 20; // Check every 50ms
            
            mbApiInterface.MB_Trace($"Looking for patterns between {minPatternLengthSeconds}-15 seconds long...");
            
            // For each potential pattern length
            for (int patternLength = minPatternLength; patternLength <= maxPatternLength; patternLength += skipStep)
            {
                // Searching with increasing granularity for finer detection
                int searchStep = skipStep;
                
                // For each potential starting point (limit the range to prevent overflow)
                for (int startIdx = 0; startIdx < audioData.Length - patternLength - 1; startIdx += searchStep)
                {
                    // Only check patterns that would leave enough room for a proper loop
                    int minEndPosition = startIdx + 10 * sampleRate; // At least 10 seconds apart
                    int maxEndPosition = audioData.Length - patternLength - 1;
                    
                    // Look for similar patterns later in the audio
                    for (int endIdx = minEndPosition; endIdx < maxEndPosition; endIdx += searchStep * 2)
                    {
                        // Calculate similarity between the two segments
                        float similarity = CalculatePatternSimilarity(
                            audioData, startIdx, endIdx, patternLength);
                        
                        if (similarity > similarityThreshold)
                        {
                            // We found a potential match - calculate time difference
                            float timeDifference = (endIdx - startIdx) / (float)sampleRate;
                            
                            // Add it to our matches
                            matches.Add(new PatternMatch
                            {
                                StartIndex = startIdx,
                                EndIndex = endIdx,
                                Length = patternLength,
                                Similarity = similarity,
                                TimeDifference = timeDifference
                            });
                            
                            mbApiInterface.MB_Trace($"Found potential pattern match: {startIdx/(float)sampleRate:F2}s → " +
                                                   $"{endIdx/(float)sampleRate:F2}s ({timeDifference:F2}s apart, {similarity:P0} similar)");
                            
                            // Skip ahead to avoid redundant matches
                            endIdx += patternLength;
                        }
                    }
                }
            }
            
            return matches;
        }
        
        /// <summary>
        /// Calculate similarity between two audio segments
        /// </summary>
        private float CalculatePatternSimilarity(float[] audioData, int firstStart, int secondStart, int length)
        {
            // Ensure we don't go out of bounds
            length = Math.Min(length, audioData.Length - Math.Max(firstStart, secondStart));
            
            // Compare waveforms using correlation coefficient
            float sum1 = 0, sum2 = 0;
            float sumSq1 = 0, sumSq2 = 0;
            float sumCoproduct = 0;
            
            // Sample a subset of points for faster performance
            int sampleStep = Math.Max(1, length / 1000);
            int sampleCount = 0;
            
            for (int i = 0; i < length; i += sampleStep)
            {
                float val1 = audioData[firstStart + i];
                float val2 = audioData[secondStart + i];
                
                sum1 += val1;
                sum2 += val2;
                sumSq1 += val1 * val1;
                sumSq2 += val2 * val2;
                sumCoproduct += val1 * val2;
                sampleCount++;
            }
            
            // Calculate correlation coefficient
            float meanX = sum1 / sampleCount;
            float meanY = sum2 / sampleCount;
            float covar = sumCoproduct / sampleCount - meanX * meanY;
            float xStdDev = (float)Math.Sqrt(sumSq1 / sampleCount - meanX * meanX);
            float yStdDev = (float)Math.Sqrt(sumSq2 / sampleCount - meanY * meanY);
            
            // Avoid division by zero
            if (xStdDev * yStdDev < 0.0001f)
                return 0;
                
            float correlation = covar / (xStdDev * yStdDev);
            
            // Also check RMS difference as a secondary measure
            float rmsDiff = 0;
            for (int i = 0; i < length; i += sampleStep)
            {
                float diff = audioData[firstStart + i] - audioData[secondStart + i];
                rmsDiff += diff * diff;
            }
            rmsDiff = (float)Math.Sqrt(rmsDiff / sampleCount);
            
            // Combine metrics (correlation is -1 to 1, convert to 0-1 scale)
            float normalizedCorrelation = (correlation + 1) / 2;
            float normalizedRmsDiff = Math.Max(0, 1 - rmsDiff * 5); // Scale RMS difference
            
            // Weight correlation more heavily
            return normalizedCorrelation * 0.7f + normalizedRmsDiff * 0.3f;
        }
        
        /// <summary>
        /// Select the best visual match based on several criteria similar to how a human would choose
        /// </summary>
        private PatternMatch SelectBestVisualMatch(List<PatternMatch> matches, double duration)
        {
            mbApiInterface.MB_Trace($"Selecting best match from {matches.Count} candidates...");
            
            // Sort by similarity first
            var sortedMatches = matches.OrderByDescending(m => m.Similarity).ToList();
            
            // Get top candidates
            var topCandidates = sortedMatches.Take(10).ToList();
            
            // Score each candidate based on multiple factors
            foreach (var match in topCandidates)
            {
                // Initial score based on similarity
                float score = match.Similarity;
                
                // Favor patterns that start earlier in the track (intro sections)
                float startPosition = match.StartIndex / (float)(duration * 44100); // Normalized position
                score *= 1.0f + (1.0f - Math.Min(1.0f, startPosition * 3));
                
                // Favor patterns that repeat after a significant gap
                // This mimics the "as far apart as possible" advice
                float normalizedGap = Math.Min(1.0f, match.TimeDifference / 60);
                score *= 1.0f + normalizedGap * 0.5f;
                
                // Update score
                match.Similarity = score;
            }
            
            // Re-sort after scoring
            topCandidates = topCandidates.OrderByDescending(m => m.Similarity).ToList();
            
            // Log top matches
            for (int i = 0; i < Math.Min(3, topCandidates.Count); i++)
            {
                var m = topCandidates[i];
                mbApiInterface.MB_Trace($"Match #{i+1}: {m.StartIndex/44100.0f:F2}s → {m.EndIndex/44100.0f:F2}s " +
                                      $"({m.Similarity:F3} score)");
            }
            
            // Return the best match
            return topCandidates.First();
        }
    }
}