using NxDesk.SignalingServer.Hubs;

var builder = WebApplication.CreateBuilder(args);

// 1. Agregar servicios de SignalR (FALTABA ESTO)
builder.Services.AddSignalR();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// 2. Configurar CORS (CRÍTICO para Ngrok y conexiones externas)
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyHeader()
              .AllowAnyMethod()
              .SetIsOriginAllowed((host) => true) // Permite cualquier origen (ngrok, localhost, etc)
              .AllowCredentials();
    });
});

var app = builder.Build();

// 3. Forzar el puerto 5000 (Para que inicies Ngrok así: "ngrok http 5000")
app.Urls.Add("http://localhost:5000");

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthorization();

// 4. Usar la política de CORS
app.UseCors("AllowAll");

app.MapControllers();

// 5. Mapear la ruta del Hub (FALTABA ESTO)
app.MapHub<SignalingHub>("/signalinghub");

app.Run();