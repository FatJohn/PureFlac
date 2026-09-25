// Copyright (c) 2026 JohnShu. Licensed under the MIT License; see LICENSE in the repository root.
//
// Ported from dr_flac (https://github.com/mackron/dr_libs, commit dfe8377631000664666519fdb83da193fd8037f4,
// dr_flac.h v0.13.4, public domain / MIT-0): metadata reading (the "read and discard" path of
// drflac__read_and_decode_metadata, and ID3v2 skipping). The original is dual-licensed Public Domain (Unlicense)
// or MIT-0; it is used here under MIT-0. Only the scalar path of the decoding core is ported -- no SIMD, seeking,
// Ogg container or file I/O. The MD5 and total-sample-count checks do not exist in dr_flac; they were added
// following RFC 9639.
using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;

namespace PureFlac
{
    /// <summary>
    /// A pure C# streaming FLAC decoder: decodes PCM frame by frame from input that is <b>not seekable and arrives
    /// incrementally</b>.
    /// <para>
    /// Input: only <see cref="Stream.Read(byte[], int, int)"/> is called -- never Seek, Position or Length. Each
    /// Read may return as little as 1 byte, and Read may block. The constructor blocks until all metadata blocks
    /// have been read (STREAMINFO is required; the other blocks are read and discarded). After that, each call to
    /// <see cref="Read(Span{byte})"/> decodes the next frame only when the PCM on hand is used up, so the first PCM
    /// is available as soon as the metadata and the first frame have arrived.
    /// </para>
    /// <para>
    /// Output: interleaved, little-endian, signed integers, <see cref="BytesPerSample"/> = ceil(<see cref="BitsPerSample"/>/8)
    /// bytes per sample. Sample values are <b>right-aligned</b> (the original FLAC sample value is written as is, not
    /// scaled) -- the same packing that the STREAMINFO MD5 is defined over. When the original bit depth is not a
    /// multiple of 8 (for example 12 or 20 bits), shift left by <c>BytesPerSample * 8 - BitsPerSample</c> bits to play
    /// at full scale.
    /// </para>
    /// <para>
    /// Errors: a frame CRC mismatch, a reserved value or any other format error throws
    /// <see cref="FlacDecodeException"/> (no resync). Input that ends in the middle of the metadata or of a frame
    /// throws <see cref="FlacDecodeErrorKind.Truncated"/>. At the end of the stream, if the STREAMINFO total sample
    /// count is known and differs, <see cref="FlacDecodeErrorKind.SampleCountMismatch"/> is thrown; if the STREAMINFO
    /// MD5 is not all zeros and differs, <see cref="FlacDecodeErrorKind.Md5Mismatch"/> is thrown. These checks run in
    /// the last <see cref="Read(Span{byte})"/> (the one that would otherwise return 0), so PCM that has already been
    /// returned may belong to a stream that is only found to be corrupt later.
    /// Once the total sample count is known and all samples have been decoded, trailing data that is not a frame
    /// (for example an ID3v1 tag) is ignored and not read any further.
    /// </para>
    /// </summary>
    public sealed class FlacStreamDecoder : IDisposable
    {
        private const int MetadataStreamInfo = 0;
        private const int MetadataInvalid = 127;

        private readonly Stream input;
        private readonly bool leaveOpen;
        private readonly FlacBitReader reader;
        private readonly FlacFrameDecoder frameDecoder;
        private readonly IncrementalHash md5;
        private readonly byte[] output;
        private readonly int bytesPerSample;
        private int outputPosition;
        private int outputLength;
        private long samplesDecoded;
        private bool finished;
        private bool disposed;
        private ExceptionDispatchInfo? failure;

