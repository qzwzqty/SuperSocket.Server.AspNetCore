using Microsoft.Extensions.Logging;
using SuperSocket.Server.AspNetCore;

namespace WebSocketDemo2
{
    public class DemoSession : KestrelSession
    {
        public DemoSession(ILoggerFactory loggerFactory) : base(loggerFactory)
        {
        }
    }
}
