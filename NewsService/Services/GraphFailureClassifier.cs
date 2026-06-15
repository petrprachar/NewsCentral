using Microsoft.Graph.Models.ODataErrors;

namespace NewsService.Services;

/// <summary>How a Microsoft Graph failure should be treated by the Entra resolution callers.</summary>
public enum GraphFailureKind
{
    /// <summary>Throttling / network / timeout — ride the grace window.</summary>
    Transient,

    /// <summary>403 — missing permission/consent. Persistent: clean removal + Error log, never grace.</summary>
    PermissionDenied,

    /// <summary>404 — the queried object does not exist. Authoritative "no".</summary>
    NotFound,

    /// <summary>Anything unrecognized — callers treat as Transient but log it.</summary>
    Other
}

/// <summary>
/// Pure mapping from a caught Graph/HTTP exception to a <see cref="GraphFailureKind"/>. No I/O,
/// no logging — callers decide the per-source action (and what to log) from the kind.
/// </summary>
public static class GraphFailureClassifier
{
    public static GraphFailureKind Classify(Exception ex) => ex switch
    {
        ODataError odata => odata.ResponseStatusCode switch
        {
            403 => GraphFailureKind.PermissionDenied,
            404 => GraphFailureKind.NotFound,
            429 => GraphFailureKind.Transient,
            >= 500 and <= 599 => GraphFailureKind.Transient,
            _ => GraphFailureKind.Other
        },

        // TaskCanceledException derives from OperationCanceledException — list it first.
        TaskCanceledException => GraphFailureKind.Transient,
        OperationCanceledException => GraphFailureKind.Transient,
        HttpRequestException => GraphFailureKind.Transient,
        TimeoutException => GraphFailureKind.Transient,

        _ => GraphFailureKind.Other
    };
}
