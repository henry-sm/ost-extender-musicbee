import numpy as np
import librosa

def find_loop_points_from_data(audio_data, sr):
    try:
        y = np.array(audio_data, dtype=np.float32)
        min_duration = librosa.get_duration(y=y, sr=sr) * 0.15
        if min_duration < 10:
            min_duration = 10

        chroma = librosa.feature.chroma_cqt(y=y, sr=sr)
        similarity_matrix = librosa.segment.recurrence_matrix(chroma, mode='affinity')

        np.fill_diagonal(similarity_matrix, 0)
        min_loop_frames = librosa.time_to_frames(min_duration, sr=sr)
        for i in range(min_loop_frames):
            np.fill_diagonal(similarity_matrix[i:, :], 0)
            np.fill_diagonal(similarity_matrix[:, i:], 0)

        frame2_coords, frame1_coords = np.unravel_index(np.argmax(similarity_matrix), similarity_matrix.shape)
        frame1, frame2 = frame1_coords.item(), frame2_coords.item()
        loop_start_frame, loop_end_frame = min(frame1, frame2), max(frame1, frame2)

        beat_frames = librosa.beat.beat_track(y=y, sr=sr, units='frames')[1]
        loop_start_frame_synced = beat_frames[np.argmin(np.abs(beat_frames - loop_start_frame))]
        loop_end_frame_synced = beat_frames[np.argmin(np.abs(beat_frames - loop_end_frame))]

        loop_start_time = librosa.frames_to_time(loop_start_frame_synced, sr=sr)
        loop_end_time = librosa.frames_to_time(loop_end_frame_synced, sr=sr)

        return { "status": "success", "loop_start": loop_start_time, "loop_end": loop_end_time }
    except Exception as e:
        return {"status": "failed", "error": str(e)}