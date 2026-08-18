using PosEdge.Workers;
using Microsoft.EntityFrameworkCore;
using PosEdge.Infrastructure;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<LeaseExpirationWorker>();

builder.Services.AddDbContext<PosEdgeDbContext>(o =>
{
    var cs = builder.Configuration.GetConnectionString("PosEdge");
    o.UseNpgsql(cs);
});

var host = builder.Build();
host.Run();
