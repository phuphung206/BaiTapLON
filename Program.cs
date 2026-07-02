using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using CanvasServer.Core;
using CanvasServer.Network;

Console.WriteLine("=== KHỞI ĐỘNG CANVAS SERVER ===");

IHost host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((hostContext, services) =>
    {
        services.AddSingleton(new ServerConfig());
        // Đăng ký Core
        services.AddSingleton<RoomManager>();
        services.AddSingleton<IPacketSender, PacketSender>();
        services.AddSingleton<ChatHandler>();
        services.AddSingleton<SyncCoordinator>();

        // Đăng ký Network Service
        services.AddHostedService<TcpServerHandler>();
        services.AddHostedService<UdpBroadcastHandler>();
    })
    .Build();

await host.RunAsync();
