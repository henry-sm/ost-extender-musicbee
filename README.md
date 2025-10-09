# OST Extender

A MusicBee plugin that provides a seamless "smart" loop for soundtracks (and similar) tracks (so that you don't have to hear that fade out at the end of regular loops).
It uses an external `looper.py` to detect the start and end loop points for the track provided.

## Installation
1. Build the plugin:
  ```
  dotnet build
```
2. Copy the compiled plugin DLL into: `%AppData%/MusicBee/Plugin` or equivalent directory.
3. Put looper.py (the analyzer) alongside the plugin DLL in the same Plugins folder. (Also make sure you have ffmpeg and these python dependencies: `librosa`, `numpy` )

## Usage
If installation is successful, you can obtain these two options when right clicking a track:
- **Activate Smart Loop**: Uses the looper program and stores the loop points.
- **Deactivate Smart Loop**: Deactivates the smart loop

## Working
- Analyzer (python/looper.py): loads audio, computes chroma features and a self-similarity matrix, finds two musically similar non-adjacent segments, snaps those points to nearest beats and returns loop_start:loop_end as seconds.
- Plugin workflow:
    1. User activates smart loop on a selected file.
    2. Plugin runs the Python analyzer (ProcessStartInfo → python `looper.py` "file").
    3.Plugin parses analyzer output and stores loop points in an in-memory map and in track metadata (MetaDataType.Virtual1/Virtual2).
    4. While track plays, plugin ensures metadata is applied when needed (so MusicBee uses the start/stop values).
    5. On deactivate, plugin restores original metadata values that it saved before changing them.
- Position control: rather than fighting MusicBee’s playback engine with repeated Player_SetPosition calls (which are often ignored/overridden), the plugin relies on MusicBee’s own handling of track start/stop times (metadata) to achieve consistent looping.

## Known Issues 
- MusicBee doesn't respond while it finds the loop.
- While the loop points are accurate (for the tracks it has been tested with), the jump to the start of the loop does not feel as seamless as expected.

## Standalone Script
For testing purposes, I had created a standalone python script for `looper.py` in `standalone/extender.py` which can be run with
```
python extender.py "path-to-audio-file" [duration in seconds]
```
This helps create an "X minute extended" version of your desired track

