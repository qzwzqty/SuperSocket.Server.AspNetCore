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

        public int SessionCount => _sessionCount;

        protected IMiddleware[] Middlewares { get; private set; }

        public KestrelConnectionHandler(
            ILogger<KestrelConnectionHandler<TPackageInfo>> logger,
            IPipelineFilterFactory<TPackageInfo> pipelineFilterFactory,
            ISessionFactory sessionFactory,
            IOptions<ServerOptions> serverOptions,
            IServiceProvider serviceProvider,
            ILoggerFactory loggerFactory)
        {
            _logger = logger;
            _pipelineFilterFactory = pipelineFilterFactory;
            _sessionFactory = sessionFactory;
            _serviceProvider = serviceProvider;
            _serverOptions = serverOptions.Value;
            _serverOptions.Logger = loggerFactory.CreateLogger(nameof(IChannel));
            InitializeMiddlewares();

            IPackageHandler<TPackageInfo> packageHandler = serviceProvider.GetService<IPackageHandler<TPackageInfo>>()
               ?? this.Middlewares.OfType<IPackageHandler<TPackageInfo>>().FirstOrDefault();

            if (packageHandler == null)
            {
                _logger.LogWarning("The PackageHandler cannot be found.");
            }
            else
            {
                Func<IAppSession, PackageHandlingException<TPackageInfo>, ValueTask<bool>> errorHandler = serviceProvider.GetService<Func<IAppSession, PackageHandlingException<TPackageInfo>, ValueTask<bool>>>()
                    ?? OnSessionErrorAsync;

                _packageHandlingScheduler = serviceProvider.GetService<IPackageHandlingScheduler<TPackageInfo>>()
                    ?? new KestrelPackageHandlingScheduler<TPackageInfo>();
                _packageHandlingScheduler.Initialize(packageHandler, errorHandler);
            }
        }

        public override async Task OnConnectedAsync(ConnectionContext connection)
        {
            // required for websocket transport to work
            ITransferFormatFeature transferFormatFeature = connection.Features.Get<ITransferFormatFeature>();
            if (transferFormatFeature != null)
            {
                transferFormatFeature.ActiveFormat = TransferFormat.Binary;
            }

            _logger.LogInformation(connection.ConnectionId + " connected");

            // 创建一个IChannel
            var channel = new KestrelPipeChannel<TPackageInfo>(_pipelineFilterFactory.Create("123"), _serverOptions, connection);

            // session
            KestrelSession session = _sessionFactory.Create() as KestrelSession;
            session.SessionID = connection.ConnectionId;
            await this.HandleSessionAscyn(session, channel);
        }

        #region 私有方法

        private void InitializeMiddlewares()
        {
            this.Middlewares = _serviceProvider.GetServices<IMiddleware>()
                .OrderBy(m => m.Order)
                .ToArray();

            foreach (IMiddleware m in this.Middlewares)
            {
                m.Start(null);
            }
        }

        private ValueTask<bool> OnSessionErrorAsync(IAppSession session, PackageHandlingException<TPackageInfo> exception)
        {
            _logger.LogError(exception, $"Session[{session.SessionID}]: session exception.");
            return new ValueTask<bool>(true);
        }

        private async ValueTask HandleSessionAscyn(KestrelSession session, IChannel channel)
        {
            if (!await InitializeSession(session, channel))
            {
                return;
            }

            try
            {
                channel.Start();

                await FireSessionConnectedEvent(session);

                IChannel<TPackageInfo> packageChannel = channel as IChannel<TPackageInfo>;
                IPackageHandlingScheduler<TPackageInfo> packageHandlingScheduler = _packageHandlingScheduler;

                await foreach (TPackageInfo p in packageChannel.RunAsync())
                {
                    await packageHandlingScheduler.HandlePackage(session, p);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Failed to handle the session {session.SessionID}.");
            }
            finally
            {
                CloseReason closeReason = channel.CloseReason ?? CloseReason.Unknown;
                await FireSessionClosedEvent(session, closeReason);
            }
        }

        private async ValueTask<bool> InitializeSession(IAppSession session, IChannel channel)
        {
            session.Initialize(null, channel);

            if (channel is IPipeChannel pipeChannel)
            {
                pipeChannel.PipelineFilter.Context = CreatePipelineContext(session);
            }

            IMiddleware[] middlewares = this.Middlewares;

            if (middlewares != null && middlewares.Length > 0)
            {
                for (int i = 0; i < middlewares.Length; i++)
                {
                    IMiddleware middleware = middlewares[i];

                    if (!await middleware.RegisterSession(session))
                    {
                        _logger.LogWarning($"A session from {session.RemoteEndPoint} was rejected by the middleware {middleware.GetType().Name}.");
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

            _logger.LogInformation($"A new session connected: {session.SessionID}");

            try
            {
                Interlocked.Increment(ref _sessionCount);
                await session.FireSessionConnectedAsync();
                await OnSessionConnectedAsync(session);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "There is one exception thrown from the event handler of SessionConnected.");
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

            _logger.LogInformation($"The session disconnected: {session.SessionID} ({reason})");

            try
            {
                Interlocked.Decrement(ref _sessionCount);

                CloseEventArgs closeEventArgs = new CloseEventArgs(reason);
                await session.FireSessionClosedAsync(closeEventArgs);
                await OnSessionClosedAsync(session, closeEventArgs);
            }
            catch (Exception exc)
            {
                _logger.LogError(exc, "There is one exception thrown from the event of OnSessionClosed.");
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
