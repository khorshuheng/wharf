using Wharf.Storage;

namespace Wharf.Api;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/healthz", () => Results.Text("ok"));

        app.MapGet("/readyz", (Database database) =>
        {
            try
            {
                using var connection = database.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT 1";
                command.ExecuteScalar();
                return Results.Text("ready");
            }
            catch (Exception ex)
            {
                return Results.Text($"not ready: {ex.Message}", statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
    }
}
