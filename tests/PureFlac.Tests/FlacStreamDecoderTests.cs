// Copyright (c) 2026 JohnShu. Licensed under the MIT License; see LICENSE in the repository root.
//
// Tests for PureFlac, a C# port of the dr_flac decoding core (https://github.com/mackron/dr_libs,
// commit dfe8377631000664666519fdb83da193fd8037f4, public domain / MIT-0). Not derived from dr_flac.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace PureFlac.Tests
{
    /// <summary>
    /// Tests for <see cref="FlacStreamDecoder"/>. Reference values come from libFLAC (expected.tsv, produced by
    /// Fixtures/flac/gen.sh); the fixture coverage matrix is in the gen.sh header and coverage.tsv.
    /// </summary>
    public class FlacStreamDecoderTests
    {
        private const int PartialReadSeed = 12345;

        public static TheoryData<string> Fixtures() => FlacFixture.Names();

        [Fact]
        public void Fixtures_AreAllPresent()
        {
            // If expected.tsv were truncated or the fixtures were not copied to the output directory, the theories would
            // silently run fewer cases; pin the count here.
            Assert.Equal(39, FlacFixture.Rows.Count);
            foreach (var row in FlacFixture.Rows.Values)
            {
                Assert.True(File.Exists(Path.Combine(FlacFixture.Directory, row.File)), row.File);
            }
        }

        [Theory]
        [MemberData(nameof(Fixtures))]
        public void Decode_MatchesLibFlacPcmAndStreamInfoMd5(string name)
        {
            var fixture = FlacFixture.Rows[name];
            using var input = new MemoryStream(fixture.ReadBytes());
            using var decoder = new FlacStreamDecoder(input);

            Assert.Equal(fixture.Rate, decoder.SampleRate);
            Assert.Equal(fixture.Channels, decoder.Channels);
            Assert.Equal(fixture.Bps, decoder.BitsPerSample);
            Assert.Equal((fixture.Bps + 7) / 8, decoder.BytesPerSample);
            Assert.Equal(fixture.Samples, decoder.StreamInfo.TotalSamples);

            var pcm = FlacTestHelpers.DrainPcm(decoder);

            Assert.Equal(fixture.PcmSha256, FlacTestHelpers.Sha256Hex(pcm));
            Assert.Equal(fixture.StreamInfoMd5, FlacTestHelpers.Md5Hex(pcm));
            Assert.Equal(fixture.Samples, decoder.SamplesDecoded);
            Assert.Equal(fixture.Samples * fixture.Channels * ((fixture.Bps + 7) / 8), pcm.Length);
            Assert.Equal(0, decoder.Read(new byte[16], 0, 16));
        }

        [Theory]
        [MemberData(nameof(Fixtures))]
        public void Decode_ForwardOnlyInputWithOneToSevenByteReads_MatchesLibFlac(string name)
        {
            var fixture = FlacFixture.Rows[name];
            var input = new ForwardOnlyTestStream(fixture.ReadBytes(), PartialReadSeed);

            byte[]? pcm = null;
            var error = FlacTestHelpers.RunWithTimeout(
                () => pcm = FlacTestHelpers.DecodeAll(input, new Random(PartialReadSeed)),
                FlacTestHelpers.DecodeTimeout);

            Assert.Null(error);
            Assert.Equal(0, input.ForbiddenCalls);
            Assert.Equal(fixture.PcmSha256, FlacTestHelpers.Sha256Hex(pcm!));
        }

        [Theory]
        [InlineData("b16_c2_44k_l8.flac")]
        [InlineData("b24_c2_96k_b16384.flac")]
        public void Decode_SpanReadsOfOddSizes_MatchLibFlac(string name)
        {
            var fixture = FlacFixture.Rows[name];
            using var decoder = new FlacStreamDecoder(new MemoryStream(fixture.ReadBytes()));
            using var output = new MemoryStream();
            Span<byte> buffer = stackalloc byte[997];
            var sizes = new Random(PartialReadSeed);
            int n;
            while ((n = decoder.Read(buffer[..sizes.Next(1, buffer.Length + 1)])) > 0)
            {
                output.Write(buffer[..n]);
            }

            Assert.Equal(fixture.PcmSha256, FlacTestHelpers.Sha256Hex(output.ToArray()));
            Assert.Equal(0, decoder.Read(Span<byte>.Empty));
        }

        [Theory]
        [InlineData("b16_c2_44k_bigmeta.flac")]
        [InlineData("b24_c2_192k.flac")]
        [InlineData("b24_c2_96k_b16384.flac")]
        [InlineData("b16_c2_44k_b192.flac")]
        public void Decode_GatedInput_DeliversFirstPcmBeforeRestOfStreamArrives(string name)
        {
            const int Chunk = 512;
            var fixture = FlacFixture.Rows[name];
            var bytes = fixture.ReadBytes();
            var metadataEnd = FlacTestHelpers.MetadataEnd(bytes);
            int maxFrameSize;
            using (var probe = new FlacStreamDecoder(new MemoryStream(bytes)))
            {
                maxFrameSize = probe.StreamInfo.MaxFrameSize;
            }

            Assert.True(maxFrameSize > 0);

            var input = new GatedTestStream(bytes);
            var firstPcmAt = -1;
            var constructedAt = -1;
            byte[]? rest = null;
            Exception? readerError = null;
            var gotFirst = new ManualResetEventSlim();
            var reader = new Thread(() =>
            {
                // Exceptions must be caught here: an unhandled exception on a background thread brings down the whole testhost.
                try
                {
                    using var decoder = new FlacStreamDecoder(input);
                    constructedAt = input.Released;
                    var buffer = new byte[16];
                    var n = decoder.Read(buffer, 0, buffer.Length);
                    firstPcmAt = input.Released;
                    gotFirst.Set();
                    var tail = FlacTestHelpers.DrainPcm(decoder);
                    rest = buffer.Take(n).Concat(tail).ToArray();
                }
                catch (Exception e)
                {
                    readerError = e;
                }
            })
            { IsBackground = true };
            reader.Start();

            // Release Chunk bytes at a time; wait until the reader is blocked in Read again (wanting more data) or has
            // delivered its first PCM before releasing the next chunk.
            var deadline = DateTime.UtcNow + FlacTestHelpers.DecodeTimeout;
            while (!gotFirst.IsSet && reader.IsAlive)
            {
                Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the first PCM.");
                input.ReleaseIfWaiting(Chunk);
                gotFirst.Wait(TimeSpan.FromMilliseconds(1));
            }

            input.Complete();
            Assert.True(reader.Join(FlacTestHelpers.DecodeTimeout));
            Assert.Null(readerError);

            // The constructor needs only the metadata; the first PCM needs only the metadata plus the first frame
            // (at most the STREAMINFO maximum frame size).
            Assert.InRange(constructedAt, metadataEnd, metadataEnd + Chunk);
            Assert.InRange(firstPcmAt, metadataEnd + 1, metadataEnd + maxFrameSize + Chunk);
            Assert.True(firstPcmAt < bytes.Length, $"The first PCM appeared only after all {bytes.Length} bytes were released ({firstPcmAt}).");
            Assert.Equal(fixture.PcmSha256, FlacTestHelpers.Sha256Hex(rest!));
        }

        [Theory]
        [MemberData(nameof(Fixtures))]
        public void Decode_TruncatedInput_ThrowsFlacDecodeExceptionWithoutHanging(string name)
        {
            var bytes = FlacFixture.Rows[name].ReadBytes();
            var metadataEnd = FlacTestHelpers.MetadataEnd(bytes);
            var cuts = new[]
            {
                1, 3, 4, 6, 20, 41, metadataEnd - 1, metadataEnd, metadataEnd + 1, metadataEnd + 5,
                metadataEnd + ((bytes.Length - metadataEnd) / 3), bytes.Length / 2, bytes.Length - 3, bytes.Length - 2, bytes.Length - 1,
            };

            foreach (var cut in cuts.Where(c => c > 0 && c < bytes.Length).Distinct())
            {
                AssertDecodeFails(bytes[..cut], $"{name} truncated at {cut}/{bytes.Length}");
            }
        }

        [Theory]
        [MemberData(nameof(Fixtures))]
        public void Decode_SingleFlippedByte_ThrowsFlacDecodeExceptionWithoutHanging(string name)
        {
            var bytes = FlacFixture.Rows[name].ReadBytes();
            var metadataEnd = FlacTestHelpers.MetadataEnd(bytes);
            var audio = bytes.Length - metadataEnd;

            // Only flip positions that have structure or checksum protection: the marker, the STREAMINFO total sample
            // count and MD5, and each section of the audio. The contents of PADDING/VORBIS_COMMENT/PICTURE and the
            // STREAMINFO min/max frame size are not covered by any checksum, so flipping them cannot be detected.
            var positions = new List<int> { 0, 3, 8 + 17, 8 + 18, 8 + 33 };
            positions.AddRange(new[] { 0, 1, 2, 3, 4, 5 }.Select(o => metadataEnd + o));
            positions.AddRange(new[] { 1, 2, 3, 4, 5, 6, 7 }.Select(k => metadataEnd + (int)((long)audio * k / 8)));
            positions.AddRange(new[] { bytes.Length - 2, bytes.Length - 1 });

            foreach (var position in positions.Distinct())
            {
                var corrupt = (byte[])bytes.Clone();
                corrupt[position] ^= 0xFF;
                AssertDecodeFails(corrupt, $"{name} with byte {position}/{bytes.Length} flipped");
            }
        }

        [Theory]
        [InlineData(100)]
        [InlineData(1500)]
        public void Decode_CorruptVerbatimSample_ThrowsCrcMismatchBeforeDeliveringThatFrame(int offsetInFirstFrame)
        {
            // The sample bytes of a verbatim subframe have no structure: only the CRC-16 can catch a flip. The very first
            // frame is corrupt, so no PCM may be delivered before the exception (the bad frame's samples must not be
            // handed out with the error only reported by the MD5 at the end).
            var bytes = FlacFixture.Rows["b16_c2_44k_verbatim.flac"].ReadBytes();
            bytes[FlacTestHelpers.MetadataEnd(bytes) + offsetInFirstFrame] ^= 0x10;
            using var decoder = new FlacStreamDecoder(new MemoryStream(bytes));

            var e = Assert.Throws<FlacDecodeException>(() => decoder.Read(new byte[1 << 20], 0, 1 << 20));

            Assert.Equal(FlacDecodeErrorKind.CrcMismatch, e.Kind);
            Assert.Equal(0, decoder.SamplesDecoded);
        }

        [Fact]
        public void Decode_FlippedFrameCrcByte_ThrowsCrcMismatch()
        {
            var bytes = FlacFixture.Rows["b16_c2_44k_l8.flac"].ReadBytes();
            bytes[^1] ^= 0x01;

            var e = Assert.Throws<FlacDecodeException>(() => FlacTestHelpers.DecodeAll(new MemoryStream(bytes)));

            Assert.Equal(FlacDecodeErrorKind.CrcMismatch, e.Kind);
        }

        [Fact]
        public void Decode_StreamInfoMd5Differs_ThrowsMd5MismatchAtEnd()
        {
            var bytes = FlacFixture.Rows["b16_c2_44k_l8.flac"].ReadBytes();
            bytes[8 + 18] ^= 0x01; // The STREAMINFO payload starts at offset 8; the MD5 is payload bytes [18, 34).
            using var decoder = new FlacStreamDecoder(new MemoryStream(bytes));

            var e = Assert.Throws<FlacDecodeException>(() => FlacTestHelpers.DrainPcm(decoder));

            Assert.Equal(FlacDecodeErrorKind.Md5Mismatch, e.Kind);
            Assert.Equal(FlacFixture.Rows["b16_c2_44k_l8.flac"].Samples, decoder.SamplesDecoded);
        }

        [Fact]
        public void Decode_StreamInfoTotalSamplesDiffers_ThrowsSampleCountMismatch()
        {
            var bytes = FlacFixture.Rows["b16_c2_44k_l8.flac"].ReadBytes();
            bytes[8 + 17] ^= 0x01; // Lowest byte of the 36-bit total sample count.

            var e = Assert.Throws<FlacDecodeException>(() => FlacTestHelpers.DecodeAll(new MemoryStream(bytes)));

            Assert.Equal(FlacDecodeErrorKind.SampleCountMismatch, e.Kind);
        }

        [Fact]
        public void Decode_StreamInfoMd5AndTotalUnknown_SkipsBothChecks()
        {
            // An all-zero STREAMINFO MD5 and a total sample count of 0 mean "unknown" (RFC 9639) and are not checked;
            // a stream truncated on a frame boundary then cannot be detected either.
            var fixture = FlacFixture.Rows["b16_c2_44k_l8.flac"];
            var bytes = fixture.ReadBytes();
            bytes.AsSpan(8 + 18, 16).Clear();
            bytes[8 + 13] &= 0xF0;
            bytes.AsSpan(8 + 14, 4).Clear();

            var pcm = FlacTestHelpers.DecodeAll(new MemoryStream(bytes));

            Assert.Equal(fixture.PcmSha256, FlacTestHelpers.Sha256Hex(pcm));
        }

        [Fact]
        public void Decode_EmptyInput_ThrowsTruncated()
        {
            var e = Assert.Throws<FlacDecodeException>(() => new FlacStreamDecoder(new MemoryStream(Array.Empty<byte>())));
            Assert.Equal(FlacDecodeErrorKind.Truncated, e.Kind);
        }

        [Theory]
        [InlineData("524946460000000057415645666D7420")] // "RIFF....WAVEfmt "
        [InlineData("4F67675300020000000000000000")] // Ogg page header
        [InlineData("666C614300000022")] // "flaC" (wrong case)
        [InlineData("FFF8690800000000")] // No fLaC; starts directly with a frame header
        public void Decode_NonFlacInput_ThrowsFormat(string prefixHex)
        {
            var bytes = Convert.FromHexString(prefixHex).Concat(new byte[64]).ToArray();
            var e = Assert.Throws<FlacDecodeException>(() => new FlacStreamDecoder(new MemoryStream(bytes)));
            Assert.Equal(FlacDecodeErrorKind.Format, e.Kind);
        }

        [Fact]
        public void Decode_Id3v2PrefixedStream_SkipsTagAndDecodes()
        {
            var fixture = FlacFixture.Rows["b16_c2_44k_l8.flac"];
            var tagBody = new byte[300];
            new Random(1).NextBytes(tagBody);
            for (var i = 0; i < tagBody.Length; i++)
            {
                tagBody[i] &= 0x7F;
            }

            // ID3v2.4 header: the size is a synchsafe integer (7 bits per byte).
            var header = new byte[] { (byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, (byte)(tagBody.Length >> 7), (byte)(tagBody.Length & 0x7F) };
            var bytes = header.Concat(tagBody).Concat(fixture.ReadBytes()).ToArray();

            var pcm = FlacTestHelpers.DecodeAll(new ForwardOnlyTestStream(bytes, PartialReadSeed));

            Assert.Equal(fixture.PcmSha256, FlacTestHelpers.Sha256Hex(pcm));
        }

        [Fact]
        public void Decode_TrailingNonFrameDataAfterAllSamples_IsIgnored()
        {
            var fixture = FlacFixture.Rows["b16_c2_44k_l8.flac"];
            var id3v1 = Encoding.ASCII.GetBytes("TAG").Concat(new byte[125]).ToArray();
            var bytes = fixture.ReadBytes().Concat(id3v1).ToArray();

            var pcm = FlacTestHelpers.DecodeAll(new MemoryStream(bytes));

            Assert.Equal(fixture.PcmSha256, FlacTestHelpers.Sha256Hex(pcm));
        }

        [Fact]
        public void Read_AfterFailure_ThrowsSameErrorAgain()
        {
            var bytes = FlacFixture.Rows["b16_c2_44k_l8.flac"].ReadBytes();
            var corrupt = bytes[..(bytes.Length / 2)];
            using var decoder = new FlacStreamDecoder(new MemoryStream(corrupt));

            var first = Assert.Throws<FlacDecodeException>(() => FlacTestHelpers.DrainPcm(decoder));
            var second = Assert.Throws<FlacDecodeException>(() => decoder.Read(new byte[16], 0, 16));

            Assert.Equal(FlacDecodeErrorKind.Truncated, first.Kind);
            Assert.Same(first, second);
        }

        [Theory]
        [InlineData(false, true)]
        [InlineData(true, false)]
        public void Dispose_DisposesInputUnlessLeaveOpen(bool leaveOpen, bool expectDisposed)
        {
            var input = new ForwardOnlyTestStream(FlacFixture.Rows["b16_c2_44k_l8.flac"].ReadBytes());
            var decoder = new FlacStreamDecoder(input, leaveOpen);

            decoder.Dispose();

            Assert.Equal(expectDisposed, input.Disposed);
            Assert.Throws<ObjectDisposedException>(() => decoder.Read(new byte[16]));
        }

        [Fact]
        public void Read_InvalidArguments_Throw()
        {
            using var decoder = new FlacStreamDecoder(new MemoryStream(FlacFixture.Rows["b16_c2_44k_l8.flac"].ReadBytes()));

            Assert.Throws<ArgumentNullException>(() => decoder.Read(null!, 0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => decoder.Read(new byte[4], -1, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => decoder.Read(new byte[4], 0, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => decoder.Read(new byte[4], 2, 3));
            Assert.Equal(0, decoder.Read(new byte[4], 4, 0));
            Assert.Equal(0, decoder.SamplesDecoded);
        }

        [Fact]
        public void Constructor_NullStream_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new FlacStreamDecoder(null!));
        }

        private static void AssertDecodeFails(byte[] input, string what)
        {
            var error = FlacTestHelpers.RunWithTimeout(
                () => FlacTestHelpers.DecodeAll(new ForwardOnlyTestStream(input)),
                FlacTestHelpers.DecodeTimeout);
            Assert.True(error is FlacDecodeException, $"{what}: expected FlacDecodeException, got {error?.GetType().Name ?? "no exception (silent success)"}: {error?.Message}");
        }
    }
}
