using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Concentus;
using Concentus.Enums;
using Watchroom.Core;

// Voice chat: the Opus pipeline, speech gate, jitter buffer and mixer, then real rooms over native WebRTC.
static class VoiceFixture
{
    const int Frame = VoicePacket.FrameSamples;

    public static void RunUnits(Action<bool, string> check)
    {
        Span<byte> packet = stackalloc byte[VoicePacket.HeaderBytes + VoicePacket.MaxPayload];
        var size = VoicePacket.Write(packet, 7, 65535, VoicePacket.EndOfSpeech, [1, 2, 3]);
        check(VoicePacket.TryRead(packet[..size], out var slot, out var sequence, out var flags, out var payload) && slot == 7 && sequence == 65535
            && flags == VoicePacket.EndOfSpeech && payload.SequenceEqual(new byte[] { 1, 2, 3 }), "voice packet header round-trips slot, sequence, end marker and payload");
        packet[0] = 9;
        check(!VoicePacket.TryRead(packet[..size], out _, out _, out _, out _) && !VoicePacket.TryRead(new byte[5], out _, out _, out _, out _)
            && !VoicePacket.TryRead(new byte[VoicePacket.HeaderBytes + VoicePacket.MaxPayload + 1], out _, out _, out _, out _),
            "unknown versions, empty payloads and oversized frames are rejected");

        // The speech gate: silence sends nothing, speech opens with pre-roll and closes with an end marker.
        var capture = new VoiceCapture { Transmitting = true };
        var frames = new List<(ushort Sequence, byte Flags, int Bytes)>();
        capture.Frame += frame => { VoicePacket.TryRead(frame, out _, out var s, out var f, out var p); frames.Add((s, f, p.Length)); };
        capture.Write(Tone(0, 50 * Frame, 440, 0));
        check(frames.Count == 0, "a silent microphone sends no voice frames");
        capture.Write(Noise(50 * Frame, 0.0005f));
        check(frames.Count == 0, "faint room noise below the speech threshold stays private");
        capture.Write(Tone(0, 25 * Frame, 220, 0.1f));
        check(frames.Count >= 25 && frames.Take(3).All(f => f.Flags == 0), $"speech opens the gate including the pre-roll frames ({frames.Count} frames)");
        check(frames.Zip(frames.Skip(1)).All(pair => (ushort)(pair.First.Sequence + 1) == pair.Second.Sequence), "voice frames carry consecutive sequence numbers");
        var speaking = capture.Speaking;
        check(speaking && capture.LevelDb > -30, $"the capture reports speaking and a speech level ({capture.LevelDb:F1} dBFS)");
        var beforeHangover = frames.Count;
        capture.Write(Tone(0, 10 * Frame, 440, 0));
        check(frames.Count == beforeHangover + 10 && frames[^1].Flags == 0, "short pauses between words keep the gate open");
        capture.Write(Tone(0, 20 * Frame, 440, 0));
        check(frames[^1].Flags == VoicePacket.EndOfSpeech && frames.Count(f => f.Flags == VoicePacket.EndOfSpeech) == 1, "the gate closes after the hangover with a single end-of-speech marker");
        var closed = frames.Count;
        capture.Write(Tone(0, 20 * Frame, 440, 0));
        check(frames.Count == closed, "the gate stays closed through continued silence");
        check(frames.All(f => f.Bytes is > 0 and <= VoicePacket.MaxPayload), $"Opus frames stay within the payload limit (largest {frames.Max(f => f.Bytes)} bytes)");
        var averageBytes = frames.Where(f => f.Flags == 0).Average(f => f.Bytes);
        check(averageBytes * 8 * 50 < 48000, $"speech averages a voice bitrate ({averageBytes * 8 * 50 / 1000:F0} kbit/s)");

        capture.Write(Tone(0, 5 * Frame, 220, 0.1f));
        capture.Transmitting = false;
        var muteCount = frames.Count;
        capture.Write(Tone(0, 2 * Frame, 220, 0.1f));
        check(frames.Count == muteCount + 1 && frames[^1].Flags == VoicePacket.EndOfSpeech, "muting mid-sentence sends one end marker and nothing more");
        capture.Write(Tone(0, 30 * Frame, 220, 0.1f));
        check(frames.Count == muteCount + 1, "a muted microphone sends nothing while the person keeps talking");

        var ptt = new VoiceCapture { PushToTalk = true, Transmitting = true };
        var pttFrames = 0; ptt.Frame += _ => pttFrames++;
        ptt.Write(Tone(0, 10 * Frame, 220, 0));
        check(pttFrames == 10, "push-to-talk sends while the key is held, even through silence");
        ptt.Transmitting = false; ptt.Write(Tone(0, 10 * Frame, 220, 0.1f));
        check(pttFrames == 11, "releasing push-to-talk ends the transmission immediately");

        var steady = new VoiceCapture { Transmitting = true };
        var steadyFrames = new List<int>(); var index = 0;
        steady.Frame += _ => steadyFrames.Add(index);
        for (index = 0; index < 1000; index++) steady.Write(Noise(Frame, 0.008f, index));
        check(steadyFrames.Count > 0 && steadyFrames.Max() < 500, $"a constant fan or hum is learned as background and stops transmitting (last frame {steadyFrames.DefaultIfEmpty().Max()})");
        var afterNoise = steadyFrames.Count;
        for (var i = 0; i < 10; i++) steady.Write(Mix(Noise(Frame, 0.008f, 2000 + i), Tone(i * Frame, Frame, 200, 0.12f)));
        check(steadyFrames.Count > afterNoise + 5, "speech over that background still opens the gate");

        // Opus round trip through the jitter buffer keeps the pitch and level of speech.
        var encoder = OpusCodecFactory.CreateEncoder(48000, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        encoder.UseInbandFEC = true; encoder.PacketLossPercent = 10; encoder.Bitrate = 32000;
        var encoded = new List<byte[]>();
        for (var i = 0; i < 100; i++)
        {
            var buffer = new byte[600];
            var length = encoder.Encode(Tone(i * Frame, Frame, 440, 0.3f), Frame, buffer, buffer.Length);
            encoded.Add(buffer[..length]);
        }
        var jitter = new VoiceJitterBuffer();
        var output = new List<float>();
        var block = new float[Frame];
        long now = 0;
        void Pull(int count) { for (var i = 0; i < count; i++) { now += 20; if (jitter.Pull(block, now)) output.AddRange(block); else output.AddRange(new float[Frame]); } }
        for (var i = 0; i < 2; i++) jitter.Push((ushort)i, 0, encoded[i], now);
        Pull(1);
        check(output.All(x => x == 0), "playback waits for a short prebuffer before the first frame");
        jitter.Push(2, 0, encoded[2], now);
        output.Clear(); Pull(3);
        var magnitude440 = Goertzel(output.Skip(Frame).ToArray(), 440); var magnitude660 = Goertzel(output.Skip(Frame).ToArray(), 660);
        check(magnitude440 > 0.05 && magnitude440 > magnitude660 * 10, $"decoded voice keeps its pitch (440 Hz {magnitude440:F3} vs 660 Hz {magnitude660:F3})");
        var rms = Math.Sqrt(output.Skip(Frame).Average(x => (double)x * x));
        check(rms is > 0.15 and < 0.3, $"decoded voice keeps its level (RMS {rms:F3} for a 0.21 RMS tone)");

        // Loss: one missing frame is rebuilt from the next frame's forward error correction.
        jitter = new VoiceJitterBuffer();
        for (ushort s = 0; s < 3; s++) jitter.Push(s, 0, encoded[s], now);
        Pull(1);
        jitter.Push(4, 0, encoded[4], now); jitter.Push(5, 0, encoded[5], now);
        output.Clear(); Pull(4);
        check(jitter.Concealed == 1 && Goertzel(output.Skip(Frame).Take(Frame).ToArray(), 440) > 0.05, "a single lost frame is recovered from forward error correction without a gap");

        // Reordering: frames that arrive swapped play in order.
        jitter = new VoiceJitterBuffer();
        for (var pair = 0; pair < 10; pair += 2)
        {
            jitter.Push((ushort)(pair + 1), 0, encoded[pair + 1], now); jitter.Push((ushort)pair, 0, encoded[pair], now);
            Pull(2);
        }
        check(jitter.Concealed == 0 && jitter.Late == 0, $"frames that arrive out of order play in sequence with no concealment (concealed {jitter.Concealed}, late {jitter.Late}, dropped {jitter.Dropped})");

        // Burst loss of 3 frames: concealment bridges it and playback continues.
        jitter = new VoiceJitterBuffer();
        for (ushort s = 0; s < 4; s++) jitter.Push(s, 0, encoded[s], now);
        Pull(4);
        for (ushort s = 7; s < 12; s++) jitter.Push(s, 0, encoded[s], now);
        output.Clear(); Pull(8);
        check(jitter.Concealed == 3 && jitter.Playing && output.Skip(3 * Frame).Take(5 * Frame).Any(x => Math.Abs(x) > 0.1),
            $"a burst loss is concealed and the following frames still play ({jitter.Concealed} concealed)");

        // A network stall: brief concealment without skipping ahead, then the late frames play in order.
        jitter = new VoiceJitterBuffer();
        for (ushort s = 0; s < 3; s++) jitter.Push(s, 0, encoded[s], now);
        Pull(3);
        Pull(2);
        check(jitter.Concealed == 2 && jitter.Playing, "a stall is bridged by concealment while waiting for late frames");
        for (ushort s = 3; s < 6; s++) jitter.Push(s, 0, encoded[s], now);
        Pull(3);
        check(jitter.Late == 0 && jitter.Buffered == 0, "frames delayed by a stall still play instead of being discarded as late");
        Pull(10);
        check(!jitter.Playing && jitter.Concealed == 2 + 5, "a speaker who vanishes without an end marker fades out after 100 ms of concealment");

        // The end-of-speech marker stops playback cleanly, and the next talk spurt starts with a new prebuffer.
        jitter = new VoiceJitterBuffer();
        for (ushort s = 0; s < 4; s++) jitter.Push(s, s == 3 ? VoicePacket.EndOfSpeech : (byte)0, encoded[s], now);
        Pull(6);
        check(!jitter.Playing && jitter.Concealed == 0, "an end-of-speech marker stops playback without concealment noise");
        now += 3000;
        for (ushort s = 4; s < 7; s++) jitter.Push(s, 0, encoded[s], now);
        output.Clear(); Pull(3);
        check(jitter.Playing && output.Any(x => Math.Abs(x) > 0.1), "the next sentence after a pause plays at once");

        // A short exclamation below the prebuffer target is not held back indefinitely.
        jitter = new VoiceJitterBuffer();
        jitter.Push(0, 0, encoded[0], now);
        output.Clear(); Pull(4);
        check(output.Any(x => Math.Abs(x) > 0.05), "a single short frame still plays after the prebuffer wait");

        // Bursts after a stall drain down so voice does not stay behind the video.
        jitter = new VoiceJitterBuffer();
        for (ushort s = 0; s < 30; s++) jitter.Push(s, 0, encoded[s], now);
        Pull(5);
        check(jitter.Dropped > 0 && jitter.Buffered <= jitter.TargetFrames + 4, $"a burst of queued frames is trimmed to keep latency low ({jitter.Dropped} dropped, {jitter.Buffered} queued)");

        // Sequence wrap-around and a sender that rejoins with a new sequence.
        jitter = new VoiceJitterBuffer();
        for (var i = 0; i < 6; i++) jitter.Push((ushort)(65533 + i), 0, encoded[i], now);
        Pull(6);
        check(jitter.Concealed == 0 && jitter.Late == 0, "sequence numbers wrap from 65535 to 0 without a glitch");
        now += 5000;
        for (ushort s = 30000; s < 30003; s++) jitter.Push(s, 0, encoded[s - 30000], now);
        output.Clear(); Pull(3);
        check(output.Any(x => Math.Abs(x) > 0.1), "a speaker who rejoins with a new sequence is heard immediately");
        jitter.Push(30001, 0, encoded[1], now);
        check(jitter.Late == 1, "a duplicate of a frame already played is ignored");

        // Corrupt frames from someone else's app are skipped as silence and never stop playback.
        jitter = new VoiceJitterBuffer();
        var garbage = new byte[80]; new Random(7).NextBytes(garbage); garbage[0] = 0xFF;
        jitter.Push(0, 0, encoded[0], now); jitter.Push(1, 0, garbage, now); jitter.Push(2, 0, [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF], now); jitter.Push(3, 0, encoded[3], now);
        for (ushort s = 4; s < 8; s++) jitter.Push(s, 0, encoded[s], now);
        var threw = false; output.Clear();
        try { Pull(8); } catch (Exception) { threw = true; }
        check(!threw && output.Skip(4 * Frame).Any(x => Math.Abs(x) > 0.1), $"corrupt voice frames are skipped and the speaker keeps playing ({jitter.Invalid} invalid)");
        var corruptMixer = new VoiceMixer(); var mixedOut = new float[Frame];
        threw = false;
        try { for (ushort s = 0; s < 20; s++) { corruptMixer.Receive(4, s, 0, s % 2 == 0 ? garbage : encoded[s]); corruptMixer.Read(mixedOut); } } catch (Exception) { threw = true; }
        check(!threw, "a stream of corrupt frames never throws on the audio device thread");

        // Someone who leaves and rejoins voice at once restarts with a lower sequence; they are heard within a few frames.
        jitter = new VoiceJitterBuffer();
        for (ushort s = 40000; s < 40010; s++) { jitter.Push(s, s == 40009 ? VoicePacket.EndOfSpeech : (byte)0, encoded[s - 40000], now); Pull(1); }
        Pull(3);
        var rejoin = 0; output.Clear();
        for (ushort s = 100; s < 130; s++) { jitter.Push(s, 0, encoded[s - 100], now); Pull(1); if (rejoin == 0 && output.Skip(output.Count - Frame).Any(x => Math.Abs(x) > 0.1)) rejoin = s - 99; }
        check(rejoin is > 0 and <= 12, $"a speaker who rejoins with a lower sequence moments later is heard within {rejoin} frames");

        // A delayed frame from the previous sentence cannot drag the next one backwards.
        jitter = new VoiceJitterBuffer();
        for (ushort s = 0; s < 10; s++) { jitter.Push(s, s == 9 ? VoicePacket.EndOfSpeech : (byte)0, encoded[s], now); Pull(1); }
        Pull(3);
        jitter.Push(20, 0, encoded[20], now); jitter.Push(5, 0, encoded[5], now); jitter.Push(21, 0, encoded[21], now); jitter.Push(22, 0, encoded[22], now);
        var concealedBefore = jitter.Concealed;
        Pull(3);
        check(jitter.Concealed == concealedBefore && jitter.Late >= 1, "a stale frame from the last sentence is ignored instead of replaying it and concealing the gap");

        // Mixing two people talking at once, fed one frame per speaker every 20 ms as the network delivers them.
        static float[] Play(VoiceMixer mixer, int frames, params (int Slot, List<byte[]> Frames)[] speakers)
        {
            var result = new float[frames * Frame];
            for (var i = 0; i < frames; i++)
            {
                foreach (var (speakerSlot, speakerFrames) in speakers) if (i < speakerFrames.Count) mixer.Receive(speakerSlot, (ushort)i, 0, speakerFrames[i]);
                // The audio device asks for uneven amounts; the mixer must not care.
                mixer.Read(result.AsSpan(i * Frame, 100)); mixer.Read(result.AsSpan(i * Frame + 100, Frame - 100));
            }
            return result;
        }
        var mixer = new VoiceMixer();
        var tenor = Encode(220, 0.2f, 50); var soprano = Encode(880, 0.2f, 50);
        var mixed = Play(mixer, 20, (1, tenor), (2, soprano));
        check(Goertzel(mixed[(Frame * 5)..], 220) > 0.05 && Goertzel(mixed[(Frame * 5)..], 880) > 0.05, "two people talking at once are both heard in the mix");
        check(mixer.IsSpeaking(1) && mixer.IsSpeaking(2) && !mixer.IsSpeaking(3) && mixer.AnyoneSpeaking, "speaking indicators follow each person's audio");
        mixer = new VoiceMixer(); mixer.SetGain(2, 0);
        mixed = Play(mixer, 20, (1, tenor), (2, soprano));
        check(Goertzel(mixed[(Frame * 5)..], 880) < 0.005 && Goertzel(mixed[(Frame * 5)..], 220) > 0.05 && mixer.IsSpeaking(2), "muting one person locally silences only them, while still showing when they talk");
        mixer = new VoiceMixer(); mixer.SetGain(2, 0);
        for (ushort s = 0; s < 5; s++) mixer.Receive(2, s, 0, soprano[s]);
        check(!mixer.AnyoneSpeaking && mixer.IsSpeaking(2), "someone muted locally never lowers the video");
        for (ushort s = 0; s < 5; s++) mixer.Receive(1, s, 0, tenor[s]);
        check(mixer.AnyoneSpeaking, "anyone else talking does lower it");
        var quiet = new VoiceMixer { Volume = 0 }; quiet.Cue(VoiceCues.Join);
        var cueOut = new float[Frame * 12]; quiet.Read(cueOut);
        check(cueOut.All(x => x == 0), "voice volume at zero also silences the join and leave chimes");
        var chime = new VoiceMixer(); chime.Cue(VoiceCues.Join); chime.Read(cueOut);
        check(cueOut.Max(Math.Abs) is > 0.05f and < 0.2f && Goertzel(cueOut[..4800], 660) > 0.01 && Goertzel(cueOut[4800..9600], 880) > 0.01 && !chime.AnyoneSpeaking,
            "the join chime is two soft rising notes that do not count as speech");
        var loud = new VoiceMixer();
        var shout = Encode(300, 0.9f, 20);
        var peaks = Play(loud, 20, (1, shout), (2, shout), (3, shout));
        check(peaks.All(x => Math.Abs(x) <= 1f) && peaks.Max(Math.Abs) > 0.85f, $"three loud voices are limited instead of clipping (peak {peaks.Max(Math.Abs):F2})");
        loud = new VoiceMixer { Volume = 0 };
        peaks = Play(loud, 20, (1, shout));
        check(peaks.All(x => x == 0), "voice volume at zero silences everyone");
        var idle = new VoiceMixer(); var silence = new float[Frame];
        check(idle.Read(silence) == Frame && silence.All(x => x == 0) && !idle.AnyoneSpeaking, "an empty voice room plays silence");
    }

    public static async Task RunIsolated(string sdk, string serverDll, string root, Action<bool, string> check)
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var url = $"http://localhost:{port}";
        var start = new ProcessStartInfo(sdk) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(serverDll); start.ArgumentList.Add("--urls"); start.ArgumentList.Add(url);
        start.Environment["WATCHROOM_DATA"] = Path.Combine(root, "voice-server");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        try
        {
            using var http = new HttpClient(); var ready = false;
            for (var i = 0; i < 100; i++)
            {
                try { if ((await http.GetAsync(url + "/health")).IsSuccessStatusCode) { ready = true; break; } } catch (HttpRequestException) { }
                await Task.Delay(100);
            }
            if (!ready) throw new Exception("Voice test server did not start");
            await Run(url, check);
        }
        finally { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); await Task.WhenAll(output, errors); }
    }

    sealed class Sink : IVoiceSink
    {
        public readonly ConcurrentQueue<(int Slot, ushort Sequence, long At, byte[] Payload)> Frames = new();
        public void Receive(int slot, ushort sequence, byte flags, ReadOnlySpan<byte> payload) => Frames.Enqueue((slot, sequence, Stopwatch.GetTimestamp(), payload.ToArray()));
        public int From(int slot) => Frames.Count(f => f.Slot == slot);
        public void Clear() => Frames.Clear();
    }
    // A talker whose microphone is a tone generator; frames go through the real capture pipeline.
    sealed class Talker
    {
        private readonly VoiceCapture capture = new() { Transmitting = true };
        private readonly ConcurrentDictionary<ushort, long> sent = new();
        private int position;
        public Talker(RoomClient room) => capture.Frame += frame =>
        {
            VoicePacket.TryRead(frame, out _, out var sequence, out _, out _);
            sent[sequence] = Stopwatch.GetTimestamp(); room.TransmitVoice(frame);
        };
        public IReadOnlyDictionary<ushort, long> Sent => sent;
        public async Task Speak(int frames, double hz)
        {
            for (var i = 0; i < frames; i++) { capture.Write(Tone(position, Frame, hz, 0.2f)); position += Frame; await Task.Delay(20); }
            capture.Write(Tone(0, 22 * Frame, hz, 0));
        }
    }

    static async Task Run(string url, Action<bool, string> check)
    {
        reporter = check;
        await using var host = new RoomClient();
        await using var a = new RoomClient();
        await using var b = new RoomClient();
        var hostSink = new Sink(); var aSink = new Sink(); var bSink = new Sink();
        host.VoiceOutput = hostSink; a.VoiceOutput = aSink; b.VoiceOutput = bSink;
        await host.ConnectAsync(url, "Voice host");
        await Wait(() => host.Snapshot?.Voice?.Length == 1, "host advertises voice support with its own slot");
        check(host.VoiceAvailable && !host.Snapshot!.Voice![0].Joined, "the host starts out of voice");
        await a.ConnectAsync(url, "Voice A", host.Identity!.Room);
        await Wait(() => a.Snapshot is not null);
        check(!a.VoiceAvailable, "a guest waiting for admission cannot use voice yet");
        // Asking to join voice before admission is remembered and applied once the host lets them in.
        a.SetVoice(true, false);
        host.Send(new("admit", Target: a.Identity!.Peer));
        await a.DirectControlsReady.WaitAsync(TimeSpan.FromSeconds(25));
        await Wait(() => host.VoiceState(a.Identity.Peer)?.Joined == true, "a guest who chose voice while waiting is in voice once admitted");
        await b.ConnectAsync(url, "Voice B", host.Identity.Room);
        await Wait(() => host.Snapshot!.People.Any(p => p.Id == b.Identity!.Peer));
        host.Send(new("admit", Target: b.Identity!.Peer));
        await b.DirectControlsReady.WaitAsync(TimeSpan.FromSeconds(25));
        await Wait(() => b.Snapshot?.Voice?.Length == 3 && b.VoiceAvailable, "every participant's voice state reaches every guest");
        var slots = host.Snapshot!.Voice!.Select(v => v.Slot).ToArray();
        check(slots.Distinct().Count() == 3 && slots.All(s => s is > 0 and <= 250), "each participant gets a distinct voice slot");
        int Slot(RoomClient client) => host.VoiceState(client.Identity!.Peer)!.Slot;
        await Wait(() => a.Snapshot?.Voice?.Single(v => v.Peer == a.Identity.Peer).Joined == true && a.InVoice, "a guest sees itself in voice");
        await WaitForVoiceChannels(host, a, b);

        var talkerA = new Talker(a);
        await talkerA.Speak(25, 440);
        await Task.Delay(300);
        check(hostSink.Frames.IsEmpty && bSink.Frames.IsEmpty, "nobody outside voice hears a guest talking");

        b.SetVoice(true, false); host.SetVoice(true, false);
        await Wait(() => host.VoiceState(b.Identity.Peer)?.Joined == true && host.VoiceState(host.Identity.Peer)?.Joined == true, "host and second guest join voice");
        await Wait(() => a.VoiceState(b.Identity.Peer)?.Joined == true && a.VoiceState(host.Identity.Peer)?.Joined == true);
        await talkerA.Speak(30, 440);
        await Wait(() => bSink.From(Slot(a)) >= 28 && hostSink.From(Slot(a)) >= 28, "the host and the other guest both hear a guest talking");
        check(aSink.Frames.IsEmpty, "a guest never hears their own voice relayed back");
        var frequency = Decode(bSink.Frames.Where(f => f.Slot == Slot(a)).Select(f => f.Payload));
        check(Goertzel(frequency, 440) > 0.05 && Goertzel(frequency, 440) > Goertzel(frequency, 660) * 10, "relayed audio decodes to the speaker's own voice at the other guest");
        var latencies = bSink.Frames.Where(f => f.Slot == Slot(a) && talkerA.Sent.ContainsKey(f.Sequence))
            .Select(f => (f.At - talkerA.Sent[f.Sequence]) * 1000.0 / Stopwatch.Frequency).Order().ToArray();
        var p95 = latencies[(int)(latencies.Length * 0.95)];
        check(p95 < 100, $"guest-to-guest relay through the host adds little delay (median {latencies[latencies.Length / 2]:F1} ms, p95 {p95:F1} ms)");

        hostSink.Clear(); aSink.Clear(); bSink.Clear();
        var talkerHost = new Talker(host);
        await talkerHost.Speak(25, 660);
        await Wait(() => aSink.From(Slot(host)) >= 23 && bSink.From(Slot(host)) >= 23, "both guests hear the host");
        check(hostSink.Frames.IsEmpty, "the host does not hear itself");
        var hostVoice = Decode(aSink.Frames.Where(f => f.Slot == Slot(host)).Select(f => f.Payload));
        check(Goertzel(hostVoice, 660) > Goertzel(hostVoice, 440) * 10, "guests hear the host's own voice");

        // Talking over each other: both voices arrive, labelled with the right speaker.
        hostSink.Clear(); aSink.Clear(); bSink.Clear();
        var talkerB = new Talker(b);
        await Task.WhenAll(talkerA.Speak(25, 440), talkerB.Speak(25, 880));
        await Wait(() => hostSink.From(Slot(a)) >= 23 && hostSink.From(Slot(b)) >= 23 && aSink.From(Slot(b)) >= 23 && bSink.From(Slot(a)) >= 23,
            "two guests talking at once are both heard by everyone else");
        var fromB = Decode(aSink.Frames.Where(f => f.Slot == Slot(b)).Select(f => f.Payload));
        check(Goertzel(fromB, 880) > Goertzel(fromB, 440) * 10, "overlapping speakers stay separate streams instead of being mixed by the host");

        // Muting: the host refuses to relay a muted guest even if their app keeps sending.
        b.SetVoice(true, true);
        await Wait(() => host.VoiceState(b.Identity.Peer)?.Muted == true && a.VoiceState(b.Identity.Peer)?.Muted == true, "muting is visible to the whole room");
        hostSink.Clear(); aSink.Clear();
        await talkerB.Speak(20, 880);
        check(aSink.From(Slot(b)) == 0, "a muted guest's app stops sending");
        var forged = new byte[VoicePacket.HeaderBytes + 3];
        for (ushort s = 0; s < 20; s++) { VoicePacket.Write(forged, 0, s, 0, [0xF8, 0xFF, 0xFE]); Peers(b)[host.Identity.Peer].SendVoice(forged); }
        await Task.Delay(400);
        check(hostSink.Frames.IsEmpty && aSink.Frames.IsEmpty, "the host drops frames from a muted guest even if they are sent anyway");
        b.SetVoice(true, false);
        await Wait(() => host.VoiceState(b.Identity.Peer)?.Muted == false);

        // A guest cannot impersonate another speaker's slot.
        aSink.Clear(); hostSink.Clear();
        for (ushort s = 100; s < 110; s++) { VoicePacket.Write(forged, (byte)Slot(host), s, 0, [0xF8, 0xFF, 0xFE]); Peers(b)[host.Identity.Peer].SendVoice(forged); }
        await Wait(() => aSink.Frames.Count >= 9);
        check(aSink.Frames.All(f => f.Slot == Slot(b)), "the host labels relayed frames with the real sender, ignoring a forged speaker slot");

        // Flooding: far more frames than real time allows are capped by the host.
        aSink.Clear(); hostSink.Clear();
        for (ushort s = 0; s < 1000; s++) { VoicePacket.Write(forged, 0, (ushort)(200 + s), 0, [0xF8, 0xFF, 0xFE]); Peers(b)[host.Identity.Peer].SendVoice(forged); }
        await Task.Delay(800);
        check(aSink.From(Slot(b)) is > 0 and < 160, $"a guest flooding voice frames is limited to real-time rates ({aSink.From(Slot(b))} of 1000 relayed)");
        var garbage = new byte[1200]; Random.Shared.NextBytes(garbage); garbage[0] = 1;
        for (var i = 0; i < 20; i++) Peers(b)[host.Identity.Peer].SendVoice(garbage);
        await Task.Delay(300);
        check(host.IsConnected && b.IsConnected && host.Snapshot!.People.Count(p => p.Approved) == 3, "malformed voice frames are ignored without disconnecting anyone");

        // Leaving voice stops hearing it.
        b.SetVoice(false, false);
        await Wait(() => host.VoiceState(b.Identity.Peer)?.Joined == false, "a guest leaves voice");
        await Task.Delay(1100);
        bSink.Clear();
        await talkerA.Speak(15, 440);
        await Task.Delay(300);
        check(bSink.Frames.IsEmpty, "after leaving voice a guest no longer receives anyone's audio");
        check(hostSink.From(Slot(a)) >= 13, "people still in voice keep hearing each other");
        host.SetVoice(false, false);
        await Wait(() => a.VoiceState(host.Identity.Peer)?.Joined == false);
        hostSink.Clear();
        await talkerA.Speak(10, 440);
        await Task.Delay(300);
        check(hostSink.Frames.IsEmpty, "the host stops hearing voice after leaving it, though it keeps relaying for guests");
        host.SetVoice(true, false); b.SetVoice(true, false);
        await Wait(() => host.VoiceState(b.Identity.Peer)?.Joined == true && host.VoiceState(host.Identity.Peer)?.Joined == true);

        // A connection drop between a guest and the host restores voice on the replacement connection.
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        a.Message += message => { if (message.Type == "peer-recovered") recovered.TrySetResult(); };
        Peers(host)[a.Identity.Peer].Dispose();
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await Wait(() => host.VoiceState(a.Identity.Peer)?.Joined == true, "a guest's voice state is restored after its connection to the host recovers");
        await WaitForVoiceChannels(host, a);
        hostSink.Clear(); bSink.Clear();
        await talkerA.Speak(25, 440);
        await Wait(() => hostSink.From(Slot(a)) >= 20 && bSink.From(Slot(a)) >= 20, "voice works again over the recovered connection");

        // Someone joining later hears the conversation.
        await using var c = new RoomClient(); var cSink = new Sink(); c.VoiceOutput = cSink;
        await c.ConnectAsync(url, "Voice C", host.Identity.Room);
        await Wait(() => host.Snapshot!.People.Any(p => p.Id == c.Identity!.Peer));
        host.Send(new("admit", Target: c.Identity!.Peer));
        await c.DirectControlsReady.WaitAsync(TimeSpan.FromSeconds(25));
        await Wait(() => c.Snapshot?.Voice?.Count(v => v.Joined) == 3, "a late joiner sees who is already in voice");
        c.SetVoice(true, false);
        await Wait(() => host.VoiceState(c.Identity.Peer)?.Joined == true);
        await WaitForVoiceChannels(host, c);
        await talkerA.Speak(25, 440);
        await Wait(() => cSink.From(Slot(a)) >= 20, "a late joiner hears the people already talking");

        // A guest leaving the room is removed from voice for everyone.
        var leaving = a.Identity.Peer;
        await a.DisposeAsync();
        await Wait(() => host.VoiceState(leaving) is null && b.VoiceState(leaving) is null && c.VoiceState(leaving) is null, "a guest who leaves the room disappears from voice for everyone");
        hostSink.Clear(); cSink.Clear();
        await talkerB.Speak(20, 880);
        await Wait(() => hostSink.From(Slot(b)) >= 15 && cSink.From(Slot(b)) >= 15, "the remaining people keep talking after someone leaves");
    }

    static async Task WaitForVoiceChannels(RoomClient host, params RoomClient[] guests)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!guests.All(g => Peers(host).TryGetValue(g.Identity!.Peer, out var p) && p.IsVoiceOpen && Peers(g).TryGetValue(host.Identity!.Peer, out var q) && q.IsVoiceOpen))
        {
            if (DateTime.UtcNow > until) throw new Exception("Timeout: voice channels did not open");
            await Task.Delay(50);
        }
    }
    static ConcurrentDictionary<string, PeerTransport> Peers(RoomClient client) =>
        (ConcurrentDictionary<string, PeerTransport>)typeof(RoomClient).GetField("peers", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(client)!;
    static Action<bool, string>? reporter;
    static async Task Wait(Func<bool> condition, string? name = null)
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (!condition()) { if (DateTime.UtcNow > until) throw new Exception("Timeout: " + (name ?? "voice condition")); await Task.Delay(25); }
        if (name is not null) reporter?.Invoke(true, name);
    }

    static short[] Tone(int start, int count, double hz, float amplitude) =>
        Enumerable.Range(start, count).Select(i => (short)(Math.Sin(2 * Math.PI * hz * i / 48000) * amplitude * 32767)).ToArray();
    static short[] Noise(int count, float amplitude, int seed = 1)
    {
        var random = new Random(seed);
        return Enumerable.Range(0, count).Select(_ => (short)((random.NextDouble() * 2 - 1) * amplitude * 32767 * 1.7)).ToArray();
    }
    static short[] Mix(short[] a, short[] b) => a.Zip(b, (x, y) => (short)Math.Clamp(x + y, short.MinValue, short.MaxValue)).ToArray();
    static List<byte[]> Encode(double hz, float amplitude, int frames)
    {
        var encoder = OpusCodecFactory.CreateEncoder(48000, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        var result = new List<byte[]>();
        for (var i = 0; i < frames; i++) { var buffer = new byte[600]; result.Add(buffer[..encoder.Encode(Tone(i * Frame, Frame, hz, amplitude), Frame, buffer, 600)]); }
        return result;
    }
    static float[] Decode(IEnumerable<byte[]> payloads)
    {
        var decoder = OpusCodecFactory.CreateDecoder(48000, 1);
        var samples = new List<float>(); var block = new float[Frame];
        foreach (var payload in payloads) { decoder.Decode(payload, block, Frame, false); samples.AddRange(block); }
        return samples.Skip(Frame * 3).ToArray();
    }
    // Normalized magnitude of one frequency, roughly the amplitude of that sine in the signal.
    static double Goertzel(IReadOnlyList<float> samples, double hz)
    {
        if (samples.Count == 0) return 0;
        var coefficient = 2 * Math.Cos(2 * Math.PI * hz / 48000);
        double previous = 0, beforePrevious = 0;
        foreach (var sample in samples) { var value = sample + coefficient * previous - beforePrevious; beforePrevious = previous; previous = value; }
        return Math.Sqrt(Math.Max(0, previous * previous + beforePrevious * beforePrevious - coefficient * previous * beforePrevious)) * 2 / samples.Count;
    }
}
