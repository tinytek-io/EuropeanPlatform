using System.Net;
using System.Text;
using FluentAssertions;

namespace BackOffice.Tests.System;

/// <summary>
///     Transport stub for account's internal API: records every forwarded request (method, path,
///     delegation header, body) and serves canned responses the tests enqueue. Fails loudly when a
///     request arrives without a canned response, so a new unstubbed call surfaces in the test
///     instead of passing against a default 200.
/// </summary>
public sealed class RecordingAccountApiHandler : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode StatusCode, string Body)> _responses = [];

    public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

    // The factory host is shared across a test class, so each test starts from a clean recording
    // surface (xUnit constructs a new test-class instance — and so calls this — before each test).
    public void Reset()
    {
        Requests.Clear();
        _responses.Clear();
    }

    public void Enqueue(HttpStatusCode statusCode, string body = "")
    {
        _responses.Enqueue((statusCode, body));
    }

    // Account's domain failures arrive as ProblemDetails (the same shape ApiResult emits), so the
    // BFF's error-propagation tests enqueue this shape.
    public void EnqueueProblem(HttpStatusCode statusCode, string detail, params (string Property, string Error)[] errors)
    {
        var errorJson = errors.Length == 0
            ? string.Empty
            : $@",""errors"":{{{string.Join(",", errors.Select(e => $@"""{e.Property}"":[""{e.Error}""]"))}}}";
        Enqueue(statusCode, $@"{{""title"":""{statusCode}"",""status"":{(int) statusCode},""detail"":""{detail}""{errorJson}}}");
    }

    public (HttpRequestMessage Request, string Body) SingleRequest()
    {
        return Requests.Should().HaveCount(1).And.Subject.Single();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request, body));

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException(
                $"No canned response for the forwarded request {request.Method} {request.RequestUri}. Enqueue one before calling the endpoint.");
        }

        var (statusCode, responseBody) = _responses.Dequeue();
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
        };
    }
}
