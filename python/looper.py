import librosa
import numpy as np
import argparse
import sys
import os

# Suppress the UserWarning from librosa about audioread
# This is optional but cleans up the error output if it occurs
if sys.platform == 'win32':
    os.environ['LIBROSA_AUDIOWRITE_BINARY'] = 'ffmpeg'

def find_loop_points(y, sr, min_duration=15.0):
    """
    Finds loop points by locating the two most similar, non-adjacent sections
    and defining the loop as the segment between them.
    """
    # 1. Use chroma features for harmonic similarity
    chroma = librosa.feature.chroma_cqt(y=y, sr=sr)
    
    # 2. Compute the self-similarity matrix
    similarity_matrix = librosa.segment.recurrence_matrix(chroma, mode='affinity', sparse=True)
    
    # 3. Find the most similar, non-adjacent segments
    # Zero out the main diagonal and areas close to it to avoid trivial loops
    min_loop_frames = librosa.time_to_frames(min_duration, sr=sr)
    similarity_matrix = similarity_matrix.toarray()
    np.fill_diagonal(similarity_matrix, 0)
    for i in range(min_loop_frames):
        np.fill_diagonal(similarity_matrix[i:, :], 0)
        np.fill_diagonal(similarity_matrix[:, i:], 0)

    # 4. Find the coordinates of the brightest point in the matrix
    frame2_coords, frame1_coords = np.unravel_index(np.argmax(similarity_matrix), similarity_matrix.shape)
    frame1, frame2 = min(frame1_coords, frame2_coords), max(frame1_coords, frame2_coords)

    # 5. Snap these points to the nearest beat for musicality
    tempo, beat_frames = librosa.beat.beat_track(y=y, sr=sr, units='frames')
    
    # Find the beat closest to our calculated start and end frames
    loop_start_frame_synced = beat_frames[np.argmin(np.abs(beat_frames - frame1))]
    loop_end_frame_synced = beat_frames[np.argmin(np.abs(beat_frames - frame2))]
    
    # Ensure start and end are not the same beat
    if loop_start_frame_synced == loop_end_frame_synced:
        raise ValueError("Loop points are too close to sync to the beat.")

    # 6. Convert the beat-synced frames back to time (seconds)
    loop_start_time = librosa.frames_to_time(loop_start_frame_synced, sr=sr)
    loop_end_time = librosa.frames_to_time(loop_end_frame_synced, sr=sr)
    
    return {"loop_start": loop_start_time, "loop_end": loop_end_time}

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Find loop points in an audio track.")
    parser.add_argument("track_path", type=str, help="The path to the audio file.")
    args = parser.parse_args()

    try:
        y, sr = librosa.load(args.track_path, sr=22050, duration=300) # Load up to 5 mins for faster analysis
        
        total_duration = librosa.get_duration(y=y, sr=sr)
        if total_duration < 20:
            raise ValueError("Audio file is too short for meaningful loop analysis.")
            
        # Set a minimum loop duration of 15% of the track length or 15 seconds, whichever is larger
        min_loop_duration = max(15.0, total_duration * 0.15)
        
        loop_points = find_loop_points(y, sr, min_duration=min_loop_duration)
        
        if loop_points and loop_points['loop_end'] > loop_points['loop_start']:
            # Print the result in a simple, parseable format: start_time:end_time
            print(f"{loop_points['loop_start']}:{loop_points['loop_end']}")
            sys.exit(0) # Success
        else:
            raise ValueError("Could not find a confident loop.")

    except Exception as e:
        # Print any errors to the standard error stream for C# to catch
        print(f"Error: {e}", file=sys.stderr)
        sys.exit(1) # Failure