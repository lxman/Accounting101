using System.Text.Json;
using System.Text.Json.Serialization;
using Accounting101.Angular.DataAccess.Services;
using Accounting101.Angular.DataAccess.Services.Interfaces;
using Accounting101.Angular.Server.Identity;
using Accounting101.Angular.Server.IdentityApiRouteBuilder;
using Accounting101.Angular.Server.Services;
using AspNetCoreIdentity.MongoDriver;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.FileProviders;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
{
    builder
        .SetMinimumLevel(LogLevel.Trace)
        .AddConsole();
});

ILogger logger = loggerFactory.CreateLogger<Program>();
logger.LogInformation("Program.cs logger ready.");

logger.LogInformation("Creating builder.");
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Add services to the container.

BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));

string? allowedOrigins = Environment.GetEnvironmentVariable("AllowedOrigins");
string? baseConnectionString = Environment.GetEnvironmentVariable("MongoClientConnectionString");
if (string.IsNullOrWhiteSpace(baseConnectionString) || string.IsNullOrWhiteSpace(allowedOrigins))
{
    throw new Exception("Environment variable(s) not available");
}

logger.LogInformation($"MongoClientConnectionString: {baseConnectionString}");
logger.LogInformation($"AllowedOrigins: {allowedOrigins}");

string[] allowed = allowedOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries);

builder.Services.AddCors(config =>
{
    config.AddPolicy("AllowAll", policyBuilder =>
    {
        policyBuilder.AllowAnyHeader();
        policyBuilder.WithMethods("GET", "POST", "PUT", "DELETE", "OPTIONS");
        policyBuilder.AllowCredentials();
        policyBuilder.WithOrigins(allowed);
        policyBuilder.SetIsOriginAllowed(origin => true);
    });
});

builder.Services.AddAuthorization();
builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddCookie(IdentityConstants.ApplicationScheme,
    options =>
    {
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.None;
        options.Cookie.Name = "AngularTest.Server";
    });
builder.Services.AddAuthorizationBuilder();

builder.Services.AddIdentityCore<ApplicationUser>()
    .AddMongoDbStores<ApplicationUser, ApplicationRole, Guid>(mongo =>
    {
        mongo.ConnectionString = $"{baseConnectionString}/Identity/";
        mongo.DisableAutoMigrations = true;
    })
    .AddDefaultTokenProviders()
    .AddApiEndpoints();

builder.Services.AddSingleton<IDataStore>(new DataStore(baseConnectionString));

builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddScoped<RoleManager<ApplicationRole>>();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<IBusinessService, BusinessService>();
builder.Services.AddScoped<IClientService, ClientService>();
builder.Services.AddScoped<IAddressService, AddressService>();
builder.Services.AddScoped<IAccountService, AccountService>();
builder.Services.AddScoped<ICoAService, CoAService>();

WebApplication app = builder.Build();

var fileProvider = new PhysicalFileProvider(Path.Combine(app.Environment.WebRootPath, "accounting101.client"));

app.Use(async (context, next) =>
{

    app.Logger.LogInformation($"Request path: {context.Request.Path}");
    app.Logger.LogInformation($"Request method: {context.Request.Method}");
    app.Logger.LogInformation("Headers:");
    app.Logger.LogInformation(JsonSerializer.Serialize(context.Request.Headers));
    context.Request.EnableBuffering();
    await next();
});

app.UseDefaultFiles(new DefaultFilesOptions
{
    FileProvider = fileProvider,
    RequestPath = "",
    DefaultFileNames = ["index.html"]
});
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = fileProvider,
    RequestPath = ""
});
app.UseRouting();

app.UseCors("AllowAll");

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.MapFallbackToFile("/index.html", new StaticFileOptions
{
    FileProvider = fileProvider,
    RequestPath = ""
});

app.MapIdentityApiFilterable<ApplicationUser>(new IdentityApiEndpointRouteBuilderOptions
{
    ExcludeRegisterPost = true,
    ExcludeLoginPost = true,
    ExcludeRefreshPost = false,
    ExcludeConfirmEmailGet = false,
    ExcludeResendConfirmationEmailPost = false,
    ExcludeForgotPasswordPost = false,
    ExcludeResetPasswordPost = false,
    ExcludeManageGroup = false,
    Exclude2FaPost = false,
    ExcludeInfoGet = false,
    ExcludeInfoPost = false
});

app.Run();
