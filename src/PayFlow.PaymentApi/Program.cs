using Microsoft.EntityFrameworkCore;
using PayFlow.PaymentApi.Payments;
using PayFlow.PaymentApi.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<PaymentsDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("PayFlowDb")));

builder.Services.AddSingleton(TimeProvider.System);

var app = builder.Build();

app.UseHttpsRedirection();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPaymentEndpoints();

app.Run();
