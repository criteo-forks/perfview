using System;
using System.IO;

namespace Microsoft.Diagnostics.Tracing.Ctf.Contract
{
    public interface ICtfEventPacket : IDisposable
    {
        ulong StreamId { get; }

        ulong PacketTimestampEnd { get; }

        Stream CreateReadOnlyStream();
    }
}
