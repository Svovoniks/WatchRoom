using System.Net;
using System.Net.Http.Json;
using Watchroom.Core;

static class RecoveryFixture
{
    public static async Task Run(Action<bool, string> check)
    {
        var pause = new PlaybackPause();
        var stop = new PlaybackState(0, "movie", false, 0, 0, new string('a', 32));
        pause.Request(stop, 10);
        for (var revision = 10; revision < 18; revision++)
            pause.Observe(new(revision, "movie", true, 5000, revision * 1000));
        check(pause.Pending, "local stop stays paused through seven seconds of stale playing updates");
        pause.Observe(new(18, "movie", false, 0, 18000, new string('b', 32)));
        check(pause.Pending, "another command cannot acknowledge our pending stop");
        pause.Observe(stop with { Revision = 19 });
        check(!pause.Pending, "matching authoritative command acknowledges immediate local stop");
        pause.Request(stop, 20); pause.Observe(new(20, "movie", false, 0, 20000));
        check(pause.Pending, "old snapshot cannot acknowledge a new stop");
        pause.Observe(new(21, "movie", false, 0, 21000));
        check(!pause.Pending, "legacy room service acknowledges stop without command IDs");
        pause.Request(stop, 21); pause.Request(stop with { Playing = true }, 21);
        check(!pause.Pending, "explicit play supersedes pending local pause");
        pause.Request(stop, 21); pause.Observe(new(22, "next-movie", false, 0, 22000));
        check(!pause.Pending, "media change clears pending pause");
        pause.Request(stop, 22); pause.Clear();
        check(!pause.Pending, "leaving room clears pending local pause");
        var time = new ClockTime(); var clock = new ServerClock(time);
        clock.Sent(1); time.Advance(100); clock.Receive(1, 100050);
        time.Utc -= 10000; time.Advance(1000);
        check(clock.Now == 101100 && clock.OffsetMs == 10000, "server clock survives backward wall-clock jump");
        clock.Sent(2); time.Advance(100); check(clock.Receive(2, 101150) && clock.Now == 101200, "monotonic RTT accepts valid response after clock jump");
        time.Utc += 20000; time.Advance(1000);
        check(clock.Now == 102200 && clock.OffsetMs == -10000, "server clock survives forward wall-clock jump");
        check(!clock.Receive(2, 900000) && !clock.Receive(123, 900000), "duplicate and uncorrelated clock responses ignored");
        clock.Sent(3); time.Advance(10001); check(!clock.Receive(3, 900000), "expired clock probes ignored");
        clock.Sent(4); time.Advance(200); check(clock.Receive(4, 113301) && clock.Now == 113401, "clock samples expire and permit new slower measurements");
        var settling = new PlaybackSettling(); settling.Seek(10, 90000, 1000);
        check(settling.Waiting(10, 0, 1250) && settling.Waiting(10, 90000, 1500) && !settling.Waiting(10, 90000, 3000), "seek waits for decoding even when VLC time updates immediately");
        settling.Seek(10, 90000, 1000);
        check(!settling.Waiting(11, 0, 1250), "new playback command interrupts seek settling");
        settling.Seek(10, 90000, 1000);
        check(!settling.Waiting(10, 0, 3000), "stalled seek expires instead of blocking playback forever");
        settling.Seek(10, 90000, 1000); settling.Observe(90000, 1050);
        check(settling.Waiting(10, 90000, 1400), "seek setter echo alone cannot complete settling");
        settling.Observe(90300, 1350);
        check(!settling.Waiting(10, 90300, 1400), "advancing native timeline completes seek settling before timeout");
        settling.Seek(10, 90000, 1000); settling.Observe(1000, 1050); settling.Observe(1300, 1350);
        check(settling.Waiting(10, 1300, 1400), "old timeline events do not complete a new seek");
        var position = new PlaybackPosition(); position.Observe(90000, 1000);
        check(position.Estimate(90000, true, 1.03f, 1250) == 90257, "position estimate advances between native observations at playback rate");
        check(position.Estimate(90000, true, 1f, 3000) == 90500 && position.Estimate(90000, false, 1f, 1250) == 90000, "position estimate is bounded during stalls and never advances paused playback");
        check(position.Estimate(150000, true, 1f, 1250) == 150000, "new native position supersedes old interpolation anchor");
        check(SyncMath.TunedCorrection(0) == 1 && SyncMath.TunedCorrection(10000) == 1.05f && SyncMath.TunedCorrection(-10000) == .95f, "rate correction is neutral when aligned and bounded in both directions");
        check(SyncMath.EstablishedCorrection(99) == 1f && SyncMath.EstablishedCorrection(100) == 1.03f && SyncMath.EstablishedCorrection(-100) == .97f, "established rate policy preserves its deadband and correction limits");
        var rateControl = new PlaybackRateControl();
        var stable = true;
        // Aligned video with VLC's 500 ms native time updates: the old raw-time
        // correction would alternate 1 and 1.03 at each UI tick.
        for (long now = 0; now <= 10000; now += 250)
        {
            var native = now / 500 * 500;
            if (now % 500 == 0) position.Observe(native, now);
            stable &= rateControl.Update(now - position.Estimate(native, true, 1, now), now) == 1;
        }
        check(stable, "sparse native clock updates do not change aligned playback speed");
        rateControl.Reset();
        check(rateControl.Update(400, 0) == 1 && rateControl.Update(400, 500) == 1 && rateControl.Update(50, 750) == 1,
            "brief clock jitter cannot trigger rate correction");
        check(rateControl.Update(400, 1000) == 1 && rateControl.Update(400, 1750) == 1.03f && rateControl.Update(150, 2000) == 1.03f,
            "persistent lag accelerates playback and hysteresis avoids rate chatter");
        check(rateControl.Update(-250, 2250) == 1 && rateControl.Update(-250, 3000) == .97f && rateControl.Update(-50, 3250) == 1,
            "overshoot returns to normal speed before sustained reverse correction");
        rateControl.Update(400, 4000); rateControl.Reset();
        check(rateControl.Update(400, 4750) == 1, "new playback commands clear previous drift history");
        var buffer = new PlaybackBuffering(); buffer.Cache(0, 1000); buffer.Cache(100, 1004);
        check(!buffer.Poll(true, 1600), "short seek buffering bursts do not pause the room");
        buffer.Cache(0, 2000);
        check(!buffer.Poll(true, 2499) && buffer.Poll(true, 2500) && !buffer.Poll(true, 3000), "sustained starvation pauses the room once");
        check(buffer.Cache(100, 3100) && !buffer.Cache(100, 3101), "recovered buffering reports ready once");
        buffer.Cache(60, 9000);
        check(buffer.IsActive && buffer.Poll(true, 9500), "partial cache starvation is detected even without a zero-percent event");
        check(buffer.Cache(100, 9600) && !buffer.IsActive, "full cache releases the synchronization hold and reports recovery");

        var cursors = new List<string>(); var gets = 0;
        using var handler = new Handler((request, ct) =>
        {
            if (request.Method != HttpMethod.Get) return Task.FromResult(Json(new { ok = true }));
            cursors.Add(request.RequestUri!.Query);
            return Task.FromResult(++gets switch {
                1 or 3 => Json(new { error = "database temporarily unavailable" }, HttpStatusCode.ServiceUnavailable),
                2 => Json(new { events = new[] { new { sequence = 1, message = new WireMessage("notice", Text: "first") } } }),
                4 => Json(new { events = new[] { new { sequence = 2, message = new WireMessage("notice", Text: "second") } } }),
                _ => Json(new { error = "Session removed" }, HttpStatusCode.Gone)
            });
        });
        await using (var connection = new SitesRoomConnection(new HttpClient(handler) { BaseAddress = new Uri("https://test.invalid/") }))
        {
            using var timeout = new CancellationTokenSource(5000);
            var first = await connection.ReadAsync(timeout.Token); var second = await connection.ReadAsync(timeout.Token);
            check(first?.Text == "first" && second?.Text == "second" && cursors.Take(4).SequenceEqual(new[] { "?since=0", "?since=0", "?since=1", "?since=1" }), "polling recovers from 503 with same session cursor and no lost events");
            var terminal = false;
            try { await connection.ReadAsync(timeout.Token); } catch (IOException ex) { terminal = ex.Message.Contains("removed"); }
            check(terminal && gets == 5, "removed session is terminal without retrying");
        }
        var ids = new List<string>(); var posts = 0;
        using var postHandler = new Handler(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Get) { await Task.Delay(Timeout.Infinite, ct); }
            if (request.Method != HttpMethod.Post) return Json(new { ok = true });
            ids.Add(request.Headers.GetValues("X-Watchroom-Request-Id").Single());
            if (++posts == 1) return Json(new { error = "busy" }, HttpStatusCode.ServiceUnavailable);
            if (posts == 2) throw new HttpRequestException("response lost after commit");
            return Json(new { ok = true });
        });
        await using (var connection = new SitesRoomConnection(new HttpClient(postHandler) { BaseAddress = new Uri("https://test.invalid/") }, true))
        {
            await connection.SendAsync(new("playback"), CancellationToken.None);
            check(posts == 3 && ids.Distinct().Count() == 1, "ambiguous POST retries reuse one idempotency key");
        }
        var legacyPosts = 0;
        using var legacyHandler = new Handler(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Get) await Task.Delay(Timeout.Infinite, ct);
            if (request.Method == HttpMethod.Post) { legacyPosts++; return Json(new { error = "busy" }, HttpStatusCode.ServiceUnavailable); }
            return Json(new { ok = true });
        });
        await using (var connection = new SitesRoomConnection(new HttpClient(legacyHandler) { BaseAddress = new Uri("https://test.invalid/") }))
        {
            try { await connection.SendAsync(new("playback"), CancellationToken.None); } catch (IOException) { }
            check(legacyPosts == 1, "legacy servers never receive ambiguous playback retries");
            await connection.DisposeAsync(); connection.Abort();
            check(true, "transport cleanup tolerates repeated disposal and abort");
        }
    }
    static HttpResponseMessage Json(object data, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = JsonContent.Create(data, options: Wire.Json) };
    sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => action(request, ct); }
    sealed class ClockTime : TimeProvider
    {
        public long Utc = 100000, Tick;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Tick;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Utc);
        public void Advance(long ms) { Utc += ms; Tick += ms; }
    }
}
