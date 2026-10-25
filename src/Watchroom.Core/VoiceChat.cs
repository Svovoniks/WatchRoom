using System.Buffers.Binary;
using Concentus;
using Concentus.Enums;

namespace Watchroom.Core;

// Room voice travels as Opus frames on an unreliable, unordered data channel. Guests send to the host,
// and the host stamps each frame with the speaker's slot before relaying it to everyone else in voice.
public static class VoicePacket
{
    public const int SampleRate = 48000, FrameSamples = 960, FrameMs = 20, HeaderBytes = 5, MaxPayload = 600;
    public const byte Version = 1;
    // The last frame of a talk spurt lets listeners stop without concealing a loss that never happened.
    public const byte EndOfSpeech = 1;
    public static int Write(Span<byte> frame, byte slot, ushort sequence, byte flags, ReadOnlySpan<byte> payload)
    {
        if (payload.Length is < 1 or > MaxPayload || frame.Length < HeaderBytes + payload.Length) throw new ArgumentException("Invalid voice payload");
        frame[0] = Version; frame[1] = slot;
        BinaryPrimitives.WriteUInt16LittleEndian(frame[2..], sequence);
        frame[4] = flags;
        payload.CopyTo(frame[HeaderBytes..]);
        return HeaderBytes + payload.Length;
    }
    public static bool TryRead(ReadOnlySpan<byte> frame, out byte slot, out ushort sequence, out byte flags, out ReadOnlySpan<byte> payload)
    {
        slot = 0; sequence = 0; flags = 0; payload = default;
        if (frame.Length is < HeaderBytes + 1 or > HeaderBytes + MaxPayload || frame[0] != Version) return false;
        slot = frame[1]; sequence = BinaryPrimitives.ReadUInt16LittleEndian(frame[2..]); flags = frame[4];
        payload = frame[HeaderBytes..];
        return true;
    }
}

public record VoiceMember(string Peer, int Slot, bool Joined, bool Muted);

// Receives relayed frames on a network thread.
public interface IVoiceSink
{
    void Receive(int slot, ushort sequence, byte flags, ReadOnlySpan<byte> payload);
}

