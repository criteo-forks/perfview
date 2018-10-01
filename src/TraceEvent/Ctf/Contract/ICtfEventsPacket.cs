﻿using System;
using System.IO;

namespace Microsoft.Diagnostics.Tracing.Ctf.Contract
{
    public interface ICtfEventsPacket : IDisposable
    {
        ulong StreamId { get; }

        Stream CreateReadOnlyStream();
    }
}
