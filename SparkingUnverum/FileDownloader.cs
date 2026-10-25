using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace SparkingUnverum
{
    // Downloads of mod files. The file servers of GameBanana send each connection slowly (0.3 to 0.5 MB/s) and drop
    // long ones: a 700 MB map pack failed after 26 and 70 MB. Big files come in several pieces at once, a piece that
    // is cut goes on from where it stopped, and the result is checked against the checksum of the page
    public static class FileDownloader
    {
        public static long PieceThreshold { get; set; } = 16L * 1024 * 1024;
        public static int Pieces { get; set; } = 4;
        // Times each piece is asked again after its connection was cut
        public static int Retries { get; set; } = 10;
        // A connection that sends nothing for this long is given up and asked again
        public static TimeSpan Stall { get; set; } = TimeSpan.FromSeconds(45);
        public static Func<int, TimeSpan> RetryDelay { get; set; } = attempt => TimeSpan.FromSeconds(Math.Min(30, 2 * attempt));

        // Bytes written by every piece, reported as one progress
        private class Counter
        {
            private long done;
            public long Total { get; init; }
            public string FileName { get; init; }
            public IProgress<DownloadProgress> Progress { get; init; }
            public long Done => Interlocked.Read(ref done);
            public void Add(long bytes)
            {
                var now = Interlocked.Add(ref done, bytes);
                if (Total > 0)
                    Progress?.Report(new DownloadProgress((float)now / Total, now, Total, FileName));
            }
        }

        // notice: what to tell the user when a connection is cut and the download goes on
        public static async Task DownloadAsync(HttpClient client, string url, string path, string fileName, string md5,
            IProgress<DownloadProgress> progress, IProgress<string> notice, CancellationToken token)
        {
            var first = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
            try
            {
                first.EnsureSuccessStatusCode();
                var total = first.Content.Headers.ContentLength ?? 0;
                var counter = new Counter { Total = total, FileName = fileName, Progress = progress };
                if (total <= 0 || !first.Headers.AcceptRanges.Contains("bytes"))
                {
                    // Without a size or ranges it can only be read in one go
                    await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                    await using var body = await first.Content.ReadAsStreamAsync(token);
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await body.ReadAsync(buffer, token)) > 0)
                    {
                        await file.WriteAsync(buffer.AsMemory(0, read), token);
                        counter.Add(read);
                    }
                    if (total > 0 && counter.Done != total)
                        throw new IOException(Loc.T("Download.Incomplete", StringConverters.FormatSize(counter.Done), StringConverters.FormatSize(total)));
                }
                else
                {
                    // Every piece writes its own part of the file
                    using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                        file.SetLength(total);
                    if (total < PieceThreshold)
                        await Piece(client, url, path, 0, total - 1, first, counter, fileName, notice, token);
                    else
                    {
                        first.Dispose();
                        first = null;
                        await InPieces(client, url, path, total, counter, fileName, notice, token);
                    }
                }
                await Task.Run(() => CheckSum(path, md5), token);
            }
            finally
            {
                first?.Dispose();
            }
        }

        // When a piece can't go on the others stop too, and the error is that piece's, not "cancelled"
        private static async Task InPieces(HttpClient client, string url, string path, long total, Counter counter, string fileName,
            IProgress<string> notice, CancellationToken token)
        {
            using var pieces = CancellationTokenSource.CreateLinkedTokenSource(token);
            var failures = new ConcurrentQueue<Exception>();
            var size = (total + Pieces - 1) / Pieces;
            await Task.WhenAll(Enumerable.Range(0, Pieces).Select(i => (Start: i * size, End: Math.Min(total, (i + 1) * size) - 1))
                .Where(p => p.Start <= p.End)
                .Select(async p =>
                {
                    try
                    {
                        await Piece(client, url, path, p.Start, p.End, null, counter, fileName, notice, pieces.Token);
                    }
                    catch (Exception e)
                    {
                        if (!(e is OperationCanceledException && pieces.IsCancellationRequested))
                            failures.Enqueue(e);
                        pieces.Cancel();
                    }
                }));
            token.ThrowIfCancellationRequested();
            if (failures.TryDequeue(out var failure))
                ExceptionDispatchInfo.Throw(failure);
        }

        // Downloads bytes start..end into their place in the file, asking again from where it stopped when cut
        private static async Task Piece(HttpClient client, string url, string path, long start, long end, HttpResponseMessage given,
            Counter counter, string fileName, IProgress<string> notice, CancellationToken token)
        {
            var position = start;
            var attempt = 0;
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            while (position <= end)
            {
                HttpResponseMessage response = null;
                try
                {
                    if (given != null)
                    {
                        response = given;
                        given = null;
                    }
                    else
                    {
                        var request = new HttpRequestMessage(HttpMethod.Get, url);
                        request.Headers.Range = new RangeHeaderValue(position, end);
                        response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                        response.EnsureSuccessStatusCode();
                        // The server sent the whole file instead of the part: it can't go on from the middle
                        if (response.StatusCode != System.Net.HttpStatusCode.PartialContent)
                            throw new InvalidDataException(Loc.T("Download.NoResume", fileName));
                    }
                    file.Position = position;
                    await using var body = await response.Content.ReadAsStreamAsync(token);
                    var buffer = new byte[81920];
                    while (position <= end)
                    {
                        using var stall = CancellationTokenSource.CreateLinkedTokenSource(token);
                        stall.CancelAfter(Stall);
                        int read;
                        try
                        {
                            read = await body.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, end - position + 1)), stall.Token);
                        }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested)
                        {
                            throw new IOException(Loc.T("Download.Stalled"));
                        }
                        if (read == 0)
                            throw new IOException(Loc.T("Download.Cut"));
                        await file.WriteAsync(buffer.AsMemory(0, read), token);
                        position += read;
                        counter.Add(read);
                    }
                }
                // Cut, stalled, or GameBanana slow to answer (the client gives up waiting for the headers)
                catch (Exception e) when (!token.IsCancellationRequested && e is IOException or HttpRequestException or TaskCanceledException && attempt < Retries)
                {
                    attempt++;
                    notice?.Report(Loc.T("Download.Resuming", fileName, StringConverters.FormatSize(counter.Done), attempt, Retries));
                    await Task.Delay(RetryDelay(attempt), token);
                }
                finally
                {
                    response?.Dispose();
                }
            }
        }

        // The file is the one on the page
        private static void CheckSum(string path, string md5)
        {
            if (string.IsNullOrWhiteSpace(md5) || md5.Trim().Length != 32)
                return;
            using var stream = File.OpenRead(path);
            if (!Convert.ToHexString(MD5.HashData(stream)).Equals(md5.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(Loc.T("Download.Corrupt"));
        }
    }
}
