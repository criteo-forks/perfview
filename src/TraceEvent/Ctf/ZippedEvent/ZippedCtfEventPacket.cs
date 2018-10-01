using System;
using System.IO;
using System.IO.Compression;
using Microsoft.Diagnostics.Tracing.Ctf.Contract;

namespace Microsoft.Diagnostics.Tracing.Ctf.ZippedEvent
{
    internal class ZippedCtfEventPacket : ICtfEventPacket
    {
        private readonly Stream _zipStream;

        public ZippedCtfEventPacket(ZipArchiveEntry entry, int traceId, ulong streamId)
        {
            _zipStream = entry.Open();
            Filename = entry.FullName;
            TraceId = traceId;
            StreamId = streamId;
        }

        public string Filename { get; }
        public int TraceId { get; }

        public ulong StreamId { get; }

        public ulong PacketTimestampEnd { get; } = ulong.MaxValue;

        public Stream CreateReadOnlyStream()
        {
            return _zipStream;
        }

        public void Dispose()
        {
            _zipStream?.Dispose();
        }
    }
}