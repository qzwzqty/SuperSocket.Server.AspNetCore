using System;
using System.Linq;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace SuperSocket.Server.AspNetCore
{
    public static class KestrelServerExtensions
    {
        public static void UseTcp<TPackageInfo>(this KestrelServerOptions options)
        {
            var serverOptions = options.ApplicationServices.GetRequiredService<IOptions<ServerOptions>>()?.Value;
            if (serverOptions == null)
            {
                throw new ArgumentNullException(nameof(serverOptions));
            }

            if (serverOptions.Listeners == null || !serverOptions.Listeners.Any())
            {
                throw new ArgumentNullException(nameof(serverOptions.Listeners));
            }

            foreach (var listener in serverOptions.Listeners)
            {
                var port = listener.Port;
                var ip = listener.Ip;

                var ipAddress = "any".Equals(ip, StringComparison.OrdinalIgnoreCase)
                    ? IPAddress.Any
                    : "IpV6Any".Equals(ip, StringComparison.OrdinalIgnoreCase) ? IPAddress.IPv6Any : IPAddress.Parse(ip);

                options.Listen(ipAddress, port, l => l.UseTcp<TPackageInfo>());
            }
        }

        public static IConnectionBuilder UseTcp<TPackageInfo>(this IConnectionBuilder builder)
        {
            return builder.UseConnectionHandler<KestrelConnectionHandler<TPackageInfo>>();
        }

        /// <summary>
        /// WebSocket
        /// </summary>
        /// <typeparam name="TPackageInfo"></typeparam>
        /// <param name="endpoints"></param>
        /// <param name="pattern"></param>
        public static void MapWebSocket<TPackageInfo>(this IEndpointRouteBuilder endpoints, string pattern)
        {
            endpoints.MapConnectionHandler<KestrelConnectionHandler<TPackageInfo>>(pattern);
        }
    }
}
