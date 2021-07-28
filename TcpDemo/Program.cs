using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SuperSocket.Server.AspNetCore;

namespace TcpDemo
{
    public class Program
    {
        public static void Main(string[] args)
        {
            CreateHostBuilder(args).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args)
        {
            return Host.CreateDefaultBuilder(args)
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.ConfigureKestrel(opt =>
                    {
                        var configuration = opt.ApplicationServices.GetRequiredService<IConfiguration>();

                        // TCP
                        opt.UseTcp<DemoPackInfo>();

                        // TCP 8007
                        opt.ListenLocalhost(8007, builder =>
                        {
                            builder.UseConnectionHandler<MyConnectionHanlder>();
                        });

                        // http
                        opt.ListenAnyIP(configuration.GetValue<int>("App:HttpPort"), configure => configure.Protocols = HttpProtocols.Http1);
                    });

                    webBuilder.UseStartup<Startup>();
                });
        }
    }
}
