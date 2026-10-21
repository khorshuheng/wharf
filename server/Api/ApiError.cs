namespace Wharf.Api;

public sealed record ErrorEnvelope(ErrorBody Error);

public sealed record ErrorBody(string Code, string Message, object? Details);

public static class ApiError
{
    public static IResult Create(int status, string code, string message, object? details = null) =>
        Results.Json(new ErrorEnvelope(new ErrorBody(code, message, details)), ApiJson.Options, statusCode: status);

    public static IResult BadRequest(string message) =>
        Create(400, "bad_request", message);

    public static IResult Forbidden(string message) =>
        Create(403, "forbidden", message);

    public static IResult NotFound(string message) =>
        Create(404, "not_found", message);

    public static IResult Conflict(string message, object? details = null) =>
        Create(409, "conflict", message, details);

    public static IResult CursorExpired(long current) =>
        Create(410, "cursor_expired", "Cursor is older than the retention window", new { Current = current });

    public static IResult SessionExpired() =>
        Create(410, "session_expired", "Upload session expired or already completed");

    public static IResult PreconditionFailed(string message) =>
        Create(412, "precondition_failed", message);

    public static IResult PayloadTooLarge(long limit) =>
        Create(413, "payload_too_large", $"Payload exceeds the {limit} byte limit");

    public static IResult QuotaExceeded() =>
        Create(507, "quota_exceeded", "Storage quota exceeded");
}
