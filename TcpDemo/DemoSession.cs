using Microsoft.Extensions.Logging;
using SuperSocket.Server.AspNetCore;

namespace TcpDemo
{
    public class DemoSession : KestrelSession
    {
        public DemoSession(ILoggerFactory loggerFactory) : base(loggerFactory)
        {
        }
    }
}
