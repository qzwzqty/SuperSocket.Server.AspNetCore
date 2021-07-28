using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SuperSocket.Command;
using SuperSocket.ProtoBase;
using SuperSocket.Server.AspNetCore.WebSockets;
using SuperSocket.SessionContainer;

namespace SuperSocket.Server.AspNetCore
{
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// FULL
        /// </summary>
        /// <typeparam name="TReceivePackage"></typeparam>
        /// <typeparam name="TPipelineFilter"></typeparam>
        /// <typeparam name="TSession"></typeparam>
        /// <typeparam name="TKey"></typeparam>
        /// <param name="services"></param>
        /// <param name="configuration"></param>
        /// <param name="cmdConfigurator"></param>
        public static IServiceCollection AddKestrelSuperSocket<TReceivePackage, TPipelineFilter, TSession, TKey>(
            this IServiceCollection services,
            IConfiguration configuration,
            Action<CommandOptions> cmdConfigurator)
             where TPipelineFilter : class, IPipelineFilter<TReceivePackage>
             where TReceivePackage : class, IKeyedPackageInfo<TKey>
             where TSession : IAppSession
        {
            services
                .ConfigureSuperSocket(configuration)
                .AddDiPipelineFilter<TReceivePackage, TPipelineFilter>()
                .AddSession<TSession>()
                .AddSessionContainer()
                .AddCommand<TReceivePackage, TKey>(cmdConfigurator)
                .AddClearIdleSession();

            return services;
        }

        /// <summary>
        /// WebSocket
        /// </summary>
        /// <typeparam name="TReceivePackage"></typeparam>
        /// <typeparam name="TPipelineFilter"></typeparam>
        /// <typeparam name="TSession"></typeparam>
        /// <typeparam name="TKey"></typeparam>
        /// <param name="services"></param>
        /// <param name="configuration"></param>
        /// <param name="cmdConfigurator"></param>
        public static IServiceCollection AddKestrelSuperWebSocket<TReceivePackage, TPipelineFilter, TSession, TKey>(
            this IServiceCollection services,
            IConfiguration configuration,
            Action<CommandOptions> cmdConfigurator)
             where TPipelineFilter : class, IPipelineFilter<TReceivePackage>
             where TReceivePackage : class, IKeyedPackageInfo<TKey>
             where TSession : IAppSession
        {
            services
                .ConfigureSuperSocket(configuration)
                .AddDiPipelineFilter<TReceivePackage, TPipelineFilter>()
                .AddSession<TSession>()
                .AddSessionContainer()
                .AddCommand<TReceivePackage, TKey>(cmdConfigurator)
                .AddClearIdleSession();

            services
                .AddSingleton<KestrelConnectionHandler<TReceivePackage>>()
                .AddConnections();

            return services;
        }

        public static IServiceCollection ConfigureSuperSocket(this IServiceCollection services, Action<ServerOptions> configurator)
        {
            services.Configure(configurator);
            return services;
        }

        public static IApplicationBuilder UseSuperSocketWebSocket<TReceivePackage>(this IApplicationBuilder app, string pattern)
        {
            app.UseWebSockets();
            app.UseMiddleware<WebSocketMiddleware<TReceivePackage>>(pattern);

            return app;
        }

        public static IServiceCollection ConfigureSuperSocket(this IServiceCollection services, IConfiguration configuration)
        {
            var serverConfig = configuration.GetSection("serverOptions");
            services.Configure<ServerOptions>(serverConfig);
            return services;
        }

        public static IServiceCollection AddPipelineFilter<TReceivePackage, TPipelineFilter>(this IServiceCollection services)
           where TPipelineFilter : IPipelineFilter<TReceivePackage>, new()
        {
            services.AddSingleton<IPipelineFilterFactory<TReceivePackage>, DefaultPipelineFilterFactory<TReceivePackage, TPipelineFilter>>();

            return services;
        }

        /// <summary>
        /// TPipelineFilter can use dependency injection
        /// </summary>
        /// <typeparam name="TReceivePackage"></typeparam>
        /// <typeparam name="TPipelineFilter"></typeparam>
        /// <param name="services"></param>
        /// <returns></returns>
        public static IServiceCollection AddDiPipelineFilter<TReceivePackage, TPipelineFilter>(this IServiceCollection services)
             where TPipelineFilter : class, IPipelineFilter<TReceivePackage>
        {
            services.AddSingleton<IPipelineFilterFactory<TReceivePackage>, KestrelPipelineFilterFactory<TReceivePackage, TPipelineFilter>>();

            return services;
        }

        public static IServiceCollection AddSessionContainer(this IServiceCollection services)
        {
            services.AddSingleton<InProcSessionContainerMiddleware>();
            services.AddSingleton<ISessionContainer>((s) => s.GetRequiredService<InProcSessionContainerMiddleware>());
            services.AddSingleton((s) => s.GetRequiredService<ISessionContainer>().ToAsyncSessionContainer());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IMiddleware, InProcSessionContainerMiddleware>(s => s.GetRequiredService<InProcSessionContainerMiddleware>()));
            return services;
        }

        /// <summary>
        /// Commad
        /// </summary>
        /// <typeparam name="TPackageInfo"></typeparam>
        /// <param name="services"></param>
        /// <param name="configurator"></param>
        /// <returns></returns>
        public static IServiceCollection AddCommand<TPackageInfo, TKey>(this IServiceCollection services, Action<CommandOptions> configurator)
            where TPackageInfo : class, IKeyedPackageInfo<TKey>
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IMiddleware, CommandMiddleware<TKey, TPackageInfo>>());
            services.Configure(configurator);
            return services;
        }

        public static IServiceCollection AddClearIdleSession(this IServiceCollection services)
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IMiddleware, ClearIdleSessionMiddleware>());
            return services;
        }

        public static IServiceCollection AddSession<TSession>(this IServiceCollection services)
             where TSession : IAppSession
        {
            services.AddSingleton<ISessionFactory, KestrelSessionFactory<TSession>>();
            return services;
        }
    }
}
