using NAudio.Wave;
using PartyPulse.Audio;

var checks = 0;
void Equal<T>(T actual, T expected, string name)
{
    if (!EqualityComparer<T>.Default.Equals(actual, expected))
        throw new Exception($"{name}: expected {expected}; got {actual}");
    checks++;
}

using (var loop = new LoopStream(new ByteWave([1, 2, 3])))
{
    var bytes = new byte[8];
    Equal(loop.Read(bytes, 0, bytes.Length), 8, "fills across repeated wraps");
    Equal(string.Join(',', bytes), "1,2,3,1,2,3,1,2", "wrap sequence");
    Equal(loop.Position, 2L, "position after wraps");
    var offset = new byte[] { 99, 99, 99, 99, 99 };
    Equal(loop.Read(offset, 1, 3), 3, "offset read count");
    Equal(string.Join(',', offset), "99,3,1,2,99", "respects caller offset");
    loop.Position = loop.Length;
    var restart = new byte[2];
    Equal(loop.Read(restart, 0, 2), 2, "starts at EOF");
    Equal(string.Join(',', restart), "1,2", "restarts at EOF");
    Equal(loop.Read([], 0, 0), 0, "zero byte request");
}

using (var loop = new LoopStream(new ByteWave([])))
    Equal(loop.Read(new byte[8], 0, 8), 0, "empty source terminates");

var source = new ByteWave([7]);
var owned = new LoopStream(source);
owned.Dispose();
owned.Dispose();
Equal(source.DisposalCount, 1, "source disposed exactly once");
Equal(owned.Read(new byte[8], 0, 8), 0, "late worker read after dispose");

using (var audio = new AlertAudio())
{
    audio.Configure("missing-alert-file.wav", 0.5f);
    Equal(audio.Error is not null, true, "missing file reports error");
    var missingFileError = audio.Error;
    audio.SetVolume(0f);
    audio.SetVolume(0.5f);
    audio.SetVolume(1f);
    Equal(audio.Error, missingFileError, "volume changes do not reload a missing file or clear its error");
    Equal(audio.IsPlaying, false, "volume changes do not start playback");
    audio.SetAlert(true);
    audio.Preview();
    Equal(audio.IsPlaying, false, "invalid file never plays");
    audio.Stop();
    Equal(audio.IsPreviewing, false, "stop cancels preview");
    var malformed = Path.Combine(AppContext.BaseDirectory, "malformed.wav");
    File.WriteAllText(malformed, "This is not audio");
    audio.Configure(malformed, float.NaN);
    Equal(audio.Error is not null, true, "corrupt WAV reports error");
    audio.Dispose();
    audio.SetVolume(0.8f);
    audio.Configure(malformed, 1f);
    audio.SetAlert(true);
    Equal(audio.IsPlaying, false, "disposed audio remains silent");
    File.Delete(malformed);
}

// The decoded samples use AudioFileReader.Volume, exactly as the alert engine does.
// No WaveOut device is constructed for these tests and no sound is played.
var generatedWave = Path.Combine(AppContext.BaseDirectory, "volume-check.wav");
try
{
    using (var writer = new WaveFileWriter(generatedWave, WaveFormat.CreateIeeeFloatWaveFormat(8000, 1)))
    {
        // Longer than five seconds also checks that recovery playback is never preview-truncated.
        var samples = Enumerable.Range(0, 50000).Select(i => 0.65f * MathF.Sin(i * 0.1f)).ToArray();
        writer.WriteSamples(samples, 0, samples.Length);
    }
    ValidateSound(generatedWave, "generated WAV");
    ValidateOneShot(generatedWave);
}
finally
{
    File.Delete(generatedWave);
}

foreach (var path in args)
    ValidateSound(Path.GetFullPath(path), "supplied sound");

Console.WriteLine($"PASS: {checks} audio checks (no sound played).");