        /// <summary>
        /// Reads the FLAC metadata from <paramref name="stream"/> and prepares to decode. Blocks until all metadata
        /// blocks have been read.
        /// </summary>
        /// <param name="stream">The FLAC stream. Only <see cref="Stream.Read(byte[], int, int)"/> is ever called.</param>
        /// <param name="leaveOpen">
        /// <see langword="true"/> to leave <paramref name="stream"/> open when the decoder is disposed;
        /// <see langword="false"/> (the default) to dispose it together with the decoder. If the constructor throws,
        /// the stream is not disposed.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
        /// <exception cref="FlacDecodeException">The input is not FLAC, its metadata is invalid, or it ends inside the metadata.</exception>
        public FlacStreamDecoder(Stream stream, bool leaveOpen = false)
        {
            ArgumentNullException.ThrowIfNull(stream);

            input = stream;
            this.leaveOpen = leaveOpen;
            reader = new FlacBitReader(stream);
            StreamInfo = ReadMetadata(reader);
            bytesPerSample = (StreamInfo.BitsPerSample + 7) / 8;
            frameDecoder = new FlacFrameDecoder(StreamInfo.Channels, StreamInfo.BitsPerSample, StreamInfo.MaxBlockSize);
            output = new byte[StreamInfo.MaxBlockSize * StreamInfo.Channels * bytesPerSample];
#pragma warning disable CA5351 // FLAC uses MD5 as a PCM integrity checksum (STREAMINFO), not for security
            md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
#pragma warning restore CA5351
        }

        /// <summary>The contents of the STREAMINFO metadata block.</summary>
        public FlacStreamInfo StreamInfo { get; }

        /// <summary>Sample rate in Hz.</summary>
        public int SampleRate => StreamInfo.SampleRate;

        /// <summary>Number of interleaved channels in the output.</summary>
        public int Channels => StreamInfo.Channels;

        /// <summary>Bit depth of the FLAC stream (the STREAMINFO bits per sample, 4–32).</summary>
        public int BitsPerSample => StreamInfo.BitsPerSample;

        /// <summary>
        /// Width in bytes of each output sample: ceil(<see cref="BitsPerSample"/>/8), that is 1, 2, 3 or 4. Sample
        /// values are right-aligned within this container; see the class remarks.
        /// </summary>
        public int BytesPerSample => bytesPerSample;

        /// <summary>Number of samples per channel (PCM frames) decoded so far.</summary>
        public long SamplesDecoded => samplesDecoded;

        /// <summary>
        /// Reads decoded PCM into <paramref name="buffer"/>; see <see cref="Read(Span{byte})"/>.
        /// </summary>
        /// <param name="buffer">The destination buffer.</param>
        /// <param name="offset">The offset in <paramref name="buffer"/> at which to start writing.</param>
        /// <param name="count">The maximum number of bytes to write.</param>
        /// <returns>The number of bytes written; 0 at the end of the stream (or when <paramref name="count"/> is 0).</returns>
        /// <exception cref="ArgumentNullException"><paramref name="buffer"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> or <paramref name="count"/> is out of range.</exception>
        /// <exception cref="ObjectDisposedException">The decoder has been disposed.</exception>
        /// <exception cref="FlacDecodeException">The stream is corrupt, truncated or fails the end-of-stream checks.</exception>
        public int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(count, buffer.Length - offset);
            return Read(buffer.AsSpan(offset, count));
        }

        /// <summary>
        /// Reads decoded PCM (interleaved, little-endian, signed, <see cref="BytesPerSample"/> bytes per sample) into
        /// <paramref name="destination"/>. Returns at most the PCM left over from the current frame, so it may return
        /// fewer bytes than requested; a returned count is not necessarily a multiple of the sample or PCM frame size.
        /// Blocks while the input stream blocks. Returns 0 only at the end of the stream, after the end-of-stream checks
        /// have passed (or immediately when <paramref name="destination"/> is empty).
        /// </summary>
        /// <param name="destination">The destination buffer.</param>
        /// <returns>The number of bytes written; 0 at the end of the stream.</returns>
        /// <exception cref="ObjectDisposedException">The decoder has been disposed.</exception>
        /// <exception cref="FlacDecodeException">
        /// The stream is corrupt, truncated or fails the end-of-stream checks. After this has been thrown once, every
        /// later call throws the same exception again.
        /// </exception>
        public int Read(Span<byte> destination)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            failure?.Throw();

