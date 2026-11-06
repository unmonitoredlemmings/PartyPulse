using System;
using System.IO;
using NAudio.Wave;

namespace PartyPulse.Audio;

/// <summary>Plays a finite recovery sound once, with independent software sample volume.</summary>
internal sealed class OneShotAudio : IDisposable
{
    private readonly object gate = new();
    private readonly Func<IWavePlayer> outputFactory;
    private PlaybackSession? session;
    private string? configuredPath;
    private string? error;
    private float volume = 0.5f;
    private bool disposed;

    public OneShotAudio() : this(() => new WaveOutEvent { DesiredLatency = 100, NumberOfBuffers = 2 }) { }

    // Allows finite playback and callback lifecycle checks without using an audio device.
    internal OneShotAudio(Func<IWavePlayer> outputFactory) => this.outputFactory = outputFactory;

    public string? Error { get { lock (gate) return error ?? session?.Failure?.Message; } }
    public bool IsPlaying { get { lock (gate) return session?.IsPlaying == true; } }

    /// <summary>Validates a file without playing it or opening an output device.</summary>
    public void Configure(string path, float newVolume)
    {
        lock (gate)
        {
            if (disposed)
                return;

            volume = NormalizeVolume(newVolume);
            if (Error is null && string.Equals(configuredPath, path, StringComparison.OrdinalIgnoreCase))
            {
                session?.SetVolume(volume);
                return;
            }

            RetireSession();
            configuredPath = null;
            error = null;
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    throw new FileNotFoundException("The recovery sound was not found. Choose an existing WAV or MP3 file.");

                var extension = Path.GetExtension(path);
                if (!extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException("Choose a WAV or MP3 recovery sound.");

                using var reader = new AudioFileReader(path);
                var probe = new float[Math.Max(1024, reader.WaveFormat.Channels)];
                if (reader.Read(probe, 0, probe.Length) == 0)
                    throw new InvalidDataException("The selected file contains no playable audio.");

                configuredPath = path;
            }
            catch (Exception exception)
            {
                error = $"Could not prepare the recovery sound: {exception.Message}";
            }
        }
    }

    /// <summary>
    /// Plays the complete file once. Repeated calls stop the previous session first,
    /// then open a fresh decoder/output at the beginning; sounds never stack.
    /// </summary>
    public void PlayOnce()
    {
        lock (gate)
        {
            if (disposed || configuredPath is null)
                return;

            RetireSession();
            error = null;
            try
            {
                session = new PlaybackSession(configuredPath, volume, outputFactory);
                session.Play();
            }
            catch (Exception exception)
            {
                error = $"Recovery sound playback failed: {exception.Message}";
                RetireSession();
            }
        }
    }

    public void SetVolume(float newVolume)
    {
        lock (gate)
        {
            if (disposed)
                return;

            volume = NormalizeVolume(newVolume);
            session?.SetVolume(volume);
        }
    }

    public void Stop()
    {
        lock (gate)
            RetireSession();
    }

    private static float NormalizeVolume(float value) =>
        float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0.5f;

    private void RetireSession()
    {
        var previous = session;
        error ??= previous?.Failure?.Message;
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
            configuredPath = null;
            RetireSession();
        }
    }

    private sealed class PlaybackSession
    {
        private readonly object resourceGate = new();
        private readonly AudioFileReader reader;
        private readonly IWavePlayer output;
        private Exception? failure;
        private bool started;
        private bool ended;
        private bool resourcesDisposed;

        public PlaybackSession(string path, float volume, Func<IWavePlayer> outputFactory)
        {
            reader = new AudioFileReader(path) { Volume = volume };
            try
            {
                output = outputFactory();
            }
            catch
            {
                reader.Dispose();
                throw;
            }

            output.PlaybackStopped += OnPlaybackStopped;
            try
            {
                // A plain reader reaches EOF naturally: no LoopStream or preview timer.
                output.Init(reader);
            }
            catch
            {
                DisposeResources();
                throw;
            }
        }

        public Exception? Failure { get { lock (resourceGate) return failure; } }
        public bool IsPlaying { get { lock (resourceGate) return !resourcesDisposed && output.PlaybackState == PlaybackState.Playing; } }

        public void SetVolume(float value)
        {
            lock (resourceGate)
            {
                if (!resourcesDisposed)
                    reader.Volume = value;
            }
        }

        public void Play()
        {
            lock (resourceGate)
            {
                try
                {
                    output.Play();
                    started = true;
                }
                catch
                {
                    DisposeResources();
                    throw;
                }
            }
        }

        public void Retire()
        {
            lock (resourceGate)
            {
                if (resourcesDisposed)
                    return;

                try
                {
                    output.Stop();
                    if (!started || ended)
                        DisposeResources();
                    // A running worker releases its own reader/buffers in PlaybackStopped.
                }
                catch
                {
                    DisposeResources();
                }
            }
        }

        private void OnPlaybackStopped(object? sender, StoppedEventArgs args)
        {
            lock (resourceGate)
            {
                if (resourcesDisposed)
                    return;

                ended = true;
                // Null is normal EOF or an explicit stop, not an audio failure.
                failure = args.Exception;
                DisposeResources();
            }
        }

        private void DisposeResources()
        {
            if (resourcesDisposed)
                return;

            resourcesDisposed = true;
            output.PlaybackStopped -= OnPlaybackStopped;
            try { output.Dispose(); }
            catch { /* Device cleanup must not prevent reader cleanup. */ }
            try { reader.Dispose(); }
            catch { /* Cleanup must not escape through a native audio callback. */ }
        }
    }
}
