// Copyright (c) 2026 JohnShu. Licensed under the MIT License; see LICENSE in the repository root.
//
// Tests for PureFlac, a C# port of the dr_flac decoding core (https://github.com/mackron/dr_libs,
// commit dfe8377631000664666519fdb83da193fd8037f4, public domain / MIT-0). Not derived from dr_flac.
using System;
using System.IO;

namespace PureFlac.Tests
{
    public class FlacStreamInfoTests
    {
        private static byte[] BuildMd5(byte seed)
        {
            var md5 = new byte[16];
            for (var i = 0; i < md5.Length; i++)
            {
                md5[i] = (byte)(seed + i);
            }

            return md5;
        }

        [Fact]
        public void ParseBlock_RoundTripsEncodedStreamInfo()
        {
            var md5 = BuildMd5(0x10);
            var block = StreamInfoEncoder.EncodeBlock(
                minBlockSize: 4096,
                maxBlockSize: 4096,
                minFrameSize: 1000,
                maxFrameSize: 8000,
                sampleRate: 44100,
                channels: 2,
                bitsPerSample: 16,
                totalSamples: 123456789,
                md5Signature: md5);

            Assert.Equal(FlacStreamInfo.BlockPayloadLength, block.Length);

            var info = FlacStreamInfo.ParseBlock(block);

            Assert.Equal(4096, info.MinBlockSize);
            Assert.Equal(4096, info.MaxBlockSize);
            Assert.Equal(1000, info.MinFrameSize);
            Assert.Equal(8000, info.MaxFrameSize);
            Assert.Equal(44100, info.SampleRate);
            Assert.Equal(2, info.Channels);
            Assert.Equal(16, info.BitsPerSample);
            Assert.Equal(123456789L, info.TotalSamples);
            Assert.Equal(md5, info.Md5Signature.ToArray());
        }

        [Fact]
        public void Decoder_StreamInfoOnlyFile_ExposesStreamInfoAndEndsWithoutPcm()
        {
            var md5 = BuildMd5(0x20);
            var file = BuildStreamInfoOnlyFile(md5, totalSamples: 0);

            using var decoder = new FlacStreamDecoder(new MemoryStream(file));

            Assert.Equal(48000, decoder.SampleRate);
            Assert.Equal(1, decoder.Channels);
            Assert.Equal(24, decoder.BitsPerSample);
            Assert.Equal(3, decoder.BytesPerSample);
            Assert.Equal(4608, decoder.StreamInfo.MaxBlockSize);
            Assert.Equal(md5, decoder.StreamInfo.Md5Signature.ToArray());

            // No frames and an unknown total: the only check left is the MD5, which differs from the MD5 of no PCM.
            var e = Assert.Throws<FlacDecodeException>(() => decoder.Read(new byte[16]));
            Assert.Equal(FlacDecodeErrorKind.Md5Mismatch, e.Kind);
        }

        [Fact]
        public void Equality_ComparesMd5ByContent()
        {
            var a = FlacStreamInfo.ParseBlock(StreamInfoEncoder.EncodeBlock(16, 4096, 0, 0, 44100, 2, 16, 10, BuildMd5(1)));
            var b = FlacStreamInfo.ParseBlock(StreamInfoEncoder.EncodeBlock(16, 4096, 0, 0, 44100, 2, 16, 10, BuildMd5(1)));
            var c = FlacStreamInfo.ParseBlock(StreamInfoEncoder.EncodeBlock(16, 4096, 0, 0, 44100, 2, 16, 10, BuildMd5(2)));

            Assert.Equal(a, b);
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
            Assert.NotEqual(a, c);
        }

        [Fact]
        public void Decoder_MissingMagic_ThrowsFormat()
        {
            var file = BuildStreamInfoOnlyFile(BuildMd5(0x20), totalSamples: 0);
            file[0] = (byte)'X';

            var e = Assert.Throws<FlacDecodeException>(() => new FlacStreamDecoder(new MemoryStream(file)));
            Assert.Equal(FlacDecodeErrorKind.Format, e.Kind);
        }

        private static byte[] BuildStreamInfoOnlyFile(byte[] md5, long totalSamples)
        {
            var streamInfoBlock = StreamInfoEncoder.EncodeBlock(
                minBlockSize: 4608,
                maxBlockSize: 4608,
                minFrameSize: 500,
                maxFrameSize: 6000,
                sampleRate: 48000,
                channels: 1,
                bitsPerSample: 24,
                totalSamples: totalSamples,
                md5Signature: md5);

            // "fLaC" + metadata block header (is_last=1, type=0 STREAMINFO, length=34) + payload
            var file = new byte[4 + 4 + streamInfoBlock.Length];
            file[0] = (byte)'f';
            file[1] = (byte)'L';
            file[2] = (byte)'a';
            file[3] = (byte)'C';
            file[4] = 0x80; // is_last=1, type=0
            file[5] = 0x00;
            file[6] = 0x00;
            file[7] = 0x22; // length = 34
            Array.Copy(streamInfoBlock, 0, file, 8, streamInfoBlock.Length);
            return file;
        }
    }

    /// <summary>The inverse of <see cref="FlacStreamInfo.ParseBlock"/>: encodes a 34-byte STREAMINFO payload (test helper).</summary>
    internal static class StreamInfoEncoder
    {
        public static byte[] EncodeBlock(
            int minBlockSize, int maxBlockSize, int minFrameSize, int maxFrameSize,
            int sampleRate, int channels, int bitsPerSample, long totalSamples, byte[] md5Signature)
        {
            var writer = new BigEndianBitWriter(FlacStreamInfo.BlockPayloadLength);
            writer.WriteBits((ulong)minBlockSize, 16);
            writer.WriteBits((ulong)maxBlockSize, 16);
            writer.WriteBits((ulong)minFrameSize, 24);
            writer.WriteBits((ulong)maxFrameSize, 24);
            writer.WriteBits((ulong)sampleRate, 20);
            writer.WriteBits((ulong)(channels - 1), 3);
            writer.WriteBits((ulong)(bitsPerSample - 1), 5);
            writer.WriteBits((ulong)totalSamples, 36);
            writer.WriteBytes(md5Signature);
            return writer.ToArray();
        }

        private sealed class BigEndianBitWriter
        {
            private readonly byte[] buffer;
            private int bitPosition;

            public BigEndianBitWriter(int byteLength)
            {
                buffer = new byte[byteLength];
            }

            public void WriteBits(ulong value, int count)
            {
                for (var i = count - 1; i >= 0; i--)
                {
                    var bit = (int)((value >> i) & 1);
                    var byteIndex = bitPosition / 8;
                    var bitIndexInByte = 7 - (bitPosition % 8);
                    if (bit != 0)
                    {
                        buffer[byteIndex] |= (byte)(1 << bitIndexInByte);
                    }

                    bitPosition++;
                }
            }

            public void WriteBytes(byte[] bytes)
            {
                foreach (var b in bytes)
                {
                    WriteBits(b, 8);
                }
            }

            public byte[] ToArray() => buffer;
        }
    }
}
