using Vortice.Multimedia;
using Vortice.XAudio2;

namespace NodeRunner.App.Audio;

// Plays one sound at a time (16-bit, mono or interleaved stereo) through XAudio2, from the UI thread. Never throws: if
// the sound engine cannot start (no audio device), Error says why and nothing plays.
internal sealed class SoundOutput : IDisposable
{
    private IXAudio2? _engine;
    private IXAudio2MasteringVoice? _master;
    private bool _triedEngine;
    private IXAudio2SourceVoice? _voice;
    private AudioBuffer? _buffer;
    private AudioBuffer? _loopBuffer;

    private short[]? _samples;
    private int _channels = 1;
    private int _rate;
    private long _frames;
    private long _startFrame;
    private int _loopStart = -1;
    private long _pausedAt = -1;
    private float _volume = 0.8f;

    public string? Error { get; private set; }

    // The sound loaded (null if none); IsPlaying is false when paused or finished.
    public short[]? Samples => _samples;
    public int Channels => _channels;
    public int SampleRate => _rate;
    public double Duration => _rate > 0 ? (double)_frames / _rate : 0;
    public bool Loop => _loopStart >= 0;
    public bool IsPaused => _pausedAt >= 0;

    public bool IsPlaying => _voice is not null && _pausedAt < 0 && (Loop || _voice.State.BuffersQueued > 0);

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            _voice?.SetVolume(_volume);
        }
    }

    // Seconds from the start of the sound.
    public double Position
    {
        get
        {
            if (_rate <= 0)
                return 0;
            long frame = _pausedAt >= 0 ? _pausedAt : _voice is null ? 0 : _startFrame + (long)_voice.State.SamplesPlayed;
            if (frame >= _frames)
                frame = Loop && _frames > _loopStart ? _loopStart + (frame - _frames) % (_frames - _loopStart) : _frames;
            return (double)frame / _rate;
        }
    }

    // loopStart: frame where the sound starts again at its end, -1 to play it once.
    public void Play(short[] samples, int channels, int sampleRate, int loopStart = -1, double from = 0)
    {
        Stop();
        _samples = samples;
        _channels = Math.Max(1, channels);
        _rate = sampleRate;
        _frames = samples.Length / _channels;
        _loopStart = loopStart >= 0 && loopStart < _frames ? loopStart : -1;
        Start((long)(from * sampleRate));
    }

    public void SetLoop(bool loop, int loopStart = 0)
    {
        if (loop == Loop)
            return;
        long at = (long)(Position * _rate);
        _loopStart = loop ? Math.Clamp(loopStart, 0, (int)Math.Max(0, _frames - 1)) : -1;
        if (_voice is not null && _pausedAt < 0)
            Start(at);
    }

    public void Seek(double seconds)
    {
        long frame = Math.Clamp((long)(seconds * _rate), 0, Math.Max(0, _frames - 1));
        if (_pausedAt >= 0)
            _pausedAt = frame;
        else if (_samples is not null)
            Start(frame);
    }

    public void Pause()
    {
        if (_voice is null || _pausedAt >= 0)
            return;
        long frame = (long)(Position * _rate);
        DestroyVoice();
        _pausedAt = frame;
    }

    public void Resume()
    {
        if (_samples is null)
            return;
        long frame = _pausedAt >= 0 ? _pausedAt : 0;
        if (!Loop && frame >= _frames - 1)
            frame = 0;
        Start(frame);
    }

    // Unloads the sound.
    public void Stop()
    {
        DestroyVoice();
        _samples = null;
        _frames = 0;
        _pausedAt = -1;
    }

    private void Start(long frame)
    {
        DestroyVoice();
        _pausedAt = -1;
        if (_samples is null || _frames == 0 || !EnsureEngine())
            return;
        frame = Math.Clamp(frame, 0, _frames - 1);
        try
        {
            _voice = _engine!.CreateSourceVoice(new WaveFormat(_rate, 16, _channels), false);
            _voice.SetVolume(_volume);
            _buffer = AudioBuffer.Create(_samples);
            _buffer.PlayBegin = (uint)frame;
            _buffer.PlayLength = (uint)(_frames - frame);
            if (Loop && frame <= _loopStart)
            {
                SetLoopRegion(_buffer);
                _voice.SubmitSourceBuffer(_buffer, null);
            }
            else if (Loop)
            {
                // Started inside the loop: the rest once, then the same samples looping from the loop start.
                _buffer.Flags = BufferFlags.None;
                _voice.SubmitSourceBuffer(_buffer, null);
                _loopBuffer = new AudioBuffer(_buffer.AudioDataPointer, _buffer.AudioBytes, BufferFlags.EndOfStream);
                SetLoopRegion(_loopBuffer);
                _voice.SubmitSourceBuffer(_loopBuffer, null);
            }
            else
            {
                _buffer.Flags = BufferFlags.EndOfStream;
                _voice.SubmitSourceBuffer(_buffer, null);
            }
            _startFrame = frame;
            _voice.Start();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            DestroyVoice();
        }
    }

    private void SetLoopRegion(AudioBuffer buffer)
    {
        buffer.PlayBegin = Math.Min(buffer.PlayBegin, (uint)_loopStart);
        buffer.PlayLength = (uint)(_frames - buffer.PlayBegin);
        buffer.LoopBegin = (uint)_loopStart;
        buffer.LoopLength = 0;
        buffer.LoopCount = (uint)XAudio2.LoopInfinite;
        buffer.Flags = BufferFlags.EndOfStream;
    }

    private bool EnsureEngine()
    {
        if (_master is not null)
            return true;
        if (_triedEngine)
            return false;
        _triedEngine = true;
        try
        {
            _engine = XAudio2.XAudio2Create(ProcessorSpecifier.DefaultProcessor, false);
            _master = _engine.CreateMasteringVoice(0, 0, AudioStreamCategory.GameEffects);
            return true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            _engine?.Dispose();
            _engine = null;
            return false;
        }
    }

    private void DestroyVoice()
    {
        if (_voice is not null)
        {
            try
            {
                _voice.Stop();
                _voice.FlushSourceBuffers();
            }
            catch (Exception)
            {
                // A lost device: the voice is destroyed anyway.
            }
            _voice.DestroyVoice();
            _voice.Dispose();
            _voice = null;
        }
        _loopBuffer = null;
        _buffer?.Dispose();
        _buffer = null;
    }

    public void Dispose()
    {
        Stop();
        _master?.DestroyVoice();
        _master?.Dispose();
        _master = null;
        _engine?.Dispose();
        _engine = null;
    }
}
