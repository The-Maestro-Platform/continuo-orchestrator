namespace Orchestrator.Hosting.Endpoints;

public sealed class SecurityEndpoints : TechEndpointBase {
    public SecurityEndpoints(WebApplication app) : base(app) { }

    public override void Map() {
        AsSecurity(App.MapGet("/csrf/token", (HttpContext context, IWebHostEnvironment env) => {
            var token = GenerateCsrfToken();
            var cookieDomain = App.Configuration["COOKIE_DOMAIN"] ?? App.Configuration["AUTH_COOKIE_DOMAIN"];
            var options = new CookieOptions {
                HttpOnly = false,
                Secure = !env.IsDevelopment(),
                SameSite = SameSiteMode.Lax,
                Path = "/",
                MaxAge = TimeSpan.FromMinutes(30)
            };
            if (!string.IsNullOrWhiteSpace(cookieDomain) &&
                !string.Equals(cookieDomain, "localhost", StringComparison.OrdinalIgnoreCase)) {
                options.Domain = cookieDomain;
            }
            context.Response.Cookies.Append("csrf_token", token, options);
            return Results.Ok(new { token });
        }));
    }
}
