using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Diagnostics.Tracing.Ctf;
using Microsoft.Diagnostics.Tracing.Ctf.Contract;
using Microsoft.Diagnostics.Tracing.Ctf.ZippedEvent;

#pragma warning disable 1591

namespace Microsoft.Diagnostics.Tracing
{
    public sealed class CtfTraceEventSource : TraceEventDispatcher
    {
        private readonly CtfEventConverter _ctfEventsConverter;
        private readonly Dictionary<int, string> _processNames = new Dictionary<int, string>();
        private readonly ICtfTraceProvider _provider;
        private readonly Dictionary<int, CtfMetadata> _traceIdToMetadata;
        private bool _isDisposed;
        private readonly ChannelList _channels;

#if DEBUG
        private StreamWriter _debugOut;
#endif

        public CtfTraceEventSource(string fileName)
            : this(new ZippedCtfTraceProvider(fileName))
        {
        }

        public CtfTraceEventSource(ICtfTraceProvider provider)
        {
            _isDisposed = false;
            _traceIdToMetadata = new Dictionary<int, CtfMetadata>();
            _provider = provider;
            _ctfEventsConverter = new CtfEventConverter(_provider.PointerSize);
            _provider.NewCtfMetadata += OnNewMetadata;
            _provider.NewCtfEventTraces += OnNewCtfTraces;
            _channels = new ChannelList();

#if DEBUG
//// Uncomment for debug output.
//_debugOut = File.CreateText("debug.txt");
//_debugOut.AutoFlush = true;
#endif
        }

        ~CtfTraceEventSource()
        {
            Dispose(false);
        }

        public override int EventsLost => 0;

        public override void StopProcessing()
        {
            _provider.StopProcessing();
            _provider.NewCtfEventTraces -= OnNewCtfTraces;
            _provider.NewCtfMetadata -= OnNewMetadata;
            base.StopProcessing();
        }

        public void ParseMetadata()
        {
            // We don't get this data in LTTng traces (unless we decide to emit them as events later).
            osVersion = new Version("0.0.0.0");
            cpuSpeedMHz = 10;

            // TODO:  This is not IFastSerializable
            /*
            var env = _metadata.Environment;
            var trace = _metadata.Trace;
            userData["hostname"] = env.HostName;
            userData["tracer_name"] = env.TracerName;
            userData["tracer_version"] = env.TracerMajor + "." + env.TracerMinor;
            userData["uuid"] = trace.UUID;
            userData["ctf version"] = trace.Major + "." + trace.Minor;
            */
        }

        public override bool Process()
        {
            _provider.Process();

            return true;
        }

        internal override string ProcessName(int processID, long timeQPC)
        {
            if (_processNames.TryGetValue(processID, out var result))
                return result;

            return base.ProcessName(processID, timeQPC);
        }

        protected override void Dispose(bool disposing)
        {
            if (_isDisposed)
                return;

            _isDisposed = true;

            if (disposing)
            {
                _provider.Dispose();
                _provider.NewCtfEventTraces -= OnNewCtfTraces;
                _provider.NewCtfMetadata -= OnNewMetadata;
                _ctfEventsConverter.Dispose();
                _channels.Dispose();
            }

            base.Dispose(disposing);
        }

        private void OnNewMetadata(ICtfMetadata metadata)
        {
            var parser = new CtfMetadataLegacyParser(metadata.CreateReadOnlyStream());
            if (_traceIdToMetadata.TryGetValue(metadata.TraceId, out var parsedMetadata))
            {
                parsedMetadata.Load(parser);
            }
            else
            {
                parsedMetadata = new CtfMetadata(parser);
                _traceIdToMetadata[metadata.TraceId] = parsedMetadata;
            }
        }

        private unsafe void OnNewCtfTraces(IEnumerable<ICtfEventTrace> ctfTraces)
        {
            ulong lastTimestamp = 0;

            int events = 0;
            var channels = UpdateChannelEntriesFromTraces(ctfTraces);
            foreach (var entry in channels)
            {
                if (stopProcessing)
                {
                    break;
                }

                var header = entry.Current;

                var evt = header.Event;
                if (IsFirstEvent()) Initialize(entry, header);

                lastTimestamp = header.Timestamp;

                entry.Reader.ReadEventIntoBuffer(evt);


                var eventRecord = _ctfEventsConverter.ToEventRecord(header, entry.Reader);
                if (eventRecord == null)
                {
                    Console.WriteLine("Unknown event: " + header.Event.Name);
                    continue;
                }

                events++;
#if DEBUG
                if (_debugOut != null && header.Event.Name.Contains("DotNETRuntime:GC") && entry.Current.Pid != System.Diagnostics.Process.GetCurrentProcess().Id)
                {
                    _debugOut.WriteLine($"Timestamp: {entry.Current.Timestamp} [{evt.Name}] PID: {entry.Current.Pid} TID: {entry.Current.Tid} Event ID: {entry.Current.Event.ID} Channel: {entry.Current.Event.Stream} Event #{events}: {evt.Name}");
                }
#endif

                if (!string.IsNullOrWhiteSpace(header.ProcessName))
                {
                    _processNames[header.Pid] = header.ProcessName;
                }

                var traceEvent = Lookup(eventRecord);
                traceEvent.eventRecord = eventRecord;
                traceEvent.userData = entry.Reader.BufferPtr;
                traceEvent.EventTypeUserData = evt;

                traceEvent.DebugValidate();
                Dispatch(traceEvent);
            }

            sessionEndTimeQPC = (long)lastTimestamp;
        }


