using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using NAudio.Wave;

namespace PartyPulse.Audio;

/// <summary>
/// Owns one looping alert. UI and framework calls are serialized by a private lock.
/// Device callbacks only publish failures and release retired playback resources.
/// </summary>
internal sealed class AlertAudio : IDisposable
{
    private readonly object gate = new();
    private PlaybackSession? session;
    private string? configuredPath;
    private string? error;
    private bool alertRequested;
    private long previewDeadline;
    private bool disposed;

    public string? Error { get { lock (gate) return error ?? session?.Failure?.Message; } }
    public bool IsPlaying { get { lock (gate) return session?.State == PlaybackState.Playing; } }
    public bool IsPreviewing { get { lock (gate) return previewDeadline > Stopwatch.GetTimestamp() && !alertRequested && IsPlaying; } }

    /// <summary>Loads a sound, retries a previous failure, or changes its software volume.</summary>
    public void Configure(string path, float volume)
    {
        lock (gate)
            ConfigureCore(path, volume);
    }

    /// <summary>
    /// Changes only this alert's decoded sample gain. Does not reload the file,
    /// restart playback, cancel a preview, or change the device/game volume.
    /// </summary>
    public void SetVolume(float volume)
    {
        lock (gate)
        {
            if (!disposed && session is not null)
                session.Volume = NormalizeVolume(volume);
        }
    }

    private static float NormalizeVolume(float volume) =>
        float.IsFinite(volume) ? Math.Clamp(volume, 0f, 1f) : 0.5f;

    private void ConfigureCore(string path, float volume)
    {
        if (disposed)
            return;

        volume = NormalizeVolume(volume);
        if (session is not null && Error is null && string.Equals(path, configuredPath, StringComparison.OrdinalIgnoreCase))
        {
            session.Volume = volume;
            return;
        }

        RetireSession();
        configuredPath = path;
        previewDeadline = 0;
        error = null;

        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("The alert sound was not found. Choose an existing WAV or MP3 file.");

            var extension = Path.GetExtension(path);
            if (!extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Choose a WAV or MP3 sound file.");

            session = new PlaybackSession(path, volume);
            RefreshPlayback();
        }
        catch (Exception exception)
        {
            error = $"Could not prepare the alert sound: {exception.Message}";
            RetireSession();
        }
    }

    /// <summary>Call every update, including when healthy, to expire previews and notice device errors.</summary>
    public void SetAlert(bool shouldAlert)
    {
        lock (gate)
        {
            if (disposed)
                return;

            alertRequested = shouldAlert;
            if (shouldAlert || previewDeadline <= Stopwatch.GetTimestamp())
                previewDeadline = 0;

            RefreshPlayback();
        }
    }

    public void Preview()
    {
        lock (gate)
        {
            if (disposed || alertRequested || Error is not null || session is null)
                return;

            previewDeadline = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
            RefreshPlayback();
        }
    }

    public void StopPreview()
    {
        lock (gate)
        {
            previewDeadline = 0;
            RefreshPlayback();
        }
    }

    /// <summary>Silences both real alerts and previews without unloading the chosen file.</summary>
    public void Stop()
    {
        lock (gate)
        {
            alertRequested = false;
            previewDeadline = 0;
            RefreshPlayback();
        }
    }

    private void RefreshPlayback()
    {
        if (disposed || session is null)
            return;

        if (session.Failure is { } failure)
        {
            error = $"Audio playback failed: {failure.Message}. Apply the sound again to retry.";
            previewDeadline = 0;
            RetireSession();
            return;
        }

        try
        {
            if (alertRequested || previewDeadline > Stopwatch.GetTimestamp())
                session.Play();
            else
                session.Pause();
        }
        catch (Exception exception)
        {
            error = $"Audio playback failed: {exception.Message}. Apply the sound again to retry.";
            previewDeadline = 0;
            RetireSession();
        }
    }

    private void RetireSession()
    {
        var previous = session;
        session = null;
        previous?.Retire();
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;

            disposed = true;
            alertRequested = false;
            previewDeadline = 0;
            RetireSession();
        }
    }

    private sealed class PlaybackSession
    {
        private readonly AudioFileReader reader;
        private readonly LoopStream loop;
        private readonly WaveOutEvent output;
        private Exception? failure;
        private int retired;
        private int resourcesDisposed;
        private int playbackEnded;
        private bool started;

        public PlaybackSession(string path, float volume)
        {
            reader = new AudioFileReader(path) { Volume = volume };
            loop = new LoopStream(reader);
            output = new WaveOutEvent { DesiredLatency = 100, NumberOfBuffers = 2 };
            output.PlaybackStopped += OnPlaybackStopped;

            try
            {
                // Decode a frame now so an empty/corrupt file fails at Apply, not on a later HP dip.
                var probe = new byte[Math.Max(reader.WaveFormat.BlockAlign, 4096 / reader.WaveFormat.BlockAlign * reader.WaveFormat.BlockAlign)];
                if (reader.Read(probe, 0, probe.Length) == 0)
                    throw new InvalidDataException("The selected file contains no playable audio.");
                reader.Position = 0;
                output.Init(loop);
            }
            catch
            {
                DisposeResources();
                throw;
            }
        }

        public Exception? Failure => Volatile.Read(ref failure);
        public PlaybackState State => output.PlaybackState;
        public float Volume { set => reader.Volume = value; }

        public void Play()
        {
            if (output.PlaybackState == PlaybackState.Playing)
                return;

            // A stopped device after initial playback indicates an error; do not restart
            // while its final callback might still be queued on the synchronization context.
            if (started && output.PlaybackState == PlaybackState.Stopped)
            {
                Interlocked.CompareExchange(ref failure, new IOException("The audio device stopped unexpectedly"), null);
                return;
            }

            output.Play();
            started = true;
        }

        public void Pause()
        {
            if (output.PlaybackState == PlaybackState.Playing)
                output.Pause();
        }

        public void Retire()
        {
            if (Interlocked.Exchange(ref retired, 1) != 0)
                return;

            try
            {
                // Pause/resume is used during normal operation. Only retirement stops the worker.
                // Release its buffers/decoder after the worker has exited, never while Read is active.
                output.Stop();
                if (!started || Volatile.Read(ref playbackEnded) != 0)
                    DisposeResources();
            }
            catch
            {
                // Unload must remain safe even after the audio device is removed.
                DisposeResources();
            }
        }

        private void OnPlaybackStopped(object? sender, StoppedEventArgs args)
        {
            if (args.Exception is not null)
                Interlocked.CompareExchange(ref failure, args.Exception, null);
            else if (Volatile.Read(ref retired) == 0)
                Interlocked.CompareExchange(ref failure, new IOException("The audio device stopped unexpectedly"), null);

            Volatile.Write(ref playbackEnded, 1);
            if (Volatile.Read(ref retired) != 0)
                DisposeResources();
        }

        private void DisposeResources()
        {
            if (Interlocked.Exchange(ref resourcesDisposed, 1) != 0)
                return;

            output.PlaybackStopped -= OnPlaybackStopped;
            try { output.Dispose(); }
            catch { /* A failing device must not prevent the decoder from being released. */ }
            try { loop.Dispose(); }
            catch { /* Disposal must not escape through a native audio callback. */ }
        }
    }
}
