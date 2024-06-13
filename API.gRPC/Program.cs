using Neighborly;
using Neighborly.API;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddLogging();
builder.Services.AddGrpc();
builder.Services.AddSingleton<VectorDatabase>();

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(serviceName: "Neighborly"))
    .WithTracing(builder => 
    {
        builder.AddAspNetCoreInstrumentation()
            .AddInstrumentation<Instrumentation>()
            .AddSource(Instrumentation.Instance.ActivitySource.Name);
    })
    .WithMetrics(builder =>
    {
        builder.AddRuntimeInstrumentation()
            .AddAspNetCoreInstrumentation()
            .AddInstrumentation<Instrumentation>()
            .AddMeter(Instrumentation.Instance.Meter.Name);
    })
    .UseOtlpExporter();

var app = builder.Build();

// Get the application lifetime object
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();

// Get the VectorDatabase instance
var vectorDatabase = app.Services.GetRequiredService<VectorDatabase>();

var databasePath = Environment.GetEnvironmentVariable("DATABASE_PATH");
if (string.IsNullOrEmpty(databasePath))
{
    databasePath = "";
}

bool gRPCEnable = Environment.GetEnvironmentVariable("PROTO_GRPC") == "true";
bool RESTEnable = Environment.GetEnvironmentVariable("PROTO_REST") == "true";


// Shutdown the application if both gRPC and REST are disabled
if (!gRPCEnable && !RESTEnable)
{
    app.Logger.AppShuttingDownAsNoProtocolsEnabled();
    await app.StopAsync();
    return;
}

// Load Database on application start
await vectorDatabase.LoadAsync(databasePath);

// Save Database on application shutdown
lifetime.ApplicationStopping.Register(() =>
{
    vectorDatabase.SaveAsync(databasePath).RunSynchronously();
});

// Configure the gRPC API
if (gRPCEnable)
{
    app.MapGrpcService<VectorService>();
}

// Configure the REST API
if (RESTEnable)
{
    API.Services.RestServices.MapVectorRoutes(app);
}

app.Run();
