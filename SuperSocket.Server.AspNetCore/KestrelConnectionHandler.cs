using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SuperSocket.Channel;
using SuperSocket.ProtoBase;

namespace SuperSocket.Server.AspNetCore
{
    public class KestrelConnectionHandler<TPackageInfo> : ConnectionHandler
    {
        private readonly ILogger<KestrelConnectionHandler<TPackageInfo>> _logger;
        private readonly IPipelineFilterFactory<TPackageInfo> _pipelineFilterFactory;
        private readonly ISessionFactory _sessionFactory;
        private readonly IServiceProvider _serviceProvider;
        private readonly IPackageHandlingScheduler<TPackageInfo> _packageHandlingScheduler;
        private readonly ServerOptions _serverOptions;
        private int _sessionCount;

        public int SessionCount => this._sessionCount;

        protected IMiddleware[] Middlewares { get; private set; }

        public KestrelConnectionHandler(
            ILogger<KestrelConnectionHandler<TPackageInfo>> logger,
            IPipelineFilterFactory<TPackageInfo> pipelineFilterFactory,
            ISessionFactory sessionFactory,
            IOptions<ServerOptions> serverOptions,
            IServiceProvider serviceProvider,
            ILoggerFactory loggerFactory)
        {
            this._logger = logger;
            this._pipelineFilterFactory = pipelineFilterFactory;
            this._sessionFactory = sessionFactory;
            this._serviceProvider = serviceProvider;
            this._serverOptions = serverOptions.Value;
            this._serverOptions.Logger = loggerFactory.CreateLogger(nameof(IChannel));
            this.InitializeMiddlewares();

            var packageHandler = serviceProvider.GetService<IPackageHandler<TPackageInfo>>()
               ?? this.Middlewares.OfType<IPackageHandler<TPackageInfo>>().FirstOrDefault();

            if (packageHandler == null)
            {
                this._logger.LogWarning("The PackageHandler cannot be found.");
            }
            else
            {
                var errorHandler = serviceProvider.GetService<Func<IAppSession, PackageHandlingException<TPackageInfo>, ValueTask<bool>>>()
                    ?? this.OnSessionErrorAsync;

                this._packageHandlingScheduler = serviceProvider.GetService<IPackageHandlingScheduler<TPackageInfo>>()
                    ?? new KestrelPackageHandlingScheduler<TPackageInfo>();
                this._packageHandlingScheduler.Initialize(packageHandler, errorHandler);
            }
        }

        public override async Task OnConnectedAsync(ConnectionContext connection)
        {
            // required for websocket transport to work
            var transferFormatFeature = connection.Features.Get<ITransferFormatFeature>();
            if (transferFormatFeature != null)
            {
                transferFormatFeature.ActiveFormat = TransferFormat.Binary;
            }

            this._logger.LogInformation(connection.ConnectionId + " connected");

            // 创建一个IChannel
            var channel = new KestrelPipeChannel<TPackageInfo>(this._pipelineFilterFactory.Create("123"), this._serverOptions, connection);

            // session
            var session = this._sessionFactory.Create() as KestrelSession;
            session.SessionID = connection.ConnectionId;
            await this.HandleSessionAscyn(session, channel);
        }

        #region 私有方法

        private void InitializeMiddlewares()
        {
            this.Middlewares = this._serviceProvider.GetServices<IMiddleware>()
                .OrderBy(m => m.Order)
                .ToArray();

            foreach (var m in this.Middlewares)
            {
                m.Start(null);
            }
        }

        private ValueTask<bool> OnSessionErrorAsync(IAppSession session, PackageHandlingException<TPackageInfo> exception)
        {
            this._logger.LogError(exception, $"Session[{session.SessionID}]: session exception.");
            return new ValueTask<bool>(true);
        }

        private async ValueTask HandleSessionAscyn(KestrelSession session, IChannel channel)
        {
            if (!await this.InitializeSession(session, channel))
            {
                return;
            }

            try
            {
                channel.Start();

                await this.FireSessionConnectedEvent(session);

                var packageChannel = channel as IChannel<TPackageInfo>;
                var packageHandlingScheduler = this._packageHandlingScheduler;

                await foreach (var p in packageChannel.RunAsync())
                {
                    await packageHandlingScheduler.HandlePackage(session, p);
                }
            }
            catch (Exception e)
            {
                this._logger.LogError(e, $"Failed to handle the session {session.SessionID}.");
            }
            finally
            {
                var closeReason = channel.CloseReason ?? CloseReason.Unknown;
                await this.FireSessionClosedEvent(session, closeReason);
            }
        }

        private async ValueTask<bool> InitializeSession(IAppSession session, IChannel channel)
        {
            session.Initialize(null, channel);

            if (channel is IPipeChannel pipeChannel)
            {
                pipeChannel.PipelineFilter.Context = this.CreatePipelineContext(session);
            }

            var middlewares = this.Middlewares;

            if (middlewares != null && middlewares.Length > 0)
            {
                for (var i = 0; i < middlewares.Length; i++)
                {
                    var middleware = middlewares[i];

                    if (!await middleware.RegisterSession(session))
                    {
                        this._logger.LogWarning($"A session from {session.RemoteEndPoint} was rejected by the middleware {middleware.GetType().Name}.");
                        return false;
                    }
                }
            }

            return true;
        }

        protected virtual object CreatePipelineContext(IAppSession session)
        {
            return session;
        }

        protected virtual async ValueTask FireSessionConnectedEvent(KestrelSession session)
        {
            if (session is IHandshakeRequiredSession handshakeSession)
            {
                if (!handshakeSession.Handshaked)
                {
                    return;
                }
            }

            this._logger.LogInformation($"A new session connected: {session.SessionID}");

            try
            {
                Interlocked.Increment(ref this._sessionCount);
                await session.FireSessionConnectedAsync();
                await this.OnSessionConnectedAsync(session);
            }
            catch (Exception e)
            {
                this._logger.LogError(e, "There is one exception thrown from the event handler of SessionConnected.");
            }
        }

        protected virtual async ValueTask FireSessionClosedEvent(KestrelSession session, CloseReason reason)
        {
            if (session is IHandshakeRequiredSession handshakeSession)
            {
                if (!handshakeSession.Handshaked)
                {
                    return;
                }
            }

            this._logger.LogInformation($"The session disconnected: {session.SessionID} ({reason})");

            try
            {
                Interlocked.Decrement(ref this._sessionCount);

                var closeEventArgs = new CloseEventArgs(reason);
                await session.FireSessionClosedAsync(closeEventArgs);
                await this.OnSessionClosedAsync(session, closeEventArgs);
            }
            catch (Exception exc)
            {
                this._logger.LogError(exc, "There is one exception thrown from the event of OnSessionClosed.");
            }
        }

        protected virtual ValueTask OnSessionConnectedAsync(IAppSession session)
        {
            return new ValueTask();
        }

        protected virtual ValueTask OnSessionClosedAsync(IAppSession session, CloseEventArgs e)
        {
            return new ValueTask();
        }

        #endregion
    }
}
