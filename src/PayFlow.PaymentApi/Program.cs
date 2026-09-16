using Azure.Messaging.ServiceBus;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using PayFlow.PaymentApi.Outbox;
using PayFlow.PaymentApi.Payments;
using PayFlow.PaymentApi.Persistence;

// Dapper needs its timestamptz mapping before any read path runs.
DapperMappings.EnsureRegistered();

var builder = WebApplication.CreateBuilder(args);

// One data source, so the EF write path and the dispatcher's Dapper reads share
// a single connection pool instead of opening two against the same database.
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(
    builder.Configuration.GetConnectionString("PayFlowDb")
    ?? throw new InvalidOperationException("The PayFlowDb connection string is not configured.")));

builder.Services.AddDbContext<PaymentsDbContext>((serviceProvider, options) =>
    options.UseNpgsql(serviceProvider.GetRequiredService<NpgsqlDataSource>()));

builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddOptions<ServiceBusOptions>()
    .Bind(builder.Configuration.GetSection(ServiceBusOptions.SectionName))
    .ValidateDataAnnotations();

builder.Services.AddOptions<OutboxDispatcherOptions>()
    .Bind(builder.Configuration.GetSection(OutboxDispatcherOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// Not ValidateOnStart for the Service Bus options: the credential is missing in
// any environment that does not dispatch, and the API's own write path stays
// useful there. The failure surfaces when the client is first resolved instead.
builder.Services.AddSingleton(serviceProvider => new ServiceBusClient(
    serviceProvider.GetRequiredService<IOptions<ServiceBusOptions>>().Value.ConnectionString));

builder.Services.AddSingleton<IOutboxPublisher, ServiceBusOutboxPublisher>();

builder.Services.AddHostedService<OutboxDispatcher>();

var app = builder.Build();

app.UseHttpsRedirection();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPaymentEndpoints();

app.Run();
