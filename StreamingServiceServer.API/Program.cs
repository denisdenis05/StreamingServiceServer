using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using StreamingServiceDownloader.BackgroundServices;
using StreamingServiceServer.Data;
using StreamingServiceServer.Business.Services.Images.Assets;

var builder = WebApplication.CreateBuilder(args);

Microsoft.IdentityModel.Logging.IdentityModelEventSource.ShowPII = builder.Environment.IsDevelopment();

builder.Services.AddApplicationServices();
builder.Services.RegisterHelpers();
builder.Services.AddHostedService<MusicDownloader>();
builder.Services.AddHostedService<PendingDownloadChecker>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddOptions<CoverAssetOptions>().Bind(builder.Configuration.GetSection(CoverAssetOptions.Section))
    .PostConfigure(options => options.Domain = builder.Configuration["Domain"] ?? string.Empty)
    .Validate(options => options.ParallelJobs > 0 && options.BatchSize > 0 && options.MaximumInputBytes > 0
        && options.MaximumOutputBytes > 0 && options.FullDimension >= options.SmallDimension
        && options.SmallDimension >= options.ThumbnailDimension && options.ThumbnailDimension > 0
        && options.JobPollInterval > TimeSpan.Zero && options.RepairInterval > TimeSpan.Zero
        && options.RenderTimeout > TimeSpan.Zero && options.LeaseLifetime > options.RenderTimeout,
        "Cover asset limits and intervals must be positive and dimensions must be ordered.").ValidateOnStart();
builder.Services.AddSingleton<ICoverImageRenderer, FfmpegCoverImageRenderer>();
builder.Services.AddSingleton<ICoverAssetStore>(services => new CoverAssetStore(
    () => services.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(CoverAssetStore)),
    services.GetRequiredService<ICoverImageRenderer>(), services.GetRequiredService<Microsoft.Extensions.Options.IOptions<CoverAssetOptions>>(),
    services.GetRequiredService<TimeProvider>()));
builder.Services.AddScoped<CoverJobInterceptor>();
builder.Services.AddHostedService<StreamingServiceServer.Background.CoverAssetJobService>();

builder.Services.AddControllers();
builder.Services.AddHttpClient();
builder.Services.AddHttpClient(nameof(CoverAssetStore), client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.Add("User-Agent", builder.Configuration["Music:External:MusicBrainz:User-Agent"] ?? "StreamingService/1.0");
});
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

builder.Services.AddAuthorization();

builder.Services.AddEndpointsApiExplorer(); 
builder.Services.AddDbContext<StreamingDbContext>((services, options) =>
{
    var connStr = builder.Configuration.GetConnectionString("DefaultConnection");
    if (string.IsNullOrWhiteSpace(connStr) || connStr == "-")
    {
        connStr = "Host=localhost;Database=streaming_db;Username=postgres;Password=postgres";
    }
    options.UseNpgsql(connStr);
    options.AddInterceptors(services.GetRequiredService<CoverJobInterceptor>());
    options.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
});

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
    options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
    options.SaveToken = true;
    
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = builder.Configuration["Jwt:Issuer"],
        ValidAudience = builder.Configuration["Jwt:Audience"], 
        IssuerSigningKey = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(builder.Configuration["Jwt:SecretKey"])),
        RoleClaimType = ClaimTypes.Role,
        NameClaimType = "name",
        ClockSkew = TimeSpan.Zero 
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
app.UseDefaultFiles();
app.UseStaticFiles();

var coverPath = app.Configuration["Cover:Path"];
if (!string.IsNullOrWhiteSpace(coverPath))
{
    Directory.CreateDirectory(coverPath);
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Path.GetFullPath(coverPath)),
        RequestPath = "/" + app.Configuration.GetValue<string>("Cover:DomainExtension", "/images/")!.Trim('/'),
        OnPrepareResponse = context => context.Context.Response.Headers.CacheControl =
            StreamingServiceServer.Business.Services.Images.Assets.CoverServingPolicy.CacheControl(context.File.Name)
    });
}
app.UseHttpsRedirection();
app.UseAuthentication();    
app.UseAuthorization();
app.MapControllers();
app.Run();
