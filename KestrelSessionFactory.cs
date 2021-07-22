using System;
using Microsoft.Extensions.DependencyInjection;

namespace SuperSocket.Server.AspNetCore
{
    public class KestrelSessionFactory<TSession> : ISessionFactory
         where TSession : IAppSession
    {
        public Type SessionType => typeof(TSession);

        public IServiceProvider ServiceProvider { get; private set; }

        public KestrelSessionFactory(IServiceProvider serviceProvider)
        {
            this.ServiceProvider = serviceProvider;
        }

        public IAppSession Create()
        {
            return ActivatorUtilities.CreateInstance<TSession>(this.ServiceProvider);
        }
    }
}
