// PoC backend: standard JWT bearer validation. Not a single line about Kerberos.
using Microsoft.AspNetCore.Authentication.JwtBearer;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.Authority = "http://keycloak.bank.local:8080/realms/bank";
        o.RequireHttpsMetadata = false; // PoC only (dev mode http)
        o.TokenValidationParameters.ValidAudience = "boa-api";
    });
builder.Services.AddAuthorization();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/api/hello", (System.Security.Claims.ClaimsPrincipal user) =>
        $"Hello {user.Identity?.Name ?? user.FindFirst("preferred_username")?.Value}! " +
        "The token signature was validated locally against the JWKS.")
   .RequireAuthorization();

app.Run("http://localhost:5080");