            if (destination.IsEmpty)
            {
                return 0;
            }

            try
            {
                while (outputPosition == outputLength)
                {
                    if (finished || !DecodeNextFrame())
                    {
                        return 0;
                    }
                }
            }
            catch (FlacDecodeException e)
            {
                failure = ExceptionDispatchInfo.Capture(e);
                throw;
            }

            var copy = Math.Min(destination.Length, outputLength - outputPosition);
            output.AsSpan(outputPosition, copy).CopyTo(destination);
            outputPosition += copy;
            return copy;
        }

        /// <summary>Releases the decoder and, unless it was created with <c>leaveOpen: true</c>, the input stream.</summary>
        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            md5.Dispose();
            if (!leaveOpen)
            {
                input.Dispose();
            }
        }

        /// <summary>Decodes the next frame into output; returns false at the end of the stream (after the final checks have passed).</summary>
        private bool DecodeNextFrame()
        {
            var blockSize = frameDecoder.DecodeNextFrame(reader);
            if (blockSize < 0)
            {
                var total = StreamInfo.TotalSamples;
                if (total == 0 || samplesDecoded != total)
                {
                    throw new FlacDecodeException(FlacDecodeErrorKind.Format, "No FLAC frame sync code found between frames.");
                }

                blockSize = 0;
            }

            if (blockSize == 0)
            {
                Finish();
                return false;
            }

            var length = blockSize * StreamInfo.Channels * bytesPerSample;
            Pack(frameDecoder.Samples, blockSize, output.AsSpan(0, length));
            md5.AppendData(output, 0, length);
            outputPosition = 0;
            outputLength = length;
            samplesDecoded += blockSize;
            return true;
        }

        private void Finish()
        {
            finished = true;
            var total = StreamInfo.TotalSamples;
            if (total != 0 && samplesDecoded != total)
            {
                throw new FlacDecodeException(FlacDecodeErrorKind.SampleCountMismatch, $"Decoded {samplesDecoded} samples but STREAMINFO records {total} (the stream may be truncated).");
            }

            Span<byte> actual = stackalloc byte[16];
            md5.GetHashAndReset(actual);
            var expected = StreamInfo.Md5Signature.Span;
            if (expected.IndexOfAnyExcept((byte)0) >= 0 && !actual.SequenceEqual(expected))
            {
                throw new FlacDecodeException(FlacDecodeErrorKind.Md5Mismatch, $"The MD5 of the decoded PCM, {Convert.ToHexString(actual)}, differs from the STREAMINFO MD5 {Convert.ToHexString(expected)}.");
            }
        }

        /// <summary>Per-channel int32 samples -> interleaved, little-endian, bytesPerSample bytes per sample.</summary>
        private void Pack(int[][] channels, int blockSize, Span<byte> destination)
        {
            var channelCount = channels.Length;
            var stride = channelCount * bytesPerSample;
            for (var c = 0; c < channelCount; c++)
            {
                var source = channels[c].AsSpan(0, blockSize);
                var position = c * bytesPerSample;
                switch (bytesPerSample)
                {
                    case 1:
                        for (var i = 0; i < source.Length; i++, position += stride)
                        {
                            destination[position] = (byte)source[i];
                        }

                        break;
                    case 2:
                        for (var i = 0; i < source.Length; i++, position += stride)
                        {
                            BinaryPrimitives.WriteInt16LittleEndian(destination[position..], (short)source[i]);
                        }

                        break;
                    case 3:
                        for (var i = 0; i < source.Length; i++, position += stride)
                        {
                            var v = source[i];
                            destination[position] = (byte)v;
                            destination[position + 1] = (byte)(v >> 8);
                            destination[position + 2] = (byte)(v >> 16);
                        }

                        break;
                    default:
                        for (var i = 0; i < source.Length; i++, position += stride)
                        {
                            BinaryPrimitives.WriteInt32LittleEndian(destination[position..], source[i]);
                        }

                        break;
                }
            }
        }

        // ---------------------------------------------------------------- metadata

        private static FlacStreamInfo ReadMetadata(FlacBitReader reader)
        {
            if (!reader.TryEnsure(8))
            {
                throw new FlacDecodeException(FlacDecodeErrorKind.Truncated, "The input is empty (not a FLAC stream).");
            }

            Span<byte> magic = stackalloc byte[4];
            reader.ReadBytes(magic);

            // Like dr_flac, accept an ID3v2 tag before the stream marker: a 10-byte header (its size is a
            // synchsafe integer), plus 10 more bytes when a footer is present.
            if (magic[0] == 'I' && magic[1] == 'D' && magic[2] == '3')
            {
                Span<byte> id3 = stackalloc byte[6];
                reader.ReadBytes(id3);
                var flags = id3[1];
                var size = (id3[2] << 21) | (id3[3] << 14) | (id3[4] << 7) | id3[5];
                if (((id3[2] | id3[3] | id3[4] | id3[5]) & 0x80) != 0)
                {
                    throw new FlacDecodeException(FlacDecodeErrorKind.Format, "The ID3v2 tag size field is not a synchsafe integer.");
                }

                reader.SkipBytes(size + ((flags & 0x10) != 0 ? 10 : 0));
                reader.ReadBytes(magic);
            }

            if (magic[0] != 'f' || magic[1] != 'L' || magic[2] != 'a' || magic[3] != 'C')
            {
                throw new FlacDecodeException(FlacDecodeErrorKind.Format, "Not a FLAC stream (missing the 'fLaC' marker).");
            }

            FlacStreamInfo? streamInfo = null;
            Span<byte> payload = stackalloc byte[FlacStreamInfo.BlockPayloadLength];
            var isLast = false;
            while (!isLast)
            {
                var header = reader.ReadBits(32);
                isLast = (header & 0x80000000) != 0;
                var type = (int)((header >> 24) & 0x7F);
                var length = (int)(header & 0xFFFFFF);

                if (streamInfo == null)
                {
                    if (type != MetadataStreamInfo || length != FlacStreamInfo.BlockPayloadLength)
                    {
                        throw new FlacDecodeException(FlacDecodeErrorKind.Format, $"The first metadata block is not a valid STREAMINFO (type {type}, length {length}).");
                    }

                    reader.ReadBytes(payload);
                    streamInfo = FlacStreamInfo.ParseBlock(payload);
                    Validate(streamInfo);
                    continue;
                }

                if (type == MetadataStreamInfo || type == MetadataInvalid)
                {
                    throw new FlacDecodeException(FlacDecodeErrorKind.Format, $"Metadata block type {type} is not allowed (a second STREAMINFO, or the invalid value 127).");
                }

                reader.SkipBytes(length);
            }

            return streamInfo!;
        }

        private static void Validate(FlacStreamInfo info)
        {
            if (info.BitsPerSample < 4)
            {
                throw new FlacDecodeException(FlacDecodeErrorKind.Format, $"STREAMINFO bits per sample {info.BitsPerSample} is below the FLAC minimum of 4.");
            }

            if (info.SampleRate == 0)
            {
                throw new FlacDecodeException(FlacDecodeErrorKind.Format, "STREAMINFO sample rate is 0.");
            }

            if (info.MaxBlockSize < 16 || info.MinBlockSize > info.MaxBlockSize)
            {
                throw new FlacDecodeException(FlacDecodeErrorKind.Format, $"STREAMINFO block size range {info.MinBlockSize}-{info.MaxBlockSize} is invalid.");
            }
        }
    }
}
