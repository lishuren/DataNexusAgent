using DataNexus.Core;

namespace DataNexus.Endpoints;

public static class RegistryExceptionResults
{
    public static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (ResourceNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (ResourceConflictException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
        catch (ResourceAccessDeniedException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }
}