using System;
using System.IO;
using NAudio.Wave;
using NLayer;

namespace PartyPulse.Audio;

/// <summary>
/// Streams MP3 and uncompressed WAV as floating-point samples without Windows codecs.
/// A sound can be prepared on the UI thread and read/rewound on the output worker.
/// </summary>
internal sealed class SoundFileReader : WaveStream, ISampleProvider
{
    private readonly object gate = new();
    private readonly FileStream file;
    private readonly MpegFile? mpeg;
    private readonly WaveFileReader? wave;
    private readonly ISampleProvider? waveSamples;
    private float[] byteReadBuffer = [];
    private float volume = 1f;
    private bool disposed;

    public SoundFileReader(string path)
    {
        file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (Path.GetExtension(path).Equals(".mp3", StringComparison.OrdinalIgnoreCase))
            {
                // Borrow the stream so even a decoder constructor failure closes the file.
                mpeg = new MpegFile(file);
                WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(mpeg.SampleRate, mpeg.Channels);
                Length = mpeg.Length;
            }
            else if (Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase))
            {
                wave = new WaveFileReader(file);
                var format = wave.WaveFormat.AsStandardWaveFormat();
                if (!(format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample is 8 or 16 or 24 or 32) &&
                    !(format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample is 32 or 64))
                    throw new NotSupportedException("This WAV encoding is unsupported. Use an uncompressed PCM/float WAV or an MP3 file.");

                waveSamples = new StandardWaveProvider(wave, format).ToSampleProvider();
                WaveFormat = waveSamples.WaveFormat;
                Length = wave.Length / wave.WaveFormat.BlockAlign * WaveFormat.BlockAlign;
            }
            else
            {
                throw new NotSupportedException("Choose a WAV or MP3 sound file.");
            }

            if (Length <= 0)
                throw new InvalidDataException("The selected file contains no playable audio.");
        }
        catch
        {
            mpeg?.Dispose();
            wave?.Dispose();
            file.Dispose();
            throw;
        }
    }

    public override WaveFormat WaveFormat { get; }
    public override long Length { get; }

    public override long Position
    {
        get
        {
            lock (gate)
                return mpeg?.Position ?? wave!.Position / wave.WaveFormat.BlockAlign * WaveFormat.BlockAlign;
        }
        set
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (value < 0 || value > Length)
                    throw new ArgumentOutOfRangeException(nameof(value));
                var framePosition = value / WaveFormat.BlockAlign;
                if (mpeg is not null)
                    mpeg.Position = framePosition * WaveFormat.BlockAlign;
                else
                    wave!.Position = framePosition * wave.WaveFormat.BlockAlign;
            }
        }
    }

    public float Volume
    {
        get { lock (gate) return volume; }
        set { lock (gate) volume = float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0.5f; }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        ValidateBuffer(buffer, offset, count);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            count -= count % WaveFormat.Channels;
            var read = mpeg?.ReadSamples(buffer, offset, count) ?? waveSamples!.Read(buffer, offset, count);
            for (var i = offset; i < offset + read; i++)
                buffer[i] *= volume;
            return read;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBuffer(buffer, offset, count);
        lock (gate)
        {
            var samplesRequested = count / WaveFormat.BlockAlign * WaveFormat.Channels;
            if (byteReadBuffer.Length < samplesRequested)
                byteReadBuffer = new float[samplesRequested];
            var read = Read(byteReadBuffer, 0, samplesRequested) * sizeof(float);
            Buffer.BlockCopy(byteReadBuffer, 0, buffer, offset, read);
            return read;
        }
    }

    private static void ValidateBuffer<T>(T[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset > buffer.Length - count)
            throw new ArgumentOutOfRangeException(nameof(count));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (gate)
            {
                if (!disposed)
                {
                    disposed = true;
                    mpeg?.Dispose();
                    wave?.Dispose();
                    file.Dispose();
                }
            }
        }
        base.Dispose(disposing);
    }

    // Normalize PCM/float WAVEFORMATEXTENSIBLE headers without a codec conversion.
    private sealed class StandardWaveProvider(WaveFileReader source, WaveFormat format) : IWaveProvider
    {
        public WaveFormat WaveFormat => format;
        public int Read(byte[] buffer, int offset, int count) => source.Read(buffer, offset, count);
    }
}
