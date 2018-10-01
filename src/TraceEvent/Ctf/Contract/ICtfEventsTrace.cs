using System.Collections.Generic;

namespace Microsoft.Diagnostics.Tracing.Ctf.Contract
{
    public interface ICtfEventsTrace
    {
        IEnumerable<ICtfEventsPacket> EventPackets { get; }

        int TraceId { get; }
        ulong NextSynchronisationTimestamp { get; }
    }
}
