using System.Text.RegularExpressions;

namespace Idempotency.Poc03;

// Mesmo registro de serviços e pipeline para Kestrel e TestServer.
public static class ApplicationSetup
{
    public static void AddServices(WebApplicationBuilder builder)
    {
        var id = builder.Configuration["Instance:Id"] ?? "local";
        if (!Regex.IsMatch(id, @"\A[A-Za-z0-9_-]{1,64}\z"))
            throw new ArgumentException("Instance:Id deve ter de 1 a 64 letras ASCII, números, hífen ou underscore.");
        var delay = builder.Configuration.GetValue("Experiment:DelayMilliseconds", 500);
        if (delay is < 0 or > 30_000)
            throw new ArgumentOutOfRangeException(nameof(delay), "Use um atraso entre 0 e 30000 ms.");
        builder.Services.AddSingleton(new InstanceInfo(id, Guid.NewGuid().ToString("N")));
        builder.Services.AddSingleton(new ExperimentOptions(delay));
        builder.Services.AddMemoryCache();
        builder.Services.AddSingleton<OrderStore>();
        builder.Services.AddSingleton<KeyedRequestLock>();
    }

    public static void ConfigurePipeline(WebApplication app)
    {
        // Executa antes da idempotência, inclusive para respostas reaproveitadas.
        app.Use(async (context, next) =>
        {
            var instance = context.RequestServices.GetRequiredService<InstanceInfo>();
            context.Response.Headers["X-Instance-Id"] = instance.Id;
            context.Response.Headers["X-Instance-Run-Id"] = instance.RunId;
            await next(context);
        });
        app.UseMiddleware<IdempotencyMiddleware>();
        app.MapGet("/instance", (InstanceInfo instance) => instance);
        app.MapPost("/orders", async (CreateOrder request, OrderStore store,
            ExperimentOptions experiment, CancellationToken cancellationToken) =>
        {
            if (experiment.DelayMilliseconds > 0)
                await Task.Delay(experiment.DelayMilliseconds, cancellationToken);
            var order = store.Create(request);
            return Results.Created($"/orders/{order.Id}", order);
        });
        app.MapGet("/orders", (OrderStore store) => store.All());
        app.MapGet("/orders/{id:int}", (int id, OrderStore store) =>
            store.Find(id) is { } order ? Results.Ok(order) : Results.NotFound());
    }
}

public sealed record InstanceInfo(string Id, string RunId);
public sealed record ExperimentOptions(int DelayMilliseconds);
