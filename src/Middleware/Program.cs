var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.Run();

// Exposed so the test harness can reference this host with WebApplicationFactory.
public partial class Program;
