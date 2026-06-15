using Microsoft.Graph.Models.ODataErrors;
using NewsService.Services;
using Xunit;

namespace NewsService.Tests;

/// <summary>
/// Covers GraphFailureClassifier — the pure exception → GraphFailureKind mapping used by both the
/// device read and the group read. 403 is persistent; throttling/5xx/network/cancel are transient.
/// </summary>
public sealed class GraphFailureClassifierTests
{
    private static ODataError ODataWith(int status) => new() { ResponseStatusCode = status };

    [Fact]
    public void ODataError403_IsPermissionDenied() =>
        Assert.Equal(GraphFailureKind.PermissionDenied, GraphFailureClassifier.Classify(ODataWith(403)));

    [Fact]
    public void ODataError404_IsNotFound() =>
        Assert.Equal(GraphFailureKind.NotFound, GraphFailureClassifier.Classify(ODataWith(404)));

    [Fact]
    public void ODataError429_IsTransient() =>
        Assert.Equal(GraphFailureKind.Transient, GraphFailureClassifier.Classify(ODataWith(429)));

    [Fact]
    public void ODataError503_IsTransient() =>
        Assert.Equal(GraphFailureKind.Transient, GraphFailureClassifier.Classify(ODataWith(503)));

    [Fact]
    public void ODataError400_IsOther() =>
        Assert.Equal(GraphFailureKind.Other, GraphFailureClassifier.Classify(ODataWith(400)));

    [Fact]
    public void HttpRequestException_IsTransient() =>
        Assert.Equal(GraphFailureKind.Transient, GraphFailureClassifier.Classify(new HttpRequestException()));

    [Fact]
    public void TaskCanceledException_IsTransient() =>
        Assert.Equal(GraphFailureKind.Transient, GraphFailureClassifier.Classify(new TaskCanceledException()));

    [Fact]
    public void OperationCanceledException_IsTransient() =>
        Assert.Equal(GraphFailureKind.Transient, GraphFailureClassifier.Classify(new OperationCanceledException()));

    [Fact]
    public void TimeoutException_IsTransient() =>
        Assert.Equal(GraphFailureKind.Transient, GraphFailureClassifier.Classify(new TimeoutException()));

    [Fact]
    public void UnknownException_IsOther() =>
        Assert.Equal(GraphFailureKind.Other, GraphFailureClassifier.Classify(new InvalidOperationException()));
}
