using Accounting101.Angular.DataAccess.Services;
using Accounting101.Angular.DataAccess.Services.Interfaces;
using Accounting101.Angular.Server.Identity;
using Accounting101.Angular.Server.IdentityApiRouteBuilder;
using Accounting101.Angular.Server.Services;
using AspNetCoreIdentity.MongoDriver;
using Microsoft.AspNetCore.Identity;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Add services to the container.

BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));

builder.Services.AddCors(config =>
{
    config.AddPolicy("AllowAll", policyBuilder =>
    {
        policyBuilder.AllowAnyHeader();
        policyBuilder.AllowAnyMethod();
        policyBuilder.AllowCredentials();
        policyBuilder.WithOrigins("https://localhost:51597");
        policyBuilder.SetIsOriginAllowed(origin => true);
        policyBuilder.WithMethods("Options");
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
        mongo.ConnectionString = builder.Configuration.GetConnectionString("MongoIdentityDb")!;
        mongo.DisableAutoMigrations = true;
    })
    .AddDefaultTokenProviders()
    .AddApiEndpoints();

builder.Services.AddSingleton<IDataStore>(new DataStore(builder.Configuration.GetConnectionString("MongoClientConnectionString")!));

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddScoped<RoleManager<ApplicationRole>>();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<IBusinessService, BusinessService>();
builder.Services.AddScoped<IClientService, ClientService>();
builder.Services.AddScoped<IAddressService, AddressService>();

WebApplication app = builder.Build();

app.UseCors("AllowAll");

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();

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

app.MapFallbackToFile("/index.html");

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
