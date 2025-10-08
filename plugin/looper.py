import librosa
import numpy as np
import sys
import os

def find_loop_points(y, sr, min_duration=5.0):
    """Find loop points and return start:end format"""
    try:
        # Use chroma features for harmonic similarity
        chroma = librosa.feature.chroma_cqt(y=y, sr=sr)
        similarity_matrix = librosa.segment.recurrence_matrix(chroma, mode='affinity')
        
        # Find the most similar, non-adjacent segments
        np.fill_diagonal(similarity_matrix, 0)
        min_loop_frames = librosa.time_to_frames(min_duration, sr=sr)
        for i in range(min_loop_frames):
            np.fill_diagonal(similarity_matrix[i:, :], 0)
            np.fill_diagonal(similarity_matrix[:, i:], 0)

        frame2_coords, frame1_coords = np.unravel_index(np.argmax(similarity_matrix), similarity_matrix.shape)
        frame1, frame2 = frame1_coords.item(), frame2_coords.item()
        loop_start_frame, loop_end_frame = min(frame1, frame2), max(frame1, frame2)

        # Snap to beats
        beat_frames = librosa.beat.beat_track(y=y, sr=sr, units='frames')[1]
        loop_start_frame_synced = beat_frames[np.argmin(np.abs(beat_frames - loop_start_frame))]
        loop_end_frame_synced = beat_frames[np.argmin(np.abs(beat_frames - loop_end_frame))]

        loop_start_time = librosa.frames_to_time(loop_start_frame_synced, sr=sr)
        loop_end_time = librosa.frames_to_time(loop_end_frame_synced, sr=sr)
        
        return f"{loop_start_time}:{loop_end_time}"
    except Exception as e:
        return f"Error: {str(e)}"

if __name__ == "__main__":
    if len(sys.argv) != 2:
        print("Usage: python looper.py <audio_file>")
        sys.exit(1)
    
    file_path = sys.argv[1]
    if not os.path.exists(file_path):
        print(f"Error: File not found: {file_path}")
        sys.exit(1)
    
    try:
        y, sr = librosa.load(file_path, sr=22050)
        total_duration = librosa.get_duration(y=y, sr=sr)
        
        if total_duration < 20:
            print("Error: Audio file too short for analysis")
            sys.exit(1)
            
        min_loop_duration = total_duration * 0.15
        result = find_loop_points(y, sr, min_duration=min_loop_duration)
        print(result)
        
    except Exception as e:
        print(f"Error: {str(e)}")
        sys.exit(1)