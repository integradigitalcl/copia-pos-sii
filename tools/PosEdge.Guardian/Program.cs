using PosEdge.Guardian;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "PosEdgeGuardian");
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