void ValidateOneShot(string path)
{
    var outputs = new List<FakeWavePlayer>();
    using var audio = new OneShotAudio(() =>
    {
        Equal(outputs.All(output => output.PlaybackState != PlaybackState.Playing), true,
            "one-shot: old playback stops before a new device is created");
        var output = new FakeWavePlayer();
        outputs.Add(output);
        return output;
    });

    audio.Configure("missing-recovery-sound.mp3", 0.5f);
    Equal(audio.Error is not null, true, "one-shot: missing file error");
    audio.PlayOnce();
    Equal(outputs.Count, 0, "one-shot: invalid selection cannot play");

    audio.Configure(path, 1f);
    Equal(audio.Error, null, "one-shot: valid file clears configuration error");
    Equal(outputs.Count, 0, "one-shot: configure never opens or plays an output device");
    Equal(audio.IsPlaying, false, "one-shot: configure remains silent");
    audio.PlayOnce();
    Equal(audio.IsPlaying, true, "one-shot: explicit playback starts");
    var complete = outputs[^1];
    var expected = Decode(path, 1f);
    Equal(complete.PumpToEnd(), expected.Samples.Length * sizeof(float), "one-shot: complete file reaches finite EOF, including after five seconds");
    Equal(audio.IsPlaying, false, "one-shot: EOF ends playback");
    Equal(audio.Error, null, "one-shot: natural EOF is not an error");
    Equal(complete.IsDisposed, true, "one-shot: EOF releases output resources");

    audio.PlayOnce();
    var previous = outputs[^1];
    var firstBytes = previous.ReadBytes(512);
    var lateStopped = previous.CaptureStoppedCallback();
    audio.PlayOnce();
    var current = outputs[^1];
    Equal(previous.StopCount, 1, "one-shot: retrigger stops earlier playback");
    Equal(previous.IsDisposed, true, "one-shot: retrigger releases earlier playback");
    Equal(firstBytes.SequenceEqual(current.ReadBytes(512)), true, "one-shot: retrigger restarts at the beginning");
    lateStopped?.Invoke(previous, new StoppedEventArgs(new IOException("old callback")));
    Equal(audio.IsPlaying, true, "one-shot: stale callback cannot stop current playback");
    Equal(audio.Error, null, "one-shot: stale callback cannot set current error");

    var positionBeforeVolume = current.Position;
    audio.SetVolume(0f);
    Equal(current.Position, positionBeforeVolume, "one-shot: live volume does not seek");
    var mutedBytes = current.ReadBytes(512);
    var mutedSamples = new float[mutedBytes.Length / sizeof(float)];
    Buffer.BlockCopy(mutedBytes, 0, mutedSamples, 0, mutedBytes.Length);
    Equal(mutedSamples.All(sample => sample == 0f), true, "one-shot: live zero volume mutes decoded samples");
    audio.SetVolume(0.5f);
    var halfBytes = current.ReadBytes(512);
    var halfSamples = new float[halfBytes.Length / sizeof(float)];
    Buffer.BlockCopy(halfBytes, 0, halfSamples, 0, halfBytes.Length);
    Equal(halfSamples.Where((sample, index) => Math.Abs(sample - expected.Samples[256 + index] * 0.5f) > 0.000001f).Any(), false,
        "one-shot: live half volume scales only this sound's current samples");
    Equal(audio.IsPlaying, true, "one-shot: live volume leaves playback running");

    audio.Stop();
    Equal(audio.IsPlaying, false, "one-shot: stop silences playback");
    Equal(current.IsDisposed, true, "one-shot: stop releases resources");
    audio.PlayOnce();
    outputs[^1].Fail(new IOException("simulated device failure"));
    Equal(audio.Error?.Contains("simulated device failure"), true, "one-shot: device errors remain visible");
    Equal(audio.IsPlaying, false, "one-shot: device failure ends playback");
    audio.Configure(path, 0.5f);
    Equal(audio.Error, null, "one-shot: applying a valid sound clears a previous device error");

    var malformed = Path.Combine(AppContext.BaseDirectory, "malformed-recovery.wav");
    try
    {
        File.WriteAllText(malformed, "This is not audio");
        audio.PlayOnce();
        var beforeInvalidChange = outputs[^1];
        audio.Configure(malformed, 0.5f);
        Equal(audio.Error is not null, true, "one-shot: corrupt file error");
        Equal(beforeInvalidChange.IsDisposed, true, "one-shot: switching files stops previous sound even when new file is invalid");
        var previousCount = outputs.Count;
        audio.PlayOnce();
        Equal(outputs.Count, previousCount, "one-shot: corrupt file cannot play");
        audio.Configure(path, 0.5f);
        audio.PlayOnce();
        var beforeDispose = outputs[^1];
        audio.Dispose();
        audio.SetVolume(1f);
        audio.PlayOnce();
        Equal(audio.IsPlaying, false, "one-shot: disposed audio remains silent");
        Equal(beforeDispose.IsDisposed, true, "one-shot: dispose releases active playback");
    }
    finally
    {
        File.Delete(malformed);
    }
}

