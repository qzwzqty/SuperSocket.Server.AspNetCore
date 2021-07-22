using System;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SuperSocket.Channel;

namespace SuperSocket.Server.AspNetCore
{
    public class ClearIdleSessionMiddleware : MiddlewareBase
    {
        private readonly ISessionContainer _sessionContainer;

        private Timer _timer;

        private readonly ServerOptions _serverOptions;

        private readonly ILogger _logger;

        public ClearIdleSessionMiddleware(IServiceProvider serviceProvider, IOptions<ServerOptions> serverOptions, ILoggerFactory loggerFactory)
        {
            this._sessionContainer = serviceProvider.GetService<ISessionContainer>();

            var a = this._sessionContainer.GetHashCode();
            if (this._sessionContainer == null)
            {
                throw new Exception($"{nameof(ClearIdleSessionMiddleware)} needs a middleware of {nameof(ISessionContainer)}");
            }

            this._serverOptions = serverOptions.Value;
            this._logger = loggerFactory.CreateLogger<ClearIdleSessionMiddleware>();
        }

        public override void Start(IServer server)
        {
            this._timer = new Timer(this.OnTimerCallback, null, this._serverOptions.ClearIdleSessionInterval * 1000, this._serverOptions.ClearIdleSessionInterval * 1000);
        }

        private void OnTimerCallback(object state)
        {
            this._timer.Change(Timeout.Infinite, Timeout.Infinite);

            try
            {
                var timeoutTime = DateTimeOffset.Now.AddSeconds(0 - this._serverOptions.IdleSessionTimeOut);

                foreach (var s in this._sessionContainer.GetSessions())
                {
                    if (s.LastActiveTime <= timeoutTime)
                    {
                        try
                        {
                            s.Channel.CloseAsync(CloseReason.TimeOut);
                            this._logger.LogWarning($"Close the idle session {s.SessionID}, it's LastActiveTime is {s.LastActiveTime}.");
                        }
                        catch (Exception exc)
                        {
                            this._logger.LogError(exc, $"Error happened when close the session {s.SessionID} for inactive for a while.");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                this._logger.LogError(e, "Error happened when clear idle session.");
            }

            this._timer.Change(this._serverOptions.ClearIdleSessionInterval * 1000, this._serverOptions.ClearIdleSessionInterval * 1000);
        }

        public override void Shutdown(IServer server)
        {
            this._timer.Change(Timeout.Infinite, Timeout.Infinite);
            this._timer.Dispose();
            this._timer = null;
        }
    }
}
