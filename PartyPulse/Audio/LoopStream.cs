using System;
using NAudio.Wave;

namespace PartyPulse.Audio;

/// <summary>Repeats a seekable audio stream without inserting silence at its end.</summary>
internal sealed class LoopStream : WaveStream
{
    private readonly WaveStream source;
    private readonly object gate = new();
    private bool disposed;

    public LoopStream(WaveStream source)
    {
        if (!source.CanSeek)
            throw new ArgumentException("The alert sound must be seekable.", nameof(source));

        this.source = source;
    }

    public override WaveFormat WaveFormat => source.WaveFormat;
    public override long Length => source.Length;

    public override long Position
    {
        get { lock (gate) return source.Position; }
        set { lock (gate) source.Position = value; }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        lock (gate)
        {
            if (disposed)
                return 0;

            var totalRead = 0;
            var justRewound = false;
            while (totalRead < count)
            {
                var read = source.Read(buffer, offset + totalRead, count - totalRead);
                if (read > 0)
                {
                    totalRead += read;
                    justRewound = false;
                    continue;
                }

                // Empty or undecodable streams must not spin on the audio thread.
                if (justRewound)
                    break;

                source.Position = 0;
                justRewound = true;
            }

            return totalRead;
        }
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
                    source.Dispose();
                }
            }
        }

        base.Dispose(disposing);
    }
}