// Turns microphone samples into Opus frames. Speech opens a gate that tracks the background noise
// level, a short pre-roll keeps the first syllable, and a hangover keeps pauses between words.
public sealed class VoiceCapture
{
    private readonly IOpusEncoder encoder;
    private readonly short[] frame = new short[VoicePacket.FrameSamples];
    private readonly short[][] preroll = [new short[VoicePacket.FrameSamples], new short[VoicePacket.FrameSamples]];
    private readonly byte[] encoded = new byte[VoicePacket.MaxPayload];
    private readonly byte[] packet = new byte[VoicePacket.HeaderBytes + VoicePacket.MaxPayload];
    private readonly object gate = new();
    private int filled, prerollCount, prerollNext, hangover;
    private ushort sequence = (ushort)Random.Shared.Next(ushort.MaxValue);
    private double noiseFloor = -60;
    private bool open;
    private long lastSpeech;
    // Raised on the capture thread with a complete frame; slot 0 lets the host fill in the speaker.
    public event VoiceFrameHandler? Frame;
    public delegate void VoiceFrameHandler(ReadOnlySpan<byte> frame);
    // Whether the gate may open at all: false while muted or when push-to-talk is released.
    public bool Transmitting { get; set; }
    // Push-to-talk sends everything while the key is held instead of waiting for speech.
    public bool PushToTalk { get; set; }
    public double LevelDb { get; private set; } = -100;
    public bool Speaking => Environment.TickCount64 - Volatile.Read(ref lastSpeech) < 250;
    public VoiceCapture()
    {
        encoder = OpusCodecFactory.CreateEncoder(VoicePacket.SampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        encoder.Bitrate = 32000; encoder.UseInbandFEC = true; encoder.PacketLossPercent = 10;
        encoder.SignalType = OpusSignal.OPUS_SIGNAL_VOICE; encoder.Complexity = 8; encoder.UseVBR = true;
    }
    public void Write(ReadOnlySpan<short> samples)
    {
        lock (gate)
        {
            while (samples.Length > 0)
            {
                var count = Math.Min(samples.Length, frame.Length - filled);
                samples[..count].CopyTo(frame.AsSpan(filled)); filled += count; samples = samples[count..];
                if (filled < frame.Length) continue;
                filled = 0; Process();
            }
        }
    }
    private void Process()
    {
        double sum = 0;
        foreach (var sample in frame) sum += (double)sample * sample;
        var level = 10 * Math.Log10(sum / frame.Length / (32768.0 * 32768.0) + 1e-10);
        LevelDb = level;
        // The floor falls quickly to quiet frames and rises slowly so speech does not raise it.
        noiseFloor = level < noiseFloor ? noiseFloor + (level - noiseFloor) * 0.3 : noiseFloor + Math.Min(level - noiseFloor, 6) * 0.004;
        var speech = level > Math.Max(-52, Math.Min(noiseFloor + 12, -30));
        if (!Transmitting)
        {
            if (open) Send(EndFrame(), VoicePacket.EndOfSpeech);
            open = false; hangover = 0; prerollCount = 0;
            return;
        }
        if (PushToTalk || speech) { hangover = 20; Volatile.Write(ref lastSpeech, Environment.TickCount64); }
        if (!open && hangover > 0)
        {
            open = true; encoder.ResetState();
            // Frames from just before the gate opened carry the start of the first word.
            for (var i = 0; i < prerollCount; i++) Send(preroll[(prerollNext - prerollCount + i + preroll.Length) % preroll.Length], 0);
            prerollCount = 0;
        }
        if (open)
        {
            if (--hangover > 0) Send(frame, 0);
            else { open = false; Send(frame, VoicePacket.EndOfSpeech); }
            return;
        }
        frame.CopyTo(preroll[prerollNext], 0);
        prerollNext = (prerollNext + 1) % preroll.Length; prerollCount = Math.Min(prerollCount + 1, preroll.Length);
    }
    private short[] EndFrame() { Array.Clear(frame); return frame; }
    private void Send(short[] samples, byte flags)
    {
        var length = encoder.Encode(samples, VoicePacket.FrameSamples, encoded, encoded.Length);
        if (length <= 0) return;
        var size = VoicePacket.Write(packet, 0, sequence++, flags, encoded.AsSpan(0, length));
        Frame?.Invoke(packet.AsSpan(0, size));
    }
}

// One remote speaker: reorders frames, conceals short losses with Opus FEC or loss concealment,
// waits briefly for late frames, and drops frames when the queue grows too far behind real time.
public sealed class VoiceJitterBuffer
{
    private const int Capacity = 64, MaxConcealed = 5;
    private readonly IOpusDecoder decoder = OpusCodecFactory.CreateDecoder(VoicePacket.SampleRate, 1);
    private readonly byte[]?[] packets = new byte[Capacity][];
    private readonly bool[] endings = new bool[Capacity];
    private readonly bool[] present = new bool[Capacity];
    private ushort expected, newest, lastPlayed;
    private bool playing, started, ended, hasPlayed;
    private int buffered, missing, lateRun;
    private long firstArrival, lastArrival;
    public int TargetFrames { get; set; } = 3;
    public int Buffered { get { lock (packets) return buffered; } }
    public int Concealed { get; private set; }
    public int Dropped { get; private set; }
    public int Late { get; private set; }
    public bool Playing { get { lock (packets) return playing; } }
    public void Push(ushort sequence, byte flags, ReadOnlySpan<byte> payload) => Push(sequence, flags, payload, Environment.TickCount64);
    internal void Push(ushort sequence, byte flags, ReadOnlySpan<byte> payload, long now)
    {
        lock (packets)
        {
            // After a second of silence any sequence starts afresh, so a speaker who rejoins voice is heard at once.
            if (started && !playing && buffered == 0 && now - lastArrival > 1000) { started = false; hasPlayed = false; }
            if (started)
            {
                var offset = (short)(sequence - expected);
                if (offset < 0)
                {
                    // Already played, concealed, or part of a finished talk spurt. A run of them means the sender
                    // restarted with a lower sequence (rejoining voice, or a new person in a reused slot): start over.
                    var stale = playing || buffered == 0 || (short)(newest - sequence) >= Capacity || hasPlayed && (short)(sequence - lastPlayed) <= 0;
                    if (stale && ++lateRun < 8) { Late++; return; }
                    if (stale) { Reset(); hasPlayed = false; }
                    else expected = sequence; // Arrived out of order before playback began.
                }
                else if (offset >= Capacity) Reset();
            }
            lateRun = 0; lastArrival = now;
            if (!started) { started = true; expected = newest = sequence; firstArrival = now; }
            else if (!playing && buffered == 0) { expected = newest = sequence; firstArrival = now; }
            var index = sequence % Capacity;
            if (present[index]) return;
            present[index] = true; packets[index] = payload.ToArray(); endings[index] = (flags & VoicePacket.EndOfSpeech) != 0; buffered++;
            if ((short)(sequence - newest) > 0) newest = sequence;
            if (!playing && (buffered >= TargetFrames || endings[index])) { playing = true; ended = false; missing = 0; }
        }
    }
    private void Reset()
    {
        Array.Clear(present); Array.Clear(packets); Array.Clear(endings); buffered = 0; playing = false; started = false; missing = 0;
    }
    // Fills one 20 ms frame; returns false when this speaker is silent.
    public bool Pull(Span<float> output) => Pull(output, Environment.TickCount64);
    internal bool Pull(Span<float> output, long now)
    {
        lock (packets)
        {
            if (!playing)
            {
                // Frames that trickle in slower than the prebuffer target still play once they have waited long enough.
                if (buffered == 0 || now - firstArrival < TargetFrames * VoicePacket.FrameMs) return false;
                playing = true; ended = false; missing = 0;
            }
            // Behind real time (a sender's clock running fast, or a burst after a network stall): catch up.
            while (buffered > TargetFrames + 4 && present[expected % Capacity])
            {
                Decode(expected, output); Take(expected); Played(); Dropped++;
            }
            var index = expected % Capacity;
            if (present[index])
            {
                Decode(expected, output);
                var last = endings[index];
                Take(expected); Played(); missing = 0;
                if (last) { playing = false; ended = true; }
                return true;
            }
            if (buffered == 0)
            {
                // Nothing newer has arrived: either the speaker stopped without an end marker or the
                // network is late. Conceal briefly without advancing so late frames still play in order.
                if (ended || ++missing > MaxConcealed) { playing = false; missing = 0; return false; }
                Run(output, ReadOnlySpan<byte>.Empty, false); Concealed++;
                return true;
            }
            // A newer frame exists, so this one was lost: recover it from the next frame's FEC data when possible.
            var next = (ushort)(expected + 1);
            Run(output, present[next % Capacity] ? packets[next % Capacity] : ReadOnlySpan<byte>.Empty, present[next % Capacity]);
            Concealed++; Played();
            return true;
        }
    }
    private void Played() { lastPlayed = expected; hasPlayed = true; expected++; }
    private void Decode(ushort sequence, Span<float> output) => Run(output, packets[sequence % Capacity], false);
    // Frames come from other people's apps; a corrupt one is skipped as silence rather than ever stopping playback.
    private void Run(Span<float> output, ReadOnlySpan<byte> data, bool fec)
    {
        try
        {
            var samples = decoder.Decode(data, output, VoicePacket.FrameSamples, fec);
            if (samples < VoicePacket.FrameSamples) output[Math.Max(0, samples)..].Clear();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            output.Clear(); Invalid++;
            try { decoder.ResetState(); } catch (Exception reset) when (reset is not OutOfMemoryException) { }
        }
    }
    public int Invalid { get; private set; }
    private void Take(ushort sequence)
    {
        var index = sequence % Capacity;
        if (!present[index]) return;
        present[index] = false; packets[index] = null; buffered--;
    }
}

// Mixes every remote speaker into one 48 kHz mono stream that the audio device pulls in real time.
public sealed class VoiceMixer : IVoiceSink
{
    private sealed class Speaker
    {
        public readonly VoiceJitterBuffer Buffer = new();
        public long LastPacket;
    }
    private readonly Dictionary<int, Speaker> speakers = [];
    private readonly float[] mixed = new float[VoicePacket.FrameSamples];
    private readonly float[] decoded = new float[VoicePacket.FrameSamples];
    private int offset = VoicePacket.FrameSamples;
    private readonly Dictionary<int, float> gains = [];
    private long lastSpeech;
    private float[]? cue;
    private int cueOffset;
    public float Volume { get; set; } = 1;
    // A short local sound, such as someone joining voice. It is not speech, so it never lowers the video.
    public void Cue(float[] samples) { lock (speakers) { cue = samples; cueOffset = 0; } }
    // Senders only transmit while their speech gate is open, so arriving frames mean someone is talking.
    // That keeps speaking indicators and video ducking working even when no speaker device is playing.
    public void Receive(int slot, ushort sequence, byte flags, ReadOnlySpan<byte> payload)
    {
        Speaker speaker;
        lock (speakers)
        {
            if (!speakers.TryGetValue(slot, out speaker!)) { if (speakers.Count >= 16) return; speakers[slot] = speaker = new(); }
            var now = Environment.TickCount64;
            speaker.LastPacket = now;
            if (Volume > 0 && gains.GetValueOrDefault(slot, 1f) > 0 && (flags & VoicePacket.EndOfSpeech) == 0) Volatile.Write(ref lastSpeech, now);
        }
        speaker.Buffer.Push(sequence, flags, payload);
    }
    public void SetGain(int slot, float gain) { lock (speakers) gains[slot] = Math.Clamp(gain, 0, 2); }
    public void Clear() { lock (speakers) { speakers.Clear(); offset = mixed.Length; } }
    public void Forget(int slot) { lock (speakers) speakers.Remove(slot); }
    public bool IsSpeaking(int slot) { lock (speakers) return speakers.TryGetValue(slot, out var s) && Environment.TickCount64 - s.LastPacket < 300; }
    // Only people you can hear count: someone you muted for yourself never lowers the video.
    public bool AnyoneSpeaking => Environment.TickCount64 - Volatile.Read(ref lastSpeech) < 300;
    public long LastSpeechTick => Volatile.Read(ref lastSpeech);
    public int Read(Span<float> output)
    {
        lock (speakers)
        {
            var written = 0;
            while (written < output.Length)
            {
                if (offset == mixed.Length) { MixFrame(); offset = 0; }
                var count = Math.Min(output.Length - written, mixed.Length - offset);
                mixed.AsSpan(offset, count).CopyTo(output[written..]);
                offset += count; written += count;
            }
            return written;
        }
    }
    private void MixFrame()
    {
        Array.Clear(mixed);
        var now = Environment.TickCount64;
        foreach (var (slot, speaker) in speakers.ToArray())
        {
            if (!speaker.Buffer.Pull(decoded))
            {
                // Speakers that have been silent for a minute release their decoder.
                if (now - speaker.LastPacket > 60000 && !speaker.Buffer.Playing) speakers.Remove(slot);
                continue;
            }
            var gain = Volume * gains.GetValueOrDefault(slot, 1f);
            for (var i = 0; i < decoded.Length; i++) mixed[i] += decoded[i] * gain;
        }
        if (cue is { } sound)
        {
            var count = Math.Min(mixed.Length, sound.Length - cueOffset);
            var volume = Math.Min(1, Volume);
            for (var i = 0; i < count; i++) mixed[i] += sound[cueOffset + i] * volume;
            cueOffset += count;
            if (cueOffset >= sound.Length) cue = null;
        }
        // Several people talking at once can exceed full scale; soften peaks instead of clipping.
        for (var i = 0; i < mixed.Length; i++)
        {
            var sample = mixed[i];
            if (sample is > 0.9f or < -0.9f) mixed[i] = MathF.Sign(sample) * (0.9f + 0.1f * MathF.Tanh((MathF.Abs(sample) - 0.9f) * 10));
        }
    }
}

// Soft two-note chimes: rising when someone joins voice, falling when they leave.
public static class VoiceCues
{
    public static readonly float[] Join = Notes(660, 880);
    public static readonly float[] Leave = Notes(660, 440);
    public static readonly float[] Test = [.. Notes(523, 659), .. Notes(784, 1047)];
    private static float[] Notes(double first, double second)
    {
        const int note = 4800; // 100 ms each
        var samples = new float[note * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var within = i % note;
            // Quick attack and a smooth decay keep the chime from clicking.
            var envelope = Math.Min(1, within / 240.0) * Math.Exp(-3.0 * within / note);
            samples[i] = (float)(0.12 * envelope * Math.Sin(2 * Math.PI * (i < note ? first : second) * i / VoicePacket.SampleRate));
        }
        return samples;
    }
}
