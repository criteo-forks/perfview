using System.Collections.Generic;
using Microsoft.Diagnostics.Tracing.Ctf.Contract;

namespace Microsoft.Diagnostics.Tracing.Ctf.ZippedEvent
{
    internal class ZippedCtfEventsTrace : ICtfEventsTrace
    {
        public ZippedCtfEventsTrace(int traceId, IEnumerable<ICtfEventsPacket> eventPackets)
        {
            TraceId = traceId;
            EventPackets = eventPackets;
        }

        public int TraceId { get; }
        public ulong NextSynchronisationTimestamp { get; } = ulong.MaxValue;

        public IEnumerable<ICtfEventsPacket> EventPackets { get; }
    }
}