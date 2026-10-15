using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;

namespace Idempotency.Poc01;

public sealed class IdempotencyMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IMemoryCache cache)
    {
        if (!HttpMethods.IsPost(context.Request.Method) || context.Request.Path != "/orders")
        {
            await next(context);
            return;
        }

        var keys = context.Request.Headers["Idempotency-Key"];
        if (keys.Count != 1 || string.IsNullOrWhiteSpace(keys[0]) || keys[0]!.Length > 128)
        {
            await Results.BadRequest(new { error = "Informe uma única Idempotency-Key de até 128 caracteres." })
                .ExecuteAsync(context);
            return;
        }

        context.Request.EnableBuffering();
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(context.Request.Body, context.RequestAborted));
        context.Request.Body.Position = 0;
        var cacheKey = $"POST:/orders:{keys[0]}";

        if (cache.TryGetValue<CachedResponse>(cacheKey, out var saved) && saved is not null)
        {
            if (saved.RequestHash != hash)
            {
                await Results.Conflict(new { error = "Esta chave já foi utilizada com outro corpo." })
                    .ExecuteAsync(context);
                return;
            }

            context.Response.StatusCode = saved.StatusCode;
            context.Response.ContentType = saved.ContentType;
            if (saved.Location is not null)
                context.Response.Headers.Location = saved.Location;
            context.Response.Headers["Idempotency-Replayed"] = "true";
            await context.Response.Body.WriteAsync(saved.Body, context.RequestAborted);
            return;
        }

        // Intencionalmente sem reserva atômica: concorrência será o tema da POC 2.
        var originalBody = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next(context);
            var bytes = buffer.ToArray();
            if (context.Response.StatusCode is >= 200 and < 300)
            {
                cache.Set(cacheKey, new CachedResponse(hash, context.Response.StatusCode,
                    context.Response.ContentType, context.Response.Headers.Location.FirstOrDefault(), bytes),
                    TimeSpan.FromMinutes(10));
            }

            context.Response.Headers["Idempotency-Replayed"] = "false";
            await originalBody.WriteAsync(bytes, context.RequestAborted);
        }
        finally
        {
            context.Response.Body = originalBody;
        }
    }

    private sealed record CachedResponse(string RequestHash, int StatusCode,
        string? ContentType, string? Location, byte[] Body);
}