        private ChannelList UpdateChannelEntriesFromTraces(IEnumerable<ICtfEventTrace> ctfTraces)
        {
            foreach (var ctfEventTrace in ctfTraces)
            {
                if (!_traceIdToMetadata.TryGetValue(ctfEventTrace.TraceId, out var currentMetadata))
                    throw new Exception($"Metadata for trace {ctfEventTrace.TraceId} does not exist");
                foreach (var ctfEventPacket in ctfEventTrace.EventPackets)
                {
                    _channels.Add(ctfEventPacket, currentMetadata);
                }
            }

            return _channels;
        }

        private void Initialize(ChannelEntry entry, CtfEventHeader header)
        {
            var currentMetadata = entry.Metadata;
            // TODO: Need to cleanly separate clocks, but in practice there's only the one clock.
            var clock = currentMetadata.Clocks.First();
            var time = ConvertEventTimestampToDateTime(header, clock);

            var firstEventTimestamp = (long)header.Timestamp;

            sessionStartTimeQPC = firstEventTimestamp;
            sessionEndTimeQPC = long.MaxValue;
            _syncTimeQPC = firstEventTimestamp;
            _syncTimeUTC = time;
            _QPCFreq = (long)clock.Frequency;

            pointerSize = _provider.PointerSize;
            numberOfProcessors = _provider.ProcessorCount;
        }

        private bool IsFirstEvent()
        {
            return _syncTimeQPC == 0;
        }

        private static DateTime ConvertEventTimestampToDateTime(CtfEventHeader header, CtfClock clock)
        {
            var offset =
                new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc).AddSeconds((clock.Offset - 1) / clock.Frequency);
            var ticks = (long)(header.Timestamp * 10000000.0 / clock.Frequency);

            return new DateTime(offset.Ticks + ticks, DateTimeKind.Utc);
        }

        // Each file has streams which have sets of events.  These classes help merge those channels
        // into one chronological stream of events.

        #region Enumeration Helper

        private class ChannelList : IEnumerable<ChannelEntry>, IDisposable
        {
            private readonly Dictionary<ulong, ChannelEntry> _channelEntries;

            public ChannelList()
            {
                _channelEntries = new Dictionary<ulong, ChannelEntry>();
            }

            public void Add(ICtfEventPacket ctfEventPacket, CtfMetadata metadata)
            {
                if (!_channelEntries.TryGetValue(ctfEventPacket.StreamId, out var channelEntry))
                {
                    channelEntry = new ChannelEntry(ctfEventPacket, metadata);
                    if (channelEntry.MoveNext())
                    {
                        _channelEntries[ctfEventPacket.StreamId] = channelEntry;
                    }
                    else
                    {
                        channelEntry.Dispose();
                    }
                }
                else
                {
                    channelEntry.Add(ctfEventPacket, metadata);
                }

            }

            public IEnumerator<ChannelEntry> GetEnumerator()
            {
                var stopTimestamp = _channelEntries.Values.Min(channel => channel.EndTimestamp);
                return new ChannelListEnumerator(_channelEntries.Values, stopTimestamp);
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                var stopTimestamp = _channelEntries.Values.Min(channel => channel.EndTimestamp);
                return new ChannelListEnumerator(_channelEntries.Values, stopTimestamp);
            }

            public void Dispose()
            {
                foreach (var channel in _channelEntries.Values)
                    channel.Dispose();
            }
        }

        private class ChannelListEnumerator : IEnumerator<ChannelEntry>
        {
            private List<ChannelEntry> _channels;
            private int _current;
            private bool _first = true;
            private readonly ulong _endTimestamp;

            public ChannelListEnumerator(IEnumerable<ChannelEntry> channels, ulong endTimestamp)
            {
                _channels = channels.Where(c => c.HasEvents).ToList();
                _endTimestamp = endTimestamp;
                _current = GetCurrent();
            }

            public ChannelEntry Current => _current != -1 ? _channels[_current] : null;

            public void Dispose()
            {
                _channels = null;
            }

            object IEnumerator.Current => Current;

