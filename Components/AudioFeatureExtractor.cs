using System;
using System.Linq;
using static MusicBeePlugin.Plugin;

namespace MusicBeePlugin
{
    /// <summary>
    /// Handles extraction of audio features for analysis
    /// </summary>
    public class AudioFeatureExtractor
    {
        private readonly MusicBeeApiInterface mbApiInterface;

        public AudioFeatureExtractor(MusicBeeApiInterface mbApiInterface)
        {
            this.mbApiInterface = mbApiInterface;
        }

        /// <summary>
        /// Converts stereo audio to mono for analysis
        /// </summary>
        public float[] ConvertToMono(float[] audioData, int channels)
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

        /// <summary>
        /// Extracts multiple audio features for analysis with enhanced accuracy
        /// </summary>
        public void ExtractAudioFeatures(float[] monoData, int sampleRate, int frameSize, int frameHop,
                                     float[] rmsEnergy, float[] zeroCrossings, float[] spectralCentroid)
        {
            int numFrames = rmsEnergy.Length;
            
            // Apply Hamming window for better frequency analysis
            float[] window = new float[frameSize];
            for (int i = 0; i < frameSize; i++)
            {
                // Hamming window: 0.54 - 0.46 * cos(2π * i / (N-1))
                window[i] = 0.54f - 0.46f * (float)Math.Cos((2 * Math.PI * i) / (frameSize - 1));
            }
            
            // Precompute beat detection parameters
            int beatWindowSize = sampleRate / 2; // Look at half-second windows for beat detection
            float[] energyHistory = new float[Math.Min(numFrames, 100)]; // Track recent energy for beat detection
            int energyHistoryIndex = 0;
            float energyAverage = 0;
            
            for (int i = 0; i < numFrames; i++)
            {
                int startIdx = i * frameHop;
                
                // Ensure we don't go out of bounds
                if (startIdx + frameSize > monoData.Length)
                    break;
                
                // 1. RMS Energy with windowing for better accuracy
                float sumSquared = 0;
                for (int j = 0; j < frameSize; j++)
                {
                    float windowed = monoData[startIdx + j] * window[j];
                    sumSquared += windowed * windowed;
                }
                float currentEnergy = (float)Math.Sqrt(sumSquared / frameSize);
                rmsEnergy[i] = currentEnergy;
                
                // Keep track of energy history for beat detection
                energyHistory[energyHistoryIndex] = currentEnergy;
                energyHistoryIndex = (energyHistoryIndex + 1) % energyHistory.Length;
                
                // Recalculate running average
                energyAverage = energyHistory.Sum() / energyHistory.Count(e => e > 0);
                
                // 2. Enhanced zero-crossing rate with hysteresis to reduce noise
                int zcCount = 0;
                float hysteresis = 0.01f; // Small threshold to avoid counting tiny zero-crossings
                bool wasPositive = monoData[startIdx] > hysteresis;
                
                for (int j = 1; j < frameSize; j++)
                {
                    bool isPositive = monoData[startIdx + j] > hysteresis;
                    if (isPositive != wasPositive)
                    {
                        zcCount++;
                        wasPositive = isPositive;
                    }
                }
                zeroCrossings[i] = (float)zcCount / frameSize;
                
                // 3. Improved pseudo-spectral centroid
                // Use a weighted combination of energy and zero-crossings
                // Higher zero-crossing rate generally indicates higher frequency content
                float energyWeight = 0.7f;
                float zcWeight = 0.3f;
                
                // Estimate spectral centroid using energy and zero-crossing rate
                // Higher values indicate more high-frequency content
                spectralCentroid[i] = (energyWeight * currentEnergy) + 
                                     (zcWeight * zeroCrossings[i] * sampleRate / 4);
                
                // 4. Beat detection: Look for energy spikes
                if (i > 0 && currentEnergy > 1.5f * energyAverage && currentEnergy > rmsEnergy[i-1] * 1.2f)
                {
                    // This is likely a beat - mark it by boosting the spectral centroid
                    // This helps identify rhythm patterns for better loop matching
                    spectralCentroid[i] *= 1.5f;
                }
                
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

        /// <summary>
        /// Normalize array to 0-1 range
        /// </summary>
        public void NormalizeArray(float[] array)
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
        
        /// <summary>
        /// Apply Gaussian blur to reduce noise in the similarity matrix
        /// </summary>
        public float[,] GaussianSmoothMatrix(float[,] matrix, int size)
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
    }
}