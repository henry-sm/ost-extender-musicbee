import librosa
import numpy as np
import argparse
import soundfile as sf
import os

def find_loop_points(y, sr, min_duration=5.0):
    """
    Finds loop points by finding the two most similar, non-adjacent sections
    and defining the loop as the segment between them.
    """
    print("Analyzing harmony...")
    # Use chroma features for harmonic similarity
    chroma = librosa.feature.chroma_cqt(y=y, sr=sr)
    # Compute the self-similarity matrix
    similarity_matrix = librosa.segment.recurrence_matrix(chroma, mode='affinity')
    
    # Find the most similar, non-adjacent segments
    np.fill_diagonal(similarity_matrix, 0)
    min_loop_frames = librosa.time_to_frames(min_duration, sr=sr)
    for i in range(min_loop_frames):
        np.fill_diagonal(similarity_matrix[i:, :], 0)
        np.fill_diagonal(similarity_matrix[:, i:], 0)

    # Find the coordinates of the brightest point in the matrix
    frame2_coords, frame1_coords = np.unravel_index(np.argmax(similarity_matrix), similarity_matrix.shape)
    frame1, frame2 = frame1_coords.item(), frame2_coords.item()
    loop_start_frame, loop_end_frame = min(frame1, frame2), max(frame1, frame2)

    print("Finding the beat and snapping points...")
    # Snap these points to the nearest beat for musicality
    beat_frames = librosa.beat.beat_track(y=y, sr=sr, units='frames')[1]
    loop_start_frame_synced = beat_frames[np.argmin(np.abs(beat_frames - loop_start_frame))]
    loop_end_frame_synced = beat_frames[np.argmin(np.abs(beat_frames - loop_end_frame))]

    # Convert the beat-synced frames back to time (seconds)
    loop_start_time = librosa.frames_to_time(loop_start_frame_synced, sr=sr)
    loop_end_time = librosa.frames_to_time(loop_end_frame_synced, sr=sr)
    
    return {"loop_start": loop_start_time, "loop_end": loop_end_time}

def extend_track(file_path, loop_start, loop_end, target_duration_seconds):
    """
    Creates a new audio file with the B part looped to reach the target duration.
    """
    print(f"Extending track to {target_duration_seconds} seconds...")
    y, sr = librosa.load(file_path, sr=None)
    
    loop_start_samples = librosa.time_to_samples(loop_start, sr=sr)
    loop_end_samples = librosa.time_to_samples(loop_end, sr=sr)

    intro_and_first_loop = y[:loop_end_samples]
    loop_segment = y[loop_start_samples:loop_end_samples]
    
    extended_audio = list(intro_and_first_loop)
    current_duration = librosa.get_duration(y=np.array(extended_audio), sr=sr)

    while current_duration < target_duration_seconds:
        extended_audio.extend(loop_segment)
        current_duration = librosa.get_duration(y=np.array(extended_audio), sr=sr)
        
    final_audio = np.array(extended_audio)
    
    # Create the output filename
    base_name, ext = os.path.splitext(file_path)
    output_path = f"{base_name}_extended.wav"
    
    print(f"Saving extended file to: {output_path}")
    sf.write(output_path, final_audio, sr)
    print("Done!")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Find a seamless loop in an audio track and extend it.")
    parser.add_argument("track_path", type=str, help="The path to the audio file.")
    parser.add_argument("duration", type=int, help="The target duration of the extended track in seconds.")
    
    args = parser.parse_args()

    try:
        print(f"Loading track: {args.track_path}")
        # Load the audio data for analysis
        y, sr = librosa.load(args.track_path, sr=22050) # Downsample for faster analysis
        
        total_duration = librosa.get_duration(y=y, sr=sr)
        if total_duration < 20:
            raise ValueError("Audio file is too short for loop analysis.")
            
        min_loop_duration = total_duration * 0.15
        
        # Find the loop points
        loop_points = find_loop_points(y, sr, min_duration=min_loop_duration)
        
        if loop_points:
            print(f"Loop found! Start: {loop_points['loop_start']:.2f}s, End: {loop_points['loop_end']:.2f}s")
            # Extend the track using the original, full-quality audio
            extend_track(args.track_path, loop_points['loop_start'], loop_points['loop_end'], args.duration)
        else:
            print("Could not find a confident loop in the track.")

    except Exception as e:
        print(f"An error occurred: {e}")