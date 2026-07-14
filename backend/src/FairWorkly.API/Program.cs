using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using FairWorkly.API.ExceptionHandlers;
using FairWorkly.Application;
using FairWorkly.Infrastructure;
using FairWorkly.Infrastructure.Identity;
using FairWorkly.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Serilog;
using Swashbuckle.AspNetCore.Filters;

// ============ Serilog Configuration ============
var configuration = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile(
        $"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"}.json",
        optional: true
    )
    .Build();

var loggerConfig = new LoggerConfiguration().ReadFrom.Configuration(configuration);

var fileLoggingEnabled = configuration.GetValue<bool>("FileLogging:Enabled");
if (fileLoggingEnabled)
{
    var logPath = configuration.GetValue<string>("FileLogging:Path") ?? "logs/fairworkly-.log";
    loggerConfig.WriteTo.File(
        path: logPath,
        rollingInterval: RollingInterval.Day,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}"
    );
}

Log.Logger = loggerConfig.CreateLogger();

// ============ Serilog Configuration End ============

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog();

    // Register Application and Infrastructure services (DependencyInjection.cs)
    // Note: IHttpContextAccessor + ICurrentUserService registered in Infrastructure/DependencyInjection.cs
    builder.Services.AddApplicationServices();
    builder.Services.AddInfrastructureServices(builder.Configuration);

    // Add controllers with JSON enum-as-string serialization
    builder
        .Services.AddControllers()
        .AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });

    // Add Swagger generator
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(c =>
    {
        // enable example filters from Swashbuckle.AspNetCore.Filters
        c.ExampleFilters();

        // Add Bearer auth to Swagger
        c.AddSecurityDefinition(
            "Bearer",
            new OpenApiSecurityScheme
            {
                Description =
                    "JWT Authorization header using the Bearer scheme. Enter 'Bearer {token}'",
                Name = "Authorization",
                In = ParameterLocation.Header,
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
            }
        );

        c.AddSecurityRequirement(
            new OpenApiSecurityRequirement
            {
                {
                    new OpenApiSecurityScheme
                    {
                        Reference = new OpenApiReference
                        {
                            Type = ReferenceType.SecurityScheme,
                            Id = "Bearer",
                        },
                    },
                    new string[] { }
                },
            }
        );
    });

    // Register example providers from this assembly (LoginCommandExample)
    builder.Services.AddSwaggerExamplesFromAssemblyOf<FairWorkly.API.SwaggerExamples.LoginCommandExample>();

    // Explicitly register Global Exception Handler
    builder.Services.AddSingleton<IExceptionHandler, GlobalExceptionHandler>();
    builder.Services.AddProblemDetails();

    // Rate limiting — protects unauthenticated auth endpoints from abuse
    builder.Services.AddRateLimiter(options =>
    {
        options.AddPolicy(
            "auth",
            context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        Window = TimeSpan.FromMinutes(1),
                        PermitLimit = 5,
                        QueueLimit = 0,
                    }
                )
        );
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    });

    // Add CORS
    builder.Services.AddCors(options =>
    {
        options.AddPolicy(
            "AllowFrontend",
            policy =>
            {
                if (builder.Environment.IsDevelopment())
                {
                    policy
                        .WithOrigins("http://localhost:5173", "http://localhost:3000")
                        .AllowAnyMethod()
                        .AllowAnyHeader()
                        .AllowCredentials();
                }
                else
                {
                    var allowedOrigins =
                        builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                        ?? Array.Empty<string>();
                    policy
                        .WithOrigins(allowedOrigins)
                        .AllowAnyMethod()
                        .AllowAnyHeader()
                        .AllowCredentials();
                }
            }
        );
    });

    // Configure JWT authentication
    var jwtSection = builder.Configuration.GetSection("JwtSettings");
    var jwtSecret = jwtSection["Secret"] ?? builder.Configuration["JwtSettings:Secret"];
    if (string.IsNullOrWhiteSpace(jwtSecret) || jwtSecret == "REPLACE_IN_ENVIRONMENT")
    {
        throw new InvalidOperationException(
            "JwtSettings:Secret is missing. Set it in appsettings.Development.json (copy from appsettings.Development.example.json)."
        );
    }
    var jwtIssuer = jwtSection["Issuer"] ?? builder.Configuration["JwtSettings:Issuer"];
    var jwtAudience = jwtSection["Audience"] ?? builder.Configuration["JwtSettings:Audience"];

    var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret));

    builder
        .Services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(options =>
        {
            options.RequireHttpsMetadata = false;
            options.SaveToken = true;
            options.MapInboundClaims = false; // Keep JWT claim names as-is (e.g. "role" not the long URI)
            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = async context =>
                {
                    var sub =
                        context.Principal?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                        ?? context.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                    var authVersionClaim = context
                        .Principal?.FindFirst(TokenService.AuthVersionClaimType)
                        ?.Value;

                    if (
                        !Guid.TryParse(sub, out var userId)
                        || string.IsNullOrWhiteSpace(authVersionClaim)
                    )
                    {
                        context.Fail("Invalid access token claims.");
                        return;
                    }

                    var db =
                        context.HttpContext.RequestServices.GetRequiredService<FairWorklyDbContext>();
                    var user = await db.Users.FirstOrDefaultAsync(
                        u => u.Id == userId,
                        context.HttpContext.RequestAborted
                    );

                    if (user == null || !user.IsActive)
                    {
                        context.Fail("User is no longer available.");
                        return;
                    }

                    var currentAuthVersion = TokenService.GetAuthVersion(user);
                    if (
                        !string.Equals(
                            authVersionClaim,
                            currentAuthVersion,
                            StringComparison.Ordinal
                        )
                    )
                    {
                        context.Fail("Access token is stale.");
                    }
                },
            };
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = key,
                ValidateIssuer = !string.IsNullOrEmpty(jwtIssuer),
                ValidIssuer = jwtIssuer,
                ValidateAudience = !string.IsNullOrEmpty(jwtAudience),
                ValidAudience = jwtAudience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                RoleClaimType = "role", // Match the custom "role" claim in our JWT
            };
        });

    builder.Services.AddAuthorization(options =>
    {
        options.AddPolicy("RequireAdmin", policy => policy.RequireRole("Admin"));
        options.AddPolicy("RequireManager", policy => policy.RequireRole("Admin", "Manager"));
        options.AddPolicy("EmployeeOnly", policy => policy.RequireRole("Employee"));
    });

    /* -------------------------------------- */
    /* app */
    var app = builder.Build();

    // Enable exception handling middleware
    // Must be placed at the front of the pipeline
    app.UseExceptionHandler(errorApp =>
    {
        errorApp.Run(async context =>
        {
            var exceptionHandlerFeature = context.Features.Get<IExceptionHandlerFeature>();
            var exception = exceptionHandlerFeature?.Error;

            if (exception != null)
            {
                var handler = context.RequestServices.GetRequiredService<IExceptionHandler>();
                await handler.TryHandleAsync(context, exception, CancellationToken.None);
            }
        });
    });

    // Enable Swagger UI in Development
    if (app.Environment.IsDevelopment())
    {
        await DbSeeder.SeedAsync(app);
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    // Must before UseAuthorization
    app.UseCors("AllowFrontend");

    app.UseHttpsRedirection();

    app.UseRateLimiter();

    // Authentication must come before Authorization
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapControllers();

    app.MapGet("/health", () => Results.Ok(new { status = "healthy" }))
        .AllowAnonymous()
        .ExcludeFromDescription();

    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

// Make Program class accessible for integration tests
public partial class Program { }