            public bool MoveNext()
            {
                if (_current == -1)
                {
                    return false;
                }

                if (_first)
                {
                    _first = false;
                    return _channels.Count > 0;
                }

                var hasMore = _channels[_current].MoveNext();
                if (!hasMore)
                {
                    _channels.RemoveAt(_current);
                }

                _current = GetCurrent();
                return _current != -1;
            }

            public void Reset()
            {
                throw new NotImplementedException();
            }

            private int GetCurrent()
            {
                if (_channels.Count == 0)
                    return -1;

                var min = 0;
                var result = 0;
                for (var i = 1; i < _channels.Count; i++)
                {
                    if (_channels[i].Current.Timestamp < _channels[min].Current.Timestamp)
                    {
                        min = i;
                        result = min;
                    }
                }


                if (_channels[min].Current.Timestamp >= _endTimestamp)
                {
                    result = -1;
                }
                return result;
            }
        }

        private class ChannelEntry : IDisposable
        {
            private IEnumerator<CtfEventHeader> _events;
            private ICtfEventPacket _currentCtfEventPacket;
            private readonly Queue<Tuple<ICtfEventPacket, CtfMetadata>> _queuedCtfPackets;
            private Stream _stream;


            public ChannelEntry(ICtfEventPacket currentCtfEventPacket, CtfMetadata metadata)
            {
                _queuedCtfPackets = new Queue<Tuple<ICtfEventPacket, CtfMetadata>>(2);
                _currentCtfEventPacket = currentCtfEventPacket;
                _stream = currentCtfEventPacket.CreateReadOnlyStream();
                Channel = new CtfChannel(_stream, metadata);
                Reader = new CtfReader(Channel, metadata, Channel.CtfStream);
                _events = Reader.EnumerateEventHeaders().GetEnumerator();
                Metadata = metadata;
                EndTimestamp = currentCtfEventPacket.PacketTimestampEnd;
            }

            public void Add(ICtfEventPacket ctfEventPacket, CtfMetadata metadata)
            {
                if (_events != null)
                {
                    _queuedCtfPackets.Enqueue(new Tuple<ICtfEventPacket, CtfMetadata>(ctfEventPacket, metadata));
                }
                else
                {
                    _currentCtfEventPacket = ctfEventPacket;
                    _stream = ctfEventPacket.CreateReadOnlyStream();
                    Channel = new CtfChannel(_stream, metadata);
                    Reader = new CtfReader(Channel, metadata, Channel.CtfStream);
                    Metadata = metadata;
                    _events = Reader.EnumerateEventHeaders().GetEnumerator();
                    _events.MoveNext();
                }
                EndTimestamp = ctfEventPacket.PacketTimestampEnd;
            }

            public CtfChannel Channel { get; private set; }
            public CtfReader Reader { get; private set; }
            public CtfEventHeader Current => _events.Current;
            public CtfMetadata Metadata { get; private set; }
            public ulong EndTimestamp { get; private set; }
            public bool HasEvents => _events != null;

            public void Dispose()
            {
                Reader?.Dispose();
                Channel?.Dispose();
                _currentCtfEventPacket?.Dispose();
                _stream?.Dispose();
                var enumerator = _events;
                enumerator?.Dispose();
                _events = null;
                while (_queuedCtfPackets.Count > 0)
                {
                    var packetAndMetadata = _queuedCtfPackets.Dequeue();
                    packetAndMetadata.Item1.Dispose();
                }
            }

            public bool MoveNext()
            {
                var hasEvents = _events.MoveNext();


                while (!hasEvents && _queuedCtfPackets.Count > 0)
                {
                    Reader?.Dispose();
                    Reader = null;
                    Channel?.Dispose();
                    Channel = null;
                    _currentCtfEventPacket?.Dispose();
                    _currentCtfEventPacket = null;
                    _events?.Dispose();
                    _events = null;
                    _stream?.Dispose();
                    _stream = null;
                    EndTimestamp = ulong.MaxValue;

                    var packetAndMetadata = _queuedCtfPackets.Dequeue();
                    _currentCtfEventPacket = packetAndMetadata.Item1;
                    Metadata = packetAndMetadata.Item2;
                    _stream = _currentCtfEventPacket.CreateReadOnlyStream();
                    Channel = new CtfChannel(_stream, Metadata);
                    Reader = new CtfReader(Channel, Metadata, Channel.CtfStream);
                    _events = Reader.EnumerateEventHeaders().GetEnumerator();
                    hasEvents = _events.MoveNext();
                    EndTimestamp = _currentCtfEventPacket.PacketTimestampEnd;
                }

                if (!hasEvents)
                {
                    Reader.Dispose();
                    Reader = null;
                    Channel.Dispose();
                    Channel = null;
                    _currentCtfEventPacket.Dispose();
                    _currentCtfEventPacket = null;
                    _events.Dispose();
                    _events = null;
                    _stream.Dispose();
                    _stream = null;
                    EndTimestamp = ulong.MaxValue;
                }

                return hasEvents;
            }
        }

        #endregion
    }

}
