// Copyright (c) 2026 JohnShu. Licensed under the MIT License; see LICENSE in the repository root.
//
// Part of PureFlac, a C# port of the dr_flac decoding core (https://github.com/mackron/dr_libs,
// commit dfe8377631000664666519fdb83da193fd8037f4, public domain / MIT-0). This file is not derived from dr_flac:
// dr_flac has no error reporting of this kind; the error categories follow RFC 9639.
using System;

namespace PureFlac
{
    /// <summary>The kind of error reported by a <see cref="FlacDecodeException"/>.</summary>
    public enum FlacDecodeErrorKind
    {
        /// <summary>Not FLAC, a field value that violates the specification, or a reserved value (including data between frames that is not a sync code).</summary>
        Format,

        /// <summary>The CRC-8 of a frame header or the CRC-16 of a whole frame does not match.</summary>
        CrcMismatch,

        /// <summary>The input ended in the middle of the metadata or of a frame.</summary>
        Truncated,

        /// <summary>After the whole stream was decoded, the MD5 of the decoded PCM differs from the one recorded in STREAMINFO (not checked when the STREAMINFO MD5 is all zeros).</summary>
        Md5Mismatch,

        /// <summary>After the whole stream was decoded, the total sample count differs from the one recorded in STREAMINFO (not checked when STREAMINFO records 0, meaning unknown).</summary>
        SampleCountMismatch,
    }

    /// <summary>
    /// Thrown by <see cref="FlacStreamDecoder"/> when the FLAC stream is corrupt, truncated or violates the
    /// specification. The decoder does not resync: once this is thrown, every later
    /// <see cref="FlacStreamDecoder.Read(Span{byte})"/> throws the same exception again.
    /// Exceptions thrown by the input stream itself (<see cref="System.IO.IOException"/> and so on) propagate
    /// unchanged and are not wrapped in this type.
    /// </summary>
    public sealed class FlacDecodeException : Exception
    {
        /// <summary>Creates an exception of the given kind.</summary>
        /// <param name="kind">The kind of error.</param>
        /// <param name="message">A description of the error.</param>
        public FlacDecodeException(FlacDecodeErrorKind kind, string message)
            : base(message)
        {
            Kind = kind;
        }

        /// <summary>The kind of error.</summary>
        public FlacDecodeErrorKind Kind { get; }
    }
}