void ValidateSound(string path, string label)
{
    var full = Decode(path, 1f);
    var half = Decode(path, 0.5f);
    var muted = Decode(path, 0f);
    Equal(full.Samples.Length > 0, true, $"{label}: contains decoded samples");
    Equal(full.Samples.All(float.IsFinite), true, $"{label}: all samples finite");
    Equal(full.Samples.Any(sample => Math.Abs(sample) > 0.001f), true, $"{label}: contains audible signal");
    Equal(half.Samples.Length, full.Samples.Length, $"{label}: half volume preserves sample count");
    Equal(muted.Samples.Length, full.Samples.Length, $"{label}: mute preserves sample count");
    Equal(muted.Samples.All(sample => sample == 0f), true, $"{label}: zero volume produces silence");
    var maxHalfError = full.Samples.Zip(half.Samples, (original, scaled) => Math.Abs(scaled - original * 0.5f)).Max();
    Equal(maxHalfError < 0.000001f, true, $"{label}: half volume scales every sample by 0.5");

    using var live = new AudioFileReader(path);
    var chunk = new float[Math.Min(128, full.Samples.Length / 4)];
    var firstRead = live.Read(chunk, 0, chunk.Length);
    var positionBeforeVolumeChange = live.Position;
    live.Volume = 0.5f;
    Equal(live.Position, positionBeforeVolumeChange, $"{label}: live volume does not seek/restart");
    var secondRead = live.Read(chunk, 0, chunk.Length);
    var liveMatches = true;
    for (var i = 0; i < secondRead; i++)
        liveMatches &= Math.Abs(chunk[i] - full.Samples[firstRead + i] * 0.5f) < 0.000001f;
    Equal(secondRead > 0 && liveMatches, true, $"{label}: live gain applies from the current playback position");

    var duration = full.Samples.Length / (double)(full.Format.SampleRate * full.Format.Channels);
    Console.WriteLine($"Decoded {label}: {Path.GetFileName(path)}, {duration:F3}s, {full.Format.SampleRate} Hz, {full.Format.Channels} channel(s), {full.Format.BitsPerSample}-bit {full.Format.Encoding}, {full.Samples.Length} samples.");
}

static (WaveFormat Format, float[] Samples) Decode(string path, float volume)
{
    using var reader = new AudioFileReader(path) { Volume = volume };
    var samples = new List<float>();
    var buffer = new float[4096];
    int count;
    while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
        samples.AddRange(buffer.AsSpan(0, count).ToArray());
    return (reader.WaveFormat, samples.ToArray());
}

sealed class ByteWave(byte[] bytes) : WaveStream
{
    public int DisposalCount { get; private set; }
    public override WaveFormat WaveFormat => new(8000, 8, 1);
    public override long Length => bytes.Length;
    public override long Position { get; set; }
    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = (int)Math.Min(count, Length - Position);
        Array.Copy(bytes, (int)Position, buffer, offset, read);
        Position += read;
        return read;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) DisposalCount++;
        base.Dispose(disposing);
    }
}

// Deterministic in-memory output device; it never touches Windows audio APIs.
sealed class FakeWavePlayer : IWavePlayer
{
    private IWaveProvider? provider;
    public event EventHandler<StoppedEventArgs>? PlaybackStopped;
    public bool IsDisposed { get; private set; }
    public int StopCount { get; private set; }
    public PlaybackState PlaybackState { get; private set; } = PlaybackState.Stopped;
    public WaveFormat OutputWaveFormat => provider!.WaveFormat;
    public long Position => (provider as WaveStream)?.Position ?? 0;
    public float Volume
    {
        get => 1f;
        set => throw new Exception("One-shot volume must modify decoded samples, never the device volume.");
    }

    public void Init(IWaveProvider waveProvider) => provider = waveProvider;
    public void Play() => PlaybackState = PlaybackState.Playing;
    public void Pause() => PlaybackState = PlaybackState.Paused;
    public void Stop()
    {
        StopCount++;
        PlaybackState = PlaybackState.Stopped;
        PlaybackStopped?.Invoke(this, new StoppedEventArgs());
    }
    public void Dispose()
    {
        IsDisposed = true;
        PlaybackState = PlaybackState.Stopped;
    }
    public EventHandler<StoppedEventArgs>? CaptureStoppedCallback() => PlaybackStopped;
    public byte[] ReadBytes(int count)
    {
        var buffer = new byte[count];
        var read = provider!.Read(buffer, 0, count);
        return buffer.AsSpan(0, read).ToArray();
    }
    public int PumpToEnd()
    {
        var total = 0;
        var buffer = new byte[4096];
        int read;
        while ((read = provider!.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > 10_000_000)
                throw new Exception("Finite one-shot audio did not end.");
        }
        PlaybackState = PlaybackState.Stopped;
        PlaybackStopped?.Invoke(this, new StoppedEventArgs());
        return total;
    }
    public void Fail(Exception exception)
    {
        PlaybackState = PlaybackState.Stopped;
        PlaybackStopped?.Invoke(this, new StoppedEventArgs(exception));
    }
}
