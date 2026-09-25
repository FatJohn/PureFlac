// Copyright (c) 2026 JohnShu. Licensed under the MIT License; see LICENSE in the repository root.
//
// Test support for PureFlac, a C# port of the dr_flac decoding core (https://github.com/mackron/dr_libs,
// commit dfe8377631000664666519fdb83da193fd8037f4, public domain / MIT-0). Not derived from dr_flac.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace PureFlac.Tests
{
    /// <summary>One row of Fixtures/flac/expected.tsv (produced with libFLAC by Fixtures/flac/gen.sh).</summary>
    internal sealed record FlacFixture(string File, int Bps, int Channels, int Rate, long Samples, string StreamInfoMd5, string PcmSha256)
    {
        public static readonly string Directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "flac");

        private static readonly Lazy<Dictionary<string, FlacFixture>> All = new(Load);

        public static IReadOnlyDictionary<string, FlacFixture> Rows => All.Value;

        public byte[] ReadBytes() => System.IO.File.ReadAllBytes(Path.Combine(Directory, File));

        public static TheoryData<string> Names()
        {
            var data = new TheoryData<string>();
            foreach (var name in Rows.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                data.Add(name);
            }

            return data;
        }

        private static Dictionary<string, FlacFixture> Load()
        {
            var rows = new Dictionary<string, FlacFixture>(StringComparer.Ordinal);
            foreach (var line in System.IO.File.ReadAllLines(Path.Combine(Directory, "expected.tsv")).Skip(1))
            {
                var c = line.Split('\t');
                var inv = CultureInfo.InvariantCulture;
                rows[c[0]] = new FlacFixture(c[0], int.Parse(c[1], inv), int.Parse(c[2], inv), int.Parse(c[3], inv), long.Parse(c[4], inv), c[5], c[6]);
            }

            return rows;
        }
    }

    internal static class FlacTestHelpers
    {
        public static readonly TimeSpan DecodeTimeout = TimeSpan.FromSeconds(30);

        /// <summary>Decodes the whole stream and returns all PCM; when <paramref name="readSizes"/> is not null each Read asks for a random 1–8192 bytes.</summary>
        public static byte[] DecodeAll(Stream input, Random? readSizes = null)
        {
            using var decoder = new FlacStreamDecoder(input);
            return DrainPcm(decoder, readSizes);
        }

        public static byte[] DrainPcm(FlacStreamDecoder decoder, Random? readSizes = null)
        {
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var want = readSizes == null ? buffer.Length : readSizes.Next(1, buffer.Length + 1);
                var n = decoder.Read(buffer, 0, want);
                if (n == 0)
                {
                    return output.ToArray();
                }

                Assert.InRange(n, 1, want);
                output.Write(buffer, 0, n);
            }
        }

        public static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

        public static string Md5Hex(byte[] data)
        {
#pragma warning disable CA5351 // FLAC STREAMINFO uses MD5 as a PCM checksum, not for security
            return Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant();
#pragma warning restore CA5351
        }

        /// <summary>
        /// Runs <paramref name="action"/> on a dedicated thread (not the thread pool, so a starved pool cannot block it),
        /// fails the test as a hang when it exceeds the timeout, and returns the exception thrown by the action (or null).
        /// </summary>
        public static Exception? RunWithTimeout(Action action, TimeSpan timeout)
        {
            Exception? caught = null;
            var thread = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    caught = e;
                }
            })
            { IsBackground = true, Name = "flac-decode-under-test" };
            thread.Start();
            Assert.True(thread.Join(timeout), $"Decoding did not finish within {timeout.TotalSeconds} seconds (suspected hang).");
            return caught;
        }

        /// <summary>Total length of fLaC plus all metadata blocks = offset of the first frame (no ID3v2 prefix handling).</summary>
        public static int MetadataEnd(byte[] flac)
        {
            var position = 4;
            while (true)
            {
                var isLast = (flac[position] & 0x80) != 0;
                var length = (flac[position + 1] << 16) | (flac[position + 2] << 8) | flac[position + 3];
                position += 4 + length;
                if (isLast)
                {
                    return position;
                }
            }
        }
    }

    /// <summary>
    /// Non-seekable input: CanSeek=false; Seek, Position (get/set), Length and SetLength all throw and are counted,
    /// proving that the decoder only calls Read. Optionally each Read returns only 1–7 bytes (fixed seed).
    /// </summary>
    internal sealed class ForwardOnlyTestStream : Stream
    {
        private readonly byte[] data;
        private readonly Random? partial;
        private int position;

        public ForwardOnlyTestStream(byte[] data, int? partialSeed = null)
        {
            this.data = data;
            partial = partialSeed.HasValue ? new Random(partialSeed.Value) : null;
        }

        public int ForbiddenCalls { get; private set; }

        public int ReadCalls { get; private set; }

        public bool Disposed { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw Forbidden(nameof(Length));

        public override long Position
        {
            get => throw Forbidden("Position get");
            set => throw Forbidden("Position set");
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCalls++;
            var n = Math.Min(count, data.Length - position);
            if (partial != null)
            {
                n = Math.Min(n, partial.Next(1, 8));
            }

            Array.Copy(data, position, buffer, offset, n);
            position += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw Forbidden(nameof(Seek));

        public override void SetLength(long value) => throw Forbidden(nameof(SetLength));

        public override void Flush()
        {
        }

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }

        private NotSupportedException Forbidden(string what)
        {
            ForbiddenCalls++;
            return new NotSupportedException($"The decoder must not call {what}.");
        }
    }

    /// <summary>
    /// Input whose data the test releases step by step: Read only sees released data and blocks until more is released
    /// or <see cref="Complete"/> is called. <see cref="ReleaseIfWaiting"/> releases the next chunk only while the reader
    /// is blocked in Read waiting for data.
    /// </summary>
    internal sealed class GatedTestStream : Stream
    {
        private readonly byte[] data;
        private readonly object gate = new();
        private int released;
        private int position;
        private bool waiting;

        public GatedTestStream(byte[] data)
        {
            this.data = data;
        }

        public int Released
        {
            get
            {
                lock (gate)
                {
                    return released;
                }
            }
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void Release(int bytes)
        {
            lock (gate)
            {
                released = Math.Min(data.Length, released + bytes);
                Monitor.PulseAll(gate);
            }
        }

        public void Complete() => Release(data.Length);

        /// <summary>
        /// Releases <paramref name="bytes"/> only when the reader is blocked in Read and has consumed everything released
        /// so far; returns whether anything was released. The check and the release happen under the same lock, so
        /// several chunks cannot be released in a row just because the reader has not woken up yet.
        /// </summary>
        public bool ReleaseIfWaiting(int bytes)
        {
            lock (gate)
            {
                if (!waiting || position != released)
                {
                    return false;
                }

                waiting = false;
                released = Math.Min(data.Length, released + bytes);
                Monitor.PulseAll(gate);
                return true;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            lock (gate)
            {
                while (position == released && released < data.Length)
                {
                    waiting = true;
                    Monitor.Wait(gate);
                }

                waiting = false;
                var n = Math.Min(count, released - position);
                Array.Copy(data, position, buffer, offset, n);
                position += n;
                return n;
            }
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
