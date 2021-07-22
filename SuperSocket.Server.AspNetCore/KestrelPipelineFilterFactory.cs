using System;
using Microsoft.Extensions.DependencyInjection;
using SuperSocket.ProtoBase;

namespace SuperSocket.Server.AspNetCore
{
    public class KestrelPipelineFilterFactory<TPackageInfo, TPipelineFilter>
        : PipelineFilterFactoryBase<TPackageInfo>
        where TPipelineFilter : class, IPipelineFilter<TPackageInfo>
    {
        private readonly IServiceProvider _serviceProvider;

        public KestrelPipelineFilterFactory(IServiceProvider serviceProvider)
            : base(serviceProvider)
        {
            this._serviceProvider = serviceProvider;
        }

        protected override IPipelineFilter<TPackageInfo> CreateCore(object client)
        {
            var pipelineFilter = ActivatorUtilities.CreateInstance<TPipelineFilter>(this._serviceProvider);

            return pipelineFilter ?? throw new ArgumentException(nameof(pipelineFilter));
        }
    }
}
