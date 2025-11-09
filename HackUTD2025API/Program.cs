var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddControllers();

builder.Services.AddHttpClient();

builder.Services.AddScoped<NvidiaDataExtractor>();
builder.Services.AddScoped<NeMoEmbeddingService>();

var app = builder.Build();

app.MapControllers();

app.UseHttpsRedirection();

app.Run();
