using System;
using System.Buffers;
using Microsoft.Extensions.Logging;
using SuperSocket.ProtoBase;

namespace WebSocketDemo2
{
    public class DemoPipelineFilter : IPipelineFilter<DemoPackInfo>
    {
        private readonly ILogger<DemoPipelineFilter> _logger;

        public DemoPipelineFilter(ILogger<DemoPipelineFilter> logger)
        {
            this._logger = logger;
        }

        public DemoPackInfo Filter(ref SequenceReader<byte> reader)
        {
            return null;
        }

        public IPackageDecoder<DemoPackInfo> Decoder { get; set; }

        public IPipelineFilter<DemoPackInfo> NextFilter { get; internal set; }

        public void Reset()
        {
            throw new NotImplementedException();
        }

        public object Context { get; set; }
    }
}
