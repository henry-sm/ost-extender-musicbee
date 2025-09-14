using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.Wave;
using System.IO;
using static MusicBeePlugin.Plugin;

namespace MusicBeePlugin
{
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
                
                // Prepare for zero-crossing analysis
                bool[] zeroMatrix = new bool[windowSize * 2];
                float zeroThreshold = 0.01f; // Small threshold to avoid noise
                
                // Calculate local RMS energy around loop points to identify good transition points
                float[] startEnergies = new float[windowSize * 2];
                float[] endEnergies = new float[windowSize * 2];
                
                // Fill energy arrays
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
                        
                        // Track zero crossings too
                        zeroMatrix[i] = Math.Abs(audioData[startIdx]) < zeroThreshold;
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
                
                // 1. First try: Find zero-crossing points (best for clean transitions)
                int bestStartOffset = 0;
                int bestEndOffset = 0;
                float bestSimilarity = float.MaxValue;
                
                for (int startOffset = -windowSize; startOffset < windowSize; startOffset += 1)
                {
                    // Only consider points near zero-crossings
                    int startIdx = windowSize + startOffset;
                    if (startIdx < 0 || startIdx >= zeroMatrix.Length || !zeroMatrix[startIdx])
                        continue;
                        
                    for (int endOffset = -windowSize; endOffset < windowSize; endOffset += 1)
                    {
                        // Ensure we maintain approximately the same loop length
                        int loopLengthChange = endOffset - startOffset;
                        if (Math.Abs(loopLengthChange) > sampleRate / 4) // Allow max 250ms change
                            continue;
                            
                        int endIdx = windowSize + endOffset;
                        if (endIdx < 0 || endIdx >= endEnergies.Length)
                            continue;
                            
                        // Compare energy profiles at these points
                        float similarity = Math.Abs(startEnergies[startIdx] - endEnergies[endIdx]);
                        
                        // Check if this is better than current best
                        if (similarity < bestSimilarity)
                        {
                            bestSimilarity = similarity;
                            bestStartOffset = startOffset;
                            bestEndOffset = endOffset;
                        }
                    }
                }
                
                // 2. If zero-crossing approach didn't find good points, try phase alignment
                if (bestSimilarity > 0.1f)
                {
                    mbApiInterface.MB_Trace("Zero-crossing approach insufficient, trying phase alignment");
                    
                    // Calculate phase correlation between end and start regions
                    int sampleWindow = 1024; // Power of 2 for efficiency
                    bestSimilarity = float.MaxValue;
                    
                    for (int startOffset = -windowSize; startOffset < windowSize; startOffset += 16)
                    {
                        int startIdx = loopStartSample + startOffset;
                        if (startIdx < 0 || startIdx + sampleWindow >= audioData.Length)
                            continue;
                            
                        for (int endOffset = -windowSize; endOffset < windowSize; endOffset += 16)
                        {
                            // Ensure we maintain approximately the same loop length
                            int loopLengthChange = endOffset - startOffset;
                            if (Math.Abs(loopLengthChange) > sampleRate / 4) // Allow max 250ms change
                                continue;
                                
                            int endIdx = loopEndSample + endOffset;
                            if (endIdx < 0 || endIdx + sampleWindow >= audioData.Length)
                                continue;
                                
                            // Calculate similarity between these regions
                            float sum = 0;
                            for (int i = 0; i < sampleWindow; i++)
                            {
                                sum += Math.Abs(audioData[endIdx + i] - audioData[startIdx + i]);
                            }
                            float similarity = sum / sampleWindow;
                            
                            // Check if this is better than current best
                            if (similarity < bestSimilarity)
                            {
                                bestSimilarity = similarity;
                                bestStartOffset = startOffset;
                                bestEndOffset = endOffset;
                            }
                        }
                    }
                }
                
                // Calculate precise sample positions
                int refinedStartSample = loopStartSample + bestStartOffset;
                int refinedEndSample = loopEndSample + bestEndOffset;
                
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
                Confidence = 0.4f
            };
        }
    }
}