using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using StreamingServiceDownloader.BackgroundServices;
using StreamingServiceServer.Data;

var builder = WebApplication.CreateBuilder(args);

// Enable PII logging for debugging JWT issues (REMOVE IN PRODUCTION!)
Microsoft.IdentityModel.Logging.IdentityModelEventSource.ShowPII = true;

builder.Services.AddApplicationServices();
builder.Services.RegisterHelpers();
builder.Services.AddHostedService<MusicDownloader>();
builder.Services.AddHostedService<PendingDownloadChecker>();

builder.Services.AddControllers();
builder.Services.AddHttpClient();
builder.Services.AddHttpClient("unsafeHttp", client =>
    {
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.Add("User-Agent", "Torrent/1.0");
    })
    .ConfigurePrimaryHttpMessageHandler(() =>
    {
        return new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = 
                (message, cert, chain, errors) => true
        };
    });

builder.Services.AddAuthorization(options =>
{
    // Add a default policy that requires authentication
    options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

builder.Services.AddEndpointsApiExplorer(); 
builder.Services.AddDbContext<StreamingDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddSwaggerGen(c =>
{
    // Add a Bearer token authorization definition to Swagger
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Enter 'Bearer' followed by your JWT token"
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            new string[] {}
        }
    });
});

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.RequireHttpsMetadata = false; // For development only
    options.SaveToken = true;
    
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = builder.Configuration["Jwt:Issuer"], // Should be "streamingservice"
        ValidAudience = builder.Configuration["Jwt:Audience"], // Should be "streamingservice-users"
        IssuerSigningKey = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(builder.Configuration["Jwt:SecretKey"])),
        RoleClaimType = ClaimTypes.Role,
        NameClaimType = "name",
        ClockSkew = TimeSpan.Zero // Remove default 5 minute clock skew
    };
    
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var authHeader = context.Request.Headers["Authorization"].FirstOrDefault();
            Console.WriteLine($"Full Authorization header: {authHeader}");
            
            var token = authHeader?.Split(" ", StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (!string.IsNullOrEmpty(token))
            {
                Console.WriteLine($"Extracted token length: {token.Length}");
                Console.WriteLine($"Token parts count: {token.Split('.').Length}");
                
                // Validate basic JWT structure
                var parts = token.Split('.');
                if (parts.Length == 3)
                {
                    Console.WriteLine($"Header length: {parts[0].Length}");
                    Console.WriteLine($"Payload length: {parts[1].Length}");
                    Console.WriteLine($"Signature length: {parts[2].Length}");
                }
                else
                {
                    Console.WriteLine("Invalid JWT structure - not 3 parts!");
                }
            }
            return Task.CompletedTask;
        },
        
        OnAuthenticationFailed = ctx =>
        {
            Console.WriteLine($"Auth failed: {ctx.Exception.Message}");
            Console.WriteLine($"Exception type: {ctx.Exception.GetType().Name}");
            if (ctx.Exception.InnerException != null)
            {
                Console.WriteLine($"Inner exception: {ctx.Exception.InnerException.Message}");
            }
            return Task.CompletedTask;
        },

        OnTokenValidated = ctx =>
        {
            var claims = ctx.Principal?.Claims.Select(c => new { c.Type, c.Value });
            Console.WriteLine($"Token validated: {System.Text.Json.JsonSerializer.Serialize(claims)}");
            return Task.CompletedTask;
        }
    };
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy.AllowAnyOrigin() // For development only, will need to be changed
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});
     
var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();   
    app.UseSwaggerUI();  
}

app.UseCors("AllowFrontend");
app.UseHttpsRedirection();
app.UseAuthentication();    
app.UseAuthorization();
app.MapControllers();
app.Run();