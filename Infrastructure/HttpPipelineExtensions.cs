namespace ComplexityScorer.Infrastructure;

public static class HttpPipelineExtensions
{
    public static WebApplication UseApiErrorHandling(this WebApplication app)
    {
        // Use sanitized errors in Development as well: never return exception details.
        app.UseExceptionHandler(error => error.Run(context =>
            Results.Problem(statusCode: 500, title: "An unexpected error occurred.").ExecuteAsync(context)));

        return app;
    }

    public static WebApplication UseApiResponseHeaders(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            await next(context);
        });

        return app;
    }

    public static WebApplication UseRequiredHttps(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
            app.UseHsts();

        app.UseHttpsRedirection();
        app.Use(async (context, next) =>
        {
            // HTTPS redirection alone allows HTTP through when no HTTPS port is configured.
            if (!context.Request.IsHttps)
            {
                await Results.Problem(statusCode: 400, title: "HTTPS is required.").ExecuteAsync(context);
                return;
            }
            await next(context);
        });

        return app;
    }
}
