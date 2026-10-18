using Idempotency.Poc03;

var builder = WebApplication.CreateBuilder(args);
ApplicationSetup.AddServices(builder);
var app = builder.Build();
ApplicationSetup.ConfigurePipeline(app);
app.Run();
